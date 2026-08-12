# OpenRevelare — How It Works

This document specifies the colour transformations by which a digitised negative sample becomes a
positive. For operating instructions, see [GUIDE.en.md](GUIDE.en.md).

The processing chain consists of a front end and a core. The front end comprises three parallel
paths (Path A / Path B / TIFF), all of which output linear light. The core is the Cineon
density-domain inversion, which applies identical processing to the output of all three paths.

---

## 1. Colour space division

### 1.1 Chain structure

```
input primaries ─[input transform]─▶ working space ACEScg (scene-referred, linear)
                              │  −log10
                              ▼
                        density domain: inversion / t_base / per-channel endpoints
                        no colour operations
                              │  10^x
                              ▼
                        ACEScg (linear positive)
                              │
                        [step 4: primaries and gamma together]
                              ▼
                        output space (display-encoded)
                              │
                          Stage 2 frame edits
                              │
                ┌─────────────┴─────────────┐
                ▼                            ▼
           exported file                  preview
```

### 1.2 Primaries in the density domain

$-\log_{10}$ is a per-channel change of scale and does not alter the chromatic meaning of a
channel: the primaries entering the log domain are the primaries leaving it. All gamut conversion
therefore sits at the input and output ends, and the density domain contains no colour operations.

### 1.3 Working space and output space

The two serve different functions and are declared separately.

**Working space** is the carrier through the inversion, set to ACEScg. Its gamut encloses every
output space, so saturated dyes outside sRGB pass through the density domain intact and are placed
by the output transform at the end of the chain.

**Output space** is the domain in which the Stage 2 adjustment controls are defined. Control
semantics depend on a bounded display-referred space: contrast pivots on 0.5 as mid-grey, the
levels endpoints are 0 and 1, and curve control points lie on a bounded perceptual ramp. ACEScg
mid-grey 0.18 lands at 0.489 after step 4 converts it into the display space (Rec709 transfer
function).

ACEScg is scene-linear and unbounded, and therefore serves as the working space rather than the
output space.

---

## 2. Input front ends

The three paths are parallel and all output linear light. They differ in how linearisation is
performed, and in whether their output carries a colour space declaration.

### 2.1 Path B — broad-spectrum copy (camera RAW)

A white light source (tungsten or daylight light box) approximates a continuous spectrum,
illuminating all three CMY dye layers across every band simultaneously; the camera's three
channels each read a mixed signal.

A RAW file stores the sensor's raw photon counts (Bayer mosaic). White balance, tone curves and
colour matrices applied by the camera body alter the film base's physical transmittance, so
decoding must take the camera's native linear light:

- camera white balance off, all channel gains at 1 (UniWB)
- no colour matrix applied
- gamma of 1
- AHD demosaic
- output normalised to float32 in [0,1]

Decoding back ends: rawpy / LibRaw (default, cross-platform); Adobe DNG Converter (optional on
Windows, via the two steps RAW → Bayer DNG → linear DNG, using the Adobe demosaic algorithm).

Output sits in the camera's native primaries.

### 2.2 Path A — narrow-spectrum copy (camera RAW + RGB separation)

Monochromatic RGB LEDs illuminate the negative in separation, each colour of light exciting
principally the absorption of its corresponding dye layer. The decoding baseline matches Path B
(the same UniWB starting point, a precondition for the calibration matrix acting on content
frames), with channel-crosstalk separation added.

#### 2.2.1 Physical basis of the separation

Under white light the responses of the three dye layers alias together at the sensor. Narrow-band
R/G/B cuts the inter-layer coupling, making the aliasing a measurable linear mixture that can
therefore be calibrated and inverted.

#### 2.2.2 Calibration matrix

Three calibration frames containing no film are captured (R, G and B lamps each illuminating a
blank area separately), each yielding a mean sensor vector across the three channels $v_R,\ v_G,\
v_B$. These are assembled into an observation matrix, inverted, and each row divided by its row
sum:

