# OpenRevelare — How It Works

This document is for readers who want to understand OpenRevelare's internals; it walks through
every physical and mathematical step between the RAW file and the finished positive. For how to
operate it, see [GUIDE.en.md](GUIDE.en.md).

---

## FilmBase and SceneBase: the philosophy behind the two-stage workflow

OpenRevelare splits the whole process into two stages of quite different character, matching the
two areas of the interface.

**FilmBase (stage one) — physical reconstruction**

Every FilmBase parameter describes an objective physical property of this roll of film: the optical
density and colour of the film base ($T_\text{base}$), the maximum density the film can record
($D_\text{max}$), the density balance between channels ($w_\text{high}$ / $w_\text{offset}$), the
contrast slope of the inversion (grade) and the chroma reconstruction coefficient (chroma_grade).

These parameters are not aesthetic choices; they are measurements. Same brand and batch of film,
same processing, and the FilmBase parameters are in theory identical for every frame — which is
what makes one calibration for a whole roll possible in the first place.

Once calibrated, what FilmBase puts out is a **physically correct linear positive**: transmittance
faithfully reconstructed as linear light, colour structure coming from the dyes themselves, with no
subjective flattery anywhere in it. That linear positive is exactly what the NONE output intent
exports, and it is what you hand to DaVinci Resolve, Nuke or another professional grading tool.

**SceneBase (stage two) — aesthetic adjustment**

The SceneBase parameters are **subjective decisions**: colour-temperature preference, exposure
brightness, contrast style, final saturation. One negative can carry entirely different SceneBase
settings for different viewing purposes and different deliverables.

This stage works in the linear light domain throughout (the sRGB gamma encoding is the last step of
all), every adjustment has a clear physical or perceptual meaning, and none of them are coupled to
each other.

**Why the split**

The point of separating the two: the FilmBase calibration is objective, done once, shareable across
a roll and reusable across projects; SceneBase is a per-frame creative decision that never
contaminates the physical reconstruction layer. A "virtual copy" holds several sets of SceneBase
settings against one negative while the FilmBase parameters stay shared.

---

## The pipeline, step by step

### Step 1: RAW decoding (linearisation)

A RAW file holds the sensor's raw photon counts, in Bayer mosaic form. The camera body lays white
balance, a tone curve and a colour matrix over them; for ordinary photography that is flattery, but
for a negative inversion it is contamination — it distorts the physical transmittance of the film
base.

What the decode has to produce is the **camera's native linear light**: the camera white balance
off (gain of 1 on every channel, i.e. UniWB), no colour matrix applied, gamma left at 1 (linear
output), demosaicing by AHD, and a float32 image normalised to [0, 1] at the end.

The white-light path (Path B) and the RGB decoupling path (Path A) both have to start from that
same line. Bake the camera white balance in at decode time and the orange backing of the film base
is stretched asymmetrically, in a way the later density normalisation cannot undo.

Optional back ends:
- **rawpy / LibRaw** (default): cross-platform, decodes the raw Bayer data directly.
- **Adobe DNG Converter** (Windows, optional): a two-pass route (RAW → Bayer DNG → linear DNG)
  using Adobe's demosaic for higher quality; EXIF is recognised automatically.

---

### Step 1 (alternative path): loading and linearising scanner TIFF

A TIFF from a film scanner is the alternative input to a RAW decode. Like RAW, it has to be brought
back to **linear light** before it enters the density inversion — because the first step of the
inversion is $D = -\log_{10}(T)$, and that arithmetic assumes flatly that its input $T$ is linear
transmittance. Get the gamma state wrong (treating something linear as sRGB, or the reverse) and
$\log_{10}$ distorts the entire density curve, with more error in the shadows than in the
highlights (a logarithm is more sensitive to small values), showing up as colour drift after the
inversion. Which is why "is the input linear?" matters more than any grading parameter downstream.

The gamma a scanner puts out does not come from the sensor (CCD/CMOS are linear in themselves) but
from an output TRC curve the scanning software **applied deliberately** and wrote into the file's
ICC profile. The software probes that and picks a linearisation accordingly:

