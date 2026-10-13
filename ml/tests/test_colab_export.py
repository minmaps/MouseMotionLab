from __future__ import annotations

import ast
import hashlib
import importlib.util
import json
import sqlite3
import zipfile
from contextlib import closing
from pathlib import Path


def load_exporter():
    path = Path(__file__).resolve().parents[2] / "scripts" / "export_colab.py"
    spec = importlib.util.spec_from_file_location("mousemotion_colab_export", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_notebook_code_is_valid_on_supported_python():
    notebook = load_exporter().build_notebook()
    assert notebook["nbformat"] == 4
    assert notebook["metadata"]["accelerator"] == "GPU"
    for cell in notebook["cells"]:
        if cell["cell_type"] == "code":
            ast.parse("".join(cell["source"]), feature_version=(3, 11))
            assert cell["execution_count"] is None
            assert cell["outputs"] == []


def test_export_includes_committed_wal_and_verifiable_sources(tmp_path):
    repository = tmp_path / "repo"
    package = repository / "ml" / "src" / "mousemotion"
    package.mkdir(parents=True)
    (package / "__init__.py").write_text("", encoding="utf-8")
    (repository / "ml" / "pyproject.toml").write_text("[project]\n", encoding="utf-8")
    (repository / "ml" / "README.md").write_text("ML\n", encoding="utf-8")
    database = repository / "capture.db"
    with closing(sqlite3.connect(database)) as collector:
        collector.execute("PRAGMA journal_mode=WAL")
        collector.execute("CREATE TABLE events (dx INTEGER)")
        collector.commit()
        collector.execute("INSERT INTO events VALUES (123)")
        collector.commit()
        assert Path(str(database) + "-wal").stat().st_size > 0
        bundle = load_exporter().export_bundle(repository, database, tmp_path / "export")
        with zipfile.ZipFile(bundle) as archive:
            manifest = json.loads(archive.read("manifest.json"))
            for name, expected in manifest["sha256"].items():
                assert hashlib.sha256(archive.read(name)).hexdigest() == expected
            archive.extractall(tmp_path / "unpacked")
        with closing(sqlite3.connect(tmp_path / "unpacked" / "data" / "mousemotion.db")) as copy:
            assert copy.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
            assert copy.execute("SELECT dx FROM events").fetchall() == [(123,)]
        assert collector.execute("SELECT dx FROM events").fetchall() == [(123,)]
