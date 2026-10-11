"""Reproduce curve-order findings from a pinned DARKTABLE-DIVERE revision.

Standard library only. Downloads two source files, checks the processing source
hash, and replays its interpolation formula. This is not a darktable runtime or
an image-quality benchmark. Run from any directory; stdout is the JSON result.
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import urllib.request


REVISION = "ec6bcbf4fa0faec5bc6a03d83e3382be7c165586"
BASE = f"https://raw.githubusercontent.com/Kaixi2004/DARKTABLE-DIVERE/{REVISION}/"
PROCESS_SHA256 = "676a90cbaecde9721fdb943be446b0ca4c2081edbcef12133358e57c537baaa4"


def interp(points, x):
    """Formula replay of _interp_linear, including its endpoint order."""
    if not points:
        return x
    if x <= points[0][0]:
        return points[0][1]
    if x >= points[-1][0]:
        return points[-1][1]
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        if x <= x1:
            t = (x - x0) / (x1 - x0) if x1 > x0 else 0.0
            return y0 + t * (y1 - y0)
    return points[-1][1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path,
                        help="Offline checkout/cache containing src/iop/*.c and *.h")
    args = parser.parse_args()
    sources = {}
    for name in ("negadoctor.c", "negadoctor_curves.h"):
        relative = f"src/iop/{name}"
        sources[name] = ((args.source_dir / relative).read_bytes() if args.source_dir
                         else urllib.request.urlopen(BASE + relative, timeout=30).read())
    process_hash = hashlib.sha256(sources["negadoctor.c"]).hexdigest()
    if process_hash != PROCESS_SHA256:
        raise ValueError(f"Unexpected processing source SHA256: {process_hash}")

    header = sources["negadoctor_curves.h"].decode("utf-8")
    arrays = {}
    pattern = r"dt_negadoctor_curve_(\w+)\[\d+\]\s*=\s*\{(.*?)\};"
    for name, body in re.findall(pattern, header, flags=re.S):
        pairs = re.findall(r"\{\s*([^,{}]+),\s*([^,{}]+)\}", body)
        arrays[name] = [(float(x), float(y)) for x, y in pairs]
    if len(arrays) != 56:
        raise ValueError(f"Expected 56 arrays, found {len(arrays)}")
    invalid = [name for name, pts in arrays.items()
               if any(a[0] >= b[0] for a, b in zip(pts, pts[1:]))]
    assert len(invalid) == 7 and all("Ilford_MGFB" in n for n in invalid)

    points = arrays["Ilford_MGFB_2_rgb"]
    probes = []
    for x in (0.2, 0.4, 0.6, 0.8, 0.9):
        native = interp(points, x)
        sorted_value = interp(sorted(points), x)
        probes.append({"normalized_input": x, "native_y": native,
                       "sorted_y": sorted_value,
                       "native_linear_output": 10 ** (-(1 - native) * math.log10(65536)),
                       "sorted_linear_output": 10 ** (-(1 - sorted_value) * math.log10(65536))})
    result = {
        "revision": REVISION,
        "method": "source inspection and scalar formula replay; not full application execution",
        "source_sha256": {n: hashlib.sha256(b).hexdigest() for n, b in sources.items()},
        "curve_arrays": len(arrays),
        "non_ascending_arrays": invalid,
        "mgfb2_probes": probes,
        "status_m_matrix_row_sums": [sum(row) for row in (
            (1.0197, 0.0317, 0.0091), (-0.0052, 0.8933, 0.0521),
            (0.0131, -0.0011, 0.9712))],
        "linear_018_srgb_encoded": 1.055 * 0.18 ** (1 / 2.4) - 0.055,
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