$$M_\text{obs} = [v_R \mid v_G \mid v_B], \qquad M = \text{rowNorm}(M_\text{obs}^{-1})$$

Properties of $M$: diagonal elements > 1, off-diagonal elements negative, row sums of 1.

Calibration frames are assigned by content recognition: the mean of the three channels is taken
over a central 40%–60% window and classified by $\text{argmax}$. With the R lamp illuminating a
blank light box, the sensor's R channel reads higher than G/B; the G and B lamps follow the same
relation. The relation holds under both the white-light mode and the base-neutral mode.

#### 2.2.3 Domain of matrix application

Two implementations use the same $M$, applied in different signal domains.

**Linear domain (default)**: CFA channel crosstalk occurs at the photoelectric conversion stage
and is a linear superposition, so the matrix is applied to linear transmittance:

$$T_\text{dec} = T_\text{raw} \cdot M^T$$

The off-diagonal elements of $M$ are negative, so a channel may fall below 0 in the shadows. So
that the subsequent $-\log_{10}$ acts on positive values, a blend coefficient bringing the minimum
channel to $\varepsilon = 10^{-6}$ is computed for each pixel that would carry a negative channel:

$$T_\text{out}[i] = (1 - \alpha_i)\,T_\text{raw}[i] + \alpha_i\,T_\text{dec}[i], \qquad \alpha_i = \min_c \frac{T_\text{raw}[i,c] - \varepsilon}{T_\text{raw}[i,c] - T_\text{dec}[i,c]}$$

Highlights and mid-tones take $\alpha = 1$ and are fully decoupled; only the deepest shadow pixels
retreat locally. The operation is per-pixel and contains no cross-pixel statistic. For film with a
base transmittance of 0.2–0.8 and a content density range of 0.3–2.5, measured impact is under 1%
of pixels, corresponding to densities > 2.5D, which map to near-white highlights in the positive.

**Density domain**: after conversion into the density domain, luminance and chroma are separated
and the matrix acts on chroma alone:

$$D_\text{mean} = \tfrac{1}{3}(D_R + D_G + D_B), \qquad D_\text{chroma} = D - D_\text{mean}$$
$$D'_\text{chroma} = D_\text{chroma}\,M^T - \overline{D_\text{chroma}\,M^T}, \qquad D_\text{out} = D_\text{mean} + D'_\text{chroma}$$

Output density is dominated by the positive $D_\text{mean}$ and no negative values arise. The
matrix acts in the log domain, so the linear proportionality of the crosstalk is subject to
nonlinear distortion; the numeric ranges of highlights (low density) and shadows (high density)
differ considerably, so the matrix acts asymmetrically at the two ends. This implementation
depends on an estimated $T_\text{base}$, and extreme chroma requires a global alpha attenuation,
applied uniformly to all pixels.

| | Linear domain (default) | Density domain |
|---|---|---|
| Correspondence to CFA crosstalk | exact | approximate (log domain) |
| Negative-value risk | handled by gamut mapping | none |
| Information change | local retreat on deepest shadows | nonlinear distortion on all pixels |
| Decoupling completeness | 99%+ of pixels fully decoupled | may attenuate globally to 70–90% |
| Dependence on $T_\text{base}$ | none | yes |

#### 2.2.4 chroma_amp

Channel differences in the linear domain are amplified nonlinearly by $-\log_{10}$ in the density
domain: decoupling raises the degree of channel separation, and density chroma widens accordingly.
The amplification factor is

$$\text{chroma\_amp} = \frac{\text{std}(D_\text{chroma,\ after})}{\text{std}(D_\text{chroma,\ before})}$$

It is divided back out of the chroma component during inversion (see 4.5). This term corrects a
narrow-band light-source characteristic and is unrelated to the film.

Output sits in the camera's native primaries.

### 2.3 TIFF — scanner output

