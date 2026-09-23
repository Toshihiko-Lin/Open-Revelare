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
| Shadow end (black level) | The base area (same as T_base) |
| Highlight end (brightness/temp/tint) | The dark area (same as D_max) |

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

> **Nikon High Efficiency / HE★ NEFs cannot be used.** LibRaw does not support either codec, and
> forcing it produces colour bars rather than a picture. The program recognises them before
> decoding and says so, instead of presenting the damage as valid pixels. Re-shoot with lossless
> NEF compression, or export a 16-bit TIFF from NX Studio or Lightroom first.

**File → New roll…** (Ctrl+N), or the first toolbar button.

**Pick the files.** Drag them in or click "Add files…". RAW covers ARW / NEF / CR2 / CR3 / DNG /
RAF / RW2 / ORF / PEF and others; TIFF covers .tif / .tiff. One roll cannot mix RAW and TIFF.
Selecting several files puts you in roll mode.

**Copying light source (camera RAW only)**: choose "Broad-spectrum (white light) — Path B" or
"Narrow-band (RGB) — Path A". The latter also wants a calibration folder.

**TIFF colour space is worked out for you**

Import asks no colour questions. A usable embedded ICC always takes priority; without one, the
application reads what the file itself declares, in order of trust:

1. a floating-point `SampleFormat` — float samples are scene data, so they are linear;
2. the TIFF 6.0 white point / primaries / `TransferFunction` tags — these exist precisely to
   declare colorimetry without an ICC, and yield an exact profile, which is far more specific than
   any "linear or sRGB" answer;
3. Exif `ColorSpace` — sRGB or Adobe RGB;
4. the `Software` tag — the fixed untagged output of a known scanner application.

Only when all four say nothing does the roll fall back on the convention for untagged scans, sRGB,
**and a notice above the preview says so** and offers to read the roll as linear instead. By then
the picture is on screen, which is the only point at which that question is answerable by looking.
Changing it re-decodes the roll, and the choice is saved in `.ncproj`.

Linear keeps the primaries uncharacterized and passes the working-space numbers through; sRGB uses
the exact sRGB profile for a complete LittleCMS transform. Only old projects with no stored choice
retain the historical bit-depth compatibility route.

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

**Strip splitting** (scans only; off by default)

Scanners routinely put a whole strip of negative into one image. **Ticked**, the import detects how
many frames each scan holds and opens a "Strip splitting" window to confirm:

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

> **Unticked, one file is one frame.** That is what you want for scans already cut in the scanner
> software, or made one negative at a time — detection has to decode the whole roll, and there is no
> reason to pay for it. Camera RAW is unaffected: always one frame per file.

**Sprocket mask** (automatic — no dialog)

The threshold is measured by a bright-end valley detection and applied to the roll on import, so an
import goes straight to a picture. **It keys off the brightness split between panel and film base,
not on the shape of any sprocket hole** — so a 120 roll with no sprockets but the panel showing
around the negative is recognised and the panel excluded just the same. Only when no panel can
actually be measured (a flatbed scan, or a negative that covers the panel completely) is the roll
treated as having none, and no mask is forced onto it.

To check or adjust it, open **Sprocket mask** at the bottom of the right-hand panel (it sits below
the Cineon / Display tabs and stays put whichever is open) and tick "show mask" for the red overlay.

- Red **should** cover: sprocket holes, blown-out panel areas
- Red should **not** cover: the orange film base, or anything with picture in it

> This switch only decides **whether the panel is filled white after inversion**. The automatic
> analysis and the automatic sampling **always** exclude the light panel (bright end) and the
> blocking card / film-edge line (dark end) from their statistics, regardless of this switch — so
> even a roll judged to have no sprockets gets its film base, D-max and levels measured without
> those two contaminating them.

---

## 4. Roll calibration (Stage 1)

The "**Roll calibration**" tab on the right. Every parameter here is an **objective physical
property** of this roll of film.

