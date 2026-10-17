"""Build the OS-native GPU backend; Linux retains the CPU implementation."""
from pathlib import Path
import platform
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[2]
system = platform.system()
if system == "Linux":
    print("GPU compute: Linux uses the CPU backend.")
    raise SystemExit(0)
arch = "arm64" if platform.machine().lower() in ("arm64", "aarch64") else "x64"
rid = {"Windows": "win", "Darwin": "osx"}[system] + "-" + arch
if rid not in ("win-x64", "osx-x64", "osx-arm64"):
    raise RuntimeError("Unsupported compute target: " + rid)
work = ROOT / "scratch/compute-build" / rid
destination = ROOT / "native" / rid
common = ["-DCMAKE_BUILD_TYPE=Release"]
if system == "Windows":
    common += ["-A", "x64"]
subprocess.run(["cmake", "-S", str(ROOT / "packaging/compute"), "-B", str(work), *common], check=True)
subprocess.run(["cmake", "--build", str(work), "--config", "Release", "--parallel", "4"], check=True)
suffix = ".dll" if system == "Windows" else ".dylib"
binary = next(work.rglob("*OpenRevelare.Compute.Native" + suffix))
destination.mkdir(parents=True, exist_ok=True)
shutil.copy2(binary, destination / binary.name)