The first step of the inversion, $D = -\log_{10}(T)$, requires linear transmittance as input. The
logarithm is more sensitive at small values, so a gamma deviation is amplified more in the shadows
than in the highlights, appearing as colour drift after inversion.

Scanner gamma originates from an output TRC curve applied by the scanning software and written
into the ICC profile; the sensor (CCD/CMOS) is itself linear. This is the only one of the three
paths that carries a colour declaration, read in two steps.

#### 2.3.1 Step 1: TRC inversion

The profile's `rTRC`/`gTRC`/`bTRC` tags are read (both `curv` sampled curves and `para` parametric
curves are supported), an inverse mapping is built from the file's own curves, and each channel is
restored to linear separately. The channels are handled separately because the scanner's three
channels differ in spectral response and gain, so their curves differ (Flextight X5 measured at
$\gamma_R \approx 1.62,\ \gamma_G \approx 1.51,\ \gamma_B \approx 1.56$). Lookup-table sampling
density keeps the maximum error below half a 16-bit code level.

When a curve's maximum deviation from the diagonal is below 0.004 (about one 8-bit quantisation
step, corresponding to γ≈1.01) and all three channels satisfy this, it is judged linear and the
original values are retained. The judgement is made per channel.

When a file has no ICC profile, or no usable TRC tags, samples are treated as linear.

#### 2.3.2 Step 2: device primaries matrix

The TRC inversion addresses encoding nonlinearity. Because the three gammas differ, the relative
gain of each channel after inversion also differs, so after a single TRC the three channels are
still not proportionally scaled versions of the same physical spectral quantity. This produces
colour casts of opposite direction in different luminance regions, which linear white balance
cannot correct.

The cause is asymmetry in the scanner's own per-channel spectral response and gain, which belongs
to device primaries. The ICC specification records the two layers separately: the TRC describes
the encoding curve, and rXYZ/gXYZ/bXYZ describe the mapping from device primaries to D50 CIE XYZ.
Complete linearisation is therefore two steps:

$$\text{linear device RGB} \xrightarrow{M = M_\text{D50→working} \cdot [rXYZ \mid gXYZ \mid bXYZ]} \text{working-space linear RGB (ACEScg)}$$