> **Note: the controls in the groups below act on the CURRENT frame only.** Once it is right, push
> it out with "**Apply calibration to the whole roll**" — there is no "select on a grid of the whole
> roll". ("Auto (whole roll)" at the top of the panel is the exception: it writes the roll itself.)

### 4.0 Black-and-white film

The "**Black-and-white negative (whole roll)**" checkbox at the top of the panel states what kind
of film this roll is. It applies to the whole roll.

With it on, the three channels are folded into one luminance signal (Rec.709 weights) **before** the
density domain, the inversion uses **one** pair of endpoints, and the output is neutral by
construction — not because a cast was corrected, but because there is nothing left to be cast. The
controls that mean nothing to a silver image — white balance, the colour-cast tools, the
per-channel ends — are hidden with it.

> **Why not "process it in colour and desaturate".** The six degrees of freedom exist for three dye
> layers. A black-and-white negative's three channels carry the same silver image, and what differs
> between them belongs to the camera's spectral response and the copy light, not to the film.
> Correcting that as a colour cast means grading a picture that has no colour in it.

> **Switching modes loses nothing.** The fold happens at render time; the white balance and R/G/B
> curves you set stay in the project and come back when you switch to colour. Switching rebuilds
> the roll's thumbnails.

### 4.1 One-press mask removal

The two buttons at the top of the panel are the shortcut through all of Stage 1. They run, in order:
**film base T_base → highlight WB → D-max → black/white points**, with no region to select
anywhere.

> **Neither button touches the crop.** They do inversion, not geometry — crop by hand under
> Geometry and cropping.

| Button | Reach | When to use it |
|---|---|---|
| **Auto (whole roll)** | Walks the roll, pools one parameter set, writes it to every frame | The default path. Import already ran it once; this is the way back to it |
| **Auto (this frame)** | Solves the current frame only, leaving **the rest of the roll and the crop** alone | When the roll-wide parameters do not suit one picture (a tungsten interior in an otherwise daylight roll) |

> **"Auto (whole roll)" overwrites the highlight WB and the levels you already have.** On a roll you
> have graded by hand, use "Auto (this frame)" instead, or the individual step buttons below.

Every step remains redoable on its own afterwards — the buttons in the groups below are unchanged.
The automatic pass only strings them together and runs them once.

### 4.2 Film base and mask removal (T_base / D_max)

**Sample the film base** — click the button, then drag a rectangle over the **semi-transparent
orange base**: between the sprocket holes, or the margin. It must contain **no picture at all**.

This is the most important step: it removes the orange mask and the D_min offset at once, and every
density that follows is measured against it.

> Hard to see? Press **N** for a temporary negative view (gamma-encoded, so the base is legible),
> sample, then press N again.

### 4.3 The inversion's two ends

The inversion is decided entirely by its two ends, and underneath they are **six absolute
densities** (three per end) — exactly as many numbers as the render actually consumes. This section
once carried a dozen parameters (grade/pivot, wb_high, wb_offset, d_max, scan_ev) describing those
same six degrees of freedom, and every surplus one showed up as two sliders doing the same job.

The three things you want to adjust are all different readings of those six numbers, and none of
them needs a parameter of its own:

| What you want | How | Measured side effect |
|---|---|---|
| **Contrast** | Drag D_min or D_max (ends closer / further apart) | cast <1% |
| **Colour cast** | Expand **Per channel** and adjust the components | contrast ~1% |
| **Overall lightness** | Use **Exposure** in Frame edit | cast exactly 0 |

> **Why lightness is not here.** No pair-of-endpoints move in the density domain can change
> lightness with zero cast: `offset[c] = −range − scale[c]·D_min[c]`, so adding a constant to both
> ends preserves the slopes but shifts each channel's offset by a different amount (R/B measured at
> ±3.5%). A cast-free brightness has to be a multiply in linear light — which is exposure, and
> Stage 2 already has one.

#### D_min (black end)

The density each channel reads as black. Two buttons:

| Button | Type | Use |
|---|---|---|
| **Sample the film base** | Manual | Select the semi-transparent orange base (between sprockets, or the margin) |
| **Auto black point** | Automatic | Finds bare base: the peak below the light panel → an edge sliver → a bright-end percentile |

