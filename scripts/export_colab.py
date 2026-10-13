"""Export a consistent capture snapshot and a configurable Google Colab notebook."""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import tempfile
import textwrap
import zipfile
from contextlib import closing
from datetime import UTC, datetime
from pathlib import Path


def make_cell(kind: str, source: str) -> dict:
    cell = {
        "cell_type": kind,
        "metadata": {},
        "source": textwrap.dedent(source).strip().splitlines(keepends=True),
    }
    if kind == "code":
        cell.update(execution_count=None, outputs=[])
    return cell


def build_notebook() -> dict:
    cells = [
        make_cell("markdown", """
        # MouseMotionLab — entraînement GPU sur Google Colab

        1. Choisir **Exécution → Modifier le type d'exécution → GPU**.
        2. Exécuter les cellules dans l'ordre. Importer `mousemotion-colab-bundle.zip`.
        3. Régler les paramètres puis lancer l'entraînement.
        4. Télécharger l'archive finale et extraire `model` dans
           `C:/Users/RAID/Desktop/ai/artifacts/colab-model`. Dans l'application,
           choisir **Diffusion** et indiquer `artifacts/colab-model`.

        L'archive d'entrée contient le code ML et une copie cohérente des captures SQLite.
        Réexporter avec `scripts/export-colab.ps1` après de nouvelles captures.
        Les fichiers importés sont envoyés au runtime Google Colab.

        Chaque entraînement démarre depuis zéro. Le pipeline actuel ne sauvegarde pas
        de checkpoint de reprise intermédiaire : une déconnexion peut perdre le travail.
        Le notebook reste dans Drive, mais les fichiers du runtime sont temporaires.
        Télécharger le résultat dès la fin. La disponibilité et le type de GPU dépendent
        des ressources et de l'offre Colab utilisée.
        """),
        make_cell("code", """
        # Vérifier le GPU et conserver le PyTorch fourni par Colab.
        import sys
        import torch
        print("Python :", sys.version)
        print("PyTorch :", torch.__version__)
        assert sys.version_info >= (3, 11), "Python 3.11 ou supérieur requis."
        if not torch.cuda.is_available():
            raise RuntimeError("Activer un GPU dans Exécution → Modifier le type d'exécution.")
        print("GPU :", torch.cuda.get_device_name(0))
        print("VRAM :", round(torch.cuda.get_device_properties(0).total_memory / 2**30, 1), "GiB")
        """),
        make_cell("code", """
        # Importer une nouvelle archive exportée depuis Windows.
        import hashlib
        import json
        import stat
        import zipfile
        from datetime import datetime, timezone
        from pathlib import Path
        from google.colab import files

        # On peut aussi importer le ZIP par le panneau Files de Colab.
        preuploaded = Path("/content/mousemotion-colab-bundle.zip")
        uploaded = {} if preuploaded.is_file() else files.upload()
        bundles = [str(preuploaded)] if preuploaded.is_file() else [
            name for name in uploaded if name.endswith(".zip")
        ]
        if len(bundles) != 1:
            raise ValueError("Importer exactement une archive mousemotion-colab-bundle.zip.")
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        ROOT = Path("/content") / ("mousemotion-" + stamp)
        ROOT.mkdir()
        with zipfile.ZipFile(bundles[0]) as archive:
            for member in archive.infolist():
                target = (ROOT / member.filename).resolve()
                if not target.is_relative_to(ROOT.resolve()):
                    raise ValueError("Chemin invalide dans l'archive.")
                if stat.S_ISLNK(member.external_attr >> 16):
                    raise ValueError("Lien symbolique interdit dans l'archive.")
            archive.extractall(ROOT)
        manifest = json.loads((ROOT / "manifest.json").read_text())
        for relative, expected in manifest["sha256"].items():
            target = (ROOT / relative).resolve()
            if not target.is_relative_to(ROOT.resolve()):
                raise ValueError("Chemin invalide dans le manifeste.")
            actual = hashlib.sha256(target.read_bytes()).hexdigest()
            if actual != expected:
                raise ValueError(f"Fichier corrompu : {relative}")
        print("Archive vérifiée :", manifest["createdUtc"])
        print("Répertoire :", ROOT)
        del uploaded
        """),
        make_cell("code", """
        # Installer le package local sans remplacer la version CUDA de PyTorch.
        import importlib
        import subprocess
        import numpy as np
        # Torch a déjà chargé NumPy : conserver cette version évite un kernel incohérent.
        subprocess.check_call([
            sys.executable, "-m", "pip", "install", "-q",
            f"numpy=={np.__version__}", "scipy>=1.15", "scikit-learn>=1.6",
            "matplotlib>=3.10", "safetensors>=0.5", "tqdm>=4.67",
        ])
        subprocess.check_call([
            sys.executable, "-m", "pip", "install", "-q", "--no-deps", "-e", str(ROOT / "ml")
        ])
        importlib.invalidate_caches()
        # Les .pth d'une installation editable ne sont chargés qu'au démarrage du kernel.
        sys.path.insert(0, str(ROOT / "ml" / "src"))
        from mousemotion.training import TrainConfig, train_pipeline
        from mousemotion.preprocess import PreprocessConfig, prepare_database
        from mousemotion.evaluation import evaluate_artifact
        from mousemotion.contracts import TrajectoryRequest
        from mousemotion.generation import TrajectoryGenerator
        print("MouseMotionLab prêt.")
        """),
        make_cell("markdown", """
        ## Réglages

        Une epoch parcourt tout le dataset. Plus d'epochs peut aider, mais peut aussi
        sur-apprendre. Comparer les pertes de validation et le rapport d'évaluation.
        Un GPU plus rapide accélère le calcul ; il ne garantit pas une meilleure qualité.
        Garder la même seed et le même batch pour comparer des durées d'entraînement.

        Commencer par les canaux `64,128,256` et un batch de `8`.
        Un batch plus grand utilise davantage de VRAM et donne moins de mises à jour
        par epoch. Réduire le batch si une erreur CUDA « out of memory » apparaît.
        Les trois canaux doivent être des multiples de 8.
        """),
        make_cell("code", """
        # @title Paramètres d'entraînement
        EPOCHS = 300 # @param {type:"integer"}
        DURATION_EPOCHS = 150 # @param {type:"integer"}
        BATCH_SIZE = 8 # @param {type:"integer"}
        LEARNING_RATE = 0.0002 # @param {type:"number"}
        DURATION_LEARNING_RATE = 0.001 # @param {type:"number"}
        WEIGHT_DECAY = 0.0001 # @param {type:"number"}
        EMA_DECAY = 0.999 # @param {type:"number"}
        CHANNELS = "64,128,256" # @param {type:"string"}
        SEED = 42 # @param {type:"integer"}
        SAMPLING_STEPS = 50 # @param {type:"integer"}
        EVALUATION_SAMPLES = 128 # @param {type:"integer"}

        channels = tuple(int(item.strip()) for item in CHANNELS.split(","))
        assert len(channels) == 3 and all(x > 0 and x % 8 == 0 for x in channels)
        assert EPOCHS > 0 and DURATION_EPOCHS > 0 and BATCH_SIZE > 0
        assert LEARNING_RATE > 0 and DURATION_LEARNING_RATE > 0 and WEIGHT_DECAY >= 0
        assert 0 <= EMA_DECAY < 1 and 1 <= SAMPLING_STEPS <= 1000
        assert EVALUATION_SAMPLES >= 2
        CONFIG = TrainConfig(
            epochs=EPOCHS, duration_epochs=DURATION_EPOCHS, batch_size=BATCH_SIZE,
            learning_rate=LEARNING_RATE, duration_learning_rate=DURATION_LEARNING_RATE,
            weight_decay=WEIGHT_DECAY, ema_decay=EMA_DECAY, channels=channels,
            seed=SEED, sampling_steps=SAMPLING_STEPS, device="cuda", num_workers=0,
        )
        print(CONFIG)
        """),
        make_cell("code", """
        # Préparer les captures natives à 1 kHz ; splits par sessions entières.
        PREPARED = ROOT / "prepared"
        dataset = prepare_database(
            ROOT / "data" / "mousemotion.db", PREPARED,
            config=PreprocessConfig(split_seed=SEED),
        )
        print(json.dumps(dataset, indent=2, ensure_ascii=False))
        if dataset["splits"]["validation"] == 0:
            print("ATTENTION : aucune session de validation indépendante.")
        if dataset["splits"]["test"] == 0:
            print("ATTENTION : aucune session test indépendante ; ce ne sera pas un holdout.")

        # Smoke-test GPU : une epoch sur un petit réseau, puis endpoint exact.
        # Ce modèle de vérification n'est pas le modèle final à télécharger.
        from dataclasses import replace
        smoke_artifact = ROOT / "smoke-model"
        smoke_config = replace(CONFIG, epochs=1, duration_epochs=1,
                               channels=(16, 32, 64), embedding_dim=32, sampling_steps=5)
        train_pipeline(PREPARED, smoke_artifact, config=smoke_config)
        smoke_result = TrajectoryGenerator(smoke_artifact, device="cuda").generate(
            TrajectoryRequest(100, 100, 800, 1000, SEED, "diffusion")
        )
        assert sum(s.dx for s in smoke_result.samples) == 100
        assert sum(s.dy for s in smoke_result.samples) == 100
        print("Smoke-test GPU entraînement → génération OK ; endpoint exact (100, 100).")
        """),
        make_cell("code", """
        # Lancer un nouvel entraînement. Le modèle précédent n'est pas écrasé.
        torch.cuda.empty_cache()
        run_stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        RUN = ROOT / "runs" / run_stamp
        ARTIFACT = RUN / "model"
        metadata = train_pipeline(PREPARED, ARTIFACT, config=CONFIG)
        print(json.dumps(metadata, indent=2, ensure_ascii=False))
        # Télécharger immédiatement les poids, avant l'évaluation plus longue.
        import shutil
        MODEL_ZIP = Path(shutil.make_archive(str(RUN / "mousemotion-model"), "zip", RUN, "model"))
        print("Poids terminés :", MODEL_ZIP)
        files.download(str(MODEL_ZIP))
        """),
        make_cell("code", """
        # Courbes : une baisse train seule ne prouve pas une meilleure généralisation.
        import matplotlib.pyplot as plt
        history = json.loads((ARTIFACT / "history.json").read_text())
        fig, axes = plt.subplots(1, 2, figsize=(13, 4))
        rows = history["diffusion"]
        axes[0].plot([r["epoch"] for r in rows], [r["trainLoss"] for r in rows], label="train")
        validation = [r for r in rows if r["validationLoss"] is not None]
        if validation:
            axes[0].plot([r["epoch"] for r in validation],
                         [r["validationLoss"] for r in validation], label="validation")
        axes[0].set(title="Diffusion", xlabel="Epoch", ylabel="Loss")
        axes[0].legend()
        axes[1].plot(range(1, len(history["duration"]) + 1), history["duration"])
        axes[1].set(title="Modèle de durée", xlabel="Epoch", ylabel="NLL")
        plt.show()
        """),
        make_cell("code", """
        # Comparer diffusion et minimum-jerk ; vérifier le split effectivement évalué.
        EVALUATION = RUN / "evaluation"
        report = evaluate_artifact(
            PREPARED, ARTIFACT, EVALUATION,
            sample_count=EVALUATION_SAMPLES, seed=SEED, device="cuda",
        )
        print(json.dumps(report, indent=2, ensure_ascii=False))
        if report["split"] != "test":
            print("ATTENTION : évaluation sur", report["split"], "et non sur un test indépendant.")
        """),
        make_cell("code", """
        # @title Aperçu d'une trajectoire générée (counts)
        DX = 1000 # @param {type:"integer"}
        DY = 1000 # @param {type:"integer"}
        DPI = 800 # @param {type:"integer"}
        GENERATION_SEED = 42 # @param {type:"integer"}
        import numpy as np
        generator = TrajectoryGenerator(ARTIFACT, device="cuda")
        fig, ax = plt.subplots(figsize=(7, 7))
        for mode in ("baseline", "diffusion"):
            result = generator.generate(TrajectoryRequest(DX, DY, DPI, 1000, GENERATION_SEED, mode))
            deltas = np.array([[s.dx, s.dy] for s in result.samples], dtype=np.int64)
            assert tuple(deltas.sum(axis=0)) == (DX, DY)
            positions = np.vstack((np.zeros((1, 2)), np.cumsum(deltas, axis=0)))
            ax.plot(positions[:, 0], positions[:, 1], label=mode)
            print(mode, "couverture :", result.coverage_score, "avertissements :", result.warnings)
            (RUN / (mode + "-example.json")).write_text(result.to_json(), encoding="utf-8")
        ax.scatter([0, DX], [0, DY])
        ax.set(xlabel="dx cumulé (counts)", ylabel="dy cumulé (counts)")
        ax.set_aspect("equal", adjustable="datalim")
        ax.legend()
        plt.show()
        """),
        make_cell("code", """
        # Télécharger poids, rapport et exemples pour réutiliser le modèle sous Windows.
        import shutil
        export_root = ROOT / "exports"
        export_root.mkdir(exist_ok=True)
        final_zip = shutil.make_archive(str(export_root / ("mousemotion-" + run_stamp)), "zip", RUN)
        print("Extraire le dossier model vers artifacts/colab-model et le choisir dans l'application.")
        files.download(final_zip)
        """),
    ]
    return {
        "cells": cells,
        "metadata": {
            "accelerator": "GPU",
            "colab": {"name": "MouseMotionLab-GPU.ipynb", "provenance": []},
            "kernelspec": {"display_name": "Python 3", "language": "python", "name": "python3"},
            "language_info": {"name": "python"},
        },
        "nbformat": 4,
        "nbformat_minor": 0,
    }


