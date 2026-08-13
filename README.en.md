<p align="center">
  <img src="docs/assets/logo.png" width="104" alt="OpenRevelare">
</p>

<h1 align="center">OpenRevelare</h1>

<p align="center">
  <b>The mask is math, not magic.</b><br>
  Camera-copied RAW or scanner TIFF, solved into positives by optics and math.<br>
  Physics does the restoring; you do the taste.
</p>

<p align="center">
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Download for Windows x64" src="https://img.shields.io/badge/Windows-x64%20installer-1677ff?style=for-the-badge&logo=windows11&logoColor=white"></a>
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Download for Linux x86_64" src="https://img.shields.io/badge/Linux-x86__64%20AppImage-e95420?style=for-the-badge&logo=linux&logoColor=white"></a>
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Download for macOS Apple Silicon" src="https://img.shields.io/badge/macOS-Apple%20Silicon-111111?style=for-the-badge&logo=apple&logoColor=white"></a>
</p>

<p align="center">
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Toshihiko-Lin/Open-Revelare?display_name=tag&sort=semver"></a>
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/actions/workflows/ci.yml"><img alt="CI build status" src="https://github.com/Toshihiko-Lin/Open-Revelare/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="GNU GPL v3" src="https://img.shields.io/badge/license-GPL--3.0--only-2ea44f.svg"></a>
</p>

<p align="center">
  <a href="README.md">中文</a> · <a href="#what-it-is">English</a>
</p>

<p align="center">
  <a href="#what-it-is">Intro</a> ·
  <a href="#why-this-project">Story</a> ·
  <a href="#three-principles">Principles</a> ·
  <a href="#who-it's-for">Who it's for</a> ·
  <a href="#how-it-compares-to-the-mainstream">Compare</a> ·
  <a href="#interface">UI</a> ·
  <a href="#download--install">Download</a> ·
  <a href="#quick-start">Start</a> ·
  <a href="#features">Features</a> ·
  <a href="#how-it-works">How</a> ·
  <a href="#building-from-source">Build</a> ·
  <a href="#roadmap--known-limitations">Roadmap</a>
</p>

<p align="center">
  <img src="docs/assets/editor-filmbase.jpg" width="100%" alt="OpenRevelare main window: roll calibration">
</p>

<p align="center"><sub>Main window "Roll calibration": roll thumbnails on the left, current frame in the middle, the roll-wide physical parameters on the right.</sub></p>

---

**OpenRevelare** converts colour negatives — camera-scanned RAW or scanner TIFF — into positives by *computing* the orange mask away instead of eyeballing curves. Input is linearised, lens-corrected, moved into the log-density domain, white-balanced and inverted on top of the Cineon standard, and written out as a positive. Every parameter is named and physically meaningful, so the same roll produces the same result today, next year, or on a different machine.

Built with C# / .NET 8 + Avalonia. **CPU only. Bilingual UI (Chinese/English)** — follows the system locale or can be locked manually. Local-first and non-destructive: source files are never modified, settings live in a `.ncproj` next to the images, and nothing requires an account or a network connection.

## What it is

Colour negatives carry an orange base — the mask. Camera-copied or scanned, they look colour-shifted until the mask is removed. OpenRevelare removes it by *computation*: the input is restored to linear light, lens defects are corrected, the signal is moved into the log-density domain, white-balanced and inverted on the Cineon standard, and a positive comes out.

Every parameter has a name and a physical meaning. The same roll gives the same result today, next year, or on a different machine — that is the difference between *computation* and *eyeballing curves*.

Tech stack: C# / .NET 8 + Avalonia, **CPU only**, one codebase for Windows / Linux / macOS. Local-first and non-destructive: source files are never modified, parameters live in a `.ncproj` next to the images; no network, no account. The UI is bilingual (Chinese/English), following the system locale or locked manually.

## Why this project

The author shoots film and was fed up with the mask-removal workflow: the mainstream options are Lightroom plugins (Negative Lab Pro, ColorPerfect, …) locked into the paid Adobe ecosystem with opaque, unreproducible processing; the free options have a steep learning curve. The word the community uses most is "dark magic": the same roll comes out different depending on who, and when, is doing the adjustment.

The idea behind OpenRevelare is simple: make mask removal *computed* instead of *tuned*. The mask is physical — the absorption of the base dyes is something you measure, not something you judge by taste. Built on the Cineon density domain, every parameter maps to a real physical quantity, and the same roll gives the same result every time.

