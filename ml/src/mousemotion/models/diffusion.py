from __future__ import annotations

import math

import torch
from torch import Tensor, nn


def cosine_beta_schedule(steps: int, offset: float = 0.008) -> Tensor:
    phase = torch.linspace(0, steps, steps + 1, dtype=torch.float64) / steps
    cumulative = torch.cos(((phase + offset) / (1.0 + offset)) * math.pi * 0.5).square()
    cumulative = cumulative / cumulative[0]
    betas = 1.0 - cumulative[1:] / cumulative[:-1]
    return betas.clamp(0.0001, 0.999).float()


def project_zero_sum(values: Tensor, mask: Tensor) -> Tensor:
    if values.ndim != 3 or values.shape[1] != 2:
        raise ValueError("values must have shape (batch, 2, length).")
    if mask.shape != (values.shape[0], values.shape[2]):
        raise ValueError("mask must have shape (batch, length).")
    active = mask.unsqueeze(1).to(dtype=values.dtype)
    count = active.sum(dim=-1, keepdim=True).clamp_min(1.0)
    correction = (values * active).sum(dim=-1, keepdim=True) / count
    projected = (values - correction) * active
    last_index = (mask.sum(dim=-1, keepdim=True).long() - 1).clamp_min(0)
    last_index = last_index.unsqueeze(1).expand(-1, values.shape[1], -1)
    current_last = projected.gather(dim=-1, index=last_index)
    sum_without_last = projected.sum(dim=-1, keepdim=True) - current_last
    return projected.scatter(dim=-1, index=last_index, src=-sum_without_last)


class GaussianDiffusion1D(nn.Module):
    def __init__(self, model: nn.Module, *, steps: int = 1_000) -> None:
        super().__init__()
        self.model = model
        self.steps = steps
        betas = cosine_beta_schedule(steps)
        alphas = 1.0 - betas
        cumulative = torch.cumprod(alphas, dim=0)
        self.register_buffer("betas", betas)
        self.register_buffer("alphas_cumulative", cumulative)
        self.register_buffer("sqrt_alphas_cumulative", torch.sqrt(cumulative))
        self.register_buffer("sqrt_one_minus_alphas_cumulative", torch.sqrt(1.0 - cumulative))

    def training_loss(
        self,
        clean: Tensor,
        condition: Tensor,
        mask: Tensor,
        *,
        generator: torch.Generator | None = None,
    ) -> Tensor:
        batch = clean.shape[0]
        timesteps = torch.randint(
            0,
            self.steps,
            (batch,),
            device=clean.device,
            generator=generator,
        )
        noise = torch.randn(
            clean.shape,
            dtype=clean.dtype,
            device=clean.device,
            generator=generator,
        )
        clean = project_zero_sum(clean, mask)
        noise = project_zero_sum(noise, mask)
        alpha = self.sqrt_alphas_cumulative[timesteps].view(batch, 1, 1)
        sigma = self.sqrt_one_minus_alphas_cumulative[timesteps].view(batch, 1, 1)
        noisy = project_zero_sum(alpha * clean + sigma * noise, mask)
        prediction = self.model(noisy, timesteps, condition, mask)
        active = mask.unsqueeze(1).to(dtype=prediction.dtype)
        squared_error = (prediction - noise).square() * active
        denominator = active.sum().clamp_min(1.0) * prediction.shape[1]
        return squared_error.sum() / denominator

    @torch.no_grad()
    def sample(
        self,
        condition: Tensor,
        mask: Tensor,
        *,
        sampling_steps: int = 50,
        generator: torch.Generator | None = None,
    ) -> Tensor:
        if not 1 <= sampling_steps <= self.steps:
            raise ValueError("sampling_steps must be within the diffusion schedule.")
        batch, length = mask.shape
        values = torch.randn(
            (batch, 2, length),
            dtype=condition.dtype,
            device=condition.device,
            generator=generator,
        )
        values = project_zero_sum(values, mask)
        schedule = (
            torch.linspace(
                self.steps - 1,
                0,
                sampling_steps,
                device=condition.device,
            )
            .round()
            .long()
        )
        schedule = torch.unique_consecutive(schedule)
        for index, timestep in enumerate(schedule):
            t = int(timestep.item())
            timestep_batch = torch.full((batch,), t, device=condition.device, dtype=torch.long)
            predicted_noise = self.model(values, timestep_batch, condition, mask)
            alpha = self.alphas_cumulative[t]
            clean = (values - torch.sqrt(1.0 - alpha) * predicted_noise) / torch.sqrt(alpha)
            clean = clean.clamp(min=-8.0, max=8.0)
            clean = project_zero_sum(clean, mask)
            if index == len(schedule) - 1:
                values = clean
                break
            previous_timestep = int(schedule[index + 1].item())
            previous_alpha = self.alphas_cumulative[previous_timestep]
            values = (
                torch.sqrt(previous_alpha) * clean
                + torch.sqrt(1.0 - previous_alpha) * predicted_noise
            )
            values = project_zero_sum(values, mask)
        return project_zero_sum(values, mask)
