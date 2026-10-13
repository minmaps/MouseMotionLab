from __future__ import annotations

import numpy as np

from mousemotion.database import DatabaseEvent, DatabaseTrial
from mousemotion.preprocess import PreprocessConfig, assign_session_splits, process_trial


def make_trial(dpi: int, multiplier: int = 1) -> DatabaseTrial:
    events = (
        DatabaseEvent(0, 100_000, 20 * multiplier, -4 * multiplier, 0, 0),
        DatabaseEvent(1, 180_000, 25 * multiplier, 10 * multiplier, 0, 0),
        DatabaseEvent(2, 300_000, 5 * multiplier, -6 * multiplier, 0, 1),
    )
    return DatabaseTrial(
        trial_id=f"trial-{dpi}",
        session_id=f"session-{dpi}",
        profile_id=f"profile-{dpi}",
        dpi=dpi,
        nominal_polling_hz=1_000,
        qpc_frequency=1_000_000,
        armed_qpc=50_000,
        first_motion_qpc=100_000,
        terminal_qpc=300_000,
        target_dx=50 * multiplier,
        target_dy=0,
        status="accepted",
        rejection_reason=None,
        events=events,
    )


def test_dpi_normalization_produces_same_physical_sample() -> None:
    config = PreprocessConfig(min_distance_mm=0.1)
    low, low_reason = process_trial(make_trial(800, 1), config)
    high, high_reason = process_trial(make_trial(1_600, 2), config)

    assert low_reason is None and high_reason is None
    assert low is not None and high is not None
    assert low.distance_mm == high.distance_mm
    np.testing.assert_allclose(low.residual, high.residual, atol=1e-6)
    assert int(low.mask.sum()) == 200
    np.testing.assert_allclose(low.residual[:, low.mask].sum(axis=1), 0.0, atol=1e-5)


def test_split_assignment_never_splits_a_session() -> None:
    sessions = ["a", "a", "b", "b", "c", "d"]

    first = assign_session_splits(sessions, 42)
    second = assign_session_splits(sessions, 42)

    assert first == second
    assert set(first) == {"a", "b", "c", "d"}
    assert set(first.values()) == {0, 1, 2}


def test_cumulative_position_is_interpolated_on_one_millisecond_grid() -> None:
    trial = DatabaseTrial(
        trial_id="interpolation",
        session_id="session",
        profile_id="profile",
        dpi=800,
        nominal_polling_hz=1_000,
        qpc_frequency=1_000_000,
        armed_qpc=-1_000,
        first_motion_qpc=0,
        terminal_qpc=20_000,
        target_dx=20,
        target_dy=0,
        status="accepted",
        rejection_reason=None,
        events=(
            DatabaseEvent(0, 0, 10, 0, 0, 0),
            DatabaseEvent(1, 20_000, 10, 0, 0, 1),
        ),
    )

    processed, reason = process_trial(
        trial,
        PreprocessConfig(min_distance_mm=0.1, min_duration_ms=20),
    )

    assert reason is None and processed is not None
    # The second native report is distributed over the intervening canonical
    # positions instead of being placed wholesale into one 1 ms bin.
    active = processed.residual[:, processed.mask]
    assert active.shape[1] == 20
    assert np.count_nonzero(np.abs(active[0]) > 1e-8) > 2
    assert processed.armed_to_motion_ms == 1.0