The project started as a self-use tool, was validated with real paying users (8 paid, buy-once), then rewritten in C# based on user feedback — roughly 13× faster, three platforms, pixel-identical results for existing users. Open-sourced in August 2026, free, in the hope that it helps other film shooters too.

## Three principles

1. **The mask is physics, not taste** — the base dyes' absorption is something you measure: sample it, subtract it, done. Not something you eyeball
2. **Density is the negative's native language** — in the log-density domain the mask is a constant offset and white balance/inversion are linear operations, so results reproduce; in a non-linear domain those operations interfere and you can only tune by feel
3. **Restoration and creation are separate** — physical restoration (FilmBase) is shared by the roll; aesthetic edits (SceneBase) are per-frame; neither pollutes the other

## Who it's for

**Good fit**

- People copying negatives with a camera or a scanner, processing whole rolls and wanting consistent tones across the roll
- People not satisfied with "pull a curve and hope", who want to know what each step does physically
- People who need reproducible results — reopen a project in three years and get the same image

**Probably not a fit**

- People who want one-click output and don't want to understand any parameter: there is auto-calibration, but the point of the tool is that everything *can* be inspected and corrected
- Strict colour-accurate work — heritage copying, commercial archiving, research: OpenRevelare does not do per-roll colour-chart calibration. For that, use [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)

For standard C-41 stocks like Gold 200, the difference between the defaults and a per-roll calibration is barely visible on screen and essentially indistinguishable in print; stocks further from the reference need a calibration tweak or a SceneBase touch-up to close most of the gap.

## How it compares to the mainstream

| | Ecosystem plugins (NLP / ColorPerfect …) | Hardware calibration (DiVERE) | OpenRevelare |
|---|---|---|---|
| Form | Lightroom/PS plugin | Standalone app | Standalone app |
| Ecosystem | Locked to Adobe, $99+ | Free, open-source | Free, open-source |
| Processing | Black box, unexplainable | Physically explainable | Physically explainable |
| Barrier | Low | Needs colour chart + narrowband light | None — copy and go |
| Reproducibility | No | Yes | Yes (every parameter has a physical meaning) |

In one line: plugins sell mask removal as a filter, hardware calibration builds precision on extra gear, OpenRevelare goes "no hardware, explainable, reproducible".

## Interface

One window takes a whole roll from start to finish. The first stage, "Roll calibration": base transmittance `t_base`, `d_max`, shadows/highlights white balance, grade, sprocket mask, geometry crop. Calibrate the current frame, apply to the roll, and the other frames share the same physical parameters.

<p align="center">
  <img src="docs/assets/editor-scenebase.jpg" width="100%" alt="Main window: frame edit">
</p>

<p align="center"><sub>Second stage, "Frame edit": colour temperature/tint, exposure, black point / shadows / highlights / white point, contrast and saturation, with W/R/G/B curves at the bottom over a live histogram. Aesthetic edits stay on this page; the physical restoration is untouched.</sub></p>

<table>
  <tr>
    <td width="50%"><img src="docs/assets/library.jpg" width="100%" alt="Library roll wall"></td>
    <td width="50%"><img src="docs/assets/contactsheet-light.jpg" width="100%" alt="Contact sheet"></td>
  </tr>
  <tr>
    <td valign="top"><sub><b>Library</b>　You open the app and see your rolls, not an empty editor. Each roll has a contact-sheet cover labelled with film stock, camera, processing date and frame count; double-click to resume where you left off.</sub></td>
    <td valign="top"><sub><b>Contact sheet</b>　Lab-style full-roll contact sheets with sprocket layout and roll info burned in. Light and dark styles; exports a full-size image ready for archiving or printing.</sub></td>
  </tr>
</table>

## Download & install

Builds are on [Releases](https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest). The .NET runtime is bundled — nothing else to install.

| Platform | Package | Requirements | Maturity |
|---|---|---|---|
| Windows 10/11 x64 | `setup.exe` | none | **Stable** — developed on it, tested on every release |
| Linux x86_64 | `.AppImage` | glibc ≥ 2.35 (Ubuntu 22.04 / Debian 12+) | **Beta** |
| macOS Apple Silicon | `.dmg` | macOS 12+ | **Beta, never run on real hardware** |

<details>
<summary><b>Windows</b> — "Windows protected your PC"</summary>

Run the installer and click through.

If a blue "Windows protected your PC" dialog appears, click **More info → Run anyway**. This is SmartScreen's routine notice for software without a code-signing certificate — not a virus warning.

</details>