The matrix targets the working space. Professional scanners' device primaries are typically wider
than sRGB (one unit measured at about 1.6× sRGB's primary-triangle area), so the target gamut must
be wide enough for the excess dye to enter the density computation. The matrix has two stages:
Bradford-adapt the D50 PCS to the working-space white point (ACEScg sits at ~D60), then convert
XYZ → working-space RGB. The target is derived from `ColorPipeline.Working`.

When a LUT-only profile lacks these tags, step 2 is skipped and the file retains its device native
primaries.

TIFF uses the white-light model and contains no RGB decoupling.

### 2.4 State at which the three paths converge

| Front end | Primaries on entry to the density domain |
|---|---|
| TIFF, ICC containing rXYZ/gXYZ/bXYZ | working space ACEScg, carried in by the device primaries matrix |
| TIFF, LUT-only or no ICC | device native primaries |
| Path A / Path B (RAW) | camera native primaries |

Only the first sits in the pipeline's declared working space. The others are processed as working
space: `InputTransform` (which carries declared input primaries into ACEScg) is conditioned on
`FrameParams.InputPrimaries`, and that quantity currently has no entry point.

This state does not affect the density inversion. The inversion is $D = -\log_{10}(T/T_\text{base})$,
where $T_\text{base}$ and the per-channel endpoints are taken from the same buffer; density is a
self-referential ratio and depends on no colour space assumption. The effect lies on the output
side, where step 4 interprets the buffer as ACEScg. Magnitudes are given in section 3.

---

## 3. Known limitation: input primaries are not declared

### 3.1 The absorbed component of the error

Step 4 applies the ACEScg → output space matrix to an unconverted buffer; taking sRGB as the
example:

$$M_{\text{ACEScg}\to\text{sRGB}} \approx \begin{pmatrix} 1.7313 & -0.6040 & -0.0801 \\ -0.1316 & 1.1348 & -0.0087 \\ -0.0246 & -0.1258 & 1.0656 \end{pmatrix}$$

Its row sums are 1.047 / 0.995 / 0.915, corresponding to roughly a 14% warm cast on neutral grey.
This component does not appear in the result: $T_\text{base}$ and the per-channel endpoints are
calibrated against the rendered output (film base read as neutral, darkest area read as neutral),
which corresponds to the matrix's diagonal part and is absorbed by per-channel normalisation.

### 3.2 The residual component

The off-diagonal residual after dividing out the per-channel gains ($M \cdot
\operatorname{diag}(M)^{-1}$):

$$\begin{pmatrix} 1 & -0.5323 & -0.0752 \\ -0.0760 & 1 & -0.0082 \\ -0.0142 & -0.1109 & 1 \end{pmatrix}$$

The G→R term is 0.53, constituting hue and saturation error, and no per-channel operation acts on
this component. The error is confined to saturated colour and does not include an overall cast.

### 3.3 Conditions for a solution

The quantity to be solved is the equivalent primaries of the whole chain: scene → film dye → light
source → lens → sensor CFA. The dye layer cannot be separated after the fact: what the sensor
reads is the density of three dye layers, whose absorption bands overlap the CFA passbands, and
that overlap is the object of the solve (`FrameParams.InputPrimaries` is defined as "the
EQUIVALENT primaries of the whole chain, sensor spectral response composed with the film's dye
transmission").

Calibration condition: the chart must pass through the film. A standard colour chart is
photographed on the emulsion being calibrated, that negative is copied on the copy rig being
calibrated, and the primaries are solved jointly with $T_\text{base}$ and the endpoints so that
the transform and the inversion are mutually consistent (DiVERE's `divere/utils/ccm_optimizer`
uses this method, and its `primaries_xy` is derived this way). One calibration per copy rig and
per emulsion.

The joint solve is a necessary condition: the matrix must participate in the calibration to be
consistent with its result. $T_\text{base}$ and `InputPrimaries` are therefore mutually coupled —
the base is sampled in the space the primaries declaration defines — and the two must be
established together.

---

## 4. Core: Cineon density-domain inversion

Processing is identical once the three front ends converge.

### 4.1 Film-base normalisation (shadow endpoint)

The base of a colour negative is the unexposed region and is not transparent. It has two
components: slight absorption of blue light by the base material itself (rendering the base
yellowish), and the orange anti-halation layer — an orange coupler placed behind the emulsion
layers to control halation between dye layers. Their sum gives three channel transmittances that
may differ by a factor of two to three, typically $T_R > T_G > T_B$.

Each channel is normalised by the sampled $T_\text{base} = [T_R, T_G, T_B]$:

$$T_\text{norm} = T / T_\text{base}$$

The operation removes two things at once: $D_\text{min}$ (blank areas take a transmittance of 1 in
every channel and a density of 0), and the orange cast at the shadow end (each channel is divided
by its corresponding base value).

This step is the shadow endpoint: $D_\text{min}$ is fixed at 0 for every channel and the mask is
removed with it, so only the highlight end remains to be declared.

### 4.2 Conversion to the density domain

Optical density is the negative logarithm of transmittance, with a lower clamp:

$$D = -\log_{10}\!\bigl(\max(T_\text{norm},\ 10^{-D_{\text{floor},c}})\bigr)$$

The clamp floor $D_{\text{floor},c}$ takes that channel's measured deepest density $D_{\max,c}$,
which is a different quantity from the output range (see 4.3). The physical range of density is
typically 0 (fully transparent) to above 3.0.

The film-base divide, the logarithm and the scan-exposure compensation are folded into a
per-channel 65536-entry lookup table. A 16-bit input holds only 65536 distinct values, so the
lookup is exact. Pixels raised above 1 by vignette correction fall outside the table and are
computed directly.

### 4.3 Highlight endpoint

The quantity declared at the highlight end is each channel's deepest density $D_{\max,c}$, taken
as the per-channel mean density over the darkest area of the normalised image — three values. The
three channels' deepest densities differ, and that difference is the highlight colour balance, so
retaining all three makes the highlight endpoint and the highlight cast one fact.

In roll mode the 90th percentile across frames is taken per channel. $D_{\max}$ is a property of
the film and its development rather than of any single scene, so one value is taken for the roll;
this percentile makes the result correspond to those frames that do contain a deep black region.

Sampling excludes two classes of pixel: light board and sprockets (by a luma cut), and pixels
whose total density exceeds 3.0 — fully opaque sprockets or frame edges reach the $-\log_{10}$
clamp (about 6–10), well above real picture tones (about 1–1.5). The criterion is the total
density across the three channels, taken or rejected per pixel, so that the relationship between
the three endpoints is not biased by per-channel independent rejection.

The scalar $D_\text{max}$ is a separate quantity: the output range, which determines where density
0 is mapped, uniform across the roll.

### 4.4 Endpoint nudges

$w_\text{offset}$ (shadow end) and $w_\text{high}$ (highlight end) act per channel on the measured
endpoints:

$$w_\text{offset}[c]:\ D_{\min,c} \to -w o_c \qquad w_\text{high}[c]:\ D_{\max,c} \to D_{\max,c}/wh_c$$

The slope is subsequently derived from the span, so both ends stay independently pinned: black
lands at the bottom of the output range and white at 0, regardless of the nudge values. The two
terms therefore act on colour alone.

Consequently the two carry no calibration-order dependency; the shadow end and the highlight end
are determined independently.

### 4.5 The inversion

Each channel is one affine map:

$$D_\text{adj} = S_c \cdot D + b_c, \qquad T_\text{pos} = 10^{D_\text{adj}}$$

The span is determined by that channel's two endpoints:

$$S_c = \frac{\text{output range}}{D_{\max,c}/wh_c - (-wo_c)}, \qquad b_c = -\text{output range} - S_c \cdot (-wo_c)$$

The lower density endpoint maps to $-\text{output range}$ (black) and the upper to 0 (white).

The slope is a quantity derived from the difference of the two ends, not an independent parameter.
Deriving it from the span is what keeps both ends pinned. The three channels retain four colour
degrees of freedom: the shared part of the slope is the density range and its between-channel
difference is the highlight cast; the shared part of the offset is the black level and its
between-channel difference is the shadow cast. This form matches Cineon / DaVinci — decode,
invert, set the black and white ends, with the look carried by the output transform.

The Cineon log encoding standard was defined by Kodak in the 1990s for the digitisation of motion
picture film, expressing optical density on a logarithmic scale so that the digital encoding
corresponds to the film's photochemistry (the logarithmic density–exposure relation). The standard
is itself a storage encoding, mapping density 0–2.046 to code values 95–685. The other model this
pipeline references, darktable negadoctor, uses two-ended density calibration plus a single gamma.
Both complete the inversion and three-channel alignment in the density domain.

#### 4.5.1 Basis for per-channel slopes

Negative density records scene luminance by the relation $D \propto \gamma_\text{film} \cdot \log
H$, and reconstructing the scene corresponds to dividing by $\gamma_\text{film}$. That quantity is
per-channel: `docs/calibration/grade_is_overloaded.py` solves the slope per channel and finds a
mean between-channel spread of 0.141, with the red layer steepest (Portra 160 at R 1.318 / G 1.112
/ B 1.121). The same fact appears in the luminance/chroma decomposition: solved separately within
one chain, the gains are 1.010 and 1.347, a ratio of 1.33. The form of this solve is therefore
per-channel, i.e. three slopes each derived from its own endpoints.

Absolute $\gamma$ values cannot be obtained from the above data: that chart data describes
densities on PAPER, with paper contrast already present on both sides of the observation, so
solving a luminance slope from it returns a circular result of ≈1.0. The ratios and
between-channel spreads cited above are comparisons internal to the chain and are not subject to
this limit. Absolute per-channel $\gamma$ requires the negative's D-logE curve, i.e. the film
datasheet, which is the authoritative content of a datasheet (the opposite of the crosstalk
matrix, which is dominated by the sensor).

#### 4.5.2 Black-floor normalisation

The film base ($D=0$) corresponds to $T_\text{pos} = 10^{b_c}$ and is mapped to pure black so that
the sampled base lands at 0: $(v - \text{floor})/(1-\text{floor})$, clamped below at 0 with no
upper clamp. The floor takes the deepest $b_c$ of the three channels: the shadow nudge places each
channel's black at a different point, and taking the deepest keeps every channel's shadow detail
above the clamp. The step is folded into the write stage of the inversion.

#### 4.5.3 Path A branch

RGB light-box rolls take the luminance/chroma decomposition here in order to apply the chroma
compensation matrix or per-channel amp produced by the decouple calibration.

With a matrix, the matrix output is multiplied by a single scalar (the mean of the three channel
slopes). The matrix maps the sum-zero plane to itself, and a single multiplier keeps the result
summing to zero, i.e. pure chroma; luminance is determined by the endpoint affine and is not
disturbed by the chroma compensation.

With a per-channel amp only, chroma follows its own channel's slope (identical to the result of
the plain per-channel affine), is divided by the amp, and has its mean removed again.

The matrix and the amp are alternatives: the chroma compensation matrix is built from
$1/\text{amp}_{Yb}$ and $1/\text{amp}_{Rg}$ and already carries the amplification per chroma axis.

### 4.6 Parameter differences for ECN-2

ECN-2 is Kodak's development process for motion picture negative (Kodak Vision 3, Fuji Eterna,
Kodak 5219 and others). The Cineon standard was designed for the digitisation of ECN-2 motion
picture film.

