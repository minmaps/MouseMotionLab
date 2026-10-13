from __future__ import annotations

import json
import math
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
from numpy.typing import NDArray

from mousemotion.baseline import (
    counts_to_millimeters,
    minimum_jerk_deltas,
    project_zero_sum_numpy,
)
from mousemotion.database import DatabaseTrial, iter_trials, sha256_file, sqlite_snapshot

FloatArray = NDArray[np.float32]
BoolArray = NDArray[np.bool_]


@dataclass(frozen=True, slots=True)
class PreprocessConfig:
    canonical_hz: int = 1_000
    max_length: int = 1_024
    min_duration_ms: float = 20.0
    max_duration_ms: float = 1_000.0
    min_distance_mm: float = 0.5
    split_seed: int = 42


@dataclass(frozen=True, slots=True)
class ProcessedTrial:
    trial_id: str
    session_id: str
    residual: FloatArray
    mask: BoolArray
    condition: FloatArray
    duration_condition: FloatArray
    duration_ms: float
    distance_mm: float
    angle_radians: float
    target_counts: NDArray[np.int32]
    dpi: int
    armed_to_motion_ms: float


@dataclass(slots=True)
class PreparedDataset:
    root: Path
    residuals: NDArray[np.float32]
    masks: NDArray[np.bool_]
    conditions: NDArray[np.float32]
    duration_conditions: NDArray[np.float32]
    durations_ms: NDArray[np.float32]
    distances_mm: NDArray[np.float32]
    angles: NDArray[np.float32]
    target_counts: NDArray[np.int32]
    dpis: NDArray[np.int32]
    armed_to_motion_ms: NDArray[np.float32]
    splits: NDArray[np.int8]
    trial_ids: NDArray[np.str_]
    session_ids: NDArray[np.str_]
    metadata: dict[str, Any]

    @classmethod
    def load(cls, root: str | Path, *, mmap_mode: str | None = "r") -> PreparedDataset:
        directory = Path(root)
        metadata = json.loads((directory / "metadata.json").read_text(encoding="utf-8"))
        arrays: dict[str, NDArray[Any]] = {}
        for name in (
            "residuals",
            "masks",
            "conditions",
            "duration_conditions",
            "durations_ms",
            "distances_mm",
            "angles",
            "target_counts",
            "dpis",
            "armed_to_motion_ms",
            "splits",
            "trial_ids",
            "session_ids",
        ):
            arrays[name] = np.load(
                directory / f"{name}.npy", mmap_mode=mmap_mode, allow_pickle=False
            )
        return cls(root=directory, metadata=metadata, **arrays)  # type: ignore[arg-type]

    def indices(self, split: str) -> NDArray[np.int64]:
        split_code = {"train": 0, "validation": 1, "test": 2}[split]
        return np.flatnonzero(self.splits == split_code).astype(np.int64)