<details>
<summary><b>macOS</b> — "is damaged and can't be opened"</summary>

Open the dmg and drag OpenRevelare into Applications.

The first launch may report "damaged" or "unidentified developer". **The file is not damaged** — the build is simply not notarised (no Apple Developer Program membership, $99/year). Either bypass:

```bash
xattr -dr com.apple.quarantine /Applications/OpenRevelare.app
```

Or try opening once (it will be blocked), then **System Settings → Privacy & Security → Open Anyway**.

> Don't follow the old "right-click → Open" advice: macOS 15 (Sequoia) removed that entry.

**Beta notes**: the macOS build is produced in CI; the author has no Mac hardware and it has never been run on a real machine. Known gaps: `SystemMemory` has no macOS implementation (decode concurrency is a conservative fixed value) and there is no Adobe DNG Converter fallback. **Issues welcome**, especially RAW import reports.

</details>

<details>
<summary><b>Linux</b> — running the AppImage</summary>

The AppImage is a single green executable — no installation. Make it executable and double-click, or run from a terminal:

```bash
chmod +x OpenRevelare-*.AppImage && ./OpenRevelare-*.AppImage
```

(In a file manager: right-click → Properties → Permissions → check "Executable".)

FUSE is bundled; no libfuse2 needed. If it still won't start, run with `--appimage-extract-and-run`.

</details>

## Quick start

1. **Import** — drag your copied/scanned negatives into the window; enter roll info (stock, camera, processing date)
2. **Roll calibration** — calibrate the current frame: auto-calibration estimates base, white balance, grade, etc.; fix anything by hand
3. **Apply to the roll** — sync these physical parameters to the whole roll
4. **Frame edit** — per-frame aesthetic edits: colour temperature, exposure, contrast, saturation, curves
5. **Export** — 8/16-bit TIFF or JPEG, with an optional embedded ICC profile

There is no Save button — everything is written automatically to a `.ncproj` next to your images.

## Features

### Imaging

