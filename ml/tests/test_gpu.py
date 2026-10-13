from __future__ import annotations

import pytest
import torch

from mousemotion.models.diffusion import GaussianDiffusion1D
from mousemotion.models.unet1d import ConditionalUNet1D


@pytest.mark.skipif(not torch.cuda.is_available(), reason="CUDA is not available")
def test_default_model_forward_backward_on_cuda() -> None:
    device = torch.device("cuda")
    model = ConditionalUNet1D(channels=(64, 128, 256), embedding_dim=256).to(device)
    diffusion = GaussianDiffusion1D(model, steps=1_000).to(device)
    clean = torch.randn(1, 2, 1_024, device=device)
    condition = torch.randn(1, 4, device=device)
    mask = torch.ones(1, 1_024, device=device)

    loss = diffusion.training_loss(clean, condition, mask)
    loss.backward()
    torch.cuda.synchronize()

    assert torch.isfinite(loss)
    assert any(parameter.grad is not None for parameter in model.parameters())