What it measures is the **bare base's absolute density**, against clear (T=1). A C-41 orange base is
always **R<G<B** — red passes most — typically reading `0.086 / 0.292 / 0.538`. Those are verifiable
physical numbers: one look tells you whether the automatic calibration found the right place. Shadow
cast is adjusted under **Per channel**.

> If no bare base is found (a scan already cropped past it, say) an orange warning appears: the
> automatic result is then just the brightest part of the picture, so sample by hand.

> T_base is retired entirely (fixed at 1,1,1). It used to be the divisor for density, which hid the
> film base in a field with no slider while D_min always read `0,0,0` — leaving no objective number
> for the black end anywhere in the UI. Moving the base from divisor to subtrahend is the same
> affine map, so **the render is bit-identical**, but the readout becomes a verifiable quantity
> instead of a sentinel.

#### Cast correction

Between the two ends, because it is the third calibration quantity besides them: what cast this
roll carries — what the film, the scan and the base leave on the two ends. Calibration has no notion
of white balance; that is the warmth of the scene's light, and lives in the Display tab.

| Button | Type | Use |
|---|---|---|
| **Grey card · Cineon 470** | Manual | Select the grey card in the picture; it is the Cineon standard grey, so all three channels land on code 470 |
| **Smart cast correction (beta)** | Automatic | Neural inference; needs nothing neutral in the picture |

Both solve the same quantity: the card is a measurement, smart cast correction a guess — with a card
in the roll, use the card. Box the card in the picture; it is the Cineon standard grey, so all three
channels land on code **470**, which sets cast and placement together. The status bar reports how
far brightness moved from the roll calibration — close to zero when calibration already had the
card near 470, in which case the picture barely changing is expected. The card's reading stays under
the button until the next roll. For smart cast correction, **crop the sprockets and film edge away
first**.

Both write the three D_max densities. Sampling the highlight asserts the box is neutral at the white
end (1032), sampling the card asserts it is neutral at 470 — with the black end pinned by the film
base each channel has one degree of freedom left, so they and Sample the highlight are alternatives;
the last one pressed wins.

#### D_max (white end)

The density each channel reads as white, typically 1.8–2.4. Its distance from D_min is the
contrast; the differences between the three are the highlight cast — **these numbers are the white
balance itself**. They need no reference zero, because they are absolute densities rather than
correction factors.