**Base colour ($T_\text{base}$)**: C-41's orange mask is the result of a standardised process and
varies little between manufacturers. ECN-2 base carries a rem-jet antistatic backing (a carbon
black coating, removed mechanically during development), after whose removal a residual
brown-red/magenta cast is common, giving three-channel ratios noticeably different from C-41;
residue is more common still when developed in a C-41-compatible process (cross-process).

**Dynamic range ($D_{\max,c}$)**: motion picture film targets theatrical projection and the
digital intermediate (DI), with a dynamic range typically half a stop to a stop above C-41
consumer negative, and typical endpoints of about 2.5–3.0 (C-41 about 2.0–2.5). The slope is
derived from measured endpoints, so the mapping adapts automatically once the endpoints change.

**Chroma**: ECN-2's DIR coupler formulation and dye layer structure differ from C-41, and its
saturation logic derives from theatrical printing standards. The pipeline provides no
corresponding compensation parameter; each roll's difference is presented by its own density
structure under the same endpoint model. The theatrical look comes from the print stock's density
curves and a 3D LUT rather than from primary coordinates; the corresponding procedure is to export
scene-linear ACEScg and apply a 2383 print LUT in a grading application.

---

## 5. Precision boundary of Path A, and Status M

### 5.1 The second layer of crosstalk

