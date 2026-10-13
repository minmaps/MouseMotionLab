from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from mousemotion.contracts import TrajectoryRequest
from mousemotion.evaluation import evaluate_artifact
from mousemotion.generation import TrajectoryGenerator
from mousemotion.preprocess import PreprocessConfig, prepare_database
from mousemotion.training import TrainConfig, train_pipeline


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="mousemotion", description="MouseMotionLab ML tools")
    subcommands = parser.add_subparsers(dest="command", required=True)

    prepare = subcommands.add_parser("prepare", help="Prepare a SQLite capture dataset")
    prepare.add_argument("--db", required=True, type=Path)
    prepare.add_argument("--output", required=True, type=Path)
    prepare.add_argument("--seed", type=int, default=42)
    prepare.add_argument("--max-length", type=int, default=1_024)
    prepare.add_argument("--min-duration-ms", type=float, default=20.0)
    prepare.add_argument("--max-duration-ms", type=float, default=1_000.0)
    prepare.add_argument("--min-distance-mm", type=float, default=0.5)

    train = subcommands.add_parser("train", help="Train duration and diffusion models")
    train.add_argument("--prepared", required=True, type=Path)
    train.add_argument("--artifact", required=True, type=Path)
    train.add_argument("--seed", type=int, default=42)
    train.add_argument("--epochs", type=int, default=50)
    train.add_argument("--duration-epochs", type=int, default=100)
    train.add_argument("--batch-size", type=int, default=32)
    train.add_argument("--device", default="auto")

    generate = subcommands.add_parser("generate", help="Generate one trajectory")
    generate.add_argument("--dx", required=True, type=int)
    generate.add_argument("--dy", required=True, type=int)
    generate.add_argument("--dpi", required=True, type=int)
    generate.add_argument("--output-hz", type=int, default=1_000)
    generate.add_argument("--seed", type=int, default=0)
    generate.add_argument("--mode", choices=("baseline", "diffusion"), default="diffusion")
    generate.add_argument("--artifact", type=Path)
    generate.add_argument("--device", default="auto")
    generate.add_argument("--output", type=Path)
    generate.add_argument("--json-stdout", action="store_true")

    evaluate = subcommands.add_parser("evaluate", help="Evaluate an artifact on held-out data")
    evaluate.add_argument("--prepared", required=True, type=Path)
    evaluate.add_argument("--artifact", required=True, type=Path)
    evaluate.add_argument("--output", required=True, type=Path)
    evaluate.add_argument("--samples", type=int, default=128)
    evaluate.add_argument("--seed", type=int, default=42)
    evaluate.add_argument("--device", default="auto")
    return parser


def run(arguments: argparse.Namespace) -> int:
    if arguments.command == "prepare":
        metadata = prepare_database(
            arguments.db,
            arguments.output,
            config=PreprocessConfig(
                max_length=arguments.max_length,
                min_duration_ms=arguments.min_duration_ms,
                max_duration_ms=arguments.max_duration_ms,
                min_distance_mm=arguments.min_distance_mm,
                split_seed=arguments.seed,
            ),
        )
        print(json.dumps(metadata, indent=2, sort_keys=True))
        return 0
    if arguments.command == "train":
        metadata = train_pipeline(
            arguments.prepared,
            arguments.artifact,
            config=TrainConfig(
                seed=arguments.seed,
                epochs=arguments.epochs,
                duration_epochs=arguments.duration_epochs,
                batch_size=arguments.batch_size,
                device=arguments.device,
            ),
        )
        print(json.dumps(metadata, indent=2, sort_keys=True, default=list))
        return 0
    if arguments.command == "generate":
        request = TrajectoryRequest(
            dx_counts=arguments.dx,
            dy_counts=arguments.dy,
            dpi=arguments.dpi,
            output_hz=arguments.output_hz,
            seed=arguments.seed,
            mode=arguments.mode,
        )
        generator = TrajectoryGenerator(arguments.artifact, device=arguments.device)
        result = generator.generate(request)
        document = result.to_json(indent=None if arguments.json_stdout else 2)
        if arguments.output:
            arguments.output.parent.mkdir(parents=True, exist_ok=True)
            arguments.output.write_text(document + "\n", encoding="utf-8")
        if arguments.json_stdout or not arguments.output:
            print(document)
        return 0
    if arguments.command == "evaluate":
        report = evaluate_artifact(
            arguments.prepared,
            arguments.artifact,
            arguments.output,
            sample_count=arguments.samples,
            seed=arguments.seed,
            device=arguments.device,
        )
        print(json.dumps(report, indent=2, sort_keys=True))
        return 0
    raise AssertionError(f"Unhandled command: {arguments.command}")


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    arguments = parser.parse_args(argv)
    try:
        return run(arguments)
    except Exception as exception:  # noqa: BLE001 - CLI boundary
        print(f"mousemotion: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
