from __future__ import annotations

import hashlib
import sqlite3
from collections.abc import Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path
from tempfile import TemporaryDirectory


@dataclass(frozen=True, slots=True)
class DatabaseEvent:
    sequence: int
    qpc_ticks: int
    dx: int
    dy: int
    wheel_delta: int
    button_flags: int
    cursor_dx_pixels: int | None = None
    cursor_dy_pixels: int | None = None


@dataclass(frozen=True, slots=True)
class DatabaseTrial:
    trial_id: str
    session_id: str
    profile_id: str
    dpi: int
    nominal_polling_hz: int
    qpc_frequency: int
    armed_qpc: int
    first_motion_qpc: int
    terminal_qpc: int
    target_dx: int
    target_dy: int
    status: str
    rejection_reason: str | None
    events: tuple[DatabaseEvent, ...]


def sha256_file(path: str | Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


@contextmanager
def sqlite_snapshot(database_path: str | Path) -> Iterator[Path]:
    """Create a consistent read snapshot, including committed WAL pages."""
    source_path = Path(database_path).resolve()
    if not source_path.is_file():
        raise FileNotFoundError(source_path)
    with TemporaryDirectory(prefix="mousemotion-snapshot-") as temporary:
        snapshot = Path(temporary) / "dataset.db"
        source = sqlite3.connect(f"file:{source_path.as_posix()}?mode=ro", uri=True)
        destination = sqlite3.connect(snapshot)
        try:
            source.backup(destination)
        finally:
            destination.close()
            source.close()
        yield snapshot


def read_schema_version(connection: sqlite3.Connection) -> int:
    row = connection.execute("SELECT version FROM schema_info LIMIT 1").fetchone()
    if row is None:
        raise ValueError("Dataset has no schema_info row.")
    return int(row[0])


def iter_trials(
    database_path: str | Path,
    *,
    accepted_only: bool = True,
) -> Iterator[DatabaseTrial]:
    path = Path(database_path)
    if not path.is_file():
        raise FileNotFoundError(path)
    connection = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    try:
        version = read_schema_version(connection)
        if version not in {1, 2, 3}:
            raise ValueError(f"Unsupported SQLite dataset schema: {version}.")
        event_columns = {
            str(row[1]) for row in connection.execute("PRAGMA table_info(events)").fetchall()
        }
        cursor_projection = (
            "cursor_dx_pixels, cursor_dy_pixels"
            if {"cursor_dx_pixels", "cursor_dy_pixels"}.issubset(event_columns)
            else "NULL AS cursor_dx_pixels, NULL AS cursor_dy_pixels"
        )
        where = "WHERE t.status = 'accepted'" if accepted_only else ""
        rows = connection.execute(
            f"""
            SELECT
                t.id AS trial_id,
                t.session_id,
                s.profile_id,
                p.dpi,
                p.nominal_polling_hz,
                s.qpc_frequency,
                t.armed_qpc,
                t.first_motion_qpc,
                t.terminal_qpc,
                t.target_dx,
                t.target_dy,
                t.status,
                t.rejection_reason
            FROM trials t
            JOIN sessions s ON s.id = t.session_id
            JOIN profiles p ON p.id = s.profile_id
            {where}
            ORDER BY t.created_utc, t.ordinal
            """
        ).fetchall()
        event_statement = f"""
            SELECT sequence, qpc_ticks, dx, dy, wheel_delta, button_flags,
                   {cursor_projection}
            FROM events
            WHERE trial_id = ?
            ORDER BY sequence
        """
        for row in rows:
            events = tuple(
                DatabaseEvent(
                    sequence=int(event["sequence"]),
                    qpc_ticks=int(event["qpc_ticks"]),
                    dx=int(event["dx"]),
                    dy=int(event["dy"]),
                    wheel_delta=int(event["wheel_delta"]),
                    button_flags=int(event["button_flags"]),
                    cursor_dx_pixels=(
                        None
                        if event["cursor_dx_pixels"] is None
                        else int(event["cursor_dx_pixels"])
                    ),
                    cursor_dy_pixels=(
                        None
                        if event["cursor_dy_pixels"] is None
                        else int(event["cursor_dy_pixels"])
                    ),
                )
                for event in connection.execute(event_statement, (row["trial_id"],))
            )
            yield DatabaseTrial(
                trial_id=str(row["trial_id"]),
                session_id=str(row["session_id"]),
                profile_id=str(row["profile_id"]),
                dpi=int(row["dpi"]),
                nominal_polling_hz=int(row["nominal_polling_hz"]),
                qpc_frequency=int(row["qpc_frequency"]),
                armed_qpc=int(row["armed_qpc"]),
                first_motion_qpc=int(row["first_motion_qpc"]),
                terminal_qpc=int(row["terminal_qpc"]),
                target_dx=int(row["target_dx"]),
                target_dy=int(row["target_dy"]),
                status=str(row["status"]),
                rejection_reason=row["rejection_reason"],
                events=events,
            )
    finally:
        connection.close()
