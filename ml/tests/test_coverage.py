from __future__ import annotations

import numpy as np

from mousemotion.coverage import CoverageModel, target_feature


def test_coverage_flags_far_target(tmp_path) -> None:
    features = np.stack(
        [target_feature(distance, angle) for distance in (10, 12, 14, 16) for angle in (0, 0.1)]
    )
    model = CoverageModel.fit(features, neighbors=2)

    in_score, in_ood, _ = model.score(target_feature(13, 0.05))
    out_score, out_ood, _ = model.score(target_feature(500, 3.0))

    assert not in_ood
    assert out_ood
    assert in_score > out_score
    path = tmp_path / "coverage.npz"
    model.save(path)
    restored = CoverageModel.load(path)
    assert restored.score(target_feature(500, 3.0))[1]