- **Parse the ICC's TRC curves**: read the profile's `rTRC`/`gTRC`/`bTRC` tags (both the `curv`
  sampled-curve and `para` parametric-curve types are supported) and work out whether the transfer
  function is linear, sRGB, γ≈2.2 or some other device gamma. The device profiles of professional
  scanners (Flextight, Noritsu and the like) often carry a non-standard gamma — one measured
  Flextight X5 came out at an equivalent γ≈1.5–1.6 — which is neither linear nor sRGB.

- **Invert the gamma accurately, per channel**: where the curve is a non-standard device gamma, the
  software builds the inverse mapping (`np.interp`) from the file's **own sampled TRC curves** and
  takes the encoded values back to linear channel by channel (R/G/B each with its own curve),
  rather than applying an approximate inverse sRGB curve. The three channels are handled
  separately because a scanner's three channels differ in spectral response and gain, so their
  curves genuinely differ.

- **Snap to linear when it is near enough**: if the curve's largest departure from the diagonal is
  under roughly one 8-bit quantisation step (a tolerance of 0.004, i.e. γ≈1.01) and **all three
  channels** satisfy that, it is treated as linear and the values are kept as they are — no point
  running a meaningless inversion over an essentially linear curve and adding interpolation noise
  for it. The test is per channel, so a channel with a real gamma is never silently skipped.

- **Fallback**: with no ICC, or with no usable TRC tags, a warning is issued and standard sRGB gamma
  is assumed (the reasonable default for most non-linear scan output).

- **Step 2: the device primaries matrix (ICC rXYZ/gXYZ/bXYZ)**: inverting the TRC only solves the
  "encoding is non-linear" problem, but the difference in gamma between the three channels (the
  Flextight measured at $\gamma_R \approx 1.62,\ \gamma_G \approx 1.51,\ \gamma_B \approx 1.56$)
  means their relative gains after inversion differ too, and one TRC pass still does not leave the
  three channels as proportional scalings of one physical spectral quantity. That produces colour
  casts running in opposite directions at different brightnesses — correct the mid-tones with one
  set of white balance parameters and the shadows or highlights still lean the other way, which no
  linear WB can fix.

  The root of it: the TRC differences between channels reflect an asymmetry in the scanner's
  **spectral response and gain** themselves. That is a device primaries problem, not an encoding
  one. The ICC specification records the two layers separately: the TRC tags describe the encoding
  curve, and the rXYZ/gXYZ/bXYZ tags describe how the device primaries map to D50 CIE XYZ.

  Complete linearisation therefore takes two steps:

  $$\text{linear device RGB} \xrightarrow{M = M_\text{sRGB→D50}^{-1} \cdot [rXYZ \mid gXYZ \mid bXYZ]} \text{linear sRGB}$$

  The matrix $M$ converts the scanner's linear device RGB into standard linear sRGB (both under the
  D50 white point). Measured on a Flextight X5 it comes to roughly:

  $$M \approx \begin{pmatrix} 1.258 & -0.158 & -0.099 \\ -0.174 & 1.241 & -0.068 \\ -0.001 & -0.166 & 1.166 \end{pmatrix}$$

  The matrix is applied only when the ICC profile carries rXYZ/gXYZ/bXYZ tags (most professional
  scanners do); with a LUT-only profile those tags are missing, and step 2 is skipped with a
  warning.

**How this relates to the RAW path, and why chroma_grade differs**

After the TRC inversion and the matrix, the TIFF path puts out **standard linear sRGB**, aligned in
colour space with the camera-native linear light of the RAW path (both linear, both ready to enter
the density domain).

The two paths currently take different `chroma_grade` defaults — 3.05 for RAW, 1.0 for TIFF
(passthrough, no extra chroma amplification).

**That split has no defensible basis.** The earlier account was that 3.05 undoes the camera CFA's
channel crosstalk while the scanner's ICC matrix has already undone its own, so scans need none.
Tracing where 3.05 actually came from, that explanation does not hold: the dataset it was fitted
against carries no camera or scanner information whatsoever, and what it compensates is not sensor
crosstalk. The full trace is in [CALIBRATION.md](../../../../docs/CALIBRATION.md).

The practical consequence is that one and the same negative renders differently depending on
whether it was copied with a camera or run through a scanner, with no physical justification for
the difference. The status quo is kept only so existing projects render unchanged; the real fix is
the colour-space rendering described below, at which point this split retires along with
`chroma_grade` itself.

