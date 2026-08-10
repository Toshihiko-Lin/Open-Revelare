"""
Is "input primaries = sRGB" actually a good assumption for this pipeline?

The pipeline never declares what colour space its decoded negative is in. Nothing converts it,
so the density inversion implicitly treats the sensor's own primaries as sRGB's. chroma_grade
was then added downstream to fix the colour that assumption gets wrong.

DiVERE does not have a chroma_grade. It SOLVES for the input primaries instead, jointly with
gamma and dmax, against a ColorChecker — so its matrix and its density parameters are consistent
by construction (see divere/utils/ccm_optimizer). Its optimiser starts from sRGB's primaries but
treats them as free.

This script asks the narrow question that decides whether that is worth copying: fit the same
model to DiVERE's Kodak Gold 200 dataset and see how far the best-fit primaries land from sRGB.
Close → the assumption is fine and chroma_grade is fixing something else. Far → the fixed sRGB
assumption is the root cause, and the fix belongs at the input, not in a downstream scalar.

Usage:  python3 solve_input_primaries.py [path/to/DiVERE]
"""
import json
import os
import sys

import numpy as np
from scipy.optimize import minimize

DIVERE = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DIVERE_ROOT", "../../../DiVERE")

SRGB_PRIMARIES = np.array([[0.64, 0.33], [0.30, 0.60], [0.15, 0.06]])
D65 = np.array([0.3127, 0.3290])


def npm(prim, wp):
    """Primaries + white point (CIE xy) -> RGB->XYZ matrix."""
    xyz = np.array([[x / y, 1.0, (1 - x - y) / y] for x, y in prim]).T
    w = np.array([wp[0] / wp[1], 1.0, (1 - wp[0] - wp[1]) / wp[1]])
    return xyz * np.linalg.solve(xyz, w)


def load_patches():
    """
    The Gold 200 negative densities, and the scene reference they should reproduce.

    The reference file stores XYZ, not RGB — its own description says so. Comparing the pipeline's
    RGB output against raw XYZ numbers is meaningless, so it is converted to linear sRGB here.
    (Getting this wrong once made the target look like 0.61 saturation when it is really 0.81.)
    """
    neg = json.load(open(
        f"{DIVERE}/config/colorchecker/"
        "kodak_gold_200_kodak_endura_premier_d60_cc24data.json"))["data"]
    ref = json.load(open(
        f"{DIVERE}/config/colorchecker/original_color_cc24data.json"))["data"]
    keys = [k for k in neg if k in ref]
    ref_xyz = np.array([ref[k] for k in keys], float)
    ref_rgb = ref_xyz @ np.linalg.inv(npm(SRGB_PRIMARIES, D65)).T
    return np.array([neg[k] for k in keys], float), ref_rgb, keys


def render(neg, prim, gamma, dmax, gains):
    """
    The pipeline, reduced to the parts that matter here: interpret the negative in a candidate
    input space, take it to density, invert, come back to linear.
    """
    to_srgb = np.linalg.inv(npm(SRGB_PRIMARIES, D65)) @ npm(prim, D65)
    lin = np.clip(neg @ to_srgb.T, 1e-6, None)

    density = -np.log10(lin)
    pivot = density.mean()
    adj = pivot + (density - pivot) * gamma - dmax
    out = 10.0 ** adj
    return out * gains


def saturation(a):
    mx, mn = a.max(1), a.min(1)
    return np.where(mx > 1e-9, (mx - mn) / np.maximum(mx, 1e-9), 0.0)


def main():
    neg, ref, keys = load_patches()
    # Compare in a scale-free way: both sides normalised to their own mean, since exposure is
    # not what is being solved for here.
    ref_n = ref / ref.mean()

    def loss(p, free_primaries):
        prim = p[:6].reshape(3, 2) if free_primaries else SRGB_PRIMARIES
        gamma, dmax = p[-5], p[-4]
        gains = np.array([p[-3], p[-2], p[-1]])
        try:
            out = render(neg, prim, gamma, dmax, gains)
        except np.linalg.LinAlgError:
            return 1e6
        if not np.all(np.isfinite(out)):
            return 1e6
        out_n = out / max(out.mean(), 1e-9)
        return float(np.mean((out_n - ref_n) ** 2))

    tail0 = [0.6, 2.0, 1.0, 1.0, 1.0]
    tail_bounds = [(0.2, 3.0), (0.0, 4.0), (0.2, 5.0), (0.2, 5.0), (0.2, 5.0)]

    fixed = minimize(loss, np.array(tail0), args=(False,),
                     bounds=tail_bounds, method="L-BFGS-B")

    x0 = np.concatenate([SRGB_PRIMARIES.ravel(), tail0])
    bounds = [(0.0, 1.0)] * 6 + tail_bounds
    free = minimize(loss, x0, args=(True,), bounds=bounds, method="L-BFGS-B")

    print(f"patches: {len(keys)}\n")
    print(f"input primaries FIXED at sRGB : MSE {fixed.fun:.6f}")
    print(f"input primaries SOLVED        : MSE {free.fun:.6f}")
    if fixed.fun > 0:
        print(f"improvement                   : {(1 - free.fun / fixed.fun) * 100:.1f}%")

    prim = free.x[:6].reshape(3, 2)
    print("\nbest-fit primaries vs sRGB (CIE xy):")
    for name, got, want in zip("RGB", prim, SRGB_PRIMARIES):
        d = np.hypot(*(got - want))
        print(f"  {name}: ({got[0]:.4f}, {got[1]:.4f})   sRGB ({want[0]:.4f}, {want[1]:.4f})"
              f"   distance {d:.4f}")

    # What the two fits do to saturation is the quantity chroma_grade was invented to fix.
    for label, res, fp in (("fixed sRGB", fixed, False), ("solved", free, True)):
        p = res.x
        pr = p[:6].reshape(3, 2) if fp else SRGB_PRIMARIES
        out = render(neg, pr, p[-5], p[-4], np.array([p[-3], p[-2], p[-1]]))
        chromatic = [i for i, k in enumerate(keys) if not k.startswith("D")]
        print(f"\n{label}: mean saturation {saturation(out[chromatic]).mean():.4f} "
              f"(reference {saturation(ref[chromatic]).mean():.4f})")


if __name__ == "__main__":
    main()
