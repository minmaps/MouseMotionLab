from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any

SCHEMA_VERSION = 1


@dataclass(frozen=True, slots=True)
class TrajectoryRequest:
    dx_counts: int
    dy_counts: int
    dpi: int
    output_hz: int = 1_000
    seed: int = 0
    mode: str = "diffusion"

    def validate(self) -> None:
        if self.dx_counts == 0 and self.dy_counts == 0:
            raise ValueError("The requested endpoint cannot be (0, 0).")
        if not 100 <= self.dpi <= 100_000:
            raise ValueError("dpi must be between 100 and 100000.")
        if not 125 <= self.output_hz <= 8_000:
            raise ValueError("output_hz must be between 125 and 8000.")
        if self.mode not in {"baseline", "diffusion"}:
            raise ValueError("mode must be either 'baseline' or 'diffusion'.")


@dataclass(frozen=True, slots=True)
class TrajectorySample:
    dt_us: int
    dx: int
    dy: int

    def to_wire(self) -> dict[str, int]:
        return {"dtUs": self.dt_us, "dx": self.dx, "dy": self.dy}


@dataclass(frozen=True, slots=True)
class TrajectoryResult:
    samples: tuple[TrajectorySample, ...]
    click_at_us: int
    coverage_score: float
    warnings: tuple[str, ...] = field(default_factory=tuple)
    model_version: str = "baseline-v1"
    seed: int = 0
    schema_version: int = SCHEMA_VERSION

    @property
    def endpoint(self) -> tuple[int, int]:
        return sum(item.dx for item in self.samples), sum(item.dy for item in self.samples)

    def validate_against(self, request: TrajectoryRequest) -> None:
        request.validate()
        if self.schema_version != SCHEMA_VERSION:
            raise ValueError(f"Unsupported schema version: {self.schema_version}.")
        if not self.samples or any(item.dt_us <= 0 for item in self.samples):
            raise ValueError("A trajectory needs at least one positive-duration sample.")
        if self.endpoint != (request.dx_counts, request.dy_counts):
            raise ValueError(
                f"Endpoint {self.endpoint} does not match "
                f"({request.dx_counts}, {request.dy_counts})."
            )
        if self.click_at_us != sum(item.dt_us for item in self.samples):
            raise ValueError("click_at_us must equal the sum of sample durations.")

    def to_wire(self) -> dict[str, Any]:
        return {
            "schemaVersion": self.schema_version,
            "samples": [item.to_wire() for item in self.samples],
            "clickAtUs": self.click_at_us,
            "coverageScore": self.coverage_score,
            "warnings": list(self.warnings),
            "modelVersion": self.model_version,
            "seed": self.seed,
        }

    def to_json(self, *, indent: int | None = None) -> str:
        return json.dumps(self.to_wire(), indent=indent, separators=None if indent else (",", ":"))
