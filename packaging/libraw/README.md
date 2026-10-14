# LibRaw 0.22.2

All supported platforms build the same application-owned native bridge from
the official LibRaw **0.22.2** archive. CMake verifies the SHA-256 of LibRaw,
libjpeg-turbo **3.1.2**, and Little CMS **2.19.1** before extracting them.
Upstream sources are not patched. JPEG and Little CMS are linked statically
inside the replaceable shared library; OpenMP and optional SDKs are disabled.
The independent Little CMS shared library used by the application's display
and export pipeline is unchanged.

Requirements: CMake 3.21+, a C/C++ compiler, and internet access on the first
build. Windows uses Visual Studio 2022 with the C++ workload; macOS uses Xcode
command-line tools; Linux uses GCC or Clang plus make. CMake is available in
the Visual Studio CMake component or via `python -m pip install cmake`.

```powershell
./packaging/libraw/build-windows.ps1
```

```bash
bash packaging/libraw/build-unix.sh
```

Build artifacts are cached in `scratch/libraw-build-<rid>` and installed under
`native/<rid>`. The scripts never clear that shared native directory. macOS's
old `packaging/macos/bundle-libraw.sh` entry point forwards to the new build;
arbitrary prebuilt LibRaw dylibs cannot implement this bridge's ABI.

`Directory.Build.targets` copies the library and license texts into executable
and test outputs, using the explicit publish RID or SDK host RID. Publish fails
if the library is missing. CI builds the real library on Windows, Linux and
Apple Silicon and requires a runtime version assertion and decode tests.
The release workflow also builds the Intel Mac library on its native runner.
Linux CI/release builds stay on Ubuntu 22.04 to preserve the previous native
runtime's glibc baseline. Windows statically links the MSVC runtime so the RAW
decoder does not require a separately installed VC++ redistributable.

`bridge.cpp` owns all access to LibRaw's C++ structures. Managed code passes
only opaque handles, primitives and flat arrays, and checks the runtime version
before opening files. Safe handles own contexts, pinned input buffers and
independently allocated processed images. Decode parameters preserve the
previous camera-native, unit-WB, linear 16-bit output, AHD, half-size, FBDD and
vendor/region cropping behavior. Metadata probes open the file directly and
do not unpack or read the entire file into managed memory.

To build a modified LibRaw, change the source URL/hash in `CMakeLists.txt`, build
the bridge with compatible headers, and replace the installed native library.
For another release, also update the managed version check and runtime test.
The complete bridge source and build scripts are available in this repository;
upstream source URLs/checksums are in `CMakeLists.txt`, and redistributed license
texts are in `licenses/`. This software is based in part on the work of the
Independent JPEG Group.

Camera support is separate from the runtime version. S1M2E RW2 support must be
verified using the actual camera's original files; upgrading is not evidence
that all recording modes decode correctly.