def process_trial(
    trial: DatabaseTrial,
    config: PreprocessConfig,
) -> tuple[ProcessedTrial | None, str | None]:
    if trial.qpc_frequency <= 0:
        return None, "invalid_qpc_frequency"
    if not trial.events:
        return None, "no_events"
    if any(
        current.qpc_ticks < previous.qpc_ticks
        for previous, current in zip(trial.events, trial.events[1:], strict=False)
    ):
        return None, "non_monotonic_timestamp"

    duration_ms = (trial.terminal_qpc - trial.first_motion_qpc) * 1_000.0 / trial.qpc_frequency
    if duration_ms < config.min_duration_ms:
        return None, "duration_too_short"
    if duration_ms > config.max_duration_ms:
        return None, "duration_too_long"

    actual_target = np.asarray(
        [sum(item.dx for item in trial.events), sum(item.dy for item in trial.events)],
        dtype=np.float64,
    )
    if tuple(actual_target.astype(np.int64).tolist()) != (trial.target_dx, trial.target_dy):
        return None, "endpoint_mismatch"
    target_mm = counts_to_millimeters(actual_target, trial.dpi)
    distance_mm = float(np.linalg.norm(target_mm))
    if distance_mm < config.min_distance_mm:
        return None, "distance_too_short"

    canonical_period_ms = 1_000.0 / config.canonical_hz
    length = max(2, int(math.ceil(duration_ms / canonical_period_ms)))
    if length > config.max_length:
        return None, "sequence_too_long"

    # Reconstruct cumulative native positions and interpolate those positions onto
    # the canonical clock. This preserves the endpoint while avoiding an invented
    # assumption that an idle HID report exists for every missing millisecond.
    event_times_ms: list[float] = []
    event_positions: list[np.ndarray] = []
    cumulative = np.zeros(2, dtype=np.float64)
    for event in trial.events:
        cumulative += (event.dx, event.dy)
        relative_ms = float(
            np.clip(
                (event.qpc_ticks - trial.first_motion_qpc) * 1_000.0 / trial.qpc_frequency,
                0.0,
                duration_ms,
            )
        )
        if event_times_ms and relative_ms == event_times_ms[-1]:
            event_positions[-1] = cumulative.copy()
        else:
            event_times_ms.append(relative_ms)
            event_positions.append(cumulative.copy())

    sample_times_ms = np.minimum(
        np.arange(1, length + 1, dtype=np.float64) * canonical_period_ms,
        duration_ms,
    )
    native_positions = np.stack(event_positions)
    sampled_positions = np.column_stack(
        [np.interp(sample_times_ms, event_times_ms, native_positions[:, axis]) for axis in range(2)]
    )
    grid_counts = np.diff(
        np.vstack((np.zeros((1, 2), dtype=np.float64), sampled_positions)), axis=0
    )
    grid_counts[-1] += actual_target - grid_counts.sum(axis=0)

    deltas_mm = counts_to_millimeters(grid_counts, trial.dpi)
    angle = float(math.atan2(target_mm[1], target_mm[0]))
    cosine = math.cos(angle)
    sine = math.sin(angle)
    aligned = np.column_stack(
        (
            deltas_mm[:, 0] * cosine + deltas_mm[:, 1] * sine,
            -deltas_mm[:, 0] * sine + deltas_mm[:, 1] * cosine,
        )
    )
    baseline = minimum_jerk_deltas(np.asarray([distance_mm, 0.0]), length)
    residual_active = (aligned - baseline) / distance_mm
    residual_active = project_zero_sum_numpy(residual_active)
    residual = np.zeros((2, config.max_length), dtype=np.float32)
    residual[:, :length] = residual_active.T.astype(np.float32)
    mask = np.zeros(config.max_length, dtype=np.bool_)
    mask[:length] = True
    duration_condition = np.asarray([math.log1p(distance_mm), sine, cosine], dtype=np.float32)
    condition = np.asarray([*duration_condition, math.log1p(duration_ms)], dtype=np.float32)
    armed_to_motion_ms = max(
        0.0, (trial.first_motion_qpc - trial.armed_qpc) * 1_000.0 / trial.qpc_frequency
    )
    return (
        ProcessedTrial(
            trial_id=trial.trial_id,
            session_id=trial.session_id,
            residual=residual,
            mask=mask,
            condition=condition,
            duration_condition=duration_condition,
            duration_ms=duration_ms,
            distance_mm=distance_mm,
            angle_radians=angle,
            target_counts=actual_target.astype(np.int32),
            dpi=trial.dpi,
            armed_to_motion_ms=armed_to_motion_ms,
        ),
        None,
    )


