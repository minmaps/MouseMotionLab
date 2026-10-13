from __future__ import annotations

import numpy as np
import pytest

from mousemotion.baseline import (
    generate_baseline,
    minimum_jerk_deltas,
    project_zero_sum_numpy,
    quantize_deltas_exact,
)
from mousemotion.contracts import TrajectoryRequest
from mousemotion.generation import TrajectoryGenerator


@pytest.mark.parametrize(
    ("dx", "dy", "hz"),
    [(800, -250, 1_000), (-1_201, 4, 500), (3, 7, 8_000), (-45, -92, 125)],
)
def test_baseline_preserves_exact_endpoint(dx: int, dy: int, hz: int) -> None:
    request = TrajectoryRequest(dx, dy, 800, hz, 123, "baseline")

    first = generate_baseline(request)
    second = generate_baseline(request)

    assert first.endpoint == (dx, dy)
    assert first.click_at_us == sum(item.dt_us for item in first.samples)
    assert first.to_json() == second.to_json()


def test_quantization_uses_cumulative_rounding() -> None:
    deltas = np.asarray([[0.01, -0.02], [0.02, 0.03], [0.5, -0.1]], dtype=np.float64)

    quantized = quantize_deltas_exact(deltas, (17, -3), dpi=800)

    assert tuple(quantized.sum(axis=0)) == (17, -3)


def test_zero_sum_projection_respects_mask() -> None:
    values = np.asarray([[1.0, 2.0], [3.0, -2.0], [99.0, 99.0]])
    projected = project_zero_sum_numpy(values, np.asarray([1.0, 1.0, 0.0]))

    np.testing.assert_allclose(projected[:2].sum(axis=0), 0.0, atol=1e-12)
    np.testing.assert_allclose(projected[2], 0.0)


def test_negative_seed_is_reproducible() -> None:
    request = TrajectoryRequest(300, -120, 800, 1_000, -42, "baseline")

    first = generate_baseline(request)
    second = generate_baseline(request)

    assert first == second
    assert first.endpoint == (300, -120)


def test_generated_residual_is_constrained_to_training_geometry() -> None:
    distance = 20.0
    length = 100
    baseline = minimum_jerk_deltas(np.asarray([distance, 0.0]), length)
    residual = np.zeros((length, 2), dtype=np.float64)
    residual[: length // 2, 1] = 0.02
    residual[length // 2 :, 1] = -0.02

    class ArtifactStub:
        metadata = {
            "trainingGeometry": {
                "pathRatioP95": 1.1,
                "lateralFractionP95": 0.05,
                "backtrackFractionP95": 0.02,
                "overshootFractionP95": 0.03,
            }
        }

    constrained, warnings = TrajectoryGenerator._constrain_residual_geometry(
        baseline,
        residual,
        distance,
        ArtifactStub(),  # type: ignore[arg-type]
    )
    trajectory = baseline + constrained * distance
    path_ratio = np.linalg.norm(trajectory, axis=1).sum() / distance
    lateral_fraction = np.max(np.abs(np.cumsum(trajectory[:, 1]))) / distance
    longitudinal = np.cumsum(trajectory[:, 0]) / distance

    assert warnings and warnings[0].startswith("residual_scaled_to_training_geometry:")
    assert path_ratio <= 1.1 + 1e-6
    assert lateral_fraction <= 0.05 + 1e-6
    assert longitudinal.min() >= -0.02 - 1e-6
    assert longitudinal.max() <= 1.03 + 1e-6