When a file carries no ICC profile, its samples are taken as already linear (no TRC inverse, no
matrix); when the profile is LUT-only and has no rXYZ/gXYZ/bXYZ tags, the matrix step is skipped.
In both cases the scanner's channel differences go uncorrected, so pull back any resulting cast
with SceneBase's saturation and white balance.

Scanner TIFF goes down Path B (the white-light path); Lensfun and RGB decoupling are not used.

---

### Step 2: Lensfun lens correction (optional, linear domain)

Every correction happens in the **linear light domain**, after the decode and before the inversion,
which is the only correct moment for it:

- **Distortion**: pixel coordinates are remapped from the Lensfun database's polynomial model.
- **Chromatic aberration**: R/G/B are remapped with different scale factors each, removing purple
  and green fringing.
- **Vignetting**: per-pixel brightness compensation for the falloff towards the corners.

The EXIF information (camera model, lens model, focal length, aperture) is read automatically and
matched against the Lensfun database; there is nothing to pick by hand.

---

### Step 3: the light-source branch

#### Path B — broadband source (white light, recommended)

White light (a tungsten or daylight box) approximates a continuous spectrum and lights all three
CMY dye layers evenly across the band. The camera's three sensor channels each read the mixed
signal, and it goes straight into the density domain with no extra calibration.

#### Path A — narrowband source (RGB mix)

Monochromatic RGB LEDs light the negative one band at a time, so each colour of light mainly
excites the absorption of its corresponding dye layer, which makes the **channel crosstalk** at the
sensor measurable and separable exactly.

**Automatic identification of the RGB calibration shots** (commit 8cd2638)

Early versions required the calibration shots to be named `R.ARW` / `G.ARW` / `B.ARW`, which raised
the bar for using it. The improved implementation identifies them **by content** (argmax):

1. **ROI sampling**: for each candidate image, the mean of the three RGB channels is taken over the
   central region (50% × 50%), giving a vector $v = [R, G, B]$.
2. **Argmax classification**: compute $\text{argmax}(v)$ — whichever channel is largest names the
   colour of that calibration shot. The physical basis: when the R lamp lights the bare box, the
   sensor's R channel reads far higher than G or B; the G and B lamps likewise.
3. **Uniqueness check**: the three images' argmax values must all differ (one R, one G, one B);
   otherwise identification fails and an error is raised.
4. **GUI confirmation**: the result is shown in a dialog listing each image's ROI mean (e.g.
   `R=245 G=78 B=52`), and the channels can be corrected from drop-downs before confirming.

**Both dimming modes work**

Path A supports two light-source configurations, and identifies both correctly:
- **White-light mode**: R/G/B intensities adjusted until the mixed light is pure white. The
  calibration shots' argmax still holds, because even with the three intensities close together
  each channel's maximum still corresponds to the lamp that excited it.
- **Neutral-base mode**: R/G/B intensities adjusted until the light through the film base is
  neutral, cancelling the mask physically. The three calibration shots then differ in absolute
  brightness, but the argmax relationship is unchanged.

**Building the calibration matrix**: three calibration shots are taken with no film in place (the R
lamp, the G lamp and the B lamp each lighting a blank area in front of the lens on its own), and
each gives a vector of the sensor's three channel means, $v_R,\ v_G,\ v_B$. Those three vectors are
stacked into an observation matrix, inverted, and each row divided by its own sum, giving the
decouple matrix $M$:

$$M_\text{obs} = [v_R \mid v_G \mid v_B], \qquad M = \text{rowNorm}(M_\text{obs}^{-1})$$

The mathematical shape of $M$: diagonal elements > 1 (each light mainly excites its own channel),
off-diagonal elements negative (crosstalk subtracted), row sums = 1.

**Two ways of implementing the decoupling**

The software offers two implementations, switchable in the preferences. Both use the same
calibration matrix $M$; what differs is the signal domain the matrix acts in.

---

**Option one: linear-domain decoupling (default, physically correct)**

Channel crosstalk in a CFA sensor happens at photoelectric conversion — physically it is a linear
superposition. So applying the matrix directly to linear transmittance is the physically exact
thing to do:

$$T_\text{dec} = T_\text{raw} \cdot M^T$$

which is equivalent to undoing the linear mixing at the sensor outright.

