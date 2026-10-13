from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import matplotlib
import numpy as np
from numpy.typing import NDArray
from scipy.stats import wasserstein_distance
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import roc_auc_score
from sklearn.model_selection import train_test_split

from mousemotion.baseline import minimum_jerk_deltas
from mousemotion.contracts import TrajectoryRequest, TrajectoryResult
from mousemotion.generation import TrajectoryGenerator
from mousemotion.preprocess import PreparedDataset

matplotlib.use("Agg")
from matplotlib import pyplot as plt  # noqa: E402

FEATURE_NAMES = (
    "duration_ms",
    "path_ratio",
    "peak_speed_phase",
    "acceleration_energy",
    "jerk_energy",
    "curvature",
    "overshoot",
)


def trajectory_features(
    deltas_mm: NDArray[np.float64],
    dt_us: NDArray[np.float64],
) -> NDArray[np.float64]:
    if len(deltas_mm) < 2:
        raise ValueError("At least two deltas are required for evaluation.")
    seconds = np.maximum(dt_us, 1.0) / 1_000_000.0
    duration_ms = float(dt_us.sum() / 1_000.0)
    step_distance = np.linalg.norm(deltas_mm, axis=1)
    endpoint = deltas_mm.sum(axis=0)
    target_distance = max(float(np.linalg.norm(endpoint)), 1e-9)
    path_ratio = float(step_distance.sum() / target_distance)
    speed = step_distance / seconds
    cumulative_time = np.cumsum(dt_us)
    peak_speed_phase = float(cumulative_time[int(np.argmax(speed))] / cumulative_time[-1])
    acceleration = np.diff(speed) / np.maximum((seconds[1:] + seconds[:-1]) * 0.5, 1e-9)
    acceleration_energy = float(np.mean(np.square(acceleration))) if len(acceleration) else 0.0
    jerk = np.diff(acceleration) / np.maximum(seconds[2:], 1e-9)
    jerk_energy = float(np.mean(np.square(jerk))) if len(jerk) else 0.0
    moving = deltas_mm[step_distance > 1e-9]
    if len(moving) >= 2:
        headings = np.unwrap(np.arctan2(moving[:, 1], moving[:, 0]))
        curvature = float(np.mean(np.abs(np.diff(headings))))
    else:
        curvature = 0.0
    target_direction = endpoint / target_distance
    longitudinal = np.cumsum(deltas_mm @ target_direction)
    overshoot = float(max(0.0, longitudinal.max() / target_distance - 1.0))
    return np.asarray(
        [
            duration_ms,
            path_ratio,
            peak_speed_phase,
            np.log1p(acceleration_energy),
            np.log1p(jerk_energy),
            curvature,
            overshoot,
        ],
        dtype=np.float64,
    )


def result_features(result: TrajectoryResult, dpi: int) -> NDArray[np.float64]:
    deltas = np.asarray([(item.dx, item.dy) for item in result.samples], dtype=np.float64)
    deltas *= 25.4 / dpi
    durations = np.asarray([item.dt_us for item in result.samples], dtype=np.float64)
    return trajectory_features(deltas, durations)


def _classifier_auc(real: NDArray[np.float64], generated: NDArray[np.float64], seed: int) -> float:
    if len(real) < 6 or len(generated) < 6:
        return float("nan")
    features = np.vstack((real, generated))
    labels = np.concatenate((np.ones(len(real)), np.zeros(len(generated))))
    train_x, test_x, train_y, test_y = train_test_split(
        features, labels, test_size=0.4, random_state=seed, stratify=labels
    )
    classifier = LogisticRegression(max_iter=2_000, random_state=seed)
    classifier.fit(train_x, train_y)
    return float(roc_auc_score(test_y, classifier.predict_proba(test_x)[:, 1]))