Path A addresses spectral coupling at the light-source/film level. A second spectral mismatch
exists in the system, at the sensor/paper level: the orange mask of a colour negative is designed
for the spectral response of silver-halide paper, whose cyan dye absorbs additional red light, and
the mask cancels this with a corresponding complementary density so that the print reads neutral.
A digital sensor's CFA differs from silver-halide paper's spectral curves, so this cancellation
fails in the digital domain and produces a systematic cast. Path A's decouple matrix describes the
relationship between light source and dye and contains no information about the relationship
between sensor and paper.

After the first layer is handled, the second-layer residual is typically ΔE 3–6. Reducing it to
ΔE < 2 requires calibrating from chart data and applying a density-domain channel mixing matrix,
i.e. modelling the second layer separately.

### 5.2 Form of this layer

Fitting a 3×3 density-domain matrix to chart density data for 8 C-41 emulsions reduces the
residual by 68–80% (RMS from ~0.10–0.14 to ~0.03).

### 5.3 Separation of direction and strength

The same Vision 3 5219 under daylight, solved independently on a Nikon 9000ED and a Hasselblad X5:

$$M_{9000\text{ED}} = \begin{bmatrix} 1.1683 & 0.0863 & -0.0253 \\ 0.1650 & 0.6741 & 0.1108 \\ 0.2447 & -0.1499 & 1.0216 \end{bmatrix}, \qquad M_{X5} = \begin{bmatrix} 1.0717 & 0.0462 & -0.0879 \\ -0.1006 & 0.7614 & 0.1701 \\ 0.1500 & -0.3916 & 1.1930 \end{bmatrix}$$