| Button | Type | Use |
|---|---|---|
| **Sample the highlight** | Manual | Select the densest part of the negative (the positive's highlights) |
| **Auto white point** | Automatic | Finds the brightest point and treats it as pure white |

Both write the same triple; whichever you press last wins — as do the two buttons of the Cast
correction group above.

> **The output range is a constant with no slider.** It sets where black lands (fixed at 10⁻²).
> While it was adjustable it competed with the endpoints for the same degree of freedom — both
> changed lightness and contrast at once, so the panel showed two sliders doing one job. Fixed, the
> black point holds still and lightness and contrast are expressed by the ends instead.

### 4.4 Lens correction (manual, optional)

Distortion, vignetting, **LCC flat field**. Besides fixing the optical faults themselves, the flat
field improves the accuracy of the auto analysis — vignetting distorts the base and D_max statistics
at the edges. The flat-field shot is an even light source photographed with no film in the way.

## 5. Sprocket mask and Geometry / cropping

Neither of these is a colour parameter, so they live outside the Cineon / Display tabs: pinned to
the bottom of the right-hand panel, in reach whichever tab is open.

### Sprocket mask (optional)

Marks over-bright areas (absolute luminance > threshold) as masked and fills them white after
inversion. "Show mask" lets you check the coverage (a red overlay).

### Geometry and cropping


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
| White balance | Temperature · tint, with an **eyedropper** beside them |
| Tone | Black · shadows · highlights · white; with "Auto levels (0.1% / 99.9%)" |
| — | Exposure, contrast, saturation |
| Tone curve | M / R / G / B curves, with an optional "Preserve hue on the white curve" |

> **The white balance here is about the LIGHT, not about the film.** The film's own cast — what the
> stock, the scan and the base left on the two ends — lives in D_min / D_max under Roll
> calibration. Whether this lamp was warm or cool, and whether to keep its warmth, is a judgement
> about the positive, and it belongs on this page.

**The white-balance eyedropper** (the marquee button beside the temperature slider): drag over
something on the positive that **ought to be neutral** — a grey wall, white paper, tarmac — and
release to solve temperature and tint.

- It moves those two sliders only and **never touches the Cineon ends**, so the physical
  restoration is unaffected and undo takes back just this step.
- It solves at Stage 2's door, which is where white balance is applied, so one sample lands it
  rather than converging over several.
- When the cast is larger than the two sliders can express it says so; the rest belongs to the two
  ends under Roll calibration, which is where it actually came from.
- A black-and-white roll does not offer it — there is no colour to balance.

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

**Virtual copies**: to keep several treatments of one frame (the other half of a half-frame scan, a
second grade), right-click the thumbnail. A copy takes every parameter of its parent, crop included,
and is independent from then on. With several frames ticked, right-click → create copy / remove
from roll acts on each ticked frame.

**Sync geometry**: the sync button beside the flip buttons in the Geometry / crop panel applies this
frame's rotation / flips and straighten to the ticked frames (the whole roll when none are ticked).
The crop is not included — to broadcast a crop, tick Sync options → Crop and use paste.

**Library (roll wall)**: press **G** to switch between library and editing. "Scan a folder into the
library…" re-registers `.ncproj` files that have been scattered around.

**Sorting**: the picker under the sidebar's search box orders the wall by added time (the default,
newest first), last modified, last opened, roll name, roll number or dev date; the button beside it
reverses the direction. A roll that has no value for the chosen field always goes last, in either
direction. Dev date is free text: entries a date can be read out of (`2025.09`, `2025-09-12`,
`2025年9月`, `20250912`) sort chronologically, and anything else sorts after all of them. The choice
is remembered between sessions.

---

## 8. Exporting

**File → Export this frame…** (Ctrl+E) / **Export roll…** / **Export contact sheet…**

**Format**: 16-bit TIFF (best quality, for further grading or archiving), JPEG (smaller, for
sharing), or **linear DNG**.

> **A linear DNG** carries the finished positive with the display curve removed: the inversion, the
> lens corrections and the frame edits are all baked in, but the output space's encoding curve is
> not applied, so the pixels are linear light in that space's primaries, at 16 bits. In Lightroom or
> Camera Raw it arrives as MATERIAL rather than as a finished picture — the raw panel is live, the
> white balance slider works, and the highlights have something to recover. The output space's
> primaries are written into the DNG's `ColorMatrix1`, and `AsShotNeutral` is 1/1/1 because the
> balance was already solved in the density domain (declaring it again would have the host undo it).
>
> It is **not** a repackaged camera RAW — the mosaic is gone long before this point — and not the
> scene-linear ACEScg master either; that is the 32-bit float TIFF below, which keeps values above 1
> and outside the primaries. An HDR roll exports its SDR rendition here; for the highlight master,
> use TIFF.

**Size**: a target long edge lands **exactly** on the value given, with the short edge following the
aspect ratio. Reduction is an area average and enlargement is Catmull-Rom, and enlargement is off by
default — a picture smaller than the target stays its own size unless "Enlarge when the long edge is
shorter" is ticked, because interpolation cannot add detail the negative does not hold.

**File names** (roll export): the template takes `{Original}` · `{Seq}` · `{Roll}` · `{RollNo}` ·
`{Camera}` · `{Film}` · `{Date}`, and the dialog shows what the first file will be called as you
type. A field left empty leaves no trace (`{Roll}_{Camera}_{Seq}` on a roll with no camera gives
`roll_001`, not `roll__001`). When a name is taken you can keep both (the default), replace or skip;
nothing is ever overwritten silently.