def evaluate_artifact(
    prepared_directory: str | Path,
    artifact_directory: str | Path,
    output_directory: str | Path,
    *,
    sample_count: int = 128,
    seed: int = 42,
    device: str = "auto",
) -> dict[str, Any]:
    prepared = PreparedDataset.load(prepared_directory)
    indices = prepared.indices("test")
    split_used = "test"
    if len(indices) == 0:
        indices = prepared.indices("validation")
        split_used = "validation"
    if len(indices) == 0:
        indices = prepared.indices("train")
        split_used = "train"
    rng = np.random.default_rng(seed)
    if len(indices) > sample_count:
        indices = rng.choice(indices, size=sample_count, replace=False)

    generator = TrajectoryGenerator(artifact_directory, device=device)
    real_features: list[NDArray[np.float64]] = []
    baseline_features: list[NDArray[np.float64]] = []
    diffusion_features: list[NDArray[np.float64]] = []
    for ordinal, source_index_raw in enumerate(indices):
        source_index = int(source_index_raw)
        mask = np.asarray(prepared.masks[source_index], dtype=np.bool_)
        length = int(mask.sum())
        distance = float(prepared.distances_mm[source_index])
        residual = np.asarray(prepared.residuals[source_index, :, :length], dtype=np.float64).T
        real_deltas = minimum_jerk_deltas(np.asarray([distance, 0.0]), length)
        real_deltas += residual * distance
        duration = float(prepared.durations_ms[source_index])
        real_dt = np.full(length, duration * 1_000.0 / length, dtype=np.float64)
        real_features.append(trajectory_features(real_deltas, real_dt))

        dx, dy = map(int, prepared.target_counts[source_index])
        dpi = int(prepared.dpis[source_index])
        baseline_request = TrajectoryRequest(dx, dy, dpi, 1_000, seed + ordinal, "baseline")
        diffusion_request = TrajectoryRequest(dx, dy, dpi, 1_000, seed + ordinal, "diffusion")
        baseline_features.append(result_features(generator.generate(baseline_request), dpi))
        diffusion_features.append(result_features(generator.generate(diffusion_request), dpi))

    real = np.stack(real_features)
    baseline = np.stack(baseline_features)
    diffusion = np.stack(diffusion_features)
    baseline_distances = {
        name: float(wasserstein_distance(real[:, index], baseline[:, index]))
        for index, name in enumerate(FEATURE_NAMES)
    }
    diffusion_distances = {
        name: float(wasserstein_distance(real[:, index], diffusion[:, index]))
        for index, name in enumerate(FEATURE_NAMES)
    }
    wins = sum(diffusion_distances[name] < baseline_distances[name] for name in FEATURE_NAMES)
    baseline_auc = _classifier_auc(real, baseline, seed)
    diffusion_auc = _classifier_auc(real, diffusion, seed)
    baseline_auc_json = baseline_auc if np.isfinite(baseline_auc) else None
    diffusion_auc_json = diffusion_auc if np.isfinite(diffusion_auc) else None
    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=True)
    for index, name in enumerate(FEATURE_NAMES):
        figure, axis = plt.subplots(figsize=(7, 4))
        axis.hist(real[:, index], bins=24, density=True, alpha=0.45, label="real")
        axis.hist(baseline[:, index], bins=24, density=True, alpha=0.35, label="baseline")
        axis.hist(diffusion[:, index], bins=24, density=True, alpha=0.35, label="diffusion")
        axis.set_title(name)
        axis.legend()
        figure.tight_layout()
        figure.savefig(output / f"{name}.png", dpi=140)
        plt.close(figure)

    report: dict[str, Any] = {
        "schemaVersion": 1,
        "split": split_used,
        "samples": int(len(indices)),
        "features": list(FEATURE_NAMES),
        "wasserstein": {
            "baseline": baseline_distances,
            "diffusion": diffusion_distances,
        },
        "diffusionWins": wins,
        "majorityImproved": wins > len(FEATURE_NAMES) // 2,
        "classifierAuc": {
            "baseline": baseline_auc_json,
            "diffusion": diffusion_auc_json,
            "diffusionCloserToChance": abs(diffusion_auc - 0.5) < abs(baseline_auc - 0.5)
            if np.isfinite(diffusion_auc) and np.isfinite(baseline_auc)
            else None,
        },
    }
    (output / "report.json").write_text(
        json.dumps(report, indent=2, sort_keys=True), encoding="utf-8"
    )
    return report
