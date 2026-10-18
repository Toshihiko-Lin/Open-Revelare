"""Check cached/built native payloads before tests or packaging consume them."""
import hashlib
import json
from pathlib import Path
import sys

rid = sys.argv[1]
root = Path(__file__).resolve().parents[2] / "native" / rid
suffix = ".dll" if rid.startswith("win-") else ".dylib" if rid.startswith("osx-") else ".so"
prefix = "" if rid.startswith("win-") else "lib"
required = [prefix + "OpenRevelare.Raw.Native" + suffix,
            prefix + "OpenRevelare.Heif.Native" + suffix,
            "heif-licenses/libheif-LGPL-3.0.txt", "heif-licenses/x265-GPL-2.0.txt"]
if not rid.startswith("linux-"):
    required.append(prefix + "OpenRevelare.Compute.Native" + suffix)
lcms = json.loads((root / "lcms2.manifest.json").read_text(encoding="utf-8-sig"))
heif = json.loads((root / "heif.manifest.json").read_text(encoding="utf-8-sig"))
assert lcms["rid"] == heif["rid"] == rid, "Native manifest target mismatch"
for name in required:
    assert (root / name).is_file() and (root / name).stat().st_size > 0, f"Missing native output: {name}"
for artifact in lcms["artifacts"]:
    actual = hashlib.sha256((root / artifact["file"]).read_bytes()).hexdigest()
    assert actual.lower() == artifact["sha256"].lower(), "Little CMS hash mismatch"
actual = hashlib.sha256((root / (prefix + "OpenRevelare.Heif.Native" + suffix)).read_bytes()).hexdigest()
assert actual.lower() == heif["sha256"].lower(), "HEIC encoder hash mismatch"
print(f"Verified native dependencies: {rid}")
