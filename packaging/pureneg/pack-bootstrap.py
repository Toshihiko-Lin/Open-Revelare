#!/usr/bin/env python3
"""Build the pinned, shared engine archive and optionally fetch verified Node archives.

Upload these assets beside components.json on BOTH GitHub and Gitee releases.
The installer then needs neither raw GitHub nor nodejs.org access.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

NODE_VERSION = "v24.14.1"
NODES = {
    "win-x64": "6e50ce5498c0cebc20fd39ab3ff5df836ed2f8a31aa093cecad8497cff126d70",
    "darwin-x64": "2526230ad7d922be82d4fdb1e7ee1e84303e133e3b4b0ec4c2897ab31de0253d",
    "darwin-arm64": "25495ff85bd89e2d8a24d88566d7e2f827c6b0d3d872b2cebf75371f93fcb1fe",
    "linux-x64": "ace9fa104992ed0829642629c46ca7bd7fd6e76278cb96c958c4b387d29658ea",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Pinned PURENeg source tree")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--include-node", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((Path(__file__).resolve().parents[2] /
        "src/OpenRevelare.Gui/Assets/pureneg-files.json").read_text())
    with zipfile.ZipFile(args.output / "engine-sources.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for name, expected in sorted(manifest.items()):
            data = (args.source / name).read_bytes()
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError("Source hash mismatch: " + name)
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.create_system = 0  # Match the published archive on every build host.
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)
    if hashlib.sha256((args.output / "engine-sources.zip").read_bytes()).hexdigest() != \
            "d98b36439a111089b12d6ac311b9d730df1469ced800d996e3582485fa0d57da":
        raise ValueError("Engine archive differs from the installer pin; do not publish it")
    print("Verified engine-sources.zip", flush=True)
    if not args.include_node:
        return

    def download(item):
        platform, expected = item
        name = f"node-{NODE_VERSION}-{platform}" + (".zip" if platform.startswith("win-") else ".tar.gz")
        destination = args.output / name
        if not destination.exists():
            with urllib.request.urlopen(f"https://nodejs.org/dist/{NODE_VERSION}/{name}", timeout=60) as response:
                data = response.read(80 * 1024 * 1024 + 1)
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError("Node hash mismatch: " + name)
            destination.write_bytes(data)
        if hashlib.sha256(destination.read_bytes()).hexdigest() != expected:
            raise ValueError("Node hash mismatch: " + name)
        print("Verified " + name, flush=True)

    with ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(download, NODES.items()))


if __name__ == "__main__":
    main()
