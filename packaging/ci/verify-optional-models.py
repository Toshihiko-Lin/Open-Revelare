#!/usr/bin/env python3
"""Verify the optional-model contract in a publish or assembled app payload."""
import argparse
from pathlib import Path


def verify(payload: Path, rid: str) -> None:
    if not payload.is_dir():
        raise ValueError(f"Missing application payload: {payload}")
    bundled = [p for p in payload.rglob("*") if p.is_file() and p.name.lower() == "net_awb.onnx"]
    if bundled:
        raise ValueError(f"Optional Deep-WB weights must not be bundled: {bundled[0]}")
    for relative in ("models/README.md", "models/DeepWB-NOTICE.txt",
                     "Microsoft.ML.OnnxRuntime.dll", "OpenRevelare.DeepWb.Onnx.dll"):
        path = payload / relative
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f"Missing optional-model documentation or runtime: {path}")
    native = ("onnxruntime.dll" if rid.startswith("win-") else
              "libonnxruntime.dylib" if rid.startswith("osx-") else "libonnxruntime.so")
    candidates = (payload / native, payload / "runtimes" / rid / "native" / native)
    if not any(p.is_file() and p.stat().st_size > 0 for p in candidates):
        raise ValueError(f"Missing ONNX native runtime for {rid} in {payload}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("payload", type=Path)
    parser.add_argument("--rid", required=True,
                        choices=("win-x64", "linux-x64", "osx-arm64", "osx-x64"))
    args = parser.parse_args()
    try:
        verify(args.payload, args.rid)
    except ValueError as error:
        parser.exit(1, f"Optional-model packaging check failed: {error}\n")
    print(f"Optional-model packaging OK ({args.rid}): {args.payload}")
