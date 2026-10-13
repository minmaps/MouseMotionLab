from __future__ import annotations

import sqlite3

import torch

from mousemotion.contracts import TrajectoryRequest
from mousemotion.evaluation import evaluate_artifact
from mousemotion.generation import TrajectoryGenerator
from mousemotion.preprocess import PreprocessConfig, prepare_database
from mousemotion.training import ExponentialMovingAverage, TrainConfig, train_pipeline


def test_ema_warmup_does_not_preserve_random_initialization() -> None:
    model = torch.nn.Linear(1, 1, bias=False)
    with torch.no_grad():
        model.weight.zero_()
    ema = ExponentialMovingAverage(model, decay=0.999)
    with torch.no_grad():
        model.weight.fill_(1.0)

    ema.update(model)
    restored = torch.nn.Linear(1, 1, bias=False)
    ema.copy_to(restored)

    torch.testing.assert_close(restored.weight, torch.full_like(restored.weight, 9.0 / 11.0))


def create_capture_database(path) -> None:
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        CREATE TABLE schema_info (version INTEGER NOT NULL);
        INSERT INTO schema_info VALUES (2);
        CREATE TABLE profiles (
            id TEXT PRIMARY KEY,
            dpi INTEGER NOT NULL,
            nominal_polling_hz INTEGER NOT NULL
        );
        CREATE TABLE sessions (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL,
            qpc_frequency INTEGER NOT NULL
        );
        CREATE TABLE trials (
            id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            armed_qpc INTEGER NOT NULL,
            first_motion_qpc INTEGER NOT NULL,
            terminal_qpc INTEGER NOT NULL,
            target_dx INTEGER NOT NULL,
            target_dy INTEGER NOT NULL,
            status TEXT NOT NULL,
            rejection_reason TEXT,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE events (
            trial_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            qpc_ticks INTEGER NOT NULL,
            dx INTEGER NOT NULL,
            dy INTEGER NOT NULL,
            wheel_delta INTEGER NOT NULL,
            button_flags INTEGER NOT NULL,
            PRIMARY KEY (trial_id, sequence)
        );
        """
    )
    for session_index in range(3):
        profile_id = f"profile-{session_index}"
        session_id = f"session-{session_index}"
        connection.execute(
            "INSERT INTO profiles VALUES (?, ?, ?)",
            (profile_id, 800, 1_000),
        )
        connection.execute(
            "INSERT INTO sessions VALUES (?, ?, ?)",
            (session_id, profile_id, 1_000_000),
        )
        for ordinal in range(3):
            sample_index = session_index * 3 + ordinal
            trial_id = f"trial-{sample_index}"
            target_x = 200 + sample_index * 15
            target_y = -80 + sample_index * 10
            connection.execute(
                """
                INSERT INTO trials VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'accepted', NULL, ?)
                """,
                (
                    trial_id,
                    session_id,
                    ordinal,
                    -50_000,
                    0,
                    24_000,
                    target_x,
                    target_y,
                    f"2026-01-01T00:00:{sample_index:02d}Z",
                ),
            )
            first_dx, second_dx = target_x // 3, target_x // 3
            first_dy, second_dy = target_y // 3, target_y // 3
            deltas = (
                (first_dx, first_dy, 0),
                (second_dx, second_dy, 0),
                (
                    target_x - first_dx - second_dx,
                    target_y - first_dy - second_dy,
                    1,
                ),
            )
            for sequence, ((dx, dy, button_flags), qpc) in enumerate(
                zip(deltas, (0, 12_000, 24_000), strict=True)
            ):
                connection.execute(
                    "INSERT INTO events VALUES (?, ?, ?, ?, ?, 0, ?)",
                    (trial_id, sequence, qpc, dx, dy, button_flags),
                )
    connection.commit()
    connection.close()


def test_sqlite_prepare_train_artifact_and_generate_exact_endpoint(tmp_path) -> None:
    database = tmp_path / "capture.db"
    prepared = tmp_path / "prepared"
    artifact = tmp_path / "artifact"
    create_capture_database(database)
    preparation = prepare_database(
        database,
        prepared,
        config=PreprocessConfig(max_length=32, min_distance_mm=0.1),
    )

    metadata = train_pipeline(
        prepared,
        artifact,
        config=TrainConfig(
            epochs=1,
            duration_epochs=1,
            batch_size=3,
            diffusion_steps=10,
            sampling_steps=3,
            channels=(8, 16, 32),
            embedding_dim=32,
            duration_hidden_dim=16,
            duration_components=2,
            device="cpu",
        ),
    )
    generator = TrajectoryGenerator(artifact, device="cpu")
    request = TrajectoryRequest(260, -40, 800, 1_000, 99, "diffusion")
    result = generator.generate(request)
    report = evaluate_artifact(
        prepared,
        artifact,
        tmp_path / "evaluation",
        sample_count=3,
        device="cpu",
    )

    assert preparation["preparedRows"] == 9
    assert set(preparation["splits"].values()) == {3}
    assert metadata["trainingRows"] == 3
    assert metadata["trainingGeometry"]["pathRatioP95"] >= 1.0
    assert metadata["trainingDomain"]["distanceMm"]["max"] > 0
    assert result.endpoint == (260, -40)
    assert result.click_at_us == sum(item.dt_us for item in result.samples)
    assert report["samples"] == 3
    assert report["classifierAuc"]["diffusion"] is None
    assert (tmp_path / "evaluation" / "report.json").is_file()
