"""Build the same pinned, encoder-only HEIC runtime on all release platforms."""
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
RID = {"Windows": "win", "Darwin": "osx", "Linux": "linux"}[platform.system()] + (
    "-arm64" if platform.machine().lower() in ("arm64", "aarch64") else "-x64")
if RID not in ("win-x64", "linux-x64", "osx-x64", "osx-arm64"):
    raise RuntimeError("Unsupported HEIC build target: " + RID)
# Do not silently ship a scalar x265 build when the assembler is missing.
nasm = shutil.which("nasm") if RID.endswith("-x64") else None
if RID.endswith("-x64") and not nasm:
    raise RuntimeError("HEIC x64 builds require NASM 2.13+ on PATH (choco/apt/brew install nasm).")
WORK = ROOT / "scratch" / "heif-build" / RID
PREFIX = WORK / "prefix"
DEST = ROOT / "native" / RID
WORK.mkdir(parents=True, exist_ok=True)
DEST.mkdir(parents=True, exist_ok=True)


def source(name, url, digest):
    archive = WORK / (name + ".tar.gz")
    if not archive.exists():
        urllib.request.urlretrieve(url, archive)
    if hashlib.sha256(archive.read_bytes()).hexdigest() != digest:
        raise RuntimeError("Source SHA-256 mismatch: " + str(archive))
    target = WORK / name
    if not target.exists():
        target.mkdir()
        with tarfile.open(archive) as tar:
            tar.extractall(target, filter="data")
    return next(p for p in target.iterdir() if p.is_dir())


def run(*args):
    subprocess.run([str(a) for a in args], check=True)


heif = source("libheif", "https://github.com/strukturag/libheif/releases/download/v1.21.2/libheif-1.21.2.tar.gz",
              "75f530b7154bc93e7ecf846edfc0416bf5f490612de8c45983c36385aa742b42")
x265 = source("x265", "https://bitbucket.org/multicoreware/x265_git/get/4.1.tar.gz",
              "7d23cdcdbd510728202c0dfbf7c51eda26a395de2096c504c2b10d6035711102")
# x265's two OLD policies were removed in CMake 4. Use their supported NEW
# behavior; the codec source and bitstream settings are unchanged.
x265_cmake = x265 / "source/CMakeLists.txt"
text = x265_cmake.read_text()
for policy in ("CMP0025", "CMP0054"):
    text = text.replace(policy + " OLD", policy + " NEW")
# CMP0025 NEW identifies Apple's compiler as AppleClang. x265 4.1 only
# recognizes Clang, otherwise skipping the flags/defines that enable ARM NEON.
text = text.replace('if(${CMAKE_CXX_COMPILER_ID} STREQUAL "Clang")',
                    'if(CMAKE_CXX_COMPILER_ID MATCHES "^(Apple)?Clang$")')
x265_cmake.write_text(text)
common = ["-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_POSITION_INDEPENDENT_CODE=ON"]
if platform.system() == "Windows":
    common += ["-A", "x64"]
elif platform.system() == "Darwin":
    # Match the existing compute/presenter deployment floor; do not accidentally
    # require the build runner's newer macOS just by linking VideoToolbox.
    common += ["-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0"]
# One Main10 fallback serves SDR and PQ alike, without a system HEVC extension.
# Windows/macOS also try their OS hardware encoder at runtime. Keep SIMD enabled; x265 dispatches at
# runtime to the instruction set supported by the user's CPU.
run("cmake", "-S", x265 / "source", "-B", WORK / "x265-build", *common,
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5", "-DCMAKE_INSTALL_PREFIX=" + str(PREFIX),
    "-DHIGH_BIT_DEPTH=ON", "-DMAIN12=OFF", "-DENABLE_SHARED=OFF", "-DENABLE_CLI=OFF",
    "-DENABLE_ASSEMBLY=ON", "-DENABLE_HDR10_PLUS=OFF",
    "-DSTATIC_LINK_CRT=" + ("ON" if platform.system() == "Windows" else "OFF"),
    *(["-DNASM_EXECUTABLE=" + nasm] if nasm else []))
run("cmake", "--build", WORK / "x265-build", "--config", "Release", "--parallel", "4")
run("cmake", "--install", WORK / "x265-build", "--config", "Release")
library = next((PREFIX / "lib").glob("*x265*.lib")) if platform.system() == "Windows" else PREFIX / "lib/libx265.a"
run("cmake", "-S", ROOT / "packaging/heif", "-B", WORK / "bridge-build", *common,
    "-DHEIF_SOURCE=" + str(heif), "-DX265_INCLUDE_DIR=" + str(PREFIX / "include"),
    "-DX265_LIBRARY=" + str(library), "-DCMAKE_INSTALL_PREFIX=" + str(DEST))
run("cmake", "--build", WORK / "bridge-build", "--target", "OpenRevelare.Heif.Native",
    "--config", "Release", "--parallel", "4")
# Install only our bridge; libheif/x265 are statically linked into it.
suffix = ".dll" if platform.system() == "Windows" else ".dylib" if platform.system() == "Darwin" else ".so"
binary = next((WORK / "bridge-build").rglob("*OpenRevelare.Heif.Native" + suffix))
shutil.copy2(binary, DEST / binary.name)
licenses = DEST / "heif-licenses"
licenses.mkdir(exist_ok=True)
shutil.copy2(heif / "COPYING", licenses / "libheif-LGPL-3.0.txt")
shutil.copy2(x265 / "COPYING", licenses / "x265-GPL-2.0.txt")
(DEST / "heif.manifest.json").write_text(json.dumps({
    "libheif": "1.21.2", "x265": "4.1", "rid": RID, "assembly": True,
    "hardware": {"Windows": "Media Foundation", "Darwin": "VideoToolbox", "Linux": None}[platform.system()],
    "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
}, indent=2) + "\n", encoding="utf-8")
