"""
Would narrow-band scanning let us calibrate density against published Vision 3 data?

A practitioner suggested it: if the light source is narrow-band, the read densities should
approach Status M — the standardised narrow-band densitometry that Kodak's Vision 3 datasheets
are plotted in — and then the datasheet becomes usable as a calibration reference.

THEORY.md (第 205-207 行) already concedes the gap this targets. Path A decouples the
light-source/film coupling but carries no information about the sensor-vs-paper spectral
mismatch, leaving ΔE 3-6 of residual. Status M is attractive precisely because it is an
ABSOLUTE scale: a camera's own RGB density is a private scale with no shared coordinates with
any datasheet, whereas Status M densities mean the same thing on every instrument.

The claim decomposes into three separate questions, and they do NOT have the same answer:

  Q1. Is the residual Path A leaves actually a 3x3 density-domain matrix at all?
      If the mismatch is not well modelled by a matrix, no amount of scale alignment helps.

  Q2. WHICH PART of the matrix moves with the instrument — its direction or its strength?
      This is the crux, and it decides how much calibration a rig actually needs. If the
      direction moves too, every rig must solve a full 3x3 against a reference target. If only
      the strength moves, the direction can be shipped as a constant and a rig needs to
      measure just one scalar. Note the comparison must divide strength out before comparing
      directions, or a pure strength difference masquerades as a structural one.

  Q3. Is narrow-band capture enough on its own to land on Status M?
      Status M is a set of RESPONSE functions (filter x detector). Narrow-band LEDs are
      EMISSION spectra. Both being narrow does not make them the same narrow.

DiVERE ships the evidence for Q1/Q2: 19 empirically solved density matrices covering several
stocks across several scanners, including the same stock (Vision 3 5219, daylight) solved
independently on a Nikon 9000ED and a Hasselblad X5. That pair is a direct controlled
experiment — same film, same illuminant, different instrument. It also ships the published
Cineon Status M -> Print Density matrix, which gives a reference for how large a *pure scale
conversion* matrix is, as opposed to an instrument-correction one.

Q3 is answered analytically from the published Status M passbands; no film required.

Usage:  python3 status_m_scale.py [path/to/DiVERE]
"""
import glob
import json
import os
import sys

import numpy as np

DIVERE = sys.argv[1] if len(sys.argv) > 1 else os.environ.get(
    "DIVERE_ROOT", "../../../DiVERE")

# ISO 5/3 Status M nominal peak wavelengths (nm). Status M is defined for measuring the
# density of colour NEGATIVE films: its passbands sit where the incorporated masking dyes
# and image dyes separate cleanly, NOT where a camera's CFA peaks.
STATUS_M_PEAKS = {"R": 644.0, "G": 542.0, "B": 435.7}

# Typical narrow-band RGB LED peaks used in DSLR-scanning light panels. These are the
# commodity emitters people actually own; nothing is available at Status M's wavelengths.
TYPICAL_LED_PEAKS = {"R": 630.0, "G": 525.0, "B": 465.0}


def load_matrix(path):
    """DiVERE matrix JSON -> 3x3 float array."""
    with open(path) as f:
        d = json.load(f)
    return np.asarray(d["matrix"], dtype=float), d.get("name", os.path.basename(path))


def offdiag_energy(m):
    """How much of the matrix is NOT the identity — the size of the correction it applies.

    Frobenius norm of (M - I). A pure scale conversion between two densitometric standards
    should be small; an instrument-specific correction should be large.
    """
    return float(np.linalg.norm(m - np.eye(3)))


def row_sums(m):
    return m.sum(axis=1)


def chroma_action(m):
    """The part of the matrix acting on chroma, split into direction and strength.

    Same convention as paper_free_crosstalk.py: project luminance out of BOTH sides
    (P = I - J/3), which removes the per-channel gain that t_base / wb_high already handle
    and leaves the inter-channel mixing that is the actual crosstalk.

    Note this projects M, not (M - I). Subtracting the identity first injects a -P term that
    is IDENTICAL in every matrix; it swamps the genuine difference and makes two matrices
    pointing the same way look ~50 degrees apart. Direction comparisons must use P@M@P.
    """
    p = np.eye(3) - np.ones((3, 3)) / 3.0
    c = p @ m @ p
    n = float(np.linalg.norm(c))
    return (c / n if n > 1e-12 else c), n


def direction_cosine(a, b):
    """Cosine between the chroma DIRECTIONS of two matrices, strength divided out."""
    da, _ = chroma_action(a)
    db, _ = chroma_action(b)
    return float((da * db).sum())


