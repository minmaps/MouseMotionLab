from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import torch

from mousemotion.artifacts import LoadedArtifact, load_artifact
from mousemotion.baseline import (
    generate_baseline,
    minimum_jerk_deltas,
    project_zero_sum_numpy,
    quantize_deltas_exact,
    resample_deltas,
    sample_durations_us,
)
from mousemotion.contracts import TrajectoryRequest, TrajectoryResult, TrajectorySample
from mousemotion.coverage import target_feature


def resolve_torch_device(selection: str = "auto") -> torch.device:
    if selection == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")
    device = torch.device(selection)
    if device.type == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA was requested but is not available to PyTorch.")
    return device


class TrajectoryGenerator:
    def __init__(
        self,
        artifact_directory: str | Path | None = None,
        *,
        device: str = "auto",
    ) -> None:
        self.artifact: LoadedArtifact | None = None
        if artifact_directory is not None:
            self.artifact = load_artifact(artifact_directory, device=resolve_torch_device(device))

    def generate(self, request: TrajectoryRequest) -> TrajectoryResult:
        request.validate()
        if request.mode == "baseline":
            return self._generate_baseline(request)
        if self.artifact is None:
            raise ValueError("A trained artifact is required for diffusion generation.")
        return self._generate_diffusion(request, self.artifact)

    def _target_context(
        self,
        request: TrajectoryRequest,
    ) -> tuple[float, float, np.ndarray, float, tuple[str, ...]]:
        target_mm = np.asarray([request.dx_counts, request.dy_counts], dtype=np.float64) * (
            25.4 / request.dpi
        )
        distance_mm = float(np.linalg.norm(target_mm))
        angle = float(math.atan2(target_mm[1], target_mm[0]))
        feature = target_feature(distance_mm, angle)
        if self.artifact is None:
            return distance_mm, angle, feature, 0.0, ("coverage_model_unavailable",)
        score, out_of_distribution, ratio = self.artifact.coverage.score(feature)
        warnings: list[str] = []
        if out_of_distribution:
            warnings.append(f"out_of_distribution:knn_ratio={ratio:.3f}")
        domain = self.artifact.metadata.get("trainingDomain", {}).get("distanceMm", {})
        minimum = float(domain.get("min", 0.0))
        maximum = float(domain.get("max", float("inf")))
        if distance_mm < minimum or distance_mm > maximum:
            warnings.append(
                f"out_of_distribution:distance_mm={distance_mm:.3f} outside "
                f"[{minimum:.3f},{maximum:.3f}]"
            )
            boundary_ratio = (
                minimum / max(distance_mm, 1e-9)
                if distance_mm < minimum
                else distance_mm / max(maximum, 1e-9)
            )
            score *= math.exp(-2.0 * max(0.0, boundary_ratio - 1.0))
        return distance_mm, angle, feature, float(np.clip(score, 0.0, 1.0)), tuple(warnings)

    def _sample_duration(
        self,
        feature: np.ndarray,
        seed: int,
        artifact: LoadedArtifact,
    ) -> float:
        normalized = (
            feature.astype(np.float32) - artifact.duration_condition_mean
        ) / artifact.duration_condition_scale
        condition = torch.from_numpy(normalized).unsqueeze(0).to(artifact.device)
        generator = torch.Generator(device=artifact.device)
        generator.manual_seed(seed & 0x7FFF_FFFF_FFFF_FFFF)
        return float(artifact.duration.sample(condition, generator=generator).item())

    @staticmethod
    def _clamp_duration(
        duration_ms: float,
        artifact: LoadedArtifact,
    ) -> tuple[float, tuple[str, ...]]:
        domain = artifact.metadata.get("trainingDomain", {}).get("durationMs", {})
        if "min" not in domain or "max" not in domain:
            return duration_ms, ()
        minimum = float(domain["min"])
        maximum = float(domain["max"])
        clamped = float(np.clip(duration_ms, minimum, maximum))
        warning = ("duration_clamped_to_training_range",) if clamped != duration_ms else ()
        return clamped, warning

    @staticmethod
    def _constrain_residual_geometry(
        baseline: np.ndarray,
        residual: np.ndarray,
        distance_mm: float,
        artifact: LoadedArtifact,
    ) -> tuple[np.ndarray, tuple[str, ...]]:
        geometry = artifact.metadata.get("trainingGeometry", {})
        required = {
            "pathRatioP95",
            "lateralFractionP95",
            "backtrackFractionP95",
            "overshootFractionP95",
        }
        if not required.issubset(geometry):
            return residual, ()

        path_limit = max(1.001, float(geometry["pathRatioP95"]))
        lateral_limit = max(1e-4, float(geometry["lateralFractionP95"]))
        backtrack_limit = max(1e-4, float(geometry["backtrackFractionP95"]))
        overshoot_limit = max(1e-4, float(geometry["overshootFractionP95"]))
        cumulative_lateral = np.cumsum(residual[:, 1] * distance_mm)
        lateral_fraction = float(np.max(np.abs(cumulative_lateral)) / distance_mm)
        scale = min(1.0, lateral_limit / max(lateral_fraction, 1e-12))

        def is_within_limits(candidate_scale: float) -> bool:
            candidate = baseline + residual * (distance_mm * candidate_scale)
            path_ratio = float(np.linalg.norm(candidate, axis=1).sum() / distance_mm)
            positions = np.cumsum(candidate, axis=0)
            backtrack = max(0.0, -float(positions[:, 0].min()) / distance_mm)
            overshoot = max(0.0, float(positions[:, 0].max()) / distance_mm - 1.0)
            return (
                path_ratio <= path_limit
                and backtrack <= backtrack_limit
                and overshoot <= overshoot_limit
            )

        if not is_within_limits(scale):
            lower = 0.0
            upper = scale
            for _ in range(24):
                middle = (lower + upper) * 0.5
                if is_within_limits(middle):
                    lower = middle
                else:
                    upper = middle
            scale = lower
        if scale >= 1.0 - 1e-6:
            return residual, ()
        return residual * scale, (f"residual_scaled_to_training_geometry:{scale:.3f}",)

    def _generate_baseline(self, request: TrajectoryRequest) -> TrajectoryResult:
        distance, _, feature, score, warnings = self._target_context(request)
        duration_ms = None
        version = "minimum-jerk-v1"
        if self.artifact is not None:
            duration_ms = self._sample_duration(feature, request.seed, self.artifact)
            duration_ms, duration_warnings = self._clamp_duration(duration_ms, self.artifact)
            warnings = (*warnings, *duration_warnings)
            version = f"{self.artifact.metadata['modelVersion']}:minimum-jerk"
        elif distance <= 0:
            raise ValueError("Target distance must be positive.")
        return generate_baseline(
            request,
            duration_ms=duration_ms,
            coverage_score=score,
            warnings=warnings,
            model_version=version,
        )

    def _generate_diffusion(
        self,
        request: TrajectoryRequest,
        artifact: LoadedArtifact,
    ) -> TrajectoryResult:
        distance_mm, angle, feature, coverage_score, warnings = self._target_context(request)
        duration_ms = self._sample_duration(feature, request.seed, artifact)
        duration_ms, duration_warnings = self._clamp_duration(duration_ms, artifact)
        warnings = (*warnings, *duration_warnings)
        max_length = int(artifact.metadata["model"]["maxLength"])
        canonical_hz = int(artifact.metadata["model"]["canonicalHz"])
        canonical_length = max(2, int(math.ceil(duration_ms * canonical_hz / 1_000.0)))
        if canonical_length > max_length:
            warnings = (*warnings, "duration_clamped_to_model_maximum")
            canonical_length = max_length
            duration_ms = max_length * 1_000.0 / canonical_hz

        raw_condition = np.asarray(
            [feature[0], feature[1], feature[2], math.log1p(duration_ms)],
            dtype=np.float32,
        )
        normalized_condition = (raw_condition - artifact.condition_mean) / artifact.condition_scale
        condition = torch.from_numpy(normalized_condition).unsqueeze(0).to(artifact.device)
        mask = torch.zeros((1, max_length), dtype=torch.float32, device=artifact.device)
        mask[:, :canonical_length] = 1.0
        generator = torch.Generator(device=artifact.device)
        generator.manual_seed(request.seed & 0x7FFF_FFFF_FFFF_FFFF)
        sampled = artifact.diffusion.sample(
            condition,
            mask,
            sampling_steps=int(artifact.metadata["model"]["samplingSteps"]),
            generator=generator,
        )[0, :, :canonical_length]
        residual = sampled.detach().cpu().numpy().T.astype(np.float64)
        residual *= artifact.residual_scale[None, :]
        residual = project_zero_sum_numpy(residual)

        baseline_aligned = minimum_jerk_deltas(
            np.asarray([distance_mm, 0.0], dtype=np.float64), canonical_length
        )
        residual, geometry_warnings = self._constrain_residual_geometry(
            baseline_aligned,
            residual,
            distance_mm,
            artifact,
        )
        warnings = (*warnings, *geometry_warnings)
        aligned = baseline_aligned + residual * distance_mm
        cosine = math.cos(angle)
        sine = math.sin(angle)
        world = np.column_stack(
            (
                aligned[:, 0] * cosine - aligned[:, 1] * sine,
                aligned[:, 0] * sine + aligned[:, 1] * cosine,
            )
        )
        output_length = max(2, int(round(duration_ms * request.output_hz / 1_000.0)))
        output_world = resample_deltas(world, output_length)
        quantized = quantize_deltas_exact(
            output_world, (request.dx_counts, request.dy_counts), request.dpi
        )
        durations_us = sample_durations_us(duration_ms, output_length)
        samples = tuple(
            TrajectorySample(int(dt), int(delta[0]), int(delta[1]))
            for dt, delta in zip(durations_us, quantized, strict=True)
        )
        result = TrajectoryResult(
            samples=samples,
            click_at_us=int(durations_us.sum()),
            coverage_score=coverage_score,
            warnings=warnings,
            model_version=str(artifact.metadata["modelVersion"]),
            seed=request.seed,
        )
        result.validate_against(request)
        return result
