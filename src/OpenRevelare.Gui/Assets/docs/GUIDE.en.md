# OpenRevelare — User Guide

This document walks through the whole of OpenRevelare, from importing files to exporting finished
pictures. For the algorithms behind it, see [THEORY.en.md](THEORY.en.md).

---

## Contents

- [OpenRevelare — User Guide](#openrevelare--user-guide)
  - [Contents](#contents)
  - [Before you start](#before-you-start)
  - [Importing files](#importing-files)
  - [FilmBase calibration](#filmbase-calibration)
    - [1. Sample the film base (T\_base)](#1-sample-the-film-base-t_base)
    - [2. Sample the shadow white balance (wb\_offset, optional)](#2-sample-the-shadow-white-balance-wb_offset-optional)
    - [3. Sample the highlight white balance (wb\_high, optional)](#3-sample-the-highlight-white-balance-wb_high-optional)
    - [4. Sample D\_max](#4-sample-d_max)
    - [5. Sample the exposure offset (scan\_exposure\_ev, optional)](#5-sample-the-exposure-offset-scan_exposure_ev-optional)
    - [6. Adjust grade (optional)](#6-adjust-grade-optional)
  - [Preview and crop](#preview-and-crop)
  - [SceneBase adjustments](#scenebase-adjustments)
  - [Working on a whole roll](#working-on-a-whole-roll)
  - [Export](#export)
  - [Path A: the narrowband light-source workflow](#path-a-the-narrowband-light-source-workflow)
  - [Troubleshooting](#troubleshooting)

---

## Before you start

**Copy setup (camera RAW)**

- Set the camera to **UniWB** — manual white balance with equal gain on every channel. The software
  switches the camera's white balance off when it decodes, so a colour cast in the camera's own
  preview is expected and does not affect the result.
- Light the whole negative evenly with a light box or panel, and make sure the film-base areas
  (sprocket holes, the blank edge) are well exposed — they are what T_base is sampled from later.

**Strongly recommended: shoot a frame that includes the film leader**

Every roll starts with a *leader*: a fully exposed area (pitch black, the densest part of the
negative) sitting right beside an unexposed film-base area (the orange backing), with a clean
boundary between them. It is the single place where every reference area the calibration needs
appears at once:

- **T_base sample**: box the orange film-base area (the unexposed part / the darkest part after
  inversion)
- **D_max sample**: box the fully exposed pure-black area (the densest part / the brightest part
  after inversion)
- **wb_offset sample** (shadow-end white balance): that same pure-black base area also serves as
  the shadow reference — an unexposed area should in theory be at equal D_min in all three
  channels, which makes it a natural shadow end point
- **wb_high sample** (highlight-end white balance): the leader itself (fully exposed, all three
  channels at D_max) is the natural highlight reference — the leader is where the positive's
  "white point" lives

Ideally the T_base and wb_high samples come from the same film-base selection, and the D_max and
wb_offset samples from the same pure-black selection — meaning one frame of the leader calibrates
all four parameters, with no switching between frames. The remaining picture frames only need
enough sprocket/blank edge left in them for a T_base sample.

**Scanner TIFF**

When TIFF files are imported, the software **probes** the first file's metadata and shows it above
the "scanner output encoding" box in the import dialog, for you to confirm: dimensions, bit depth,
whether an ICC profile is embedded (with its name, and whether it carries a device primaries matrix
— "+matrix"), and the inferred gamma type with its fitted value (e.g. `γ≈1.56`). The encoding
option is **pre-selected** from that probe, so there is usually nothing to work out by hand:

- **Linear**: the scanner already put out linear light; no gamma conversion is done.
- **sRGB gamma**: standard sRGB encoding; the inverse sRGB curve is applied to get back to linear.
- **Auto-detect**: chosen automatically when the file carries an ICC with a non-standard device
  gamma (the device profiles of professional scanners such as Flextight or Noritsu). The software
  inverts the gamma per channel from the file's **own TRC curves**, then applies the ICC device
  primaries matrix (rXYZ/gXYZ/bXYZ) to convert scanner device RGB into standard linear sRGB. Those
  two steps together are what fixes the "brightness-dependent colour cast" caused by a scanner
  whose three channels do not share one gamma. Without an ICC, or with the matrix tags missing,
  each step falls back on its own.

The probe only pre-selects; you can override it. In roll mode the first file is probed and the
result applied to the whole roll (same scanner, same settings, same parameters).

**Colour management on the scan path**

Loading a scan reads its embedded ICC profile: first the profile's own three TRC curves linearise
each channel, then the rXYZ/gXYZ/bXYZ matrix maps device RGB into linear sRGB. **The scan path is
therefore colour-managed by construction.**

Camera RAW gets no equivalent transform, and that is not a gap — the relative sensitivity
differences between the camera's three channels are normalised out by the film base (dividing by
T_base), and a base measured on the actual roll fits your real copying conditions better than a
looked-up camera matrix would: the light source, the lens and the copy geometry are all normalised
along with it.

When a file has no ICC, or the profile is LUT-only with no matrix tags, the corresponding step is
skipped and the scanner's channel differences go uncorrected; pull back any resulting cast with
SceneBase's saturation and white balance.

> Note: with an 8-bit TIFF, the log-density arithmetic behind the inversion has only a limited
> number of levels to work with in the shadows, and slight banding is possible. Export 16-bit from
> the original scan where you can.

---

## Importing files

Click "New project" on the toolbar, or File → New in the menu, to open the import dialog.

**Choosing files**

Drag files in, or click "Add files…". Supported formats:
- RAW: ARW, NEF, CR2/CR3, DNG, RAF, RW2, ORF, PEF and other common formats
- TIFF: .tif / .tiff from a scanner

One project can only hold one kind of file (RAW and TIFF cannot be mixed). Importing several files
puts the project into **roll mode**, where the FilmBase parameters are shared.

**Light source (RAW only)**

- **Broadband source (white light)**: for copying on a tungsten or daylight light box. Goes
  straight into the density pipeline, with no calibration shots. Recommended for most users.
- **Narrowband source (RGB mix)**: for a monochromatic RGB LED light box; a folder
  holding the three calibration shots must be supplied as well. See
  "[Path A: the narrowband light-source workflow](#path-a-the-narrowband-light-source-workflow)".

**After the import: the sprocket-mask confirmation window**

Once a RAW roll has been imported, the **sprocket mask** confirmation window opens by itself. The
software picks the frame with the brightest film base in the whole roll — the worst case — and
shows it there.

**The goal**: mark the **sprocket holes and the copy light source** (the most transmissive areas)
with the green overlay, so they cannot contaminate the film-base calibration that follows, and so
the sprockets are taken out.

**What to do**: drag the threshold slider to move the dividing line; the green area refreshes as
you go:
- **Green SHOULD cover**: the sprocket holes and the bare light panel (the blown-out areas)
- **Green should NOT cover**: the orange film base, or anything holding picture content

**How the threshold is found**: the software computes a starting threshold with a **bright-end
valley detection** — it finds the deepest valley between the light-panel peak (the brightest area)
and the film-base peak (the next brightest) and puts the boundary there, scanning from the bright
end so that secondary valleys caused by the pure-black shadow end are avoided. That automatic value
is usually close already; fine-tune it against what you actually see.

**Special cases**:
- If the margin between the light panel and the bright end of the film base is narrow (< 0.08), the
  threshold needs careful adjustment. The reference figures (panel brightness, film-base bright end,
  margin) are shown at the foot of the window.
- If this roll has **no sprockets and no bare light source** (120 medium format, say, where the
  picture covers the film completely), choose "**No sprockets on this roll (skip)**".
- Closing the window or pressing Esc is the same as skipping; the sprocket mask stays off.

Click "**Confirm and continue**" when it looks right; that threshold is then applied across the
whole roll.

**Automatic film-base detection**: once the sprocket mask is confirmed, the software runs a
film-base detection over the whole roll (using the sprocket threshold to exclude light-panel
pixels) and writes the result into every frame's initial T_base. The result appears in the status
bar and is **a starting point only — box the film base by hand and calibrate again** for an
accurate one. Automatic detection can be turned off under Settings → Preferences.

---

## FilmBase calibration

The FilmBase panel holds every physical-reconstruction parameter, and corresponds to the first
stage of the pipeline. Each parameter has its own "sample" button — click it, drag a rectangle on
the preview, and the parameter is computed from that area.

> The order matters. Work through the steps below in sequence.

### 1. Sample the film base (T_base)

Box an **unexposed film-base area** on the preview — usually the blank between sprocket holes, or
the orange backing along the edge of the picture. The selection must not include any exposed
picture content.

The film-base sample is the most important step of the lot. It removes the orange mask and the
D_min offset at once, and it is the physical reference every later density calculation rests on.

### 2. Sample the shadow white balance (wb_offset, optional)

Box a dark area that ought to be neutral grey or black (a fully exposed pure-black area, black
fabric, deep shadow). The three channels' densities are lined up, taking the colour cast off the
shadow end. **If you calibrated from the leader, the fully exposed area (pure black) is the ideal
selection — it can be taken from the ideal darkest D_min area.**

**Suggested order: sample the shadow WB first, then the highlight WB**, so the two ends do not
interfere with each other.

### 3. Sample the highlight white balance (wb_high, optional)

Box a highlight that ought to be neutral white (white paper, a white wall, a grey card). The three
channels' densities are lined up, taking the colour cast off the highlight end. **If you calibrated
from the leader, the fully exposed area is the ideal selection — it can be taken from the D_max
fully exposed area, where in the ideal case all three channels sit at D_max, making it a natural
highlight reference.**

### 4. Sample D_max

Box the **darkest area** of the picture — usually an overexposed masked-off region, the outside of
the sprockets, or the deepest shadow in the frame. D_max sets where the white point lands after
inversion.

In roll mode the software takes the maximum across every frame in the roll, so the white point is
consistent throughout.

### 5. Sample the exposure offset (scan_exposure_ev, optional)

**What it is for**: pushing the film base exactly onto density = 0, so that the base areas come out
pure black in the positive.

The T_base sample removes the film base's *colour* (the orange offset), but it does not guarantee
that the base's absolute brightness lands precisely on the density zero point. If the light box
fluctuated during the copy, or the edge of the lens's focal field falls off slightly, the base area
can be left with a residual density error, and the base then reads grey rather than black in the
positive.

What to do: box the **same film-base area** you used for T_base. The software measures its residual
density relative to the current T_base, works out the overall EV compensation and writes it in. A
positive value pushes the overall density up (compensating for a base that reads bright); a
negative value pulls it down. It is usually close to 0; reach for this step when the base area in
the positive still reads grey.

### 6. Adjust grade (optional)

- **grade**: controls the contrast of the positive, by analogy with paper grade in a traditional
  darkroom. The default of 1.65 suits standard C-41 consumer colour negative; ECN-2 motion-picture
  negative may want it lowered to 1.4–1.6.
The inversion has no separate chroma parameter. Cineon applies **one gamma to all three
channels** — chroma being each channel's deviation from their mean, a common multiplier preserves
it proportionally, so chroma follows luminance automatically. To make a picture richer or lighter,
use the SceneBase **saturation** slider.

Earlier versions had a `chroma_grade` parameter (defaulting to 3.05) that compensated for chroma
the pipeline was losing elsewhere. That "elsewhere" was **missing colour management** — the
inversion's output was treated as sRGB outright, so the gamut conversion that should have happened
never did. With the conversion supplied, the parameter has been removed entirely — see
[CALIBRATION.md](../../../../docs/CALIBRATION.md).

---

## Preview and crop

The preview switches to the positive as soon as the FilmBase calibration is done.

**Zoom and pan**: the mouse wheel zooms; drag to pan.

**Crop**: pick a format preset in the crop panel (135 full frame, half frame, XPan, 120 645/6×6 and
so on), or drag the crop box by hand. Cropping is non-destructive and can be reset at any time.

**Rotate and flip**: the geometry panel handles horizontal/vertical flips and rotation by an
arbitrary angle (for correcting a slight tilt in the copy setup).

**Sprocket mask**: once enabled, the software finds the sprocket areas and covers them in white, so
they do not turn up in the exported file. The threshold parameter controls how sensitive the
detection is, which helps with damaged or irregular sprockets.

---

## SceneBase adjustments

The SceneBase panel holds the aesthetic adjustments. Every one of them previews live, and none of
them touches FilmBase's physical reconstruction.

| Parameter | What it does |
|-----------|--------------|
| White balance (temperature/tint) | White balance in the positive domain, for fine colour-temperature trims |
| Exposure EV | Linear brightness scaling, for overall exposure |
| Black / white point | Level stretching, mapped linearly |
| Contrast | An S-curve that introduces no hue shift |
| Saturation | Chroma scaling in the linear domain, not a rotation of the hue wheel |

**Output intent** (this changes what gets exported):
- **NONE (linear)**: skips Stage 2 and writes out FilmBase's linear positive. Suited to taking the
  picture on into DaVinci Resolve, Nuke or another professional grading tool.
- **BASIC (sRGB gamma)**: runs the full Stage 2 and writes out a gamma-encoded standard image,
  ready to share or print as it is.

---

## Working on a whole roll

Importing several files puts the software into roll mode. The film strip runs down the left of the
window; click a thumbnail to switch to that frame.

**Applying a parameter to the whole roll**: in the FilmBase or SceneBase panel, every parameter has
an "apply to the whole roll" button beside it. Clicking it copies the current frame's value for
that parameter to every frame in the roll.

**Viewing the whole roll (contact-sheet mode)**: click "View the whole roll" at the top of the film
strip and the main preview becomes a grid of the entire roll. In that mode:

- **Sample across the whole roll**: T_base, D_max, wb and the rest can be boxed directly on the
  contact sheet — the result is broadcast to every frame automatically, which is the fastest route
  to one consistent calibration for the roll.
- **Judge the overall look**: every frame renders live with the current parameters, so how
  consistent the roll is can be seen at a glance.
- Clicking any thumbnail leaves contact-sheet mode and jumps to that frame.

A suggested order for calibrating a roll:
1. Enter contact-sheet mode and box a film-base area on the grid to sample T_base (broadcast to the
   roll automatically).
2. Box the darkest area on the same grid to sample D_max.
3. Leave contact-sheet mode and trim the frames that need it individually (badly overexposed or
   colour-cast ones).
4. After the SceneBase adjustments, decide whether to apply them to the whole roll (usually they
   stay per frame).

**Virtual copies**: to keep more than one set of SceneBase settings for a single frame, right-click
its thumbnail and make a virtual copy. A virtual copy shares the original's FilmBase parameters and
has its own SceneBase.

---

## Export

Click "Export" (or File → Export in the menu).

**Format**:
- **TIFF (16-bit)**: the highest quality, for grading afterwards or for archiving.
- **JPEG (8-bit)**: smaller, for sharing and publishing on the web.

**Colour space**: sRGB (the default, universal) / Adobe RGB / Display P3 / Kodak Endura Premier
(darkroom-print look) / Kodak 2383 (print-film look). The embedded ICC profile matches what was
actually written. Colours the destination gamut cannot hold are desaturated toward the neutral of
their own luminance — preserving hue and brightness — rather than clipped per channel.

**Resolution**: unlimited.

**Output folder**: it can be named at import time, or picked when you export. A roll export writes
every frame into one folder, named automatically after the original files.

---

## Path A: the narrowband light-source workflow

Path A is for copying negatives on a monochromatic RGB LED light box, where R, G and B are driven
separately.

**Shooting the calibration frames**

With no film in place, light the bare box with the R lamp, the G lamp and the B lamp in turn and
photograph it, giving three calibration shots. **They need no particular file names**; just put
them in one folder and the software works out which is which by analysing the content (argmax).

**Dimming (either approach works)**:
- **White-light mode**: adjust the R/G/B intensities until the mixed light is pure white
- **Neutral-base mode**: adjust the R/G/B intensities until the light through the film base is
  neutral, cancelling the mask physically

**Choosing Path A at import**

Pick "Narrowband source (RGB mix)" among the light-source options in the import dialog, then choose
the folder holding the calibration shots. The software identifies the three R/G/B frames and opens
a confirmation dialog showing what it found and each frame's ROI mean. If it got one wrong, correct
the channels from the drop-downs before confirming.

**The calibration itself is the same as for white light**

Once imported, the FilmBase calibration is exactly as it is on Path B: sample the film base, the
white balance and D_max in turn. The decouple matrix was computed at import, and α (the decoupling
strength) is determined adaptively by the software from samples across the roll — there is nothing
to set by hand.


---

## Troubleshooting

**The positive has a colour cast / looks green**

The commonest cause is a T_base sample that caught some exposed content, or one taken over an area
that is not uniform enough (a film-base edge with slight exposure on it). Sample the film base
again, keeping the selection on completely unexposed orange backing.

**Highlights read grey; there is no pure white**

The D_max sample was not taken from a dark enough area, so the white point sits too low. Sample the
darkest area of the picture again, or lower D_max a little.

**Colour comes out weak (ECN-2 motion-picture film)**

ECN-2's chroma characteristics differ from C-41's. The pipeline has no compensation parameter for
this — each stock's difference comes through on its own, from its own density structure under the
same grade. To adjust richness, use the SceneBase saturation slider; for a theatrical look, set
the export colour space to Kodak 2383 (print film), which is the gamut a motion-picture negative
actually targets.

**The export looks weaker than the preview**

The preview uses an approximate fast path and the export uses high-precision floating point; a
slight difference between them is normal. If it is pronounced, check that the output intent is set
the way you meant (BASIC / NONE).

**The corners are dark, or tinted**

Vignetting and colour non-uniformity from the copy lens. The "Lens correction (manual)" panel
offers two routes: the vignette slider compensates the brightness falloff with a formula; if the
corners also carry a colour cast, use the LCC flat field instead — shoot one blank light frame and
load it, and the per-pixel brightness AND colour non-uniformity of this lens and stand are divided
out, which is more accurate than any formula.

**The frame edges bow**

Barrel or pincushion distortion from the copy lens. Adjust the distortion slider in the "Lens
correction (manual)" panel — negative corrects barrel, positive pincushion. The macro primes
normally used for copying distort very little, and the value is fixed: measure once, type it in,
and it holds for the whole roll.
