from __future__ import annotations

import copy
import json
import random
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import numpy as np
import torch
from torch import nn
from torch.optim import AdamW
from torch.utils.data import DataLoader
from tqdm import tqdm

from mousemotion.artifacts import save_artifact
from mousemotion.baseline import minimum_jerk_deltas
from mousemotion.coverage import CoverageModel
from mousemotion.dataset import (
    DurationDataset,
    MouseTrajectoryDataset,
    compute_training_statistics,
)
from mousemotion.generation import resolve_torch_device
from mousemotion.models.diffusion import GaussianDiffusion1D
from mousemotion.models.duration import DurationMixtureNetwork
from mousemotion.models.unet1d import ConditionalUNet1D
from mousemotion.preprocess import PreparedDataset


@dataclass(frozen=True, slots=True)
class TrainConfig:
    seed: int = 42
    epochs: int = 50
    duration_epochs: int = 100
    batch_size: int = 32
    learning_rate: float = 2e-4
    duration_learning_rate: float = 1e-3
    weight_decay: float = 1e-4
    diffusion_steps: int = 1_000
    sampling_steps: int = 50
    channels: tuple[int, int, int] = (64, 128, 256)
    embedding_dim: int = 256
    duration_hidden_dim: int = 64
    duration_components: int = 5
    ema_decay: float = 0.999
    num_workers: int = 0
    device: str = "auto"


class ExponentialMovingAverage:
    def __init__(self, model: nn.Module, decay: float) -> None:
        self.decay = decay
        self.updates = 0
        self.shadow = {
            key: value.detach().clone()
            for key, value in model.state_dict().items()
            if value.dtype.is_floating_point
        }

    @torch.no_grad()
    def update(self, model: nn.Module) -> None:
        self.updates += 1
        # A fixed 0.999 decay leaves small-dataset runs dominated by the random
        # initialization (about 47% after 750 updates). Warm the decay up so the
        # shadow follows the learned weights early, then asymptotically reaches
        # the configured long-run decay.
        warmup_decay = (1.0 + self.updates) / (10.0 + self.updates)
        effective_decay = min(self.decay, warmup_decay)
        for key, value in model.state_dict().items():
            if key in self.shadow:
                self.shadow[key].lerp_(value.detach(), 1.0 - effective_decay)

    def copy_to(self, model: nn.Module) -> None:
        state = model.state_dict()
        for key, value in self.shadow.items():
            state[key].copy_(value)


def seed_everything(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)


def _training_geometry(
    prepared: PreparedDataset,
    train_indices: np.ndarray,
) -> dict[str, float]:
    path_ratios: list[float] = []
    lateral_fractions: list[float] = []
    backtrack_fractions: list[float] = []
    overshoot_fractions: list[float] = []
    for source_index_raw in train_indices:
        source_index = int(source_index_raw)
        length = int(np.asarray(prepared.masks[source_index], dtype=np.bool_).sum())
        distance = float(prepared.distances_mm[source_index])
        residual = np.asarray(
            prepared.residuals[source_index, :, :length], dtype=np.float64
        ).T
        trajectory = minimum_jerk_deltas(np.asarray([distance, 0.0]), length)
        trajectory += residual * distance
        path_ratios.append(float(np.linalg.norm(trajectory, axis=1).sum() / distance))
        lateral = np.cumsum(trajectory[:, 1])
        lateral_fractions.append(float(np.max(np.abs(lateral)) / distance))
        longitudinal = np.cumsum(trajectory[:, 0]) / distance
        backtrack_fractions.append(max(0.0, -float(longitudinal.min())))
        overshoot_fractions.append(max(0.0, float(longitudinal.max()) - 1.0))
    return {
        "pathRatioMedian": float(np.percentile(path_ratios, 50)),
        "pathRatioP95": float(np.percentile(path_ratios, 95)),
        "lateralFractionMedian": float(np.percentile(lateral_fractions, 50)),
        "lateralFractionP95": float(np.percentile(lateral_fractions, 95)),
        "backtrackFractionP95": float(np.percentile(backtrack_fractions, 95)),
        "overshootFractionP95": float(np.percentile(overshoot_fractions, 95)),
    }


