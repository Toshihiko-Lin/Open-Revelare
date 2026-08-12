# OpenRevelare — User Guide

The whole route, from copying the negative to exporting the positive. For the algorithms, see
"Theory".

One sentence on where this program stands: **the restoration is computed, the look is adjusted.**
The first lives in the "Roll calibration" panel (Stage 1), the second in "Frame edit" (Stage 2), and
neither contaminates the other.

---

## 1. The three input routes

Settle which one you are on first; everything after this follows from it.

| Route | Input | Light | Extra preparation |
|---|---|---|---|
| **Path B** | Camera RAW | Broad-spectrum (white panel) | None. Most people are here |
| **Path A** | Camera RAW | Narrow-band (RGB LEDs) | Three calibration shots, see section 9 |
| **TIFF** | Scanner TIFF | — | Keep the ICC profile, 16-bit if you can |

Path A can measure the CFA's channel crosstalk and separate it out, at the cost of three extra
shots. Path B and TIFF are identical as far as calibration goes.

---

## 2. Before you copy or scan

How accurate the calibration can get is decided at capture. Time saved here is paid back with
interest later.

### Copying with a camera (Path A / Path B)

**What to set white balance to**

Anything. The decoder turns camera white balance off and works from a UniWB baseline, so whatever
the camera is set to makes no difference to the result.

That said, **it is worth balancing against the copying panel once**: the camera's own preview and
histogram then look normal, which makes judging exposure far more comfortable. This is purely for
your own convenience.

**Exposure**

Expose so the **bare panel (an empty area with no film over it) just reaches the right-hand end of
the histogram**. That puts everything the negative carries inside the sensor's usable range —
neither wasting dynamic range nor pushing the dense end down into noise.

**What is what on a negative**

This decides where you aim later, so it is worth being explicit:

| Area of the film | How it looks on the negative | On the positive |
|---|---|---|
| **Unexposed** (film base, gaps between sprockets) | **Semi-transparent** (the orange mask) | Black |
| **Fully exposed** (the exposed patch on the leader) | **Dark / opaque** | White |

So: **shadow WB corresponds to the film base** (the semi-transparent part of the negative), and
**highlight WB to the dark area** (the opaque part). That runs against intuition — take care not to
swap them when sampling.

**Strongly recommended: shoot one frame of the leader**

The leader puts a fully-exposed patch and an unexposed base patch side by side in one frame. A
single leader frame carries every reference the calibration needs:

| Sample | Where on the leader |
|---|---|
| Film base T_base | The semi-transparent orange base |
| D_max | The dark, fully-exposed patch |
| Shadow WB offset | The base area (same as T_base) |
| Highlight WB high | The dark area (same as D_max) |

**Also**

- Focus on the emulsion, and stop down to the lens's sweet spot (usually f/5.6–f/8).
- Shoot the whole roll on **one set of settings** — light, exposure and camera position all fixed. A
  roll shares one set of Stage 1 parameters, and that is the premise.
- Keep the film and lens as clean as you can. Dust degrades the accuracy of the auto analysis (below).

### Scanner TIFF

- Export **16-bit** if you can. 8-bit leaves the log-density maths with very few levels in the
  shadows, which can band.
- Turn off every automatic colour / contrast / inversion feature in the scanner software. What is
  wanted is the rawest negative data available.
- Keep the ICC profile. A scan with a full profile (including rXYZ/gXYZ/bXYZ) is carried correctly
  into the working space, which makes that route colour-managed.

---

## 3. Importing

**File → New roll…** (Ctrl+N), or the first toolbar button.

**Pick the files.** Drag them in or click "Add files…". RAW covers ARW / NEF / CR2 / CR3 / DNG /
RAF / RW2 / ORF / PEF and others; TIFF covers .tif / .tiff. One roll cannot mix RAW and TIFF.
Selecting several files puts you in roll mode.

**Copying light source (camera RAW only)**: choose "Broad-spectrum (white light) — Path B" or
"Narrow-band (RGB) — Path A". The latter also wants a calibration folder.

**Scanner output encoding (TIFF only)**

