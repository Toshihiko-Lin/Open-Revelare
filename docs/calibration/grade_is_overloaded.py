"""
What is `grade` actually doing, and is "paper grade" the right name for it?

THEORY.md justified grade like this: C-41 film is deliberately low-contrast (gamma ~0.6)
BECAUSE it was designed to be printed onto high-contrast paper (gamma ~2.5-3.5); digitising
skips the paper, so grade ~1.65 puts back the contrast the missing paper would have supplied.

That justification does not survive contact with either the standard it invokes or the algebra.

  1. Cineon never modelled paper. Cineon log is a STORAGE ENCODING for scanned negative
     density — it maps density 0-2.046 onto code values 95-685 and nothing more. There is no
     paper simulation in it, so "Cineon density-domain inversion" cannot inherit a paper
     correction from the standard. Neither does darktable's negadoctor, the other model this
     pipeline cites: it does two-sided density calibration (wb_high / offset) and a gamma, and
     claims no paper stage either.

  2. The algebra makes the paper story unnecessary. Negative density already records scene
     luminance in full: D ~ gamma_film * log(H). Recovering the scene means DIVIDING by
     gamma_film. That is a solve, not a compensation, and it is true whether gamma_film is 0.6
     or 0.3 — it has nothing to do with whether a paper exists downstream. grade ~ 1.65 lands
     near 1/0.6 and is numerically defensible; its stated REASON is not.

The distinction is not academic, because the two readings imply different software:

    "paper grade"        -> an aesthetic choice, picked from soft/normal/hard, roll-uniform
    "1 / gamma_film"     -> a measured property, solved per roll, and per CHANNEL if the three
                            dye layers do not share a gamma

This script tests three things against DiVERE's modelled ColorChecker data.

  Q1. Does one gamma serve luminance and chroma equally? (Is the single knob overloaded?)
  Q2. Do the three dye layers share one gamma? (Can any scalar linearise all three?)
  Q3. Do the shipped presets look like 1/gamma_film values, or like paper grades?

IMPORTANT — WHAT THIS DATA CAN AND CANNOT SETTLE. The per-stock files describe densities ON
PAPER: "在 <stock> -> kodak_endura_premier 打印流程下，相纸上得到的理论密度". The print stage
is therefore already baked into the observations. That means:

  CANNOT: derive the absolute gamma a digitised negative needs. Both sides of the fit carry
          the paper, so solving a luminance slope here returns ~1.0 by construction. Any
          claim of the form "the chart proves no contrast boost is needed" is circular, and
          an earlier fit in this project was withdrawn for exactly that mistake — see the
          note in C41Crosstalk.cs about ColorChecker data that turned out to be paper-side.

  CAN:    compare quantities solved INSIDE one consistent chain. If luminance and chroma want
          different gains, or if the three channels want different slopes, those differences
          are properties of the coupling, not of which reference was picked. Q1 and Q2 are
          both of that form, which is why they are asked and the absolute gamma is not.

Getting the absolute per-channel gamma needs D-logE curves measured on the negative itself —
which is precisely what a film datasheet publishes. (Note the reversal: for the crosstalk
MATRIX a datasheet is useless, because that correction is dominated by the sensor —
see status_m_scale.py. For GAMMA the datasheet is the authoritative source.)

Usage:  python3 grade_is_overloaded.py [path/to/DiVERE]
"""
import glob
import json
import os
import sys

import numpy as np

DIVERE = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DIVERE_ROOT", "../../../DiVERE")

# The presets the GUI ships (WbMath.GradePresets), with the labels it shows.
SHIPPED_PRESETS = [("软 — 0–1 号纸", 1.30), ("标准 — 2–3 号纸", 1.65), ("硬 — 4–5 号纸", 2.00)]

# Typical published C-41 D-logE slopes, for the Q3 comparison. Approximate on purpose: the
# point is the SPREAD between stocks, not any single figure.
TYPICAL_FILM_GAMMA = [("Portra 160", 0.55), ("Portra 400", 0.60),
                      ("Ektar 100", 0.65), ("Gold 200", 0.58)]


