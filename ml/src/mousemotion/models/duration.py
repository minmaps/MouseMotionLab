from __future__ import annotations

import math

import torch
from torch import Tensor, nn
from torch.nn import functional as functional


class DurationMixtureNetwork(nn.Module):
    def __init__(
        self,
        condition_dim: int = 3,
        hidden_dim: int = 64,
        components: int = 5,
    ) -> None:
        super().__init__()
        self.components = components
        self.backbone = nn.Sequential(
            nn.Linear(condition_dim, hidden_dim),
            nn.SiLU(),
            nn.Linear(hidden_dim, hidden_dim),
            nn.SiLU(),
        )
        self.output = nn.Linear(hidden_dim, components * 3)

    def forward(self, condition: Tensor) -> tuple[Tensor, Tensor, Tensor]:
        parameters = self.output(self.backbone(condition))
        logits, means, log_scales = parameters.chunk(3, dim=-1)
        return logits, means, log_scales.clamp(min=-5.0, max=2.5)

    def negative_log_likelihood(self, condition: Tensor, log_duration: Tensor) -> Tensor:
        logits, means, log_scales = self(condition)
        target = log_duration.unsqueeze(-1)
        inverse_scale = torch.exp(-log_scales)
        component_log_probability = (
            -0.5 * ((target - means) * inverse_scale).square()
            - log_scales
            - 0.5 * math.log(2.0 * math.pi)
        )
        mixture_log_probability = torch.logsumexp(
            functional.log_softmax(logits, dim=-1) + component_log_probability,
            dim=-1,
        )
        return -mixture_log_probability.mean()

    @torch.no_grad()
    def sample(
        self,
        condition: Tensor,
        *,
        generator: torch.Generator | None = None,
        minimum_ms: float = 20.0,
        maximum_ms: float = 1_000.0,
    ) -> Tensor:
        logits, means, log_scales = self(condition)
        component = torch.multinomial(
            functional.softmax(logits, dim=-1),
            num_samples=1,
            generator=generator,
        )
        selected_mean = means.gather(-1, component).squeeze(-1)
        selected_scale = log_scales.exp().gather(-1, component).squeeze(-1)
        noise = torch.randn(
            selected_mean.shape,
            dtype=selected_mean.dtype,
            device=selected_mean.device,
            generator=generator,
        )
        duration = torch.exp(selected_mean + selected_scale * noise)
        return duration.clamp(min=minimum_ms, max=maximum_ms)