**Colour space** is not in the export dialog — it is the "output space" in the main window's footer,
as in section 6. Export branches from the same rendered result as preview and embeds the exact ICC
that describes those pixels by default. Only exact display-referred sRGB may explicitly omit it;
Display P3, Adobe RGB, Rec709 and scene-linear exports force the matching profile. Colours the
target gamut cannot hold are pulled toward the luminance-matched neutral axis (hue and luminance
preserved) rather than clipped per channel.

**Export as scene-linear ACEScg**: skips step 4 and frame editing entirely and writes the
scene-linear data straight out, for DaVinci, Nuke and the like. The file is a 32-bit IEEE floating-
point TIFF, preserves negative and greater-than-one channels, and embeds a deterministic linear
ACEScg ICC. It will still look dark and flat in an ordinary photo viewer — that is expected.

**Contact sheet**: tiles the whole roll into one grid. Roll info on the right (camera / film / ISO /
roll no. / lab / process / date / location / notes) is burned onto the foot of the sheet as one
identification strip. Never written to EXIF.

> **Want the screen to be accurate? You still need to calibrate the display.** On Windows Advanced
> Color, OpenRevelare submits a linear extended-sRGB FP16 surface and DWM performs the one display
> transform. In legacy SDR, the application uses the same LittleCMS instance and the registered
> monitor ICC for that one transform. Moving the window between displays, or changing a profile,
> DPI, or Advanced Color state, rebuilds presentation automatically. A failure is exposed as an
> emergency-sRGB warning in the status bar, with full diagnostics available to copy. Measure the
> screen with a colorimeter and register the correct system profile, then choose the output space
> you actually deliver. The dedicated macOS presenter is outside this Windows repair.

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

## 10. Three ways to look at a frame

The histogram is pinned at the top of the right-hand panel, with two switches under it:

- **Waveform**: x is position across the frame, y is display level, the three channels drawn over
  each other. A neutral reads grey and a cast separates the channels vertically. It answers the
  question a histogram cannot — **is the cast uniform or does it drift across the frame** — because
  an uneven light board or a tilted copy stand looks exactly like a uniform tint in a histogram and
  is obvious here. Computed only while it is on.
- **Over / under-exposure overlay** (J): red is clipped highlights, blue is crushed shadows. Both
  thresholds are adjustable now — switching the overlay on reveals two sliders, in percent of
  display luma. A print can hold to 98%, while on screen 95% often already reads as paper white.
  The two values are saved with the application, not with the project.

**Help → Frame technical report…** prints every number this frame's rendering rests on, and **where
each one came from**:

- how the input domain was settled (read from the file's own declaration, or taken by convention —
  the latter being the thing to change first when a result looks wrong)
- whether a camera matrix was available, and why not when it was not
- the three T_base readings, whether they are this roll's calibration or the default, and whether
  they have the R ≥ G ≥ B shape a colour negative's base must have
- the inversion's six absolute densities and the three channels' spans
- output space, print film emulation, HDR

There is a "Copy all" button for pasting into an issue. It divides work with **Help → Copy colour
diagnostics**: that one describes the DISPLAY path (this machine, this screen), this one describes
THIS FRAME. Wrong on screen — start with the former; wrong in the export too — start with this.

---

## 11. Keyboard

| Key | Action |
|---|---|
| Ctrl+N / Ctrl+O | New roll / add images |
| Ctrl+E | Export this frame |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+C / Ctrl+V | Copy / paste parameters — follows the open tab: Cineon calibration or Display |
| N | Temporary negative view (for aiming at the base) |
| K | Before/after (without Stage 2) |
| J | Over / under-exposure warning |
| F / Ctrl+1 | Fit to window / actual pixels 100% |
| G / D | Library / editing |
| Enter / Esc | Crop: apply / cancel; other sampling: Esc cancels |
| Ctrl+Shift+T | Light/dark theme |
| Ctrl+, | Preferences |

Sampling: light up a sampling button (the dashed-rectangle icon), then drag a box on the preview;
Esc cancels. Double-click a slider's label to reset it. Drag with the left button to pan once
zoomed; the wheel zooms.