**Per-pixel gamut mapping**: $M$'s off-diagonal elements are negative, so where a channel's
original value is close to zero (shadows, high-density areas) the matrix multiplication can push it
below 0. To keep the following $-\log_{10}$ from catastrophically amplifying a negative or
vanishingly small value, for every pixel about to produce a negative channel a blend coefficient
$\alpha$ is computed that puts the smallest channel exactly at $\varepsilon$ ($10^{-6}$):

$$T_\text{out}[i] = (1 - \alpha_i)\,T_\text{raw}[i] + \alpha_i\,T_\text{dec}[i], \qquad \alpha_i = \min_c \frac{T_\text{raw}[i,c] - \varepsilon}{T_\text{raw}[i,c] - T_\text{dec}[i,c]}$$

Highlights and mid-tones therefore get the full decoupling ($\alpha = 1$), and only the very
darkest pixels are pulled back locally, by as little as possible.

**chroma_amp compensation**: small differences between channels in the linear domain are amplified
non-linearly by $-\log_{10}$ once in the density domain. Decoupling separates the channels more
thoroughly → density chroma widens → without compensation the inversion oversaturates. The
amplification is measured:

$$\text{chroma\_amp} = \frac{\text{std}(D_\text{chroma,\ after})}{\text{std}(D_\text{chroma,\ before})}$$

and `chroma_grade` is divided by it during the inversion, so a decoupled roll and a white-light roll
arrive at the same saturation.

**Characteristics**:
- Physically exact — it corresponds directly to the linear nature of CFA crosstalk
- Highlights and mid-tones fully decoupled, shadows giving ground only as far as needed
- Independent of $T_\text{base}$ (the matrix acts on raw transmittance directly)
- Needs the gamut-mapping safety net plus the chroma_amp compensation (more complex code)

---

**Option two: density-domain decoupling (conservative, no risk of negatives)**

The signal is taken into the density domain first, luminance separated from chroma, and the matrix
applied to the chroma component:

$$D = -\log_{10}\!\bigl(\max(T_\text{norm},\ 10^{-D_\text{max}})\bigr)$$
$$D_\text{mean} = \frac{D_R + D_G + D_B}{3}, \qquad D_\text{chroma} = D - D_\text{mean}$$
$$D'_\text{chroma} = D_\text{chroma}\,M^T - \overline{D_\text{chroma}\,M^T}$$
$$D_\text{out} = D_\text{mean} + D'_\text{chroma}$$

and then back to linear: $T_\text{dec} = 10^{-D_\text{out}} \cdot T_\text{base\_approx}$

Because the matrix acts on density chroma (a zero-mean component), the output density is always
dominated by $D_\text{mean}$, which is positive — there is no way to arrive at a negative value or
a catastrophic near-zero.

**Characteristics**:
- No gamut mapping needed — operating in the density domain is safe by construction
- Globally consistent; there is no "some pixels give ground" non-uniformity
- **Physically inexact** — CFA crosstalk is a linear-domain phenomenon, and doing matrix arithmetic
  in the log domain applies a non-linear distortion to the mixing relationship. The crosstalk
  proportions are the same in shadows and highlights, but the numerical ranges in the density
  domain differ greatly between highlights (low density) and shadows (high density), so the
  matrix's actual effect at the two ends is asymmetric
- Depends on $T_\text{base\_approx}$ (a normalisation reference has to be estimated)
- May need a global alpha attenuation (when extreme chroma drives a density negative), at which
  point every pixel is discounted equally and the decoupling is incomplete

---

**The two options side by side**

| | Linear domain (default) | Density domain |
|---|---|---|
| Physical correctness | Exact (matches linear CFA crosstalk) | Approximate (operating in the wrong domain) |
| Risk of negatives / blow-up | Yes, backstopped by gamut mapping | None |
| Information altered | Local pull-back on the very darkest pixels (negligible) | Non-linear distortion accepted on every pixel (silently) |
| Completeness of decoupling | 99%+ of pixels fully decoupled | Possibly attenuated globally to 70–90% |
| Dependence on T_base | None | Yes |
| When to use | Recommended generally | A fallback if the linear domain misbehaves |

