"""
How much of the "21% chroma deficit" is explained by the paper gamut?

Reads DiVERE's Kodak Gold 200 ColorChecker-24 dataset — the dataset chroma_grade
was originally fitted against — and measures the mean saturation of the 18
chromatic patches twice: once in the Kodak Endura Premier paper space the data is
natively expressed in, and once reinterpreted through the sRGB gamut.

Result (see docs/CALIBRATION.md): the gamut difference accounts for about 6%, not
21%. This rules out "the deficit is just the narrow paper gamut" as an explanation
— that hypothesis was raised and does not survive measurement. The origin of the
remaining deficit is still unestablished.

Requires a DiVERE checkout for the dataset; pass its path as argv[1] or set
DIVERE_ROOT. Data file: config/colorchecker/kodak_gold_200_kodak_endura_premier_d60_cc24data.json
"""
import json
import os
import sys
import numpy as np

DIVERE = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DIVERE_ROOT", "../../../DiVERE")


def npm(prim, wp):
    """Primaries + white point (CIE xy) -> the RGB->XYZ matrix."""
    xy = np.array([prim['R'], prim['G'], prim['B']], float)
    xyz = np.array([[x / y, 1.0, (1 - x - y) / y] for x, y in xy]).T
    wx, wy = wp
    w = np.array([wx / wy, 1.0, (1 - wx - wy) / wy])
    return xyz * np.linalg.solve(xyz, w)


def sat(a):
    """HSV-style S = (max-min)/max, per row."""
    mx, mn = a.max(1), a.min(1)
    return np.where(mx > 1e-9, (mx - mn) / np.maximum(mx, 1e-9), 0.0)


endura_def = json.load(open(
    f"{DIVERE}/config/colorspace/KodakEnduraPremier.json"))
m_endura = npm(endura_def['primaries'], endura_def['white_point'])
m_srgb = npm({'R': [0.64, 0.33], 'G': [0.30, 0.60], 'B': [0.15, 0.06]},
             [0.3127, 0.3290])

data = json.load(open(
    f"{DIVERE}/config/colorchecker/"
    "kodak_gold_200_kodak_endura_premier_d60_cc24data.json"))['data']

# The 18 chromatic patches: everything except row D, which is the neutral ramp.
patches = np.array([v for k, v in data.items() if not k.startswith('D')], float)

s_paper = sat(patches)
srgb_lin = (patches @ m_endura.T) @ np.linalg.inv(m_srgb).T
s_srgb = sat(np.clip(srgb_lin, 0, None))

print(f"chromatic patches                     : {len(patches)}")
print(f"mean saturation, Endura paper space   : {s_paper.mean():.4f}")
print(f"mean saturation, sRGB space           : {s_srgb.mean():.4f}")
print(f"ratio sRGB/Endura                     : {s_srgb.mean() / s_paper.mean():.3f}")
print()
print("~1.06 => the paper gamut explains ~6% of the deficit, not 21%.")
