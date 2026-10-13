from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np
from numpy.typing import NDArray
from sklearn.neighbors import NearestNeighbors

FloatArray = NDArray[np.float64]


def target_feature(distance_mm: float, angle_radians: float) -> FloatArray:
    return np.asarray(
        [np.log1p(max(distance_mm, 0.0)), np.sin(angle_radians), np.cos(angle_radians)],
        dtype=np.float64,
    )


@dataclass(frozen=True, slots=True)
class CoverageModel:
    mean: FloatArray
    scale: FloatArray
    references: FloatArray
    threshold: float
    neighbors: int

    @classmethod
    def fit(
        cls,
        features: FloatArray,
        *,
        neighbors: int = 5,
        percentile: float = 99.0,
        max_references: int = 10_000,
        seed: int = 42,
    ) -> CoverageModel:
        values = np.asarray(features, dtype=np.float64)
        if values.ndim != 2 or values.shape[1] != 3 or len(values) < 1:
            raise ValueError("Coverage fitting needs at least one three-dimensional feature.")
        mean = values.mean(axis=0)
        scale = values.std(axis=0)
        scale = np.where(scale < 1e-6, 1.0, scale)
        standardized = (values - mean) / scale
        rng = np.random.default_rng(seed)
        if len(standardized) > max_references:
            indices = rng.choice(len(standardized), size=max_references, replace=False)
            references = standardized[indices]
        else:
            references = standardized
        if len(references) == 1:
            return cls(mean, scale, references.astype(np.float64), 1.0, 1)
        k = min(max(1, neighbors), len(references) - 1)
        estimator = NearestNeighbors(n_neighbors=k + 1).fit(references)
        distances, _ = estimator.kneighbors(references)
        self_distances = distances[:, -1]
        threshold = max(float(np.percentile(self_distances, percentile)), 1e-6)
        return cls(mean, scale, references.astype(np.float64), threshold, k)

    def score(self, feature: FloatArray) -> tuple[float, bool, float]:
        standardized = (np.asarray(feature, dtype=np.float64) - self.mean) / self.scale
        distances = np.linalg.norm(self.references - standardized[None, :], axis=1)
        k = min(self.neighbors, len(distances))
        kth_distance = float(np.partition(distances, k - 1)[k - 1])
        ratio = kth_distance / self.threshold
        score = float(np.exp(-max(0.0, ratio - 0.25)))
        return float(np.clip(score, 0.0, 1.0)), kth_distance > self.threshold, ratio

    def save(self, path: str | Path) -> None:
        np.savez_compressed(
            path,
            mean=self.mean,
            scale=self.scale,
            references=self.references,
            threshold=np.asarray(self.threshold, dtype=np.float64),
            neighbors=np.asarray(self.neighbors, dtype=np.int64),
        )

    @classmethod
    def load(cls, path: str | Path) -> CoverageModel:
        with np.load(path) as data:
            return cls(
                mean=data["mean"].astype(np.float64),
                scale=data["scale"].astype(np.float64),
                references=data["references"].astype(np.float64),
                threshold=float(data["threshold"]),
                neighbors=int(data["neighbors"]),
            )