How to choose: for the overwhelming majority of film (film-base transmittance 0.2–0.8, content
density range 0.3–2.5), the linear-domain option's gamut mapping touches under 1% of pixels
(verified), and the density range those pixels sit in (> 2.5D) maps to near-pure-white highlights
in the positive, indistinguishable to the eye. The density-domain option is kept as a conservative
fallback for unusual cases (a badly underexposed roll, an unconventional emulsion).

**The limits of Path A's accuracy**

What Path A solves is the spectral coupling at the **light-source-and-film** level: in white light
every wavelength passes through all three CMY dye layers at once, and the sensor signal is an
aliasing of the joint response of all three; narrowband R/G/B physically cuts the inter-layer
coupling, and the decouple matrix quantifies and separates that aliasing in the density domain.
That is the whole of the crosstalk Path A can deal with.

A copy setup does, however, contain a second layer of crosstalk — the **spectral mismatch at the
sensor-and-paper level**. The orange backing of colour negative film (the mask) was designed
chemically for the spectral response of silver-halide printing paper: the paper's cyan dye absorbs
extra red light, and the orange backing cancels it with a matching complementary density so the
print comes out neutral. A digital sensor's CFA has different spectral curves from printing paper,
so that cancellation does not hold in the digital domain and a systematic colour cast results. Path
A's decouple matrix comes from a calibration of the light-source response and carries no
information about the relationship between the sensor's spectra and the paper's, so it cannot
compensate for that second mismatch.

Once the first layer has been dealt with on its own, the residual of the second remains, typically
around ΔE 3–6 (depending on how far the sensor's spectra depart from the reference paper's).
Pushing both residuals below ΔE 2 needs an additional calibration from colour-chart data and a
channel-mixing matrix applied in the density domain (i.e. modelling the second layer separately) —
which is beyond what Path A does today, and a direction it could be extended in.

---

### Before step 4: the sprocket mask and automatic film-base detection

Before the film-base normalisation, the software has to deal with the sprocket holes and the bare
light panel in the copied picture — areas with no emulsion on them at all, "fully transmissive", at
a brightness near 1.0, far above the film base (the orange backing, transmitting roughly 0.2–0.8).
Left undistinguished, they contaminate the film-base sample.

**The sprocket mask**: the software builds a binary mask from a brightness threshold, excluding
pixels above it (sprockets, light panel) and keeping the ones that genuinely belong to the film.
The threshold is detected automatically by histogram analysis (finding the valley between the
light-panel peak and the film-base peak), but it depends on how even the light is and on the
exposure, and is **not 100% reliable**. It can be fine-tuned by hand in the confirmation window.

**Automatic film-base detection**: once the sprocket mask is confirmed, the software runs a
film-base detection over the whole roll — with the light panel excluded by the mask, a bright-end
sample is taken frame by frame, and the median across the roll becomes the initial $T_\text{base}$.

Sampling a frame is subject to two constraints:

- **Co-sited sampling**: the kept pixels are sorted by brightness, a stretch at the bright end is
  taken, and the three channel means are computed over **that same set of pixels**. The film base is
  one physical material, so all three channels have to be read from the same place; take a
  percentile independently per channel and R, G and B come from three different points in the
  picture, branding a false colour cast into the reference that every later density division rests
  on. (The same reasoning applies to automatic highlight white balance, `HighlightDensityFromRoll`.)
- **Spike guard**: with brightness quantised to 16 bits, if any single quantisation bucket holds
  more than 10× the sampling depth on its own while the cumulative count so far is still under 20×,
  the whole bucket is skipped. A clipped light panel, specular highlights and a solid-colour border
  all pile into one bucket and monopolise the bright end; real film base carries grain and an
  illumination gradient, spreads over several buckets, and is unaffected.

Even so, the result is **a starting point only**: automatic detection assumes the film base is the
brightest non-panel area in the roll, and if some frames hold unclipped blown highlights (sky,
lamps) those get mixed in. **Box the film base by hand and calibrate again** for an accurate result.

---

### Step 4: film-base normalisation and the density conversion

**What the film base (the D_min area) is physically made of**

A colour negative's film base is the **unexposed area**, and it is not truly "clear". It has two
parts:

1. **The slight absorption of the base material itself**: cellulose acetate or polyester absorbs
   blue light slightly, which leaves the base a little yellow.
