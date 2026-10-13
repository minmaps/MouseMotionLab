from __future__ import annotations

import numpy as np
import torch
from torch import Tensor
from torch.utils.data import Dataset

from mousemotion.preprocess import PreparedDataset


class MouseTrajectoryDataset(Dataset[dict[str, Tensor]]):
    def __init__(
        self,
        prepared: PreparedDataset,
        indices: np.ndarray,
        *,
        condition_mean: np.ndarray,
        condition_scale: np.ndarray,
        residual_scale: np.ndarray,
    ) -> None:
        self.prepared = prepared
        self.indices = np.asarray(indices, dtype=np.int64)
        self.condition_mean = np.asarray(condition_mean, dtype=np.float32)
        self.condition_scale = np.asarray(condition_scale, dtype=np.float32)
        self.residual_scale = np.asarray(residual_scale, dtype=np.float32)

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, index: int) -> dict[str, Tensor]:
        source_index = int(self.indices[index])
        residual = np.asarray(self.prepared.residuals[source_index], dtype=np.float32)
        residual = residual / self.residual_scale[:, None]
        condition = np.asarray(self.prepared.conditions[source_index], dtype=np.float32)
        condition = (condition - self.condition_mean) / self.condition_scale
        mask = np.asarray(self.prepared.masks[source_index], dtype=np.float32)
        return {
            "residual": torch.from_numpy(residual.copy()),
            "condition": torch.from_numpy(condition.copy()),
            "mask": torch.from_numpy(mask.copy()),
        }


class DurationDataset(Dataset[tuple[Tensor, Tensor]]):
    def __init__(
        self,
        prepared: PreparedDataset,
        indices: np.ndarray,
        *,
        condition_mean: np.ndarray,
        condition_scale: np.ndarray,
    ) -> None:
        self.prepared = prepared
        self.indices = np.asarray(indices, dtype=np.int64)
        self.condition_mean = np.asarray(condition_mean, dtype=np.float32)
        self.condition_scale = np.asarray(condition_scale, dtype=np.float32)

    def __len__(self) -> int:
        return len(self.indices)

    def __getitem__(self, index: int) -> tuple[Tensor, Tensor]:
        source_index = int(self.indices[index])
        condition = np.asarray(self.prepared.duration_conditions[source_index], dtype=np.float32)
        condition = (condition - self.condition_mean) / self.condition_scale
        log_duration = np.log(float(self.prepared.durations_ms[source_index]))
        return torch.from_numpy(condition.copy()), torch.tensor(log_duration, dtype=torch.float32)


def compute_training_statistics(
    prepared: PreparedDataset,
    train_indices: np.ndarray,
) -> dict[str, np.ndarray]:
    if len(train_indices) == 0:
        raise ValueError("Training split is empty.")
    conditions = np.asarray(prepared.conditions[train_indices], dtype=np.float64)
    duration_conditions = np.asarray(prepared.duration_conditions[train_indices], dtype=np.float64)
    condition_mean = conditions.mean(axis=0)
    condition_scale = np.maximum(conditions.std(axis=0), 1e-6)
    duration_condition_mean = duration_conditions.mean(axis=0)
    duration_condition_scale = np.maximum(duration_conditions.std(axis=0), 1e-6)

    residual_sums = np.zeros(2, dtype=np.float64)
    residual_squares = np.zeros(2, dtype=np.float64)
    active_count = 0
    for source_index in train_indices:
        mask = np.asarray(prepared.masks[int(source_index)], dtype=np.bool_)
        active = np.asarray(prepared.residuals[int(source_index)], dtype=np.float64)[:, mask]
        residual_sums += active.sum(axis=1)
        residual_squares += np.square(active).sum(axis=1)
        active_count += active.shape[1]
    mean = residual_sums / max(active_count, 1)
    variance = residual_squares / max(active_count, 1) - np.square(mean)
    residual_scale = np.maximum(np.sqrt(np.maximum(variance, 0.0)), 1e-5)
    return {
        "condition_mean": condition_mean.astype(np.float32),
        "condition_scale": condition_scale.astype(np.float32),
        "duration_condition_mean": duration_condition_mean.astype(np.float32),
        "duration_condition_scale": duration_condition_scale.astype(np.float32),
        "residual_scale": residual_scale.astype(np.float32),
    }
