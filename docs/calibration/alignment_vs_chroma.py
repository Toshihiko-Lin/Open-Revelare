"""
Does aligning the three channels restore chroma? No — and this shows why.

Sampling the film base to put it at black, and picking the brightest area as white, is a real
three-channel alignment with a physical basis. It is what t_base / wb_offset / wb_high do here,
and what "反相 + 三通道对齐" does in DaVinci or Photoshop. The question this settles is whether
that alignment is *sufficient*: if it were, chroma_grade would have nothing left to do.

It is not, and the reason is structural. t_base (divide), wb_offset (add) and wb_high (multiply)
are all PER-CHANNEL. A per-channel affine map can place the neutral axis wherever you like, but
saturation is a relation BETWEEN channels — and the freedom needed to move it is exactly the
freedom already spent holding the neutrals in place. The two goals compete for the same knobs.

Measured on DiVERE's Kodak Gold 200 dataset: solving the per-channel affine that makes the
neutral row as neutral as it can be moves mean chromatic saturation by essentially nothing
(0.7237 -> 0.7224), against a reference of 0.8056. About 11% of chroma is simply out of reach of
per-channel operations — it needs a cross-channel transform (a matrix, or per-hue compensation),
which is the job chroma_grade does, however crudely.

That also explains why the DaVinci/Photoshop route feels sufficient: it restores the NEUTRAL AXIS
(the visually obvious part), and the residual ~11% reads as "very slightly flat" rather than as
an error, unless measured against a chart.

Usage:  python3 alignment_vs_chroma.py [path/to/DiVERE]
"""
import json
import os
import sys

import numpy as np

DIVERE = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DIVERE_ROOT", "../../../DiVERE")

SRGB_PRIMARIES = np.array([[0.64, 0.33], [0.30, 0.60], [0.15, 0.06]])
D65 = np.array([0.3127, 0.3290])


def npm(prim, wp):
    xyz = np.array([[x / y, 1.0, (1 - x - y) / y] for x, y in prim]).T
    w = np.array([wp[0] / wp[1], 1.0, (1 - wp[0] - wp[1]) / wp[1]])
    return xyz * np.linalg.solve(xyz, w)


def saturation(a):
    a = np.clip(a, 1e-9, None)
    mx, mn = a.max(1), a.min(1)
    return (mx - mn) / mx


def main():
    neg = json.load(open(
        f"{DIVERE}/config/colorchecker/"
        "kodak_gold_200_kodak_endura_premier_d60_cc24data.json"))["data"]
    # The reference file stores XYZ (its own description says so), so convert to linear sRGB
    # before comparing saturation against an RGB pipeline.
    ref = json.load(open(
        f"{DIVERE}/config/colorchecker/original_color_cc24data.json"))["data"]

    keys = [k for k in neg if k in ref]
    n = np.array([neg[k] for k in keys], float)
    ref_rgb = np.array([ref[k] for k in keys], float) @ np.linalg.inv(npm(SRGB_PRIMARIES, D65)).T

    neutral = [i for i, k in enumerate(keys) if k.startswith("D")]      # the grey ramp
    chromatic = [i for i, k in enumerate(keys) if not k.startswith("D")]

    density = -np.log10(np.clip(n, 1e-6, None))

    # The BEST per-channel alignment there is: least-squares gain+offset per channel, fitted so
    # the neutral patches read the same in all three. This is t_base/wb_offset/wb_high taken to
    # their theoretical optimum — no sampling error, no human judgement.
    target = density[neutral].mean(1)
    aligned = density.copy()
    for c in range(3):
        A = np.vstack([density[neutral, c], np.ones(len(neutral))]).T
        gain, offset = np.linalg.lstsq(A, target, rcond=None)[0]
        aligned[:, c] = density[:, c] * gain + offset

    for label, d in (("before alignment", density),
                     ("after PERFECT per-channel alignment", aligned)):
        positive = 10.0 ** (-(d - d.max()))
        spread = np.abs(d[neutral] - d[neutral].mean(1, keepdims=True)).max()
        print(f"{label}:")
        print(f"   neutral-row max channel spread : {spread:.5f}")
        print(f"   chromatic mean saturation      : {saturation(positive[chromatic]).mean():.4f}")

    print(f"\nreference (linear sRGB)            : {saturation(ref_rgb[chromatic]).mean():.4f}")
    print("\nAlignment halves the neutral error and leaves saturation where it was.")
    print("Per-channel operations cannot reach it; that residual is what needs a")
    print("cross-channel transform.")


if __name__ == "__main__":
    main()
