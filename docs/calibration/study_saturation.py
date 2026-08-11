"""
HISTORICAL ARTEFACT — kept as evidence, not as a working script.

Recovered from the Python prototype (NegativeConvert), where it was added in
0cc7704 as the calibration reference for chroma_grade and then deleted in
fdc2c26 along with the other exploration scripts. It is restored here because
it was, for a long time, the ONLY executable evidence bearing on chroma_grade,
and it lived exclusively inside a delete commit.

It does NOT run against this repository: it imports `negative.inversion`, the
prototype's Python pipeline, which this C# port does not provide. To run it,
check out NegativeConvert at fdc2c26^ and run it from that repo root.

What it reports there, against the current prototype pipeline: the per-channel
Cineon inversion does not lose saturation on a synthetic negative — it comes out
11-14% OVER the true scene, and the scalar compensation required is 0.000, not
3.05. It also measures that a scalar cannot flatten the per-patch error
(mean 0.093, max 0.206), which is the anisotropy argument for doing a real
colour-space conversion instead.

--- original docstring follows ---

Synthetic-negative quantification: does the per-channel Cineon inversion lose
saturation, and if so, what scalar apply_saturation compensates it?

Builds known "true scene" colour patches (linear), forward-models a C-41
negative, runs them through our REAL inversion (negative.inversion.invert) with
matched calibration, then measures saturation restoration.

Two forward models:
  (1) pure per-channel gamma (γ≈0.6) — the "density compression" story.
  (2) gamma + dye inter-layer cross-talk (off-diagonal) — the real mechanism.

Saturation metric: HSV-style S = (max-min)/max per pixel, averaged over the
chromatic patches (neutrals excluded). Reported in linear domain.
"""
from __future__ import annotations
import numpy as np
from negative.inversion import invert
from negative.levels import apply_saturation
from negative.types import FrameParams

GAMMA = 0.6
DMIN = np.array([0.20, 0.50, 0.80])   # orange mask: transmits red, absorbs blue
E_MIN = 0.02                          # darkest scene linear value


def make_scene():
    """A spread of saturated hues at a few brightness levels (linear RGB)."""
    base_hues = np.array([
        [1.0, 0.1, 0.1], [0.1, 1.0, 0.1], [0.1, 0.1, 1.0],   # R G B
        [1.0, 1.0, 0.1], [0.1, 1.0, 1.0], [1.0, 0.1, 1.0],   # Y C M
        [0.9, 0.5, 0.2], [0.3, 0.6, 0.9], [0.7, 0.2, 0.4],   # skin/sky/etc
    ])
    levels = [0.25, 0.5, 0.9]
    px = np.array([h * L for L in levels for h in base_hues], dtype=np.float64)
    px = np.clip(px, E_MIN, 1.0)
    return px.reshape(1, -1, 3).astype(np.float32)


def sat(img):
    """Mean HSV-S over chromatic pixels."""
    a = img.reshape(-1, 3).astype(np.float64)
    mx = a.max(1); mn = a.min(1)
    s = np.where(mx > 1e-6, (mx - mn) / np.maximum(mx, 1e-6), 0.0)
    return float(s.mean())


def forward_negative(scene, crosstalk=None):
    """Scene linear → C-41 negative transmittance T."""
    E = np.maximum(scene.astype(np.float64), E_MIN)
    d_exposed = GAMMA * (np.log10(E) - np.log10(E_MIN))   # >=0, per channel
    if crosstalk is not None:
        flat = d_exposed.reshape(-1, 3) @ np.asarray(crosstalk).T
        d_exposed = flat.reshape(d_exposed.shape)
    D = DMIN + d_exposed
    return (10.0 ** (-D)).astype(np.float32)


def calib():
    t_base = (10.0 ** (-DMIN)).astype(np.float32)
    d_max = float(GAMMA * (np.log10(1.0) - np.log10(E_MIN)))
    return FrameParams(t_base=t_base, d_max=d_max,
                       wb_high=np.ones(3, np.float32),
                       grade=1.0 / GAMMA, pivot=0.45 * d_max,
                       scan_exposure_ev=0.0)


def normalize_exposure(pos, scene):
    """Inversion recovers scene up to a global scale; match mean luma to compare."""
    lp = pos.reshape(-1, 3).mean(); ls = scene.reshape(-1, 3).mean()
    return pos * (ls / max(lp, 1e-9))


def best_scalar_sat(pos, scene_sat):
    """Find apply_saturation value whose output saturation matches scene_sat."""
    lo, hi = 0.0, 3.0
    for _ in range(40):
        mid = (lo + hi) / 2
        if sat(apply_saturation(pos, mid)) < scene_sat:
            lo = mid
        else:
            hi = mid
    return (lo + hi) / 2


scene = make_scene()
fp = calib()
s_true = sat(scene)
print(f"true scene saturation              : {s_true:.4f}\n")

# Model 1: pure gamma
neg1 = forward_negative(scene)
pos1 = normalize_exposure(invert(neg1, fp), scene)
r1 = sat(pos1) / s_true
print("MODEL 1  pure per-channel γ (no cross-talk)")
print(f"  recovered saturation             : {sat(pos1):.4f}")
print(f"  restoration ratio                : {r1*100:.1f}%")
print(f"  → scalar comp needed             : {best_scalar_sat(pos1, s_true):.3f}\n")

# Model 2: gamma + dye cross-talk (representative C-41 inter-layer)
CT = np.array([[1.00, 0.12, 0.06],
               [0.10, 1.00, 0.10],
               [0.08, 0.14, 1.00]])
neg2 = forward_negative(scene, crosstalk=CT)
pos2 = normalize_exposure(invert(neg2, fp), scene)
r2 = sat(pos2) / s_true
comp2 = best_scalar_sat(pos2, s_true)
print("MODEL 2  γ + representative dye cross-talk")
print(f"  recovered saturation             : {sat(pos2):.4f}")
print(f"  restoration ratio                : {r2*100:.1f}%")
print(f"  → scalar comp to hit scene sat   : {comp2:.3f}")
# Is the scalar comp constant across hue? check per-patch residual after comp
fixed = apply_saturation(pos2, comp2)
a_fixed = fixed.reshape(-1, 3); a_scene = scene.reshape(-1, 3)
def persat(a):
    mx=a.max(1);mn=a.min(1);return np.where(mx>1e-6,(mx-mn)/np.maximum(mx,1e-6),0)
resid = persat(a_fixed) - persat(a_scene)
print(f"  after scalar comp, per-patch sat error: mean={np.abs(resid).mean():.3f} "
      f"max={np.abs(resid).max():.3f}  (0 = scalar fully fixes it)")