def _validation_diffusion_loss(
    diffusion: GaussianDiffusion1D,
    loader: DataLoader[dict[str, torch.Tensor]],
    device: torch.device,
) -> float | None:
    if len(loader.dataset) == 0:  # type: ignore[arg-type]
        return None
    diffusion.eval()
    losses: list[float] = []
    with torch.no_grad():
        for batch in loader:
            loss = diffusion.training_loss(
                batch["residual"].to(device),
                batch["condition"].to(device),
                batch["mask"].to(device),
            )
            losses.append(float(loss.item()))
    diffusion.train()
    return float(np.mean(losses)) if losses else None


def train_pipeline(
    prepared_directory: str | Path,
    artifact_directory: str | Path,
    *,
    config: TrainConfig | None = None,
) -> dict[str, Any]:
    selected = config or TrainConfig()
    seed_everything(selected.seed)
    device = resolve_torch_device(selected.device)
    prepared = PreparedDataset.load(prepared_directory)
    train_indices = prepared.indices("train")
    validation_indices = prepared.indices("validation")
    statistics = compute_training_statistics(prepared, train_indices)

    duration_train = DurationDataset(
        prepared,
        train_indices,
        condition_mean=statistics["duration_condition_mean"],
        condition_scale=statistics["duration_condition_scale"],
    )
    duration_loader = DataLoader(
        duration_train,
        batch_size=min(selected.batch_size, max(1, len(duration_train))),
        shuffle=True,
        num_workers=selected.num_workers,
        generator=torch.Generator().manual_seed(selected.seed),
    )
    duration_model = DurationMixtureNetwork(
        hidden_dim=selected.duration_hidden_dim,
        components=selected.duration_components,
    ).to(device)
    duration_optimizer = AdamW(
        duration_model.parameters(),
        lr=selected.duration_learning_rate,
        weight_decay=selected.weight_decay,
    )
    duration_history: list[float] = []
    duration_model.train()
    for _ in tqdm(range(selected.duration_epochs), desc="duration", unit="epoch"):
        losses: list[float] = []
        for condition, log_duration in duration_loader:
            duration_optimizer.zero_grad(set_to_none=True)
            loss = duration_model.negative_log_likelihood(
                condition.to(device), log_duration.to(device)
            )
            loss.backward()
            torch.nn.utils.clip_grad_norm_(duration_model.parameters(), 5.0)
            duration_optimizer.step()
            losses.append(float(loss.item()))
        duration_history.append(float(np.mean(losses)))

    trajectory_train = MouseTrajectoryDataset(
        prepared,
        train_indices,
        condition_mean=statistics["condition_mean"],
        condition_scale=statistics["condition_scale"],
        residual_scale=statistics["residual_scale"],
    )
    trajectory_validation = MouseTrajectoryDataset(
        prepared,
        validation_indices,
        condition_mean=statistics["condition_mean"],
        condition_scale=statistics["condition_scale"],
        residual_scale=statistics["residual_scale"],
    )
    train_loader = DataLoader(
        trajectory_train,
        batch_size=min(selected.batch_size, max(1, len(trajectory_train))),
        shuffle=True,
        num_workers=selected.num_workers,
        generator=torch.Generator().manual_seed(selected.seed + 1),
    )
    validation_loader = DataLoader(
        trajectory_validation,
        batch_size=min(selected.batch_size, max(1, len(trajectory_validation))),
        shuffle=False,
        num_workers=selected.num_workers,
    )
    unet = ConditionalUNet1D(
        channels=selected.channels,
        embedding_dim=selected.embedding_dim,
    ).to(device)
    diffusion = GaussianDiffusion1D(unet, steps=selected.diffusion_steps).to(device)
    optimizer = AdamW(
        unet.parameters(), lr=selected.learning_rate, weight_decay=selected.weight_decay
    )
    ema = ExponentialMovingAverage(unet, selected.ema_decay)
    history: list[dict[str, float | None]] = []
    diffusion.train()
    for epoch in tqdm(range(selected.epochs), desc="diffusion", unit="epoch"):
        losses: list[float] = []
        for batch in train_loader:
            optimizer.zero_grad(set_to_none=True)
            loss = diffusion.training_loss(
                batch["residual"].to(device),
                batch["condition"].to(device),
                batch["mask"].to(device),
            )
            loss.backward()
            torch.nn.utils.clip_grad_norm_(unet.parameters(), 1.0)
            optimizer.step()
            ema.update(unet)
            losses.append(float(loss.item()))
        validation_loss = _validation_diffusion_loss(diffusion, validation_loader, device)
        history.append(
            {
                "epoch": float(epoch + 1),
                "trainLoss": float(np.mean(losses)),
                "validationLoss": validation_loss,
            }
        )

    ema_unet = copy.deepcopy(unet).cpu()
    ema.copy_to(ema_unet)
    duration_model = duration_model.cpu().eval()
    train_coverage_features = np.asarray(
        prepared.duration_conditions[train_indices], dtype=np.float64
    )
    coverage = CoverageModel.fit(train_coverage_features, seed=selected.seed)
    train_distances = np.asarray(prepared.distances_mm[train_indices], dtype=np.float64)
    train_durations = np.asarray(prepared.durations_ms[train_indices], dtype=np.float64)
    training_geometry = _training_geometry(prepared, train_indices)
    model_version = datetime.now(UTC).strftime("mousemotion-diffusion-v1-%Y%m%dT%H%M%SZ")
    metadata: dict[str, Any] = {
        "schemaVersion": 1,
        "modelVersion": model_version,
        "createdUtc": datetime.now(UTC).isoformat(),
        "preparedDataset": str(Path(prepared_directory).resolve()),
        "datasetSha256": prepared.metadata["databaseSha256"],
        "trainingRows": int(len(train_indices)),
        "validationRows": int(len(validation_indices)),
        "seed": selected.seed,
        "model": {
            "channels": list(selected.channels),
            "embeddingDim": selected.embedding_dim,
            "diffusionSteps": selected.diffusion_steps,
            "samplingSteps": selected.sampling_steps,
            "maxLength": int(prepared.metadata["config"]["maxLength"]),
            "canonicalHz": int(prepared.metadata["config"]["canonicalHz"]),
            "durationHiddenDim": selected.duration_hidden_dim,
            "durationComponents": selected.duration_components,
        },
        "trainConfig": asdict(selected),
        "finalLoss": history[-1] if history else None,
        "finalDurationLoss": duration_history[-1] if duration_history else None,
        "coverage": {
            "method": "standardized_knn",
            "neighbors": coverage.neighbors,
            "threshold": coverage.threshold,
            "referenceCount": int(len(coverage.references)),
        },
        "trainingDomain": {
            "distanceMm": {
                "min": float(train_distances.min()),
                "max": float(train_distances.max()),
            },
            "durationMs": {
                "min": float(train_durations.min()),
                "max": float(train_durations.max()),
            },
        },
        "trainingGeometry": training_geometry,
        "warnings": prepared.metadata.get("warnings", []),
    }
    output = save_artifact(
        artifact_directory,
        unet=ema_unet,
        duration_model=duration_model,
        coverage=coverage,
        statistics=statistics,
        metadata=metadata,
    )
    (output / "history.json").write_text(
        json.dumps({"diffusion": history, "duration": duration_history}, indent=2, sort_keys=True),
        encoding="utf-8",
    )
    return metadata