2. **The orange anti-halation layer (particular to colour negative)**: to control halation between
   dye layers, manufacturers put orange couplers behind the emulsion. That orange backing leaves
   the base's three transmittances badly unequal, usually $T_R > T_G > T_B$.

Together they mean that, as far as the copying camera is concerned, the film base is a coloured
translucent body whose transmittance can differ two- or threefold between colour channels. Without
removing it, every later density calculation rests on a tilted reference, and every density value
carries a systematic per-channel bias.

The film-base transmittance vector $T_\text{base} = [T_R, T_G, T_B]$ is sampled from an unexposed
edge area of the negative, and each frame is normalised per channel:

$$T_\text{norm} = T / T_\text{base}$$

which does two things at once: it removes $D_\text{min}$ (after normalisation, a blank area
transmits 1 in every channel, i.e. density = 0), and it removes the orange cast at the shadow end
(each channel divided by its own base value cancels the orange offset physically).

Then into the density domain. Optical density is defined as the negative logarithm of
transmittance, with a floor to keep the arithmetic from overflowing:

$$D = -\log_{10}\!\bigl(\max(T_\text{norm},\ 10^{-D_\text{max}})\bigr)$$

Density normally runs from 0 (fully clear) to over 3.0 (very high density); the floor prevents
$\log(0) = -\infty$.

---

### Step 5: white balance in the density domain (both ends)

White balance in the density domain has two independent corrections, matching the Negadoctor
two-ended calibration model. For each channel $c$:

$$D_\text{corr}[c] = D[c] \times w_\text{high}[c] + w_\text{offset}[c]$$

**$w_\text{offset}$ (shadow end, additive)**: sampled on a dark area that ought to read neutral grey
in the positive, lining the three channels' densities up with the highest of them and removing the
shadow-end cast. An additive correction cannot diverge in a high-density area, which is why
**$w_\text{offset}$ is calibrated first**.

**$w_\text{high}$ (highlight end, multiplicative)**: sampled on a highlight that ought to read
neutral white in the positive, solved with the $w_\text{offset}$ already set folded in, so that
$(D \times w_\text{high} + w_\text{offset})$ comes out level across the three channels. The
multiplicative term is calibrated second and reads the existing $w_\text{offset}$, so the two never
interfere.

Why the order matters: calibrate $w_\text{high}$ first and $w_\text{offset}$ second, and the second
moves the densities the first had lined up, so the two ends fight each other (a residual density
error of about 0.7 in measurement). Shadows first, highlights second, and the final residual is
about 0.04.

---

### Step 6: D_max calibration

$D_\text{max}$ is the film's physical density range (the density at its darkest point), and it
decides where the maximum brightness after inversion gets mapped to 1.0 (white).

It is sampled from the darkest area of the already-normalised image ($T_\text{norm}$): the mean
density of each channel is taken, and then the largest of the three, which guarantees no channel is
clipped:

$$D_\text{max} = \max_c\!\bigl(\overline{D_c}\;\big|_\text{shadow region}\bigr)$$

In roll mode $D_\text{max}$ is the maximum across every frame in the roll, so shadow density stays
consistent throughout (otherwise the shadows vary in brightness frame to frame).

---

### Step 7: the Cineon density-domain inversion

This is the core algorithm. It follows the Cineon log-density principle, mapping the negative's
measured densities to positive densities and then back to linear light.

**Where the Cineon principle comes from**

The Cineon log encoding standard was drawn up by Kodak in the 1990s for digitising motion-picture
film. The idea at its centre: express the film's optical density on a log scale so that the digital
encoding corresponds directly to the photochemistry (the density-exposure curve being logarithmic),
rather than forcing it into a linear or gamma space. Arithmetic in the density domain therefore has
a definite physical meaning, and every step can be traced back to some specific property of the
film chemistry.

**Luminance and chroma handled independently**