The software probes the first file and shows what it found — dimensions, bit depth, whether an ICC
is embedded (and whether it carries a device-primaries matrix), the inferred gamma type and its
fitted value (e.g. `γ≈1.56`) — then **pre-selects** accordingly. Usually you can leave it alone.

> With no ICC the samples are **taken as already linear**, with no inverse applied and no warning.
> If your file is in fact gamma-encoded but carries no profile, set the encoding here by hand.

**Auto-analyse the roll and remove the mask**

Ticked by default. After import the software analyses the whole roll and measures the film base,
white balance, D_max and the per-channel density endpoints.

> **The auto analysis is not 100% accurate — treat it as a starting point, not a finish line.**
> Common things that throw it off:
>
> - **An under-exposed leader** — the analysis assumes the leader's exposed patch really is fully
>   exposed; if it is not, D_max is underestimated
> - **A light blocker** or anything else in shot that should not be part of the statistics
> - **Too much dust** — this hits the density endpoints in particular
> - **Lens vignetting** — darkened edges contaminate the base and D_max statistics
>
> Vignetting can be partly corrected first with the **LCC flat field** under lens correction (shoot
> an even light source with no film in the way), then re-run the analysis.
>
> Either way, check the result by hand afterwards and re-sample where needed.

Unticking it means **nothing is measured** — not even the film base. The roll opens on pipeline
defaults and every value is yours to set.

**Strip splitting** (scans only; appears when one file holds several frames)

Scanners routinely put a whole strip of negative into one image. The software detects how many
frames each scan holds and opens a "Strip splitting" window to confirm:

- **Drag the dividers** to adjust the boundaries. What you edit are **dividers**, not four-cornered
  boxes: frames on a strip share their edges and are evenly pitched, so one number per boundary
  describes the whole strip and makes overlaps and gaps unrepresentable.
- **Double-click** to add or remove a divider. When detection is wrong it is normally wrong by one
  divider — a blown highlight inside the picture reads like bare film base and one frame is reported
  as two — so it costs one double-click.
- **Frame count** can be typed directly.
- **Do not split this one**: import this strip whole.
- **Do not split any**: import every scan whole.
- **Crop margin**: how much slack to leave around each frame. This is not the final crop — you can
  still adjust it under "Geometry / cropping" once the roll is open.

Splitting completes during import, so the main window is handed a finished frame list. A failed
detection does not block the import: it falls back to one frame per file and says so in the status
bar.

**Sprocket mask confirmation** (pops up after a RAW import)

The software picks the frame with the brightest film base in the roll as a preview and marks the
sprocket holes and light panel with a **red** overlay.

- Red **should** cover: sprocket holes, blown-out panel areas
- Red should **not** cover: the orange film base, or anything with picture in it

The threshold comes from a bright-end valley detection and is usually close enough. A narrow gap
between panel and base (< 0.08) needs care; reference numbers are shown at the foot of the window.

If this roll has no sprockets and no panel showing (120 with full-frame coverage, say), click
"**No sprockets on this roll (skip)**". Closing the window or pressing Esc skips it too.

---

## 4. Roll calibration (Stage 1)

The "**Roll calibration**" tab on the right. Every parameter here is an **objective physical
property** of this roll of film.

> **Note: every control acts on the CURRENT frame only.** Once it is right, push it out with
> "**Apply calibration to the whole roll**" — there is no "select on a grid of the whole roll".

### 4.1 Film base and mask removal (T_base / D_max)

**Sample the film base** — click the button, then drag a rectangle over the **semi-transparent
orange base**: between the sprocket holes, or the margin. It must contain **no picture at all**.

This is the most important step: it removes the orange mask and the D_min offset at once, and every
density that follows is measured against it.

> Hard to see? Press **N** for a temporary negative view (gamma-encoded, so the base is legible),
> sample, then press N again.

**Sample D_max** — select the **dark, fully-exposed area**, or the darkest part of the picture.
There is also "**Auto-detect D_max**".

