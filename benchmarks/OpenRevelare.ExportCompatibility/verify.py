"""Independent desktop decoding, not evidence of phone gallery acceptance.

Requires Pillow, numpy, tifffile, imagecodecs. Optional --ultrahdr points to
Google's independently built ultrahdr_app (not linked into OpenRevelare).
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tempfile

import numpy as np
from PIL import Image, ImageCms
import tifffile

parser = argparse.ArgumentParser()
parser.add_argument("directory", type=Path)
parser.add_argument("--ultrahdr", type=Path)
args = parser.parse_args()
results = []
for entry in json.loads((args.directory / "manifest.json").read_text()):
    path = args.directory / entry["Name"]
    assert hashlib.sha256(path.read_bytes()).hexdigest().upper() == entry["Sha256"]
    result = {"name": path.name}
    if path.suffix == ".jpg":
        with Image.open(path) as image:
            image.load()
            assert image.size == (entry["Width"], entry["Height"])
            assert not image.info.get("progressive", False)
            profile = ImageCms.ImageCmsProfile(io.BytesIO(image.info["icc_profile"]))
            ImageCms.profileToProfile(image, profile, ImageCms.createProfile("sRGB")).load()
        result["pillow_sdr_and_lcms"] = "pass"
        if "-hdr" in path.stem and args.ultrahdr:
            # Disable a namespace in-place so MPF lengths/offsets remain unchanged.
            # Exercise ISO and Adobe XMP independently, as different OS versions do.
            namespaces = {"ISO-only": b"http://ns.adobe.com/xap/1.0/\0",
                          "XMP-only": b"urn:iso:std:iso:ts:21496:-1\0"}
            for mode, namespace in {"dual": None, **namespaces}.items():
                data = path.read_bytes()
                if namespace:
                    assert data.count(namespace) == 2
                    data = data.replace(namespace, b"x" + namespace[1:])
                with tempfile.TemporaryDirectory(prefix="revelare-uhdr-") as temporary:
                    source, output = Path(temporary) / "in.jpg", Path(temporary) / "out.raw"
                    source.write_bytes(data)
                    process = subprocess.run([str(args.ultrahdr.resolve()), "-m", "1", "-j", str(source),
                                              "-o", "0", "-O", "4", "-z", str(output)],
                                             capture_output=True, text=True, timeout=120)
                    if process.returncode:
                        raise RuntimeError(f"{path.name} {mode}: {process.stdout} {process.stderr}")
                    pixels = np.fromfile(output, dtype="<f2").reshape(entry["Height"], entry["Width"], 4)
                    assert np.isfinite(pixels).all()
                    assert float(pixels[-8, -8, :3].min()) > 2.5, "HDR highlight was lost"
                    result[mode] = {"decode": "pass", "highlight_min": float(pixels[-8, -8, :3].min())}
    else:
        with tifffile.TiffFile(path) as tiff:
            if path.suffix == ".dng":
                preview = tiff.pages[0]
                main = preview.pages[0]
                for page, value in ((preview, 1), (main, 0)):
                    assert page.tags[254].dtype == 4 and page.tags[254].value == value
                pixels = main.asarray()
                assert pixels.shape == (entry["Height"], entry["Width"], 3)
                assert pixels.dtype == np.dtype("uint16")
                assert main.photometric == 34892
                result["tifffile_full_resolution_raw"] = "pass"
                result["preview_size"] = [preview.imagewidth, preview.imagelength]
            else:
                pixels = tiff.pages[0].asarray()
                assert pixels.shape == (entry["Height"], entry["Width"], 3)
                assert hashlib.sha256(pixels.tobytes()).hexdigest().upper() == entry["PixelSha256"]
                result["tifffile_exact_pixels"] = "pass"
                if pixels.dtype == np.dtype("uint16"):
                    with Image.open(path) as image:
                        image.load()
                    result["pillow_decode"] = "pass"
                else:
                    assert pixels.max() > 1
                    result["extended_values_preserved"] = True
    results.append(result)
print(json.dumps(results, indent=2))