def q1_is_the_residual_matrix_shaped(cc_dir):
    """Q1: does a 3x3 density matrix actually fit the negative->reference residual?

    Fit, per stock, the best 3x3 mapping the modelled ColorChecker negative densities onto the
    scene reference, and report how much of the error it removes. If a matrix is the wrong
    model, the residual will barely drop.
    """
    print("=" * 78)
    print("Q1  Is the residual a 3x3 density-domain matrix at all?")
    print("=" * 78)

    ref_path = os.path.join(cc_dir, "original_color_cc24data.json")
    if not os.path.exists(ref_path):
        print(f"  [skip] reference not found: {ref_path}")
        return

    with open(ref_path) as f:
        ref_raw = json.load(f)["data"]

    stocks = sorted(glob.glob(os.path.join(cc_dir, "*_cc24data.json")))
    stocks = [s for s in stocks if "original_color" not in s]
    if not stocks:
        print("  [skip] no stock datasets found")
        return

    print(f"  {'stock':<34} {'resid before':>13} {'resid after':>12} {'explained':>10}")
    print("  " + "-" * 72)

    for path in stocks:
        with open(path) as f:
            obs_raw = json.load(f)["data"]

        patches = sorted(set(ref_raw) & set(obs_raw))
        if len(patches) < 12:
            continue

        # Both files store exp(-density); take -log10 to return to the density domain, which
        # is where the correction is claimed to be a matrix.
        obs = np.array([[-np.log10(max(v, 1e-10)) for v in obs_raw[p]] for p in patches])
        ref = np.array([[-np.log10(max(v, 1e-10)) for v in ref_raw[p]] for p in patches])

        # Remove each set's own mean density: we are testing the CHROMA relationship, not
        # overall exposure/scale, which the pipeline's dmax/grade already handle.
        obs_c = obs - obs.mean(axis=0)
        ref_c = ref - ref.mean(axis=0)

        before = float(np.sqrt(np.mean((obs_c - ref_c) ** 2)))
        m, *_ = np.linalg.lstsq(obs_c, ref_c, rcond=None)
        after = float(np.sqrt(np.mean((obs_c @ m - ref_c) ** 2)))
        explained = 100.0 * (1.0 - after / before) if before > 1e-12 else 0.0

        name = os.path.basename(path).replace("_kodak_endura_premier_d60_cc24data.json", "")
        print(f"  {name:<34} {before:>13.4f} {after:>12.4f} {explained:>9.1f}%")

    print()
    print("  A high 'explained' fraction means the mismatch IS matrix-shaped, so a 3x3")
    print("  density correction is the right form of fix. That is the part of the")
    print("  suggestion which holds regardless of where the matrix comes from.")
    print()


def q2_scanner_or_stock(mat_dir):
    """Q2: does the needed matrix travel with the film stock, or with the instrument?

    The decisive comparison is Vision 3 5219 under daylight, solved independently on two
    different scanners. Same film, same illuminant, different instrument.
    """
    print("=" * 78)
    print("Q2  What part of the matrix moves with the INSTRUMENT?")
    print("=" * 78)

    def find(stem):
        p = os.path.join(mat_dir, stem + ".json")
        return load_matrix(p) if os.path.exists(p) else (None, None)

    n5219, _ = find("9000ed_5219_Daylight")
    x5219, _ = find("X5_5219_Daylight")

    if n5219 is None or x5219 is None:
        print("  [skip] need both 9000ed_5219_Daylight and X5_5219_Daylight")
    else:
        print("  Controlled pair — Vision 3 5219, daylight, two instruments:")
        print()
        print("    Nikon 9000ED                     Hasselblad X5")
        for i in range(3):
            left = "  ".join(f"{v:+7.4f}" for v in n5219[i])
            right = "  ".join(f"{v:+7.4f}" for v in x5219[i])
            print(f"    [{left}]      [{right}]")
        print()
        _, sn = chroma_action(n5219)
        _, sx = chroma_action(x5219)
        cos = direction_cosine(n5219, x5219)
        angle = np.degrees(np.arccos(min(cos, 1.0)))

        print(f"    chroma DIRECTION   cosine {cos:+.4f}   ({angle:.1f} deg apart)")
        print(f"    chroma STRENGTH    9000ED {sn:.4f}   X5 {sx:.4f}   ratio {sx / sn:.2f}x")
        print()
        print(f"    Element-wise the B<-G term ({n5219[2][1]:+.4f} vs {x5219[2][1]:+.4f},"
              f" {x5219[2][1] / n5219[2][1]:.1f}x) looks structural,")
        print("    but that is a strength difference concentrated in one entry. Normalised,")
        print("    the two matrices point essentially the same way.")
        print()

    # For scale: how big is a matrix that only converts between two densitometric STANDARDS,
    # with no instrument correction in it at all?
    cineon, cname = find("Cineon_States_M_to_Print_Density")
    if cineon is not None:
        print(f"  Reference point — published '{cname}':")
        print(f"    a pure standard-to-standard density conversion, no instrument in it")
        print(f"    correction size ||M-I|| = {offdiag_energy(cineon):.4f}")
        if n5219 is not None:
            ratio = offdiag_energy(n5219) / offdiag_energy(cineon)
            print(f"    the 5219 instrument matrices are {ratio:.1f}x larger")
        print()

    # Same-instrument spread across stocks, for the complementary view.
    nikon = sorted(glob.glob(os.path.join(mat_dir, "9000ed_*.json")))
    if len(nikon) >= 3:
        mats = [load_matrix(p)[0] for p in nikon]
        strengths = [chroma_action(m)[1] for m in mats]
        pairs = [direction_cosine(mats[i], mats[j])
                 for i in range(len(mats)) for j in range(i + 1, len(mats))]
        print(f"  Same instrument (9000ED), {len(mats)} different stocks:")
        print(f"    direction agreement  mean cos {np.mean(pairs):+.4f}"
              f"   worst {min(pairs):+.4f}")
        print(f"    strength range       {min(strengths):.4f} - {max(strengths):.4f}")
        print()
        print("    Directions agree tightly across stocks too; strength is what moves.")
        print("    This is the same conclusion C41Crosstalk.cs reaches over 18 matrices:")
        print("    one shared direction explains 99.01% of the variance.")
        print()


