from __future__ import annotations

import math

import torch
from torch import Tensor, nn
from torch.nn import functional as functional


class SinusoidalTimeEmbedding(nn.Module):
    def __init__(self, dimension: int) -> None:
        super().__init__()
        if dimension < 4 or dimension % 2:
            raise ValueError("Time embedding dimension must be even and at least four.")
        self.dimension = dimension

    def forward(self, timesteps: Tensor) -> Tensor:
        half = self.dimension // 2
        frequency = torch.exp(
            torch.arange(half, device=timesteps.device, dtype=torch.float32)
            * (-math.log(10_000.0) / max(half - 1, 1))
        )
        angles = timesteps.float().unsqueeze(1) * frequency.unsqueeze(0)
        return torch.cat((angles.sin(), angles.cos()), dim=1)


class ResidualBlock1D(nn.Module):
    def __init__(self, input_channels: int, output_channels: int, embedding_dim: int) -> None:
        super().__init__()
        self.normalization1 = nn.GroupNorm(8, input_channels)
        self.convolution1 = nn.Conv1d(input_channels, output_channels, kernel_size=3, padding=1)
        self.normalization2 = nn.GroupNorm(8, output_channels)
        self.convolution2 = nn.Conv1d(output_channels, output_channels, kernel_size=3, padding=1)
        self.embedding_projection = nn.Linear(embedding_dim, output_channels * 2)
        self.skip = (
            nn.Identity()
            if input_channels == output_channels
            else nn.Conv1d(input_channels, output_channels, kernel_size=1)
        )

    def forward(self, values: Tensor, embedding: Tensor) -> Tensor:
        hidden = self.convolution1(functional.silu(self.normalization1(values)))
        scale, shift = self.embedding_projection(functional.silu(embedding)).chunk(2, dim=1)
        hidden = self.normalization2(hidden)
        hidden = hidden * (1.0 + scale.unsqueeze(-1)) + shift.unsqueeze(-1)
        hidden = self.convolution2(functional.silu(hidden))
        return hidden + self.skip(values)


class ConditionalUNet1D(nn.Module):
    def __init__(
        self,
        *,
        condition_dim: int = 4,
        channels: tuple[int, int, int] = (64, 128, 256),
        embedding_dim: int = 256,
    ) -> None:
        super().__init__()
        first, second, third = channels
        if any(channel % 8 for channel in channels):
            raise ValueError("All U-Net channels must be divisible by eight.")
        self.channels = channels
        self.time_embedding = nn.Sequential(
            SinusoidalTimeEmbedding(embedding_dim),
            nn.Linear(embedding_dim, embedding_dim),
            nn.SiLU(),
            nn.Linear(embedding_dim, embedding_dim),
        )
        self.condition_embedding = nn.Sequential(
            nn.Linear(condition_dim, embedding_dim),
            nn.SiLU(),
            nn.Linear(embedding_dim, embedding_dim),
        )
        self.input_convolution = nn.Conv1d(3, first, kernel_size=3, padding=1)
        self.down0 = nn.ModuleList(
            [
                ResidualBlock1D(first, first, embedding_dim),
                ResidualBlock1D(first, first, embedding_dim),
            ]
        )
        self.downsample0 = nn.Conv1d(first, second, kernel_size=4, stride=2, padding=1)
        self.down1 = nn.ModuleList(
            [
                ResidualBlock1D(second, second, embedding_dim),
                ResidualBlock1D(second, second, embedding_dim),
            ]
        )
        self.downsample1 = nn.Conv1d(second, third, kernel_size=4, stride=2, padding=1)
        self.middle = nn.ModuleList(
            [
                ResidualBlock1D(third, third, embedding_dim),
                ResidualBlock1D(third, third, embedding_dim),
            ]
        )
        self.upsample1 = nn.ConvTranspose1d(third, second, kernel_size=4, stride=2, padding=1)
        self.up1 = nn.ModuleList(
            [
                ResidualBlock1D(second * 2, second, embedding_dim),
                ResidualBlock1D(second, second, embedding_dim),
            ]
        )
        self.upsample0 = nn.ConvTranspose1d(second, first, kernel_size=4, stride=2, padding=1)
        self.up0 = nn.ModuleList(
            [
                ResidualBlock1D(first * 2, first, embedding_dim),
                ResidualBlock1D(first, first, embedding_dim),
            ]
        )
        self.output = nn.Sequential(
            nn.GroupNorm(8, first),
            nn.SiLU(),
            nn.Conv1d(first, 2, kernel_size=3, padding=1),
        )

    def forward(self, values: Tensor, timesteps: Tensor, condition: Tensor, mask: Tensor) -> Tensor:
        if values.ndim != 3 or values.shape[1] != 2:
            raise ValueError("values must have shape (batch, 2, length).")
        if mask.ndim != 2 or mask.shape != (values.shape[0], values.shape[2]):
            raise ValueError("mask must have shape (batch, length).")
        embedding = self.time_embedding(timesteps) + self.condition_embedding(condition)
        mask_channel = mask.unsqueeze(1).to(dtype=values.dtype)
        hidden = self.input_convolution(torch.cat((values * mask_channel, mask_channel), dim=1))
        for block in self.down0:
            hidden = block(hidden, embedding)
        skip0 = hidden
        hidden = self.downsample0(hidden)
        for block in self.down1:
            hidden = block(hidden, embedding)
        skip1 = hidden
        hidden = self.downsample1(hidden)
        for block in self.middle:
            hidden = block(hidden, embedding)
        hidden = self.upsample1(hidden)
        if hidden.shape[-1] != skip1.shape[-1]:
            hidden = functional.interpolate(hidden, size=skip1.shape[-1], mode="nearest")
        hidden = torch.cat((hidden, skip1), dim=1)
        for block in self.up1:
            hidden = block(hidden, embedding)
        hidden = self.upsample0(hidden)
        if hidden.shape[-1] != skip0.shape[-1]:
            hidden = functional.interpolate(hidden, size=skip0.shape[-1], mode="nearest")
        hidden = torch.cat((hidden, skip0), dim=1)
        for block in self.up0:
            hidden = block(hidden, embedding)
        return self.output(hidden) * mask_channel