The C-41 process compresses luminance and chroma by different amounts, and one uniform per-channel
gamma cannot reconstruct both. Density is decomposed into a luminance component (the mean density
of the three channels) and a chroma component (each channel's departure from that mean), controlled
by two independent parameters:

$$D_\text{mean} = \frac{D_R + D_G + D_B}{3}, \qquad D_\text{chroma} = D - D_\text{mean}$$

$$D_\text{adj} = \text{pivot} + (D_\text{mean} - \text{pivot}) \times \text{grade} + D_\text{chroma} \times \frac{\text{chroma\_grade}}{\text{chroma\_amp}} - D_\text{max}$$

$$T_\text{pos} = 10^{D_\text{adj}}$$

**grade (paper grade)**: C-41 negative is deliberately designed to be low-contrast (film
$\gamma \approx 0.5\text{–}0.65$), because it was always going to be enlarged onto high-contrast
paper ($\gamma \approx 2.5\text{–}3.5$), bringing the whole printing chain's combined $\gamma$ back
to about 1.0. Digitising has no "high-contrast paper" link in it, so a plain inversion (grade =
1.0) puts out a flat, grey, low-contrast image. grade = 1.65 ($\approx 1/0.6$) puts back the
contrast a typical C-41 negative is missing, and the pivot parameter sets where the mid-tone anchor
sits, so mid-tone brightness holds steady as grade is changed.

**chroma_grade (the chroma scale)**: a scalar applied to the chroma component in the density
domain. Defaults to 3.05 (RAW) / 1.0 (scans).

**This parameter rests on weak foundations and is being replaced.** The full trace is in
[CALIBRATION.md](../../../../docs/CALIBRATION.md); the essentials:

- 3.05 was fitted against DiVERE's Kodak Gold 200 + ColorChecker 24 dataset. That dataset is a
  spectral **simulation** of theoretical densities (its own descriptor says `"type": "DensityExp"`),
  not a measurement — earlier documentation called it "measured", which was inaccurate.
- The fit targeted moving the 18 chromatic patches' mean saturation from 0.79 back to 0.97. But
  that 0.79 was measured in the **Kodak Endura Premier paper gamut**, whereas the pipeline outputs
  sRGB, with no paper stage anywhere in between.
- The deficit was attributed to dye interlayer crosstalk and DIR couplers, yet the dataset used is
  the `auc_noDIR` variant — DIR is explicitly switched off in it, so DIR cannot be the cause.
- "The paper gamut is narrow" does not survive checking either: the gamut accounts for only about
  6% (`docs/calibration/gamut_check.py`), not 21%. The real origin of the deficit is **unresolved**.
- The only executable quantification in the repository (`docs/calibration/study_saturation.py`)
  points the opposite way: on a synthetic negative, per-channel inversion comes out 11–14% *over*
  the true scene, and the compensation required is 0.

More fundamentally: **a scalar cannot fix a gamut problem.** Gamut relationships are anisotropic
and hue-dependent — paper does not compress cyan and magenta by the same amount. That same script
measures the residual after scalar compensation at mean 0.093 / max 0.206: it does not flatten.

The root cause of all of this is that the linear data leaving the inversion never declared a colour
space, implicitly being "sRGB because that is what we export". With no gamut to convert into, a
scalar on the chroma vector was the only lever available. The correct fix is real colour-space
rendering — see below.

**chroma_grade is not exposed in the GUI**: as of 1.0 the preferences no longer offer presets or a
custom value for chroma_grade; the value follows the input type instead (3.05 for RAW, 1.0 for a
scan). The parameter itself is
still there — it is the `chroma_grade` field in the project file and `--chroma-grade` on the CLI,
and either route can change it.

Withdrawing the GUI entry point was deliberate: compensating chroma with a single global scalar is
a stopgap whose basis has since been traced and found wanting (above), and until it is replaced it
should not become a public interface users depend on. For "make it richer or lighter", use the
SceneBase saturation slider.

**On how chroma_grade is calibrated: why not a colour chart per roll**

OpenRevelare's chroma_grade is a fixed global preset. The more accurate approach is to measure per
roll — shoot a ColorChecker on one frame of every roll, copy it after processing, and solve
automatically from the chart data for this roll's actual parameter deviation under this
development, calibrating the compensation matrix for the sensor-paper spectral mismatch (the second
layer of crosstalk described in step 3) at the same time. That is the most accurate route available
today, and it is what [DiVERE](https://github.com/flipswitchingmonkey/DiVERE) does.

OpenRevelare does not do per-roll chart calibration because of what it costs to use: buying a
ColorChecker, giving up a frame on every roll to it, and copying it separately after processing is
a workflow cost far beyond what most film photographers need in accuracy.

**If you need absolute rigour**: for a workflow that needs traceable colour accuracy — copying
cultural artefacts, commercial archives, scientific use — use DiVERE directly.

**chroma_amp (RGB path only)**: the RGB decouple matrix amplifies chroma further in the density
domain (about 2×), and without compensation Path A's output oversaturates. The pipeline measures
the ratio of chroma standard deviations before and after decoupling (averaged over the three
channels) and divides chroma_grade by it during the inversion, so Path A and Path B come out at the
same saturation automatically.

#### What is particular about ECN-2 motion-picture film

ECN-2 is Kodak's process for motion-picture negative (Kodak Vision 3, Fuji Eterna, Kodak 5219 and
the like all belong to it). The Cineon standard was itself designed for the digital scanning of
ECN-2 motion-picture film — in that sense ECN-2 is the *native* case for Cineon density-domain
processing, and C-41 is borrowing it.

Several differences between ECN-2 and C-41 bear on OpenRevelare's settings:

**The film base is a different colour ($T_\text{base}$)**: C-41's orange backing is a standardised
result of a settled process, and varies little between brands. ECN-2 film carries a rem-jet
antistatic backing (a carbon-black coating that has to be removed mechanically during processing).
Once the rem-jet is off, the base often keeps a residual brown-red or magenta cast, and its
channel ratios differ noticeably from C-41's. Processed at home in a C-41-compatible chemistry
(so-called cross-processing), incompletely removed rem-jet is commoner still and the base colour is
harder to predict. Sampling $T_\text{base}$ accurately therefore matters more with ECN-2.

**A wider dynamic range ($D_\text{max}$)**: motion-picture film is designed for theatrical
projection and a digital intermediate workflow, and has to hold extreme scenes from deep shadow to
strong highlight; its dynamic range is usually half a stop to a stop above C-41 consumer colour
negative. The D_max sample comes out larger (typically about 2.5–3.0, against about 2.0–2.5 for
C-41), and how far the grade parameter should adjust contrast needs reassessing with it.

**Chroma**: ECN-2's DIR coupler formulation and dye layer structure differ from C-41's, and
motion-picture film's logic for handling saturation comes from theatrical print standards, not
C-41's consumer-photography logic. At chroma_grade = 3.05, ECN-2 stocks often come out either
oversaturated or weak depending on the brand; compensate on the SceneBase saturation slider.

There is no plan to calibrate a separate chroma_grade for ECN-2 — the parameter is weakly founded
and on its way out (above). The corresponding fix for ECN-2 is to render into the Kodak 2383 print
film gamut, which is precisely the target gamut a motion-picture negative has in the theatrical
chain; that route becomes available once colour-space rendering lands.

---

### Step 8: Stage 2 post-processing (the BASIC output intent)

With the output intent set to **NONE (linear export)**, Stage 2 is skipped and the linear positive
from the previous step is written out as it is. That is FilmBase's standalone output, ready for
professional grading software.

With the output intent set to **BASIC**, the following chain runs (all in the linear light domain,
except the last step):

1. **White balance gain**: positive-domain white balance on the final positive, for fine
   colour-temperature trims.
2. **Exposure compensation**: linear multiplicative scaling, for overall brightness.
3. **Levels**: black-point/white-point stretching, mapped linearly into [0, 1].
4. **Contrast**: an S-curve; adjusted in the linear domain it introduces no hue shift.
5. **Saturation**: chroma deviation scaled along the luminance axis in linear light RGB, which has
   a physical meaning (this is not a rotation of the hue-saturation wheel).
6. **sRGB TRC (gamma)**: the last step, the IEC 61966-2-1 standard transfer function, encoding
   linear light into display gamma.

---

### Step 9: export

**TIFF (16-bit)**: AdobeRGB (1998) gamut, $\gamma = 563/256 \approx 2.199$; ICC profile embedded;
16-bit depth to keep highlight gradation and headroom for grading.

**JPEG (sRGB)**: sRGB gamut, the IEC 61966-2-1 TRC; sRGB ICC profile embedded; TurboJPEG
acceleration supported underneath (an optional dependency).

**Global consistency in roll mode**: $T_\text{base}$, $D_\text{max}$, the decouple matrix, $\alpha$
and chroma_amp are all computed at roll level, and every frame shares one set of parameters, so the
roll's colour does not jump frame to frame — ready to go straight onto a contact sheet.
