"""Manual hardware acceptance: decode real 10-bit output and report quality."""
import ctypes as c
import json
import math
from pathlib import Path
import platform
import time

import numpy as np
import pillow_heif

rid = {"Windows": "win", "Darwin": "osx", "Linux": "linux"}[platform.system()] + (
    "-arm64" if platform.machine().lower() in ("arm64", "aarch64") else "-x64")
suffix = ".dll" if rid.startswith("win") else ".dylib" if rid.startswith("osx") else ".so"
prefix = "" if rid.startswith("win") else "lib"
library = c.CDLL(str(Path("native", rid, prefix + "OpenRevelare.Heif.Native" + suffix).resolve()))
encode = library.or_heif_encode_exif_accelerated
encode.argtypes = [c.c_void_p, c.c_int, c.c_int, c.c_int, c.c_int, c.c_void_p, c.c_int,
                   c.c_void_p, c.c_int, c.c_void_p, c.c_int, c.c_int, c.POINTER(c.c_int),
                   c.POINTER(c.c_void_p), c.POINTER(c.c_size_t), c.c_void_p, c.c_int]
library.or_heif_free.argtypes = [c.c_void_p]

width, height = 1536, 1024
x = np.linspace(0, 1, width, dtype=np.float32)[None, :]
y = np.linspace(0, 1, height, dtype=np.float32)[:, None]
noise = np.random.default_rng(42).normal(0, .02, (height, width)).astype(np.float32)
gray = np.clip(.05 + .85 * (.4 * x + .6 * y) + noise, 0, 1)
color = np.stack(np.broadcast_arrays(.1 + .7 * x + noise, .1 + .7 * y + noise,
                                    .2 + .3 * np.sin(x * 9) * np.cos(y * 7) + noise), axis=-1)
results = []
for name, values in [("neutral-texture", np.repeat(gray[:, :, None], 3, axis=2)), ("color-texture", color)]:
    pixels = np.ascontiguousarray(np.clip(values * 1023, 0, 1023).round(), dtype=np.uint16)
    for hdr in (0, 1):
        for allow in (0, 1):
            output, length, used = c.c_void_p(), c.c_size_t(), c.c_int()
            error = c.create_string_buffer(1024)
            try:
                start = time.perf_counter()
                status = encode(pixels.ctypes.data, width, height, 90, hdr, None, 0, None, 0, None, 0,
                                allow, c.byref(used), c.byref(output), c.byref(length), error, len(error))
                elapsed = time.perf_counter() - start
                assert status == 0, error.value.decode(errors="replace")
                image = pillow_heif.open_heif(c.string_at(output, length.value), convert_hdr_to_8bit=False)
                decoded = np.asarray(image)
                assert image.size == (width, height) and decoded.dtype == np.uint16, "Expected full-size 10-bit decode"
                # pillow-heif expands 10-bit samples to the high bits of uint16.
                decoded = decoded.astype(np.float64) / 64
                mse = float(np.mean((decoded - pixels) ** 2))
                result = dict(rid=rid, sample=name, hdr=bool(hdr), hardware_allowed=bool(allow), hardware_used=bool(used.value),
                              quality=90, seconds=elapsed, bytes=length.value,
                              psnr_db=10 * math.log10(1023 ** 2 / max(mse, 1e-20)),
                              fallback=error.value.decode(errors="replace"))
                assert np.isfinite(decoded).all()
                results.append(result)
                print(json.dumps(result), flush=True)
            finally:
                if output:
                    library.or_heif_free(output)
Path("heic-quality.json").write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