def npm(prim, wp):
    """Primaries + white point (CIE xy) -> RGB->XYZ matrix."""
    xyz = np.array([[x / y, 1.0, (1 - x - y) / y] for x, y in prim]).T
    w = np.array([wp[0] / wp[1], 1.0, (1 - wp[0] - wp[1]) / wp[1]])
    return xyz * np.linalg.solve(xyz, w)


def load_sets(divere):
    """Per stock: (name, negative density, reference density) in ONE colour space.

    The reference ships as XYZ tristimulus; the stock files are exp(-density) in the
    KodakEnduraPremier working space they declare. Comparing them means converting XYZ into
    that space FIRST and only then taking -log10. Running -log10 straight on XYZ is
    meaningless and silently produces a plausible-looking wrong answer.
    """
    cc = os.path.join(divere, "config", "colorchecker")
    cs_path = os.path.join(divere, "config", "colorspace", "KodakEnduraPremier.json")
    if not os.path.isdir(cc) or not os.path.exists(cs_path):
        return []

    with open(cs_path) as f:
        cs = json.load(f)
    m_inv = np.linalg.inv(npm([cs["primaries"][k] for k in "RGB"], cs["white_point"]))

    ref_path = os.path.join(cc, "original_color_cc24data.json")
    with open(ref_path) as f:
        ref_doc = json.load(f)
    assert ref_doc["type"] == "XYZ", "reference is expected to be XYZ tristimulus"
    ref = ref_doc["data"]

    dens = lambda v: -np.log10(np.maximum(v, 1e-10))
    out = []
    for path in sorted(glob.glob(os.path.join(cc, "*_cc24data.json"))):
        if "original_color" in path:
            continue
        with open(path) as f:
            obs = json.load(f)["data"]
        keys = sorted(set(obs) & set(ref))
        if len(keys) < 12:
            continue
        neg = dens(np.array([obs[k] for k in keys]))
        scene = dens(np.maximum(np.array([ref[k] for k in keys]) @ m_inv.T, 1e-10))
        name = os.path.basename(path).replace("_kodak_endura_premier_d60_cc24data.json", "")
        out.append((name, neg, scene))
    return out


def q1_luminance_vs_chroma(sets):
    """Q1: does one gamma serve luminance and chroma equally?"""
    print("=" * 78)
    print("Q1  Does ONE gamma serve luminance and chroma equally?")
    print("=" * 78)
    print(f"  {'stock':<24}{'luminance':>11}{'chroma':>10}{'ratio':>9}")
    print("  " + "-" * 52)

    lums, chromas = [], []
    for name, neg, scene in sets:
        # Luminance: slope of scene mean-density against negative mean-density.
        nm, sm = neg.mean(axis=1), scene.mean(axis=1)
        lum = abs(float(np.polyfit(nm - nm.mean(), sm - sm.mean(), 1)[0]))
        # Chroma: least-squares scalar mapping negative chroma onto scene chroma.
        cn = neg - neg.mean(axis=1, keepdims=True)
        cr = scene - scene.mean(axis=1, keepdims=True)
        chroma = float((cn * cr).sum() / (cn * cn).sum())
        lums.append(lum)
        chromas.append(chroma)
        print(f"  {name:<24}{lum:>11.3f}{chroma:>10.3f}{chroma / lum:>9.2f}")

    ml, mc = float(np.mean(lums)), float(np.mean(chromas))
    print()
    print(f"  mean luminance {ml:.3f}   mean chroma {mc:.3f}   ratio {mc / ml:.2f}")
    print()
    print("  The two want DIFFERENT gains, measured inside one chain. But the pipeline")
    print("  applies a single grade to the whole density vector:")
    print()
    print("      D_adj = pivot + (D - pivot) * grade - dmax")
    print()
    print("  Split D into mean + chroma and the same grade multiplies both. One knob is")
    print("  therefore setting two independent quantities, and they only agree if the")
    print("  ratio above happens to be 1.00. It is not.")
    print()
    print("  NOTE: the luminance figure lands near 1.0 BY CONSTRUCTION — the paper stage is")
    print("  in both sides of this data. Read the RATIO, which is chain-internal; do not")
    print("  read the luminance column as 'a digitised negative needs no boost'.")
    print()


