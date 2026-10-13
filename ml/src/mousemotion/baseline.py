from __future__ import annotations

import math

import numpy as np
from numpy.typing import NDArray

from mousemotion.contracts import TrajectoryRequest, TrajectoryResult, TrajectorySample

FloatArray = NDArray[np.float64]
IntArray = NDArray[np.int64]


def counts_to_millimeters(counts: FloatArray, dpi: int) -> FloatArray:
    if dpi <= 0:
        raise ValueError("dpi must be positive.")
    return np.asarray(counts, dtype=np.float64) * (25.4 / float(dpi))


def minimum_jerk_progress(length: int) -> FloatArray:
    if length < 1:
        raise ValueError("length must be positive.")
    phase = np.linspace(0.0, 1.0, length + 1, dtype=np.float64)[1:]
    return 10.0 * phase**3 - 15.0 * phase**4 + 6.0 * phase**5


def minimum_jerk_deltas(target_mm: FloatArray, length: int) -> FloatArray:
    target = np.asarray(target_mm, dtype=np.float64)
    if target.shape != (2,):
        raise ValueError("target_mm must have shape (2,).")
    positions = minimum_jerk_progress(length)[:, None] * target[None, :]
    return np.diff(np.vstack((np.zeros((1, 2), dtype=np.float64), positions)), axis=0)


def resample_deltas(deltas: FloatArray, output_length: int) -> FloatArray:
    values = np.asarray(deltas, dtype=np.float64)
    if values.ndim != 2 or values.shape[1] != 2 or len(values) == 0:
        raise ValueError("deltas must have shape (length, 2).")
    if output_length < 1:
        raise ValueError("output_length must be positive.")
    if len(values) == output_length:
        return values.copy()

    cumulative = np.vstack((np.zeros((1, 2), dtype=np.float64), np.cumsum(values, axis=0)))
    source_phase = np.linspace(0.0, 1.0, len(cumulative), dtype=np.float64)
    target_phase = np.linspace(0.0, 1.0, output_length + 1, dtype=np.float64)
    positions = np.column_stack(
        [np.interp(target_phase, source_phase, cumulative[:, axis]) for axis in range(2)]
    )
    return np.diff(positions, axis=0)


def project_zero_sum_numpy(values: FloatArray, mask: FloatArray | None = None) -> FloatArray:
    array = np.asarray(values, dtype=np.float64).copy()
    if array.ndim != 2 or array.shape[1] != 2:
        raise ValueError("values must have shape (length, 2).")
    if mask is None:
        active = np.ones(len(array), dtype=np.float64)
    else:
        active = np.asarray(mask, dtype=np.float64)
        if active.shape != (len(array),):
            raise ValueError("mask must have shape (length,).")
    count = float(active.sum())
    if count <= 0:
        raise ValueError("mask must contain at least one active sample.")
    correction = (array * active[:, None]).sum(axis=0) / count
    return (array - correction[None, :]) * active[:, None]


def quantize_deltas_exact(
    deltas_mm: FloatArray,
    target_counts: tuple[int, int],
    dpi: int,
) -> IntArray:
    values = np.asarray(deltas_mm, dtype=np.float64)
    if values.ndim != 2 or values.shape[1] != 2 or len(values) == 0:
        raise ValueError("deltas_mm must have shape (length, 2).")
    cumulative_counts = np.rint(np.cumsum(values, axis=0) * (float(dpi) / 25.4)).astype(np.int64)
    cumulative_counts[-1] = np.asarray(target_counts, dtype=np.int64)
    quantized = np.diff(np.vstack((np.zeros((1, 2), dtype=np.int64), cumulative_counts)), axis=0)
    if tuple(quantized.sum(axis=0).tolist()) != target_counts:
        raise AssertionError("Exact endpoint quantization invariant failed.")
    return quantized


def sample_durations_us(duration_ms: float, sample_count: int) -> IntArray:
    if duration_ms <= 0 or sample_count < 1:
        raise ValueError("duration and sample_count must be positive.")
    total_us = max(sample_count, int(round(duration_ms * 1_000.0)))
    boundaries = np.rint(np.linspace(0, total_us, sample_count + 1)).astype(np.int64)
    deltas = np.diff(boundaries)
    if np.any(deltas <= 0):
        raise AssertionError("Generated sample durations must be positive.")
    return deltas


def heuristic_duration_ms(distance_mm: float, rng: np.random.Generator) -> float:
    center = 58.0 + 36.0 * math.log1p(max(distance_mm, 0.0))
    duration = center * float(rng.lognormal(mean=0.0, sigma=0.08))
    return float(np.clip(duration, 20.0, 1_000.0))


def generate_baseline(
    request: TrajectoryRequest,
    *,
    duration_ms: float | None = None,
    coverage_score: float = 0.0,
    warnings: tuple[str, ...] = (),
    model_version: str = "minimum-jerk-v1",
) -> TrajectoryResult:
    request.validate()
    rng = np.random.default_rng(request.seed & 0xFFFF_FFFF_FFFF_FFFF)
    target_counts = np.asarray((request.dx_counts, request.dy_counts), dtype=np.float64)
    target_mm = counts_to_millimeters(target_counts, request.dpi)
    distance_mm = float(np.linalg.norm(target_mm))
    selected_duration = duration_ms or heuristic_duration_ms(distance_mm, rng)
    sample_count = max(2, int(round(selected_duration * request.output_hz / 1_000.0)))
    deltas_mm = minimum_jerk_deltas(target_mm, sample_count)
    deltas_counts = quantize_deltas_exact(
        deltas_mm, (request.dx_counts, request.dy_counts), request.dpi
    )
    durations_us = sample_durations_us(selected_duration, sample_count)
    samples = tuple(
        TrajectorySample(int(dt), int(delta[0]), int(delta[1]))
        for dt, delta in zip(durations_us, deltas_counts, strict=True)
    )
    result = TrajectoryResult(
        samples=samples,
        click_at_us=int(durations_us.sum()),
        coverage_score=float(np.clip(coverage_score, 0.0, 1.0)),
        warnings=warnings,
        model_version=model_version,
        seed=request.seed,
    )
    result.validate_against(request)
    return result
