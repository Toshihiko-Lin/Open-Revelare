#!/usr/bin/env python3
"""Package an already-built, platform-specific engine/model runtime for installation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
import zipfile

REVISION = "cbcc9fc825ba9aaf12304c7e9524643995faf4dd"
MODEL_SHA = "2538547d4e7ce5d0b69f53cd55418b2c9bce6b18e4ca4363383313c722415dc5"


def pack(rid: str, runtime: Path, native: Path, output: Path):
    model_format = "coreml" if rid.startswith("osx-") else "onnx"
    executable = "image-codec" if model_format == "coreml" else "pureneg-model-host" + (".exe" if rid.startswith("win-") else "")
    if not (runtime / executable).is_file() or not native.is_file():
        raise ValueError("Build the inference host and native engine before packaging")
    if model_format == "onnx" and hashlib.sha256((runtime / "FibreNet.onnx").read_bytes()).hexdigest() != MODEL_SHA:
        raise ValueError("The ONNX model does not match the verified source conversion")
    if model_format == "coreml" and not (runtime / "FibreNet.mlmodelc").is_dir():
        raise ValueError("Compile FibreNet.mlpackage with coremlcompiler first")
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"pureneg-runtime-{rid}.zip"
    with tempfile.TemporaryDirectory(prefix="pureneg-runtime-pack-") as temp:
        stage = Path(temp)
        shutil.copytree(runtime, stage / "runtime", ignore=shutil.ignore_patterns("*.pdb", "*.xml"))
        if model_format == "onnx":
            packages = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
            notices = stage / "runtime/licenses"
            notices.mkdir(exist_ok=True)
            for package in packages.glob("microsoft.ml.onnxruntime*"):
                for name in ("LICENSE", "ThirdPartyNotices.txt"):
                    source = package / "1.20.1" / name
                    if source.is_file(): shutil.copyfile(source, notices / (package.name + "-" + name))
        addon = stage / "vendor/film-dust/native/build/filmdust.node"
        addon.parent.mkdir(parents=True)
        shutil.copyfile(native, addon)
        (stage / "runtime/component.json").write_text(json.dumps({
            "revision": REVISION, "platform": rid, "modelFormat": model_format,
            "modelSha256": MODEL_SHA if model_format == "onnx" else None,
            "sourceWeightsSha256": "130c1b428eaad69e4ea86684148ebda24ca97d5147026ae2a526f95b95f7868a",
        }, indent=2) + "\n", encoding="utf8")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zip_file:
            for file in sorted(stage.rglob("*")):
                if file.is_file(): zip_file.write(file, file.relative_to(stage).as_posix())
    entry = {"file": archive.name, "size": archive.stat().st_size,
             "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "modelFormat": model_format}
    (output / f"{rid}.json").write_text(json.dumps({"revision": REVISION, "platforms": {rid: entry}}, indent=2) + "\n", encoding="utf8")
    return entry


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"])
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--native", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(pack(args.rid, args.runtime, args.native, args.output)))
