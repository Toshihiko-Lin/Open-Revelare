# Adaptive GPU compute

Build with `python packaging/compute/build.py` (CMake plus Visual Studio C++ on Windows,
or Xcode command-line tools on macOS). Linux uses the CPU implementation. Build outputs
are copied from `native/<rid>/` into CLI, GUI, benchmark and test outputs. Windows/macOS
publishing requires the native backend to be built, but runtime device failure falls back.
macOS supports both Apple Silicon and Intel; its dylib uses a portable `@loader_path` ID.

Accelerated operations are exact-size area/Catmull-Rom resampling, output unsharp masking,
combined resize/sharpen and tetrahedral print-LUT interpolation. LUT interpolation is shared
by preview and export; the GUI's eligible SDR deliveries use the combined resize/sharpen
route with one upload and one readback across four compute passes. The source and resized
buffers are reused after their last read to bound peak allocations. RAW decode, negative inversion, ICC conversion, grading and image compression retain
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
  only when a workload bucket is at least 10% faster than its CPU reference. The first
  complete invocation warms each path; a second full invocation supplies the decision.
  Both GPU results are validated. Diagnostics retain cold and warm timings separately;
  device/shader initialization is outside these operation timings, and the workflow
  benchmark's first-run timing includes it.
- Track subsequent Auto invocations against the calibrated CPU cost. Two consecutive
  unprofitable GPU runs retire the route until mode/calibration reset; a single outlier
  does not. `LastGpuMs` and `ConsecutiveSlowRuns` expose this decision alongside the
  original warm calibration, which can differ under driver/allocation pressure.
- Keep intermediate images on the device between dependent passes; synchronize at readback.
- Use CPU immediately when another caller holds the GPU, rather than queue an interactive
  task behind an export. Auto thresholds are 4 MP for individual resize/sharpen, 1 MP
  for combined delivery and 0.5 MP for print cubes; profitability is still required.
- Check every calibration result against CPU, including nonfinite values, with absolute
  tolerance `1e-6` plus relative tolerance `2e-6`. Failure disables the backend for the session.
  Print cubes require exact float equality instead, because a tiny LUT difference can
  cross a downstream ICC curve boundary. Matrix conversion and Cineon logarithms remain
  CPU reference operations; the rejected fused-log experiment exceeded final render parity.
- Retain compiled pipelines and modest buffers, but release export-sized device buffers
  after readback when the total retained resources exceed 128 MiB.
- Preflight the complete working set, including the full-height resize intermediate,
  taps, cached buffers and D3D readback. Windows queries the current DXGI process budget
  on each operation, uses at most half that budget and leaves at least 128 MiB available
  (512 MiB conservative fallback when budget queries are unavailable). Metal uses half
  its recommended working set. Both check individual buffer and shader index limits.
- Discard cached buffers before allocation if their retained capacities would exceed
  the budget. Sharpening retries with horizontal strips including the Gaussian radius
  above/below each strip, publishing only their interiors. Strip height starts at
  most 256 rows and halves on resource pressure, down to one output row. The scheduler
  measures the complete tiled operation, including overlap transfers and host copies.
- Resource rejection/allocation pressure falls back for the current operation when
  resizing or when even a sharpening strip cannot fit, without disabling later GPU
  work or storing a failed calibration. Device errors and parity failures still disable
  the backend for the session.

This memory policy follows the separation of total working-set, individual allocation
and device limits in darktable's
[`dt_opencl_image_fits_device`](https://github.com/darktable-org/darktable/blob/master/src/common/opencl.c)
and its [memory headroom guidance](https://docs.darktable.org/usermanual/development/en/preferences-settings/processing/).
The budget values above are OpenRevelare's policy, not darktable defaults; no OpenCL code
is copied. The native ABI returns status `2` for recoverable resource pressure and `1`
for backend errors. Rebuild the native library alongside the managed code.

Auto calibrates once per operation/size bucket (and cube content identity) per process.
Its first eligible invocation runs both paths twice, so it is slower than subsequent calls. Preferences exposes
Automatic/Off; the CLI and diagnostic runs also accept `OPENREVELARE_GPU=auto|off|on`.
`on` bypasses the size/profitability gates, but still checks parity and falls back on failure.
Device failures, resource fallback counts/reasons and calibrated timings are included
in the opt-in render performance JSON.

Workloads over 100 MP, device allocation failures and unavailable backends use CPU.
Resizing and combined delivery currently require their full device working sets.
If a combined delivery cannot fit, the managed caller retries the individual operations.
Sharpening supports overlapping strips and print cubes support independent pixel chunks.
There is no GPU-resident full grading pipeline. Further expansion should follow profiling
rather than imply that every stage is faster.

## Validation

```powershell
python packaging/compute/build.py
$env:OPENREVELARE_REQUIRE_GPU = '1'
dotnet test src/OpenRevelare.Tests -c Release --filter FullyQualifiedName~GpuComputeTests
dotnet run --project benchmarks/OpenRevelare.ComputeBenchmark -c Release
dotnet run --project benchmarks/OpenRevelare.ComputeBenchmark -c Release -- --workflow scratch/performance/gpu-workflow.json
```

The hardware tests fail if the OS backend cannot initialize or compile its kernels.
They also reject a 48 GiB resize intermediate before allocation and verify that both
the native session and managed scheduler can subsequently run a small GPU workload.
Tiled sharpening is checked at strip heights 1/2/7/23/64 with nonfinite samples at seams.
Combined delivery covers up/downscaling, source preservation and tiny/no-resize cases.
Cube acceptance compares exact floats for both shipped stocks, then checks complete SDR/HDR
ManagedV2 renders and canonical scenes against GPU-off output.
Without `OPENREVELARE_REQUIRE_GPU=1`, the hardware facts are explicitly skipped; the parity
validator test still runs. Run the hardware suite on an actual Mac for Metal acceptance:
compiling Objective-C++ alone cannot establish runtime shader correctness or performance.

The benchmark uses generated float images and five warm samples. It includes transfers,
allocation, both kernels and the sharpening publication copy, and checks pixel parity.
It measures these operations only, not full RAW-to-export or interactive UI latency.
An operation rejected by the memory policy reports a null GPU time and a resource reason;
initialization, driver and parity errors still fail the benchmark. Auto output is checked
against CPU even when the memory policy selects CPU fallback.

The workflow benchmark compares Off/On/Auto over generated 1.6 MP and 8 MP TIFF inputs,
analytic and Kodak rendering, canonical preview, half-size standard sharpening and JPEG
writing. It records five warm samples plus the first invocation and checks rendered and
delivery pixels. It excludes real RAW, desktop scheduling, compositor and display latency.
Version 1 omitted delivery preparation; its total cannot be compared with version 2 totals.

On the development Windows machine, standalone cube interpolation was slower than CPU and
Auto rejected it. Combined 1.6 MP delivery measured about 12 ms CPU versus 4 ms GPU in warm
samples. Large-image results varied and Auto may reject them; these component improvements
are not evidence of a comparable full-workflow speedup. Canonical ICC preview conversion
remains the dominant measured stage in these generated fixtures.

With `OPENREVELARE_RENDER_TRACE=1`, GUI performance summaries also collect `raw.read`,
`raw.open`, `raw.unpack`, `raw.process.preview/full`, `raw.output-copy`, `raw.rgb-float`,
`raw.region-rgb-float` and `raw.preview-box`.
These opt-in ActivitySource phases carry no file names or pixel data. `raw.process` includes
LibRaw's processing chain, not only demosaicing. External hosts can listen to the
`OpenRevelare.RawDecode` ActivitySource; with no listener, no activities are allocated.