- **Density-domain inversion** — **exactly six degrees of freedom**: on top of the film base `t_base`, three absolute densities at each end (`d_min_per_channel` for black, `d_max_per_channel` for white). That is precisely as many numbers as the render consumes, one for one. Overall lightness is both ends moving together, contrast is the ends closer or further apart, and white balance is the differences between channels — all three are readings of those six numbers, so there is **no separate brightness, contrast, gamma, chroma or white-balance parameter** (`grade` / `pivot` / `chroma_grade` / `wb_high` / `wb_offset` / `d_max` / `scan_ev` are all gone)
- **Full colour management** — a wide scene-referred ACEScg working space carries the inversion; the output space is chosen in the main window (sRGB / Display P3 / Adobe RGB), frame edits happen inside it, and the export is what you already see on screen; a scene-linear ACEScg export is also available for external grading
- **Narrowband source decoupling (Path A)** — for LED / fluorescent light-box copying, inter-channel crosstalk is solved out with a 3×3 matrix from a set of R/G/B calibration frames. Method from [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)
- **Auto-calibration** — estimates base, sprocket threshold, dark-end valley, `d_max`, highlight white balance from the roll
- **Smart white balance** — DeepWB neural network estimates the white point in one click (model separately licensed, [see below](#smart-white-balance-model--separate-licence-read-this))
- **Pre-inversion corrections** — LCC flat-field, lens distortion, vignetting, sprocket mask; all done in the linear-light domain
- **Stage 2 adjustments** — exposure / levels / contrast / shadows-highlights / PCHIP curves / saturation

### Workflow

- **Roll-based management** — importing creates a roll; the library wall uses a contact sheet as cover art, filterable by format, stock, etc.
- **No "Save" action** — changes are written automatically. `.ncproj` sits next to the source images and travels with them
- **Roll sync** — virtual copies, whole-roll or per-frame parameter sync
- **Format presets** — 135 full frame (with borders) / half frame / XPan / 645 / 6×6 / 6×7 / 6×9 / 6×12
- **80-step undo/redo** (roll snapshots, consecutive tweaks merged)
- **Lab-style full-roll contact sheets** with a roll-identifier strip at the bottom

### Input & output

| | |
|---|---|
| **RAW input** | DNG / NEF / CR2 / CR3 / ARW / RAF / RW2 / ORF / PEF / IIQ etc. (LibRaw) |
| **Other input** | TIFF / JPEG / PNG |
| **Export** | 16-bit TIFF, JPEG, three output colour spaces (plus a scene-linear ACEScg export); the embedded ICC matches the pixels |

## How it works

### Why the density domain

A colour negative's signal is density by nature. Taking the negative log of transmittance — `D = -log10(T)` — gives log density, the domain of the Cineon film-scanning standard. In this domain the R/G/B channels behave linearly and predictably: the mask is close to a constant offset (one subtraction removes it), and white balance and inversion are linear operations. In a non-linear domain those operations interfere with each other and you can only tune by feel — that is where the "dark magic" comes from, and why OpenRevelare works in density.

### Two stages: FilmBase and SceneBase

|  | **FilmBase · physical restoration** | **SceneBase · aesthetic edits** |
|---|---|---|
| Describes | The roll's objective physical properties: base colour & density, maximum density, channel balance, inversion contrast, chroma-recovery coefficient | Colour-temperature preference, exposure, contrast style, final saturation |
| Nature | Not a taste decision — a measurement. Shared by the whole roll | The same negative can have completely different settings, per frame |
| Changes | The *inputs* of the inversion equation — recompute the restoration | The *output* of the inversion equation — adjust on top of the restoration |

The point of separating the two: get the physical restoration right once and the whole roll shares it; everything you do later cannot corrupt the physics underneath. Here "physical restoration" means the mask-removal result computed only from the roll's own information (base, maximum density, channel balance), with no subjective adjustment.

### Core formulas

The path from sample to positive, in a few lines:

**Base normalisation** — divide each channel by the sampled base transmittance; the orange mask is gone in one step:

$$T_\text{norm} = T / T_\text{base}$$

**Into density** — negative log of transmittance (clamped to avoid overflow):

$$D = -\log_{10}\!\bigl(\max(T_\text{norm},\ 10^{-D_\text{max}})\bigr)$$

**Density-domain white balance** — a shadow-side additive term plus a highlight-side multiplicative term (the Negadoctor two-end model):

$$D_\text{corr}[c] = D[c] \times w_\text{high}[c] + w_\text{offset}[c]$$

**Inversion** — the Cineon way: one gamma across all three channels, chroma following
proportionally, with no second coefficient:

$$D_\text{adj} = \text{pivot} + (D - \text{pivot}) \times \text{grade} - D_\text{max}$$

$$T_\text{pos} = 10^{D_\text{adj}}$$

The full derivation of every parameter lives in the in-app **Help → Theory**.

### The per-frame pipeline

1. **Back to light** — camera-copied RAW is decoded by LibRaw with all in-camera beautification disabled, to linear; display-gamma scans are linearised in one click. Both inputs meet at the same linear-light starting line
2. **Linear-domain corrections** — distortion, LCC flat-field, vignetting, and sprocket/light-panel masks. Optical defects are only physically correct to fix in the "light" state
3. **Light-source decoupling** (optional) — white light passes through, or RGB three-colour separation precisely measures and removes inter-dye crosstalk
4. **Mask removal, into density** — sampled base transmittance cancels the mask; the signal moves to log density, built on the Cineon standard
5. **Density-domain white balance** — shadows and highlights corrected separately, shadows first. That order is exactly what separates physical computation from eyeballing curves
6. **Inversion** — solves back to scene luminance using the film's own gamma. Not "restoring contrast the paper would have added": Cineon is a storage encoding for density, and negadoctor likewise, neither carries a paper stage
7. **Output** — a physically correct positive, ready for a grading suite or for direct export after Stage 2

The in-app **Help → Guide / Theory** has the full usage instructions and derivations.

## Where your data lives

| Content | Windows | Linux / macOS | Customisable |
|---|---|---|---|
| Settings, roll index | `%APPDATA%\OpenRevelare` | `OpenRevelare/` under `$XDG_CONFIG_HOME` (default `~/.config`) | fixed |
| Contact-sheet cache | `%LOCALAPPDATA%\OpenRevelare\sheets` | `OpenRevelare/sheets/` under `$XDG_CACHE_HOME` (default `~/.cache`) | ✅ folder + cap (default 1 GB) |
| Linear DNG decode cache | `.revelare-cache/` next to the sources by default | same | ✅ folder + cap (default 5 GB), per session |
| Project `.ncproj` | next to the source images | same | wherever your photos live |

The DNG cache sits next to the sources rather than on the system drive because a single 60 MP frame expands to ~349 MB. Both caches can be moved and capped, and the preferences show their current usage. Uninstalling touches none of these.

macOS and Linux share the XDG paths instead of using `~/Library/Application Support` — one code path everywhere.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/Toshihiko-Lin/Open-Revelare.git
cd Open-Revelare
dotnet build -c Release
dotnet run --project src/OpenRevelare.Gui
```

Command-line front-end (no GUI, same Core):

```bash
dotnet run --project src/OpenRevelare.Cli -- -i neg.tiff -o pos.tiff --grade 1.65 --d-max 2.0
dotnet run --project src/OpenRevelare.Cli -- --help
```

### Packaging

```bash
# Windows — requires Inno Setup 6
dotnet publish src/OpenRevelare.Gui -c Release -r win-x64 --self-contained true -o publish/win-x64
ISCC.exe open-revelare.iss                     # → installer/OpenRevelare-{version}-setup.exe

# Linux — run on Linux (script downloads appimagetool automatically)
./packaging/linux/build-appimage.sh            # → installer/OpenRevelare-{version}-x86_64.AppImage

# macOS — run on macOS
./packaging/macos/bundle-libraw.sh             # build LibRaw 0.21.4 (no macOS runtime package on NuGet)
./packaging/macos/build-app.sh --dmg           # → installer/OpenRevelare-{version}-{arch}.dmg
```

`dotnet publish -r linux-x64` / `-r osx-arm64` also works on Windows, but `appimagetool`, `codesign` and `hdiutil` must run on their own OS. All three platform artifacts are built automatically by [`.github/workflows/release.yml`](.github/workflows/release.yml) on tag.

> **macOS must pin LibRaw to 0.21.x**: Sdcb.LibRaw 0.21.1.7 marshals against the 0.21 `libraw_data_t` layout; the 0.22 shipped by brew adds fields and shifts every offset. `bundle-libraw.sh` therefore builds 0.21.4 from source.

## Smart white balance model — separate licence, please read

"Smart white balance" uses the Deep White-Balance Editing (CVPR 2020) network weights `models/net_awb.onnx`, distributed with the repo and the installers — but:

> [!IMPORTANT]
> **This file is NOT covered by the project's GPL-3.0 grant.**
> It is distributed under the original author's **CC BY-NC-SA 4.0** (Attribution — NonCommercial — ShareAlike).

OpenRevelare is free, unsold, no subscription or in-app purchases, so redistribution itself is non-commercial and consistent with the NC clause. But the **right you get from GPL-3.0 to redistribute commercially does not extend to this file** — for commercial use, delete the `models/` directory first. The app still builds and runs; only "smart white balance" reports a missing model. Manual white balance, auto highlight white balance and Path A decoupling do not depend on it.

Details in [models/README.md](models/README.md) and item 13 of [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt). The authors require citation of their paper.

## Licence

The project code is **GPL-3.0-only**, see [LICENSE](LICENSE).

**Exception**: `models/net_awb.onnx` is a third-party asset under CC BY-NC-SA 4.0, outside the GPL-3.0 grant above — see [models/README.md](models/README.md).

Third-party components shipped with the binaries and their licences are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) — LibRaw is LGPL-2.1; keeping that notice is not optional.

## Credits

- [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple) (MIT) — the narrowband RGB decoupling (Path A) approach
- [DiVERE](https://github.com/flipswitchingmonkey/DiVERE) (MIT) — reference for the density-domain colour model
- darktable's `negadoctor` module — its model `D_corr = D × wb_high + wb_offset`

Feedback and bugs: open an [issue](https://github.com/Toshihiko-Lin/Open-Revelare/issues) with OS version, camera or scanner model, input format and error message. Please don't upload original photos containing private content.

## Roadmap & known limitations

**Planned**

- Independent ECN-2 calibration data (ColorChecker 24-based; currently approximated from the C-41 baseline)
- Real-hardware macOS validation and a `SystemMemory` implementation (the macOS build has never been run on a real machine; decode concurrency is a conservative fixed value)

**Known limitations**

- No per-roll colour-chart calibration: for strict colour-accurate work (heritage copying, commercial archiving, research) use DiVERE
- 8-bit TIFF input may show slight banding in shadows; export 16-bit from the scanner when possible

## Donate

Revelare started as a small self-use tool. Development cost real money, so most features were kept open while a few advanced copying workflows were priced to cover some of it — and people actually paid, which is much appreciated.

After listening to feedback, it was rewritten, three platforms were added, and once it felt complete enough it was open-sourced — in the hope that it helps other players and developers. The film community is small, and tools are never too many. If it's been useful, a coffee is always welcome.

<p align="center">
  <img src="docs/assets/donate-wechat.png" width="220" alt="WeChat Pay">
</p>
