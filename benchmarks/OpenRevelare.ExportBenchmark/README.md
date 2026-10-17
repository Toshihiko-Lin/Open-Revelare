# Export benchmark

```powershell
dotnet run --project benchmarks/OpenRevelare.ExportBenchmark -c Release
# Optional case prefix filter:
dotnet run --project benchmarks/OpenRevelare.ExportBenchmark -c Release -- tiff
```

Each case warms up once and reports the median of five full exports, managed allocation,
file size/hash and, for TIFF, a hash of all scanlines read back by LibTiff. Generated SDR/HDR
fixtures keep RAW decode and rendering out of the measurement. GPU acceleration is disabled
to isolate encoder changes. The temporary files are removed afterwards. Results describe
this synthetic workload and the host's warm filesystem, not full application export latency.

The optimized JPEG size search retains prepared pixels at each resolution; HDR additionally
retains the computed gain map. Every quality probe still produces complete base/map streams,
metadata and the final container before checking its byte ceiling. Resizing invalidates the
prepared images. Compression quality, 4:4:4 sampling and profiles remain unchanged.

DNG fuses the existing transfer-function decode and ties-to-even 16-bit quantization into
one parallel sweep. Its raw strip is written as a span rather than individual words; the
big-endian fallback explicitly writes little-endian blocks. No full float image copy is needed.

TIFF retains the original LZW scanline path. Uncompressed files use bounded strip buffers;
Deflate strips are independently compressed by the runtime's
[ZLibStream](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.zlibstream?view=net-8.0)
in batches of up to four workers. Only the owner touches the TIFF handle, writing finished
strips with [WriteRawStrip](https://bitmiracle.github.io/libtiff.net/help/api/BitMiracle.LibTiff.Classic.Tiff.html).
The uint16 horizontal predictor resets at every row and wraps modulo 65536; float32 stores
the original bits without prediction. TIFF compressed bytes/layout can change, while decoded
samples, bit depth, profiles and metadata must remain unchanged.

Regression tests cover JPEG quality probes before/after shrinking, merged metadata, DNG
transfer functions and nonfinite inputs, and TIFF row/strip/batch boundaries with exact sample
comparisons through LibTiff's decoder. Hardware HEIC compatibility tests are separate.
