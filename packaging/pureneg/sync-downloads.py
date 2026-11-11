#!/usr/bin/env python3
"""Publish validated PURENeg installation assets to both release mirrors.

Requires GITEE_TOKEN and GitHub Git credentials (or GH_TOKEN). Never logs credentials.
Existing assets are retained; mismatched names/sizes abort rather than replacing files.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import requests

REPO = "Toshihiko-Lin/openrevelare-models"
RELEASE = "pureneg-cbcc9fc825ba-v1"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    root = args.directory
    index = json.loads((root / "components.json").read_text())
    assets = [root / "components.json", root / "engine-sources.zip"]
    for entry in index["platforms"].values():
        file = root / entry["file"]
        if file.stat().st_size != entry["size"] or hashlib.sha256(file.read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError("Runtime integrity failure: " + file.name)
        assets.append(file)
    assets += sorted(root.glob("node-v24.14.1-*"))
    if len(assets) != 10:
        raise ValueError("Expected index, engine, four Node archives and four platform runtimes")
    token = os.environ.get("GH_TOKEN")
    if not token:
        result = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n",
            text=True, capture_output=True, check=True,
            env=dict(os.environ, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="Never"))
        token = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)["password"]
    github = requests.Session()
    github.headers["Authorization"] = "Bearer " + token
    gitee = requests.Session()
    gitee.headers["Authorization"] = "Bearer " + os.environ["GITEE_TOKEN"]

    def api(session, method, url, **kwargs):
        response = session.request(method, url, timeout=(15, 180), **kwargs)
        if not response.ok:
            raise RuntimeError(f"Release API failed: {method} {url} HTTP {response.status_code}")
        return response.json()

    gh_release = api(github, "GET", f"https://api.github.com/repos/{REPO}/releases/tags/{RELEASE}")
    for file in assets:
        existing = next((a for a in gh_release["assets"] if a["name"] == file.name), None)
        if existing:
            if existing["size"] != file.stat().st_size:
                raise ValueError("Existing GitHub asset differs: " + file.name)
            continue
        with file.open("rb") as data:
            asset = api(github, "POST", gh_release["upload_url"].split("{")[0],
                params={"name": file.name}, data=data, headers={"Content-Type": "application/octet-stream"})
        if asset.get("digest") and asset["digest"] != "sha256:" + hashlib.sha256(file.read_bytes()).hexdigest():
            raise ValueError("GitHub upload hash mismatch: " + file.name)
        print("GitHub uploaded " + file.name, flush=True)

    releases = api(gitee, "GET", f"https://gitee.com/api/v5/repos/{REPO}/releases", params={"per_page": 100})
    gt_release = next((r for r in releases if r["tag_name"] == RELEASE), None)
    if not gt_release:
        gt_release = api(gitee, "POST", f"https://gitee.com/api/v5/repos/{REPO}/releases", json={
            "name": "PURENeg platform components (prerelease)", "tag_name": RELEASE,
            "target_commitish": "master", "prerelease": True,
            "body": "Optional PURENeg components for OpenRevelare. Mirrors the matching GitHub prerelease, including Node, MIT engine sources, platform native engines and CoreML/ONNX models. See bundled licenses. This does not update the main application release.\nhttps://github.com/" + REPO + "/releases/tag/" + RELEASE,
        })
        print("Gitee component prerelease created", flush=True)
    for file in assets:
        existing = next((a for a in gt_release.get("assets", []) if a["name"] == file.name), None)
        if existing:
            print("Gitee retained " + file.name, flush=True)
            continue
        for attempt in range(3):
            try:
                with file.open("rb") as data:
                    api(gitee, "POST", f"https://gitee.com/api/v5/repos/{REPO}/releases/{gt_release['id']}/attach_files",
                        files={"file": (file.name, data, "application/octet-stream")})
                break
            except (requests.RequestException, RuntimeError):
                if attempt == 2:
                    raise
                time.sleep(1)
        print("Gitee uploaded " + file.name, flush=True)


if __name__ == "__main__":
    main()