Taking the chroma action ($P M P$, projecting out luminance), their direction cosine is +0.9918
(an angle of 7.3°) and their strengths differ by a factor of 1.21 (1.272 against 1.533).

[`C41Crosstalk`](../../../OpenRevelare.Core/C41Crosstalk.cs) records the same conclusion over a
larger sample: across 18 matrices a single common direction explains 99.01% of the variance (worst
individual agreement cosine 0.9798), with strength distributed over 0.99–1.89. That sample spans
different scanners, different manufacturers' dye sets and different processes (5207 appears
developed both C-41 and ECN-2, and both land on this direction). The direction is universal by
structural cause: three subtractive dye layers read by three sensor channels whose passbands
overlap. Strength is structurally equal to `target chroma / negative chroma` and depends on the
target declared by the calibration.

For comparison, the published Status M → Print Density matrix (a conversion between standard
scales) has $\|M-I\|$ of 0.129, one quarter of the instrument matrices above.

### 5.4 Relation between narrow-band light and Status M

Status M is a set of response functions (filter × detector); narrow-band LEDs are an emission
spectrum. Status M's nominal peaks are R 644 nm / G 542 nm / B 435.7 nm, while commercially
available RGB LED panels peak typically at 630 / 525 / 465 nm, deviations of −14 / −17 / +29 nm.
In regions where dye absorption curves are steep, a shift of this magnitude produces a measurable
difference.

### 5.5 Current implementation state

The datasheet serves to define the target scale and to supply $D_\text{min}$ / $D_\text{max}$ and
the toe and shoulder positions as verification data. Correction strength depends on the specific
sensor and the declared target and comes from measurement.

`C41Crosstalk.Direction` compiles the common direction in as a constant (switched by
`FrameParams.UseC41Crosstalk`), which currently has no interface entry point and is off by
default. Path A rolls ignore it, as their own `DecoupleChromaMatrix` already occupies the same
slot in the inversion and is measured from that roll's light source. Turning strength into a
measured quantity requires a calibration wedge of known Status M density and a corresponding
solver (neither of which exists at present), and requires the target chroma to be determined
first.

---

## 6. Output: step 4 and Stage 2

The inversion outputs an ACEScg linear positive. Under the NONE output intent, processing ends
here and the result is exported directly.

Under the BASIC output intent, a two-part post-process runs, divided according to the physical
nature of each operation.

### 6.1 Part one (linear light)

White balance gains and exposure compensation. Both scale light — a gain of 2 corresponds to twice
the photons — which in the linear domain is a single multiplication (linear 0.25 × 2 encodes to
0.735).

### 6.2 Step 4 (conversion)

Primaries and transfer function are converted together, entering the output space. The step
changes gamut and gamma at once.

