from __future__ import annotations

import torch

from mousemotion.models.diffusion import GaussianDiffusion1D, project_zero_sum
from mousemotion.models.unet1d import ConditionalUNet1D


def test_projection_is_zero_sum_and_masked() -> None:
    values = torch.randn(2, 2, 16)
    mask = torch.zeros(2, 16)
    mask[0, :8] = 1
    mask[1, :12] = 1

    projected = project_zero_sum(values, mask)

    torch.testing.assert_close(projected[0, :, :8].sum(dim=-1), torch.zeros(2), atol=1e-6, rtol=0)
    torch.testing.assert_close(projected[1, :, :12].sum(dim=-1), torch.zeros(2), atol=1e-6, rtol=0)
    assert torch.count_nonzero(projected[0, :, 8:]) == 0


def test_tiny_diffusion_sampling_is_seeded() -> None:
    model = ConditionalUNet1D(channels=(8, 16, 32), embedding_dim=32)
    diffusion = GaussianDiffusion1D(model, steps=20)
    condition = torch.zeros(1, 4)
    mask = torch.ones(1, 32)
    first_generator = torch.Generator().manual_seed(123)
    second_generator = torch.Generator().manual_seed(123)

    first = diffusion.sample(condition, mask, sampling_steps=4, generator=first_generator)
    second = diffusion.sample(condition, mask, sampling_steps=4, generator=second_generator)

    torch.testing.assert_close(first, second)
    torch.testing.assert_close(first.sum(dim=-1), torch.zeros(1, 2), atol=1e-5, rtol=0)


def test_tiny_diffusion_can_overfit_a_fixed_batch() -> None:
    torch.manual_seed(11)
    model = ConditionalUNet1D(channels=(8, 16, 32), embedding_dim=32)
    diffusion = GaussianDiffusion1D(model, steps=10)
    optimizer = torch.optim.Adam(model.parameters(), lr=3e-3)
    clean = project_zero_sum(torch.randn(2, 2, 32), torch.ones(2, 32))
    condition = torch.randn(2, 4)
    mask = torch.ones(2, 32)

    def fixed_loss() -> torch.Tensor:
        return diffusion.training_loss(
            clean,
            condition,
            mask,
            generator=torch.Generator().manual_seed(1234),
        )

    initial = float(fixed_loss().detach())
    for _ in range(30):
        optimizer.zero_grad(set_to_none=True)
        loss = fixed_loss()
        loss.backward()
        optimizer.step()
    final = float(fixed_loss().detach())

    assert final < initial * 0.5
