# HEIC export runtime

Run `python packaging/heif/build.py` with Python 3.12+, CMake and a C/C++ toolchain
(Visual Studio 2022 C++ on Windows, Xcode command-line tools on macOS, GCC/Clang on Linux).
x64 builds also require NASM 2.13+ on PATH (`choco install nasm`, `sudo apt-get install nasm`,
or `brew install nasm`). Apple Silicon uses the toolchain's ARM assembler.
The script builds for the host architecture, matching the supported release RIDs:
win-x64, linux-x64, osx-x64 and osx-arm64. CI and release jobs run it before tests/publish.

Sources are checksum-pinned: libheif 1.21.2 and x265 4.1. The only upstream source adjustment
switches x265's CMP0025/CMP0054 CMake policies from OLD to NEW for CMake 4 compatibility.
Both libraries are statically linked into one app-owned `OpenRevelare.Heif.Native` shared
library. Codec plugin loading and optional codecs are disabled, so encoding does not depend
on system codec extensions or library search paths. The runtime and license files are copied
from `native/<rid>/` into build/publish outputs by `Directory.Build.targets`.
The native manifest records source versions and the resulting binary's SHA-256.

Both SDR and HDR use HEVC Main10 with 4:2:0 chroma. x265 is built for 10-bit input only;
assembly/SIMD primitives are enabled with runtime CPU detection. Keep them enabled: scalar
x265 builds make full-resolution exports substantially slower. The encoder retains its
medium preset and quality settings. SDR stores profile-encoded pixels
with their exact ICC. Its NCLX primaries/transfer are unspecified to avoid contradicting
the ICC (in particular Adobe RGB and the project's gamma-2.4 Rec709).
Windows builds link the C/C++ runtime statically, matching the self-contained LibRaw build.

The bridge explicitly selects ordinary HEVC Main10 (`profile_idc=2`). Libheif's default
`main10-intra` plus `totalFrames=1` makes x265 signal Range Extensions (`profile_idc=4`),
which is a different decoder requirement despite also storing 10-bit samples. We disable
that constraint through supported x265 parameters, disable inter-frame delay, and request
repeated headers; the adapter still submits exactly one I frame. Regression tests inspect
the actual `hvcC` profile, not just bit depth or the HEIF brand.

Images larger than 1024 pixels on either axis use a standard HEIF `grid` with 1024x1024
tiles, keeping each coded image at Main10 Level 4 or below. This avoids requiring a phone
to decode a single 80 MP HEVC picture. The primary grid retains the exact photograph size,
ICC/NCLX and EXIF/XMP; right/bottom tile padding repeats edge samples outside that size.
Only one input tile is copied into native memory at a time. This uses the stable
[libheif tiling API](https://github.com/strukturag/libheif/wiki/Reading-and-Writing-Tiled-Images)
and the grid layout handled by
[Android's image decoder](https://android.googlesource.com/platform/frameworks/av/+/refs/heads/main/media/libstagefright/FrameDecoder.cpp).
It does not change PQ to SDR, reduce image resolution, or guarantee a particular gallery's
HDR presentation. Actual device/gallery acceptance still needs testing with the new export.

HDR converts the canonical linear extended sRGB carrier into BT.2020, preserving luminance
when fitting negative gamut components, then encodes ST 2084 PQ with 1.0 = 203 nits.
NCLX declares BT.2020 / PQ / BT.2020 non-constant-luminance (9/16/9), full range.
No linear ICC is attached to PQ pixels. This is a direct HDR image, without an SDR base or
gain map; HDR presentation and SDR tone mapping depend on the receiving viewer.

The managed boundary validates the frame before writing, keeps atomic file replacement,
and shares the quality-then-resolution size search with JPEG. The byte ceiling includes
the complete HEIC container and all profiles. Quality probes reuse quantized pixels at each
resolution so HDR gamut/PQ conversion runs only once per resolution.

Licenses and corresponding source:

- libheif: LGPL-3.0-or-later, https://github.com/strukturag/libheif/releases/tag/v1.21.2
- x265: GPL-2.0-or-later, https://bitbucket.org/multicoreware/x265_git/src/4.1/
- This bridge and build recipe are distributed with OpenRevelare under GPL-3.0-only.

## Adaptive HDR option

The GUI also offers Adaptive HDR; CLI uses `--heic-adaptive`. This packages an
ICC-tagged SDR primary (sRGB or Display P3), a hidden HEVC gain map and a standard
`tmap` derived image with ISO 21496-1 metadata. The `altr` group prefers the HDR
alternate, while ordinary HEIC readers display SDR. The alternate is described as
linear in the base primaries; the map itself has no ICC and no image transfer curve.
Maps above a 1024-pixel long edge use half the long edge and quality capped at 75;
smaller maps keep their resolution. This reduces auxiliary storage with some loss of
fine local gain detail. File-size fitting includes the entire container.

The container follows [libavif’s tmap syntax](https://github.com/AOMediaCodec/libavif/blob/main/src/write.c).
Actual Adaptive HDR display still requires testing on Android and Apple devices;
valid syntax and a correct SDR fallback do not guarantee every viewer restores HDR.