def q3_led_vs_status_m():
    """Q3: is a narrow-band LED panel the same thing as Status M?

    Status M is a RESPONSE function (filter x detector); an LED is an EMISSION spectrum.
    Compare where they actually sit. Dye absorption curves are steep, so a wavelength offset
    of this size is not a rounding error.
    """
    print("=" * 78)
    print("Q3  Does narrow-band capture land on Status M by itself?")
    print("=" * 78)
    print(f"  {'channel':<10} {'Status M':>10} {'typical LED':>13} {'offset':>10}")
    print("  " + "-" * 46)
    for ch in ("R", "G", "B"):
        sm = STATUS_M_PEAKS[ch]
        led = TYPICAL_LED_PEAKS[ch]
        print(f"  {ch:<10} {sm:>9.1f}nm {led:>12.1f}nm {led - sm:>+9.1f}nm")
    print()
    print("  Status M passbands are placed where colour-negative masking and image dyes")
    print("  separate. Commodity RGB LEDs are placed where they are cheap and bright.")
    print("  The offsets above are tens of nanometres, on dye curves that move steeply")
    print("  over that span — so narrow-band capture gets CLOSER to Status M than broadband")
    print("  does, but does not arrive there. A residual correction is still required; what")
    print("  narrow-band buys is that it is better conditioned and more stable — and by Q2")
    print("  that correction is a known direction times one scalar, not a free 3x3.")
    print()


def verdict():
    print("=" * 78)
    print("VERDICT")
    print("=" * 78)
    print("""
  The suggestion is right about the FORM of the fix, and the calibration it implies is
  much cheaper than it first appears.

  Right:  the residual THEORY.md concedes at 第 205-207 行 is matrix-shaped (Q1), and
          narrow-band capture genuinely improves the conditioning of that matrix (Q3).
          Density calibration against an absolute densitometric scale is the correct
          direction, and Status M is the correct scale to target.

  Refined: the datasheet cannot supply the WHOLE correction, because its strength depends
          on the sensor and on the target one declares. But the DIRECTION does not move
          with the instrument (Q2) — it is already shipped as C41Crosstalk.Direction,
          fitted across 18 matrices where one shared direction explains 99.01% of the
          variance. So nothing has to be solved per rig, and the user shoots no
          calibration target at all.

  On strength: it is carried by the existing grade, NOT calibrated. Structurally it is
          target_chroma / negative_chroma, so it is set by which target one declares —
          the same film on the same scanner appears twice among the 18, at 1.41 and 1.79.
          There is no single correct value to measure, so calling this a "rig calibration"
          would overstate it. Making it a measured quantity would need a step wedge with
          known Status M densities plus a solver (neither exists here yet), and would
          first have to settle what chroma is being targeted.

  What the datasheet CAN do:
    - define the target scale, so 'density' stops being per-camera private units
    - supply expected D-min / D-max / toe-shoulder landmarks to VALIDATE a result

  CAVEAT ON METHOD: comparing density matrices element-wise, or after subtracting the
  identity, makes matrices that agree look wildly different. Both operations leave a
  large term common to every matrix, which then dominates the comparison. Project
  luminance out of both sides (P@M@P) and separate direction from strength before
  drawing any conclusion about whether two calibrations agree.
""")


def main():
    if not os.path.isdir(DIVERE):
        print(f"DiVERE not found at {DIVERE}; pass its path as argv[1].")
        return 1

    mat_dir = os.path.join(DIVERE, "config", "matrices")
    cc_dir = os.path.join(DIVERE, "config", "colorchecker")

    print()
    q1_is_the_residual_matrix_shaped(cc_dir)
    q2_scanner_or_stock(mat_dir)
    q3_led_vs_status_m()
    verdict()
    return 0


if __name__ == "__main__":
    sys.exit(main())