def export_bundle(repository: Path, database: Path, output: Path) -> Path:
    """SQLite backup includes committed WAL data even while the collector is running."""
    output.mkdir(parents=True, exist_ok=True)
    destination = output / "mousemotion-colab-bundle.zip"
    with tempfile.TemporaryDirectory(prefix="mousemotion-colab-") as temporary:
        snapshot = Path(temporary) / "mousemotion.db"
        with (
            closing(sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)) as source,
            closing(sqlite3.connect(snapshot)) as target,
        ):
            source.backup(target)
            if target.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                raise RuntimeError("SQLite snapshot integrity check failed.")
        included = {
            "data/mousemotion.db": snapshot,
            "ml/pyproject.toml": repository / "ml" / "pyproject.toml",
            "ml/README.md": repository / "ml" / "README.md",
        }
        for path in sorted((repository / "ml" / "src" / "mousemotion").rglob("*.py")):
            included[path.relative_to(repository).as_posix()] = path
        manifest = {
            "schemaVersion": 1,
            "createdUtc": datetime.now(UTC).isoformat(),
            "sha256": {
                name: hashlib.sha256(path.read_bytes()).hexdigest()
                for name, path in included.items()
            },
        }
        with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, path in included.items():
                archive.write(path, name)
            archive.writestr("manifest.json", json.dumps(manifest, indent=2))
    return destination


def main() -> None:
    repository = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, default=repository / "data" / "mousemotion.db")
    parser.add_argument("--output", type=Path, default=repository / "artifacts" / "colab-export")
    arguments = parser.parse_args()
    if not arguments.database.is_file():
        parser.error(f"Database not found: {arguments.database}")
    notebook = build_notebook()
    notebook_directory = repository / "notebooks"
    notebook_directory.mkdir(exist_ok=True)
    notebook_path = notebook_directory / "MouseMotionLab-GPU.ipynb"
    notebook_path.write_text(json.dumps(notebook, indent=2, ensure_ascii=False) + "\n",
                             encoding="utf-8")
    bundle = export_bundle(repository, arguments.database, arguments.output)
    print(json.dumps({"notebook": str(notebook_path), "bundle": str(bundle)}, indent=2))


if __name__ == "__main__":
    main()
