# Adaptive GPU compute

Build with `python packaging/compute/build.py` (CMake plus Visual Studio C++ on Windows,
or Xcode command-line tools on macOS). Linux uses the CPU implementation. Build outputs
are copied from `native/<rid>/` into CLI, GUI, benchmark and test outputs. Windows/macOS
publishing requires the native backend to be built, but runtime device failure falls back.
macOS supports both Apple Silicon and Intel; its dylib uses a portable `@loader_path` ID.

The first accelerated operations are exact-size area/Catmull-Rom resampling and output
unsharp masking. They are shared Core operations used by exports, not a separate preview
look. RAW decode, negative inversion, ICC conversion, grading and image compression retain
their existing CPU paths. HDR/scene-linear sharpening remains excluded by export policy.
HEIC's x265 SIMD optimization is independent of this compute backend.

Windows uses a hardware D3D11 device and compute shaders; macOS uses Metal. Apple Silicon
and integrated GPUs use shared buffers; discrete Mac GPUs use managed buffers with explicit
upload/readback synchronization, following Apple's [resource storage guidance](https://developer.apple.com/documentation/metal/choosing-a-resource-storage-mode-for-intel-and-amd-gpus).
There is one
shared kernel source in `compute.h`, with small platform wrappers. The CPU computes exact
resampling weights and Gaussian coefficients. Fast math and multiply/add contraction are
disabled. No CUDA SDK, GPU vendor SDK, NuGet package or codec extension is required.

The scheduling approach draws on darktable's
[pixelpipe performance guidance](https://docs.darktable.org/usermanual/3.6/en/special-topics/opencl/performance/),
[device scheduling](https://docs.darktable.org/usermanual/4.2/en/special-topics/opencl/multiple-devices/),
and [memory/performance policy](https://docs.darktable.org/usermanual/4.2/en/special-topics/mem-performance/).
This implementation does not copy its OpenCL code. The relevant principles are:

- Measure upload, all kernels, allocation and readback together. Auto keeps the GPU route
  only when a workload bucket is at least 10% faster than its CPU reference.
- Keep intermediate images on the device between the two passes; synchronize at readback.
- Use CPU immediately when another caller holds the GPU, rather than queue an interactive
  task behind an export. Thumbnail/small-image operations below 4 MP stay on CPU in Auto.
- Check every calibration result against CPU, including nonfinite values, with absolute
  tolerance `1e-6` plus relative tolerance `2e-6`. Failure disables the backend for the session.
- Retain compiled pipelines and modest buffers, but release export-sized device buffers
  after readback when the total retained resources exceed 128 MiB.

Auto calibrates once per operation/size bucket per process. Its first eligible invocation
also runs the CPU reference, so it is slower than subsequent calls. Preferences exposes
Automatic/Off; the CLI and diagnostic runs also accept `OPENREVELARE_GPU=auto|off|on`.
`on` bypasses the size/profitability gates, but still checks parity and falls back on failure.
Device failures and calibrated timings are included in the opt-in render performance JSON.

Workloads over 100 MP, device allocation failures and unavailable backends use CPU.
There is currently no tiled GPU execution or GPU-resident full grading pipeline. Those
should follow profiling and memory-budget work rather than imply that every stage is faster.

## Validation

```powershell
python packaging/compute/build.py
$env:OPENREVELARE_REQUIRE_GPU = '1'
dotnet test src/OpenRevelare.Tests -c Release --filter FullyQualifiedName~GpuComputeTests
dotnet run --project benchmarks/OpenRevelare.ComputeBenchmark -c Release
```

The hardware tests fail if the OS backend cannot initialize or compile its kernels.
Without `OPENREVELARE_REQUIRE_GPU=1`, the hardware facts are explicitly skipped; the parity
validator test still runs. Run the hardware suite on an actual Mac for Metal acceptance:
compiling Objective-C++ alone cannot establish runtime shader correctness or performance.

The benchmark uses generated float images and five warm samples. It includes transfers,
allocation, both kernels and the sharpening publication copy, and checks pixel parity.
It measures these operations only, not full RAW-to-export or interactive UI latency.
