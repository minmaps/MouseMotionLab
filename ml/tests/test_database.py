from __future__ import annotations

import sqlite3

from mousemotion.database import iter_trials


def test_schema_three_reads_windows_cursor_samples(tmp_path) -> None:
    database = tmp_path / "cursor-samples.db"
    connection = sqlite3.connect(database)
    connection.executescript(
        """
        CREATE TABLE schema_info (version INTEGER NOT NULL);
        INSERT INTO schema_info VALUES (3);
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
            cursor_dx_pixels INTEGER,
            cursor_dy_pixels INTEGER,
            PRIMARY KEY (trial_id, sequence)
        );
        INSERT INTO profiles VALUES ('profile', 800, 1000);
        INSERT INTO sessions VALUES ('session', 'profile', 1000000);
        INSERT INTO trials VALUES (
            'trial', 'session', 1, 0, 1000, 2000, 5, -2,
            'accepted', NULL, '2026-01-01T00:00:00Z'
        );
        INSERT INTO events VALUES ('trial', 0, 1000, 2, -1, 0, 0, 3, -2);
        INSERT INTO events VALUES ('trial', 1, 2000, 3, -1, 0, 1, 9, -4);
        """
    )
    connection.commit()
    connection.close()

    trial = next(iter_trials(database))

    assert trial.events[0].cursor_dx_pixels == 3
    assert trial.events[0].cursor_dy_pixels == -2
    assert trial.events[1].cursor_dx_pixels == 9
    assert trial.events[1].cursor_dy_pixels == -4