> **D_max is also a parameter you can set by eye.** It decides where white lands after inversion, so
> it drives the overall brightness of the picture: raise it and the picture darkens, lower it and it
> brightens. It **usually does not affect colour** — the ratio between the three channels' endpoints
> is unchanged, only the overall mapping range moves. For a brighter or darker result this is a more
> "physical" control than exposure.

**Offset (scan_ev)** — a slider in the same panel with a sampling button beside it: select an area
that should be pure film base and the zero point is corrected automatically. T_base removes the
base's **colour** but does not guarantee its **absolute level**; a fluctuating panel or edge falloff
can leave the base grey rather than black. Usually close to 0.

### 4.2 White balance

The two ends are independent; the order does not matter.

**Shadow WB offset (additive)** — one manual button, "Select shadow WB": select the **base area**
(the semi-transparent part of the negative).

> **Usually you do not need to touch this.** Film-base normalisation already handles the shadow-end
> cast; this is here for when the **shadows are visibly off-colour**. If nothing looks wrong, leave
> it alone.

**Highlight WB high (multiplicative)** — three buttons, one manual and two automatic:

| Button | Type | Use |
|---|---|---|
| **Select highlight** | Manual | Select the **dark area** of the negative (the positive's highlights) |
| **Brightest = white** | Automatic | Treats the brightest point as pure white |
| **Deep white balance (beta)** | Automatic | Neural inference; needs nothing neutral in the picture |

For deep white balance, **crop the sprockets and film edge away first** or they will skew it.

### 4.3 Density endpoints (read-only)

**There is nothing to operate here.** The inversion is decided by its two ends: the film base is
black, D-max is white, each channel is normalised on its own, and **the slope is what those two ends
leave behind** — not an adjustable parameter.

The panel shows `D-max per channel = a, b, c`. The spread between those three is how large this
roll's highlight cast actually is, and is worth a glance. If it says no endpoints have been
measured, sample D-max once or re-run the roll analysis.

> To change richness or contrast, go to **saturation** and **contrast** in Frame edit — that is the
> aesthetic layer.

### 4.4 Lens correction (manual, optional)

Distortion, vignetting, **LCC flat field**. Besides fixing the optical faults themselves, the flat
field improves the accuracy of the auto analysis — vignetting distorts the base and D_max statistics
at the edges. The flat-field shot is an even light source photographed with no film in the way.

### 4.5 Sprocket mask (optional)

Marks over-bright areas (absolute luminance > threshold) as masked and fills them white after
inversion. "Show mask" lets you check the coverage (a red overlay).

---

## 5. Geometry and cropping

- **Crop**: pick a format preset (135 full frame, half frame, XPan, 645, 6×6, 6×7, 6×9, 6×12 …) or
  drag freely. Non-destructive, clearable at any time.
- **Rotate and flip**: 90° either way, horizontal and vertical flips.
- **Straighten**: drag a line along something that **should be horizontal** (a horizon) or
  **should be vertical** (a door frame, a flagpole) and let go — it levels to it.

---

## 6. Frame edit (Stage 2)

The "**Frame edit**" tab on the right. The aesthetic layer, **per frame**, and it does not touch
Stage 1's physical restoration.

| Panel | What is in it |
|---|---|
| Colour cast (white balance) | Temperature / tint; or select a neutral area and solve with "Grey point" |
| Tone | Black · shadows · highlights · white; with "Auto levels (0.1% / 99.9%)" |
| — | Exposure, contrast, saturation |
| Tone curve | M / R / G / B curves, with an optional "Preserve hue on the white curve" |

The **output space** is chosen in the toolbar at the foot of the main window (sRGB / Display P3 /
Adobe RGB). It is the target of step 4 in the Cineon chain: the inversion is converted into it,
frame editing happens in it, and the export is written in it — **what you see is what you get**. The
working space is ACEScg, so every choice here is a real gamut conversion. It is a roll-level
parameter and is saved with the project. When in doubt, use sRGB.

**Press K** for a before/after (the picture without Stage 2).

---

## 7. Working on the whole roll

The film strip is on the left; click a thumbnail to switch frames.

**Every panel control acts on the current frame only.** To push it out to others:

- **Apply calibration to the whole roll** / **Apply scene to the whole roll** — the buttons at the
  foot of the panel
- **Apply calibration to ticked frames** / **Apply scene to ticked frames** — tick the targets in
  the film strip first
- **Copy / paste** — the Edit menu or the film strip's right-click menu; which of the two gets
  copied follows whichever panel you are in
- **Choose what to sync…** — controls which fields all of the above carry

**Frame order**: an import is sorted by file name with digit runs compared as numbers, so `DSC_9`
comes before `DSC_10`. Drag a thumbnail to reorder by hand — a highlighted line shows where it will
land, and a virtual copy travels with its parent. Right-click → "Sort by file name" puts it back.
The order is saved with the project and sets the contact sheet's layout.

**Virtual copies**: to keep several Stage 2 treatments of one frame, right-click the thumbnail. A
virtual copy inherits Stage 1 and keeps its own Stage 2.

**Library (roll wall)**: press **G** to switch between library and editing. "Scan a folder into the
library…" re-registers `.ncproj` files that have been scattered around.

---

## 8. Exporting

**File → Export this frame…** (Ctrl+E) / **Export roll…** / **Export contact sheet…**

**Format**: 16-bit TIFF (best quality, for further grading or archiving) or JPEG (smaller, for
sharing).

**Colour space** is not in the export dialog — it is the "output space" in the main window's footer,
as in section 6. The export writes out the pixels you are looking at and attaches the matching ICC;
colours the target gamut cannot hold are pulled toward the luminance-matched neutral axis (hue and
luminance preserved) rather than clipped per channel.

**Export as scene-linear ACEScg**: skips step 4 and frame editing entirely and writes the
scene-linear data straight out, for DaVinci, Nuke and the like. These files carry no ICC and will
look dark and flat in a viewer that does no colour management — that is expected.

**Contact sheet**: tiles the whole roll into one grid. Roll info on the right (camera / film / ISO /
roll no. / lab / process / date / location / notes) is burned onto the foot of the sheet as one
identification strip. Never written to EXIF.

> **Want the screen to be accurate? Calibrate the display.** The preview does no display colour
> management — the bitmap goes to the system as-is and the panel lights up in its own primaries. The
> right fix is a colorimeter: measure the screen, generate an ICC and register it as the system
> display profile, after which every application benefits. Then set the output space to what you are
> actually delivering (sRGB for the web, Adobe RGB for some print work).

---

## 9. Path A: narrow-band light (RGB panel)

**Shoot the calibration frames**: with no film in the way, light the empty panel with the R lamp,
the G lamp and the B lamp in turn — three shots. **No particular file names are needed**; put them
in one folder and the software identifies which is which from their content.

Both dimming approaches work:

- **White-light mode**: set R/G/B so the mix is white
- **Neutral-base mode**: set R/G/B so the film base transmits neutral (cancelling the mask
  physically)

**Import**: choose "Narrow-band (RGB)" as the light source and point at the calibration folder. The
software identifies the three and shows a confirmation with each shot's ROI means; if it got one
wrong, correct it per channel from the dropdown.

**Calibration then proceeds exactly as for white light**: film base, white balance, D_max. The
decouple matrix is computed at import, and the decouple strength α is determined adaptively from
roll-wide samples — nothing to set by hand.

---

## 10. Keyboard

| Key | Action |
|---|---|
| Ctrl+N / Ctrl+O | New roll / add images |
| Ctrl+E | Export this frame |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| N | Temporary negative view (for aiming at the base) |
| K | Before/after (without Stage 2) |
| F / Ctrl+1 | Fit to window / actual pixels 100% |
| G / D | Library / editing |
| Esc | Cancel the current sampling |
| Ctrl+Shift+T | Light/dark theme |
| Ctrl+, | Preferences |

Sampling: light up a sampling button (the dashed-rectangle icon), then drag a box on the preview;
Esc cancels. Double-click a slider's label to reset it. Drag with the left button to pan once
zoomed; the wheel zooms.