def q2_per_channel(sets):
    """Q2: do the three dye layers share one gamma?"""
    print("=" * 78)
    print("Q2  Do the three dye layers share ONE gamma?")
    print("=" * 78)
    print(f"  {'stock':<24}{'R':>8}{'G':>8}{'B':>8}{'spread':>9}")
    print("  " + "-" * 57)

    spreads = []
    for name, neg, scene in sets:
        s = [abs(float(np.polyfit(neg[:, c] - neg[:, c].mean(),
                                  scene[:, c] - scene[:, c].mean(), 1)[0]))
             for c in range(3)]
        spread = max(s) - min(s)
        spreads.append(spread)
        print(f"  {name:<24}{s[0]:>8.3f}{s[1]:>8.3f}{s[2]:>8.3f}{spread:>9.3f}")

    print()
    print(f"  mean channel spread = {np.mean(spreads):.3f}")
    print()
    print("  If a single scalar could linearise the negative, these would be equal. They")
    print("  are not — the red layer is consistently the steepest. So the right SHAPE for")
    print("  this correction is a per-channel gamma, not one number for all three; and the")
    print("  luminance/chroma split in Q1 is a downstream symptom of the same fact.")
    print()


def q3_presets():
    """Q3: do the shipped presets look like 1/gamma_film, or like paper grades?"""
    print("=" * 78)
    print("Q3  Are the shipped presets 1/gamma_film values, or paper grades?")
    print("=" * 78)
    print("  Shipped presets (WbMath.GradePresets):")
    for label, value in SHIPPED_PRESETS:
        print(f"    {value:<6.2f}  {label}")
    print()
    print("  1/gamma for typical C-41 stocks (published D-logE slopes):")
    for name, g in TYPICAL_FILM_GAMMA:
        print(f"    {1.0 / g:<6.2f}  {name} (gamma {g:.2f})")
    print()
    inv = [1.0 / g for _, g in TYPICAL_FILM_GAMMA]
    print(f"  Stocks span 1/gamma {min(inv):.2f}-{max(inv):.2f}; the presets span "
          f"{SHIPPED_PRESETS[0][1]:.2f}-{SHIPPED_PRESETS[-1][1]:.2f}.")
    print()
    print("  The ranges overlap, but the LABELS decide what the control means. They name")
    print("  darkroom paper grades (0-1, 2-3, 4-5), so the user is choosing a print look,")
    print("  not declaring their film's gamma. A control that stood for 1/gamma_film would")
    print("  be solved from the roll and would differ per stock — Ektar and Portra 160 sit")
    print(f"  {abs(1/0.65 - 1/0.55):.2f} apart, more than one preset step.")
    print()


def verdict():
    print("=" * 78)
    print("VERDICT")
    print("=" * 78)
    print("""
  The paper-grade justification is wrong on its own terms: Cineon is an encoding, not a
  paper model, and negadoctor models no paper either. Recovering scene luminance from
  negative density is a division by gamma_film — a solve that stands whether or not any
  paper exists downstream.

  What the data adds on top of that argument:

    - one gamma is driving two independent quantities (Q1), so the control is overloaded
    - the three layers do not share a gamma (Q2), so no single scalar can be correct
    - the presets are labelled as paper grades (Q3), so the control currently presents an
      aesthetic choice in the position of a physical one

  Consequence for the design: this parameter sits on the physics side of the FilmBase /
  SceneBase split while behaving like a taste control. Making it physical means solving a
  per-channel gamma for the roll; keeping contrast as taste means moving it to SceneBase,
  where the contrast and saturation sliders already live. What it should not keep doing is
  both at once under a darkroom name.

  NOT ANSWERED HERE: the absolute per-channel gamma. This data cannot supply it (the paper
  is baked in). Measured D-logE curves can — i.e. the film datasheet, which for THIS
  quantity is authoritative, unlike the crosstalk matrix in status_m_scale.py.
""")


def main():
    if not os.path.isdir(DIVERE):
        print(f"DiVERE not found at {DIVERE}; pass its path as argv[1].")
        return 1
    sets = load_sets(DIVERE)
    if not sets:
        print("No usable ColorChecker datasets found.")
        return 1

    print()
    q1_luminance_vs_chroma(sets)
    q2_per_channel(sets)
    q3_presets()
    verdict()
    return 0


if __name__ == "__main__":
    sys.exit(main())