### 6.3 Part two (display space)

Levels, contrast, highlights/shadows, curves and saturation. These are perceptual operations whose
definitions hold in the encoded domain: contrast pivots on 0.5 as mid-grey, the levels endpoints
are 0 and 1, and curve control points lie on a bounded perceptual ramp. Display mid-grey through
contrast = 0.5 outputs 0.5000 under the two-part chain.

Encoding occurs exactly once in the chain and sits in the middle, so the data is already in the
corresponding domain when it reaches the curve. The curve's companding is fixed at gamma 2.2 and
is not derived from the output space: that constant defines the semantics of saved curve control
points, so the same curve represents the same shape after the output space is changed.

Samples outside [0,1] pass through the curve without truncation, as the curve is defined only on
[0,1], and the headroom of a wide working space is thereby retained (an ACEScg red reaches 1.23 on
the sRGB scale). Negatives are clamped: powers of a negative base are undefined, and a negative
indicates the colour has left the gamut entirely, which the output stage handles.

Encoding precedes these operations, and operations after it may exceed range; contrast is one such
(measured at 1.049 for a setting of 0.2, as rotating about mid-grey lifts highlights past the
white point). Display-domain encoded values are undefined outside [0,1], so an explicit clamp is
applied at the end of the chain.

The seven operations are fused into a single pass: each is per-pixel, a full frame read and write
at 24 MP is 288 MB per pass, and fusing completes all of the arithmetic in one read and write.
The chain is bandwidth-bound.

`display_referred_stage2` is saved with the roll (true for new rolls): the semantics of the slider
values depend on the domain they act in, and are pinned together with the project.

### 6.4 Output space

The output space is selected in the main window from sRGB (default), Display P3 and Adobe RGB. It
is a roll parameter and is saved into the project: Stage 2 runs inside it, so changing it changes
the rendered result. On a change the slider values are retained and the picture changes with them,
the values meaning "this much adjustment in the current output space". One output space applies
across the roll, so that the frames of a contact sheet are comparable.

`ColorSpaces` additionally registers Rec709 and two Kodak dye-set spaces (Kodak Endura Premier,
Kodak 2383) so that older projects parse; these are migrated to sRGB on load, with a note in the
status bar. Rec709 shares sRGB's primaries and differs only in transfer function. The two Kodak
spaces describe the dye set's encoding primaries (measured primary-triangle areas of 127% and 141%
of sRGB) rather than the medium's reproducible gamut; the look of a darkroom enlargement or a
theatrical projection resides in density curves and a 3D LUT.

### 6.5 Gamut mapping

Colours outside the destination gamut contract toward the luminance-matched neutral axis,
preserving hue and luminance, with in-gamut pixels unaffected. Expressed as "grey + t·(colour −
grey)", the smallest $t$ bringing the worst channel exactly to the boundary has a closed form, so
the step is a constant number of arithmetic operations rather than an iterative search. Pixels
brighter than the destination white are tone-limited first and then desaturated against the
limited grey. The working space ACEScg is wider than every output space, so this step occurs in
actual processing.

### 6.6 Export and preview

Stage 2 completes in the destination space, so export performs no further conversion and only
attaches the corresponding ICC profile to the file; the pixels on screen are the pixels in the
file. Containers are 16-bit TIFF and 8-bit JPEG.

The preview bitmap is submitted to the compositor without a profile and the values reach the panel
unaltered. On-screen accuracy is handled at the operating-system level: a colorimeter measurement
generates an ICC profile (containing that panel's per-channel TRC, real primaries and white
point), which is registered as the system display profile, and the operating system performs the
conversion centrally.

### 6.7 Roll-wide consistency

$T_\text{base}$, the per-channel endpoints $D_{\max,c}$, the output range, the decouple matrix,
$\alpha$ and chroma_amp are all computed at roll level, and all frames share one set of
parameters.
