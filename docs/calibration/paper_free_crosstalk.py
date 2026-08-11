"""
Is there a universal C-41 crosstalk direction — asked of PAPER-FREE data this time.

The earlier attempt (universal_crosstalk.py) had to be withdrawn: every ColorChecker dataset it
used describes densities on Kodak Endura Premier PAPER, so the matrix it fitted mapped paper to
scene and the eight stocks' agreement was partly the shared print chain, not the film. See
the project history.

DiVERE also ships something different: config/matrices/*.json, density correction matrices solved
by users against real scans. Their names are scanner + film — 9000ed_g200_135, X5_5219_Daylight,
5000ed_u400 — with no print stage anywhere. These are measurements of the negative itself.

That gives the cross-comparison the earlier data could not support:

  * the same film on different scanners  (g200 on 9000ED in 135 and 120, u400 on 9000ED and 5000ED)
  * different films on the same scanner  (9000ED sees g200, pt160, pt400, u400, fuji100, LuckyC200)

If a shared crosstalk direction is a property of the C-41 PROCESS, matrices should cluster by
film and stay put across scanners. If instead they cluster by scanner, what is being measured is
the instrument, and no film constant is recoverable this way.

Usage:  python3 paper_free_crosstalk.py [path/to/DiVERE]
"""
import glob
import json
import os
import sys

import numpy as np

DIVERE = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DIVERE_ROOT", "../../../DiVERE")

# Not film measurements: an identity placeholder and a densitometry-standard conversion.
SKIP = {"Identity", "Cineon_States_M_to_Print_Density"}


def load():
    out = {}
    for path in sorted(glob.glob(f"{DIVERE}/config/matrices/*.json")):
        name = os.path.basename(path)[:-5]
        if name in SKIP:
            continue
        out[name] = np.array(json.load(open(path))["matrix"], float)
    return out


def chroma_part(m):
    """
    The part of the matrix that acts on chroma, with luminance removed.

    A density matrix mixes two things: an overall per-channel scaling (exposure and film-base
    balance, which t_base and wb_high already handle) and the inter-channel mixing that is the
    actual crosstalk. Projecting out the mean removes the former from both sides, leaving the
    part this investigation is about — and making matrices from different scanners comparable
    despite their different overall gains.
    """
    p = np.eye(3) - np.ones((3, 3)) / 3.0
    c = p @ m @ p
    n = np.linalg.norm(c)
    return c / n if n > 1e-12 else c, n


def film_of(name):
    """Film token in the file name, as far as one can tell from the naming used."""
    for key in ("g200", "pt160", "pt400", "u400", "fuji100", "luckyc200",
                "5207", "5219"):
        if key in name.lower():
            return key
    return "?"


def scanner_of(name):
    low = name.lower()
    for key in ("9000ed", "5000ed", "x5"):
        if low.startswith(key):
            return key
    return "?"


def main():
    mats = load()
    if not mats:
        sys.exit("no matrices found — check the DiVERE path")

    names = list(mats)
    shapes, strengths = {}, {}
    for n in names:
        s, mag = chroma_part(mats[n])
        shapes[n], strengths[n] = s, mag

    print(f"{len(names)} paper-free density matrices\n")
    print(f"{'matrix':<28}{'film':<10}{'scanner':<9}{'chroma strength':>16}")
    for n in names:
        print(f"  {n:<26}{film_of(n):<10}{scanner_of(n):<9}{strengths[n]:>16.4f}")

    # Pairwise direction agreement, the same measure used before.
    def cos(a, b):
        return float(np.sum(shapes[a] * shapes[b]))

    same_film, same_scanner, unrelated = [], [], []
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            c = cos(a, b)
            fa, fb = film_of(a), film_of(b)
            sa, sb = scanner_of(a), scanner_of(b)
            if fa == fb and fa != "?" and sa != sb:
                same_film.append((c, a, b))
            elif sa == sb and sa != "?" and fa != fb:
                same_scanner.append((c, a, b))
            elif fa != fb and sa != sb:
                unrelated.append((c, a, b))

    def summarise(label, rows):
        if not rows:
            print(f"\n{label}: no pairs")
            return
        v = np.array([r[0] for r in rows])
        print(f"\n{label}: {len(rows)} pairs   mean cos {v.mean():+.4f}   "
              f"min {v.min():+.4f}   max {v.max():+.4f}")
        for c, a, b in sorted(rows)[:3]:
            print(f"    worst: {a} vs {b}  cos {c:+.4f}")

    summarise("SAME FILM, different scanner ", same_film)
    summarise("SAME SCANNER, different film ", same_scanner)
    summarise("unrelated                    ", unrelated)

    print("\nIf the crosstalk direction belongs to the FILM, same-film pairs should agree")
    print("markedly better than same-scanner pairs. If the two are similar, these matrices")
    print("are dominated by the instrument and no process constant follows from them.")


if __name__ == "__main__":
    main()
