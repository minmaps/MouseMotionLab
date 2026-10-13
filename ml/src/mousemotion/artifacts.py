from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import torch
from safetensors.torch import load_file, save_file

from mousemotion.coverage import CoverageModel
from mousemotion.models.diffusion import GaussianDiffusion1D
from mousemotion.models.duration import DurationMixtureNetwork
from mousemotion.models.unet1d import ConditionalUNet1D


@dataclass(slots=True)
class LoadedArtifact:
    root: Path
    metadata: dict[str, Any]
    diffusion: GaussianDiffusion1D
    duration: DurationMixtureNetwork
    coverage: CoverageModel
    condition_mean: np.ndarray
    condition_scale: np.ndarray
    duration_condition_mean: np.ndarray
    duration_condition_scale: np.ndarray
    residual_scale: np.ndarray
    device: torch.device


def save_artifact(
    output_directory: str | Path,
    *,
    unet: ConditionalUNet1D,
    duration_model: DurationMixtureNetwork,
    coverage: CoverageModel,
    statistics: dict[str, np.ndarray],
    metadata: dict[str, Any],
) -> Path:
    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=True)
    save_file(
        {key: value.detach().cpu().contiguous() for key, value in unet.state_dict().items()},
        output / "diffusion.safetensors",
    )
    save_file(
        {
            key: value.detach().cpu().contiguous()
            for key, value in duration_model.state_dict().items()
        },
        output / "duration.safetensors",
    )
    coverage.save(output / "coverage.npz")
    np.savez_compressed(output / "normalization.npz", **statistics)
    (output / "metadata.json").write_text(
        json.dumps(metadata, indent=2, sort_keys=True), encoding="utf-8"
    )
    return output


def load_artifact(
    artifact_directory: str | Path,
    *,
    device: str | torch.device = "cpu",
) -> LoadedArtifact:
    root = Path(artifact_directory)
    metadata = json.loads((root / "metadata.json").read_text(encoding="utf-8"))
    if int(metadata.get("schemaVersion", 0)) != 1:
        raise ValueError("Unsupported model artifact schema.")
    selected_device = torch.device(device)
    model_config = metadata["model"]
    channels = tuple(int(item) for item in model_config["channels"])
    unet = ConditionalUNet1D(
        condition_dim=4,
        channels=channels,  # type: ignore[arg-type]
        embedding_dim=int(model_config["embeddingDim"]),
    )
    unet.load_state_dict(load_file(root / "diffusion.safetensors", device=str(selected_device)))
    unet.to(selected_device).eval()
    diffusion = GaussianDiffusion1D(unet, steps=int(model_config["diffusionSteps"]))
    diffusion.to(selected_device).eval()

    duration = DurationMixtureNetwork(
        condition_dim=3,
        hidden_dim=int(model_config["durationHiddenDim"]),
        components=int(model_config["durationComponents"]),
    )
    duration.load_state_dict(load_file(root / "duration.safetensors", device=str(selected_device)))
    duration.to(selected_device).eval()
    coverage = CoverageModel.load(root / "coverage.npz")
    with np.load(root / "normalization.npz") as normalization:
        arrays = {name: normalization[name].astype(np.float32) for name in normalization.files}
    return LoadedArtifact(
        root=root,
        metadata=metadata,
        diffusion=diffusion,
        duration=duration,
        coverage=coverage,
        condition_mean=arrays["condition_mean"],
        condition_scale=arrays["condition_scale"],
        duration_condition_mean=arrays["duration_condition_mean"],
        duration_condition_scale=arrays["duration_condition_scale"],
        residual_scale=arrays["residual_scale"],
        device=selected_device,
    )