def assign_session_splits(session_ids: list[str], seed: int) -> dict[str, int]:
    unique = np.asarray(sorted(set(session_ids)), dtype=np.str_)
    if len(unique) == 0:
        return {}
    rng = np.random.default_rng(seed)
    rng.shuffle(unique)
    if len(unique) == 1:
        return {str(unique[0]): 0}
    if len(unique) == 2:
        return {str(unique[0]): 0, str(unique[1]): 1}

    test_count = max(1, int(round(len(unique) * 0.1)))
    validation_count = max(1, int(round(len(unique) * 0.1)))
    while test_count + validation_count >= len(unique):
        if validation_count > 1:
            validation_count -= 1
        elif test_count > 1:
            test_count -= 1
        else:
            break
    test_sessions = set(map(str, unique[:test_count]))
    validation_sessions = set(map(str, unique[test_count : test_count + validation_count]))
    return {
        str(session): 2
        if str(session) in test_sessions
        else 1
        if str(session) in validation_sessions
        else 0
        for session in unique
    }


def prepare_database(
    database_path: str | Path,
    output_directory: str | Path,
    *,
    config: PreprocessConfig | None = None,
) -> dict[str, Any]:
    selected_config = config or PreprocessConfig()
    database = Path(database_path)
    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=True)
    processed: list[ProcessedTrial] = []
    rejected: Counter[str] = Counter()
    total = 0
    with sqlite_snapshot(database) as snapshot:
        dataset_hash = sha256_file(snapshot)
        for raw_trial in iter_trials(snapshot, accepted_only=True):
            total += 1
            sample, reason = process_trial(raw_trial, selected_config)
            if sample is None:
                rejected[reason or "unknown"] += 1
            else:
                processed.append(sample)
    if not processed:
        raise ValueError("No accepted trial passed preprocessing.")

    split_by_session = assign_session_splits(
        [item.session_id for item in processed], selected_config.split_seed
    )
    arrays: dict[str, NDArray[Any]] = {
        "residuals": np.stack([item.residual for item in processed]).astype(np.float32),
        "masks": np.stack([item.mask for item in processed]).astype(np.bool_),
        "conditions": np.stack([item.condition for item in processed]).astype(np.float32),
        "duration_conditions": np.stack([item.duration_condition for item in processed]).astype(
            np.float32
        ),
        "durations_ms": np.asarray([item.duration_ms for item in processed], dtype=np.float32),
        "distances_mm": np.asarray([item.distance_mm for item in processed], dtype=np.float32),
        "angles": np.asarray([item.angle_radians for item in processed], dtype=np.float32),
        "target_counts": np.stack([item.target_counts for item in processed]).astype(np.int32),
        "dpis": np.asarray([item.dpi for item in processed], dtype=np.int32),
        "armed_to_motion_ms": np.asarray(
            [item.armed_to_motion_ms for item in processed], dtype=np.float32
        ),
        "splits": np.asarray(
            [split_by_session[item.session_id] for item in processed], dtype=np.int8
        ),
        "trial_ids": np.asarray([item.trial_id for item in processed], dtype=np.str_),
        "session_ids": np.asarray([item.session_id for item in processed], dtype=np.str_),
    }
    for name, array in arrays.items():
        np.save(output / f"{name}.npy", array, allow_pickle=False)

    split_counts = {
        "train": int(np.sum(arrays["splits"] == 0)),
        "validation": int(np.sum(arrays["splits"] == 1)),
        "test": int(np.sum(arrays["splits"] == 2)),
    }
    metadata: dict[str, Any] = {
        "schemaVersion": 1,
        "databaseSha256": dataset_hash,
        "sourceDatabase": str(database.resolve()),
        "totalAcceptedRows": total,
        "preparedRows": len(processed),
        "rejected": dict(sorted(rejected.items())),
        "splits": split_counts,
        "sessionSplit": split_by_session,
        "config": {
            "canonicalHz": selected_config.canonical_hz,
            "maxLength": selected_config.max_length,
            "minDurationMs": selected_config.min_duration_ms,
            "maxDurationMs": selected_config.max_duration_ms,
            "minDistanceMm": selected_config.min_distance_mm,
            "splitSeed": selected_config.split_seed,
        },
        "warnings": (["fewer_than_2000_trials"] if len(processed) < 2_000 else []),
    }
    (output / "metadata.json").write_text(
        json.dumps(metadata, indent=2, sort_keys=True), encoding="utf-8"
    )
    return metadata
