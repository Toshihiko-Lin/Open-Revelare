# Pinned Little CMS assembly

OpenRevelare builds its app-owned Little CMS shared library from the official
2.19.1 release archive. Both scripts verify the release SHA-256 before extracting
or compiling anything:

- URL: `https://github.com/mm2/Little-CMS/releases/download/lcms2.19.1/lcms2-2.19.1.tar.gz`
- SHA-256: `BFC54F7BAB59FBC921012014A8032E4CBA4ABD46DB47D46B76416A8C0B2815C8`

Run `bash packaging/lcms/build-unix.sh` on Linux or macOS. Run
`./packaging/lcms/build-windows.ps1` from PowerShell on a Windows machine with
the Visual Studio 2022 C++ toolchain.

The scripts write only their named Little CMS artifacts beneath `native/<rid>`;
they do not clear that directory because it is shared with LibRaw. Each build
also writes `lcms2.manifest.json`, recording the source hash, build system and
SHA-256 of every produced library.
