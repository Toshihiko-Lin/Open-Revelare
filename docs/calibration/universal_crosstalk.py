"""
Is there a UNIVERSAL C-41 crosstalk direction, shared by every stock?

This is the premise the whole design rests on. OpenRevelare deliberately does not calibrate per
roll — the goal is one fixed transform that recovers what the C-41 PROCESS loses, letting each
stock's own character come through as a difference rather than being normalised away. For that
to be possible, the chroma loss must decompose as:

    a shared DIRECTION (the process: three dye layers whose absorptions overlap)
      x a per-stock STRENGTH (the stock: how strongly its particular dyes do it)

If it does, the direction is the universal solution and the strength is style. If instead each
stock needs a structurally different matrix, no universal solution exists and chroma_grade's
whole premise is wrong.

The test: DiVERE ships modelled ColorChecker densities for six C-41 stocks (Gold 200, Portra
160/400/800, Ektar 100, Ultramax 400). Solve, per stock, the 3x3 that best maps its negative
densities onto the scene reference. Then ask whether those six matrices are the same shape.

Two things are checked, because either alone could mislead:
  1. Do the six matrices agree in direction, once each is normalised for overall strength?
  2. Does ONE shared matrix, with only a per-stock scalar allowed, fit nearly as well as six
     independent ones? If the shared fit is close, the extra freedom was not buying anything.

Usage:  python3 universal_crosstalk.py [path/to/DiVERE]
"""
import glob
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


def load_reference():
    """Scene reference, converted from the stored XYZ into linear sRGB."""
    ref = json.load(open(
        f"{DIVERE}/config/colorchecker/original_color_cc24data.json"))["data"]
    return ref, np.linalg.inv(npm(SRGB_PRIMARIES, D65)).T


def stock_name(path):
    n = os.path.basename(path)
    return n.replace("_kodak_endura_premier_d60_cc24data.json", "")


def solve_matrix(neg_density, ref_density):
    """
    Least-squares 3x3 taking negative density chroma to reference density chroma.

    Working in DENSITY, and on the CHROMA component (each patch's deviation from its own
    three-channel mean), because that is the domain and the quantity chroma_grade operates on —
    the question is what shape of transform belongs in its place, so the comparison has to be
    made where it would live.
    """
    n = neg_density - neg_density.mean(1, keepdims=True)
    r = ref_density - ref_density.mean(1, keepdims=True)
    m, *_ = np.linalg.lstsq(n, r, rcond=None)
    return m.T                                   # so that out = M @ in


def main():
    ref_raw, xyz_to_srgb = load_reference()
    files = sorted(glob.glob(f"{DIVERE}/config/colorchecker/kodak_*cc24data.json"))
    if not files:
        sys.exit("no stock datasets found — check the DiVERE path")

    stocks, mats, negs, refs = [], [], [], []
    for f in files:
        data = json.load(open(f))["data"]
        keys = [k for k in data if k in ref_raw and not k.startswith("D")]
        neg = np.array([data[k] for k in keys], float)
        ref = np.array([ref_raw[k] for k in keys], float) @ xyz_to_srgb

        nd = -np.log10(np.clip(neg, 1e-6, None))
        rd = -np.log10(np.clip(ref, 1e-6, None))
        stocks.append(stock_name(f))
        mats.append(solve_matrix(nd, rd))
        negs.append(nd)
        refs.append(rd)

    # --- 1. Direction agreement -------------------------------------------------
    # Scale each matrix to unit Frobenius norm, so only its SHAPE is compared, then measure how
    # far each sits from the mean shape.
    unit = [m / np.linalg.norm(m) for m in mats]
    mean_shape = np.mean(unit, axis=0)
    mean_shape /= np.linalg.norm(mean_shape)

    print(f"stocks: {len(stocks)}\n")
    print("per-stock matrix, normalised for strength, vs the mean shape:")
    worst = 0.0
    for name, u, m in zip(stocks, unit, mats):
        # Cosine similarity between the flattened shapes: 1.0 = identical direction.
        cos = float(np.sum(u * mean_shape))
        dev = float(np.linalg.norm(u - mean_shape))
        worst = max(worst, dev)
        print(f"  {name:34s} strength {np.linalg.norm(m):6.3f}   "
              f"cos {cos:7.4f}   shape distance {dev:.4f}")

    # --- 2. Shared matrix + per-stock scalar ------------------------------------
    # Fit one matrix jointly, letting each stock keep only a scalar. Compare against the six
    # independent fits: if the shared model is nearly as good, the shape really is universal.
    def residual(pred, target):
        return float(np.mean((pred - target) ** 2))

    indep = np.mean([residual((m @ n.T).T, r) for m, n, r in zip(mats, negs, refs)])

    all_n = np.vstack(negs)
    all_r = np.vstack(refs)
    shared = solve_matrix(all_n + all_n.mean(1, keepdims=True),
                          all_r + all_r.mean(1, keepdims=True))
    scaled = []
    for n, r in zip(negs, refs):
        p = (shared @ n.T).T
        # best scalar for this stock
        a = float(np.sum(p * r) / max(np.sum(p * p), 1e-12))
        scaled.append(residual(a * p, r))
    joint = float(np.mean(scaled))

    print(f"\nmean squared residual, six INDEPENDENT matrices : {indep:.6f}")
    print(f"mean squared residual, ONE shared + per-stock scalar: {joint:.6f}")
    if indep > 0:
        print(f"cost of forcing a shared shape                  : "
              f"{(joint / indep - 1) * 100:+.1f}%")

    print("\nshared matrix (density chroma, negative -> reference):")
    for row in shared:
        print("   " + "  ".join(f"{v:8.4f}" for v in row))

    print(f"\nworst shape distance from the mean: {worst:.4f}")
    print("Small distances + a small cost for sharing => a universal direction exists,")
    print("and per-stock strength is the style difference to preserve.")


if __name__ == "__main__":
    main()
