# HEIC export runtime

Run `python packaging/heif/build.py` with Python 3.12+, CMake and a C/C++ toolchain
(Visual Studio 2022 C++ on Windows, Xcode command-line tools on macOS, GCC/Clang on Linux).
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
assembly is disabled so building does not require NASM. SDR stores profile-encoded pixels
with their exact ICC. Its NCLX primaries/transfer are unspecified to avoid contradicting
the ICC (in particular Adobe RGB and the project's gamma-2.4 Rec709).
Windows builds link the C/C++ runtime statically, matching the self-contained LibRaw build.

HDR converts the canonical linear extended sRGB carrier into BT.2020, preserving luminance
when fitting negative gamut components, then encodes ST 2084 PQ with 1.0 = 203 nits.
NCLX declares BT.2020 / PQ / BT.2020 non-constant-luminance (9/16/9), full range.
No linear ICC is attached to PQ pixels. This is a direct HDR image, without an SDR base or
gain map; HDR presentation and SDR tone mapping depend on the receiving viewer.

The managed boundary validates the frame before writing, keeps atomic file replacement,
and shares the quality-then-resolution size search with JPEG. The byte ceiling includes
the complete HEIC container and all profiles.

Licenses and corresponding source:

- libheif: LGPL-3.0-or-later, https://github.com/strukturag/libheif/releases/tag/v1.21.2
- x265: GPL-2.0-or-later, https://bitbucket.org/multicoreware/x265_git/src/4.1/
- This bridge and build recipe are distributed with OpenRevelare under GPL-3.0-only.
