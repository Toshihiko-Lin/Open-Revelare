# RAW preview reduction benchmark

```powershell
dotnet run --project benchmarks/OpenRevelare.ComputeBenchmark -c Release -- --raw-previews
```

Uses generated native 16-bit buffers to compare independent box reduction against shared
RGB16 sample conversion and tiled accumulation. Timings include preview allocation and
reduction, exclude RAW parsing, unpacking, demosaicing and disk I/O, and report the median
of seven interleaved samples after warmup. Each accepted case checks output float bits
against the previous independent kernel. Unsupported channel layouts, unavailable SIMD
or incompatible box grids retain the independent production path.

`--sample-conversion` measures integer/float sample conversion alone. `--tiff-regions`
compares separate TIFF crop reads with a shared streamed pass using generated TIFFs.
The default invocation runs the GPU compute benchmark; `--workflow` runs the generated
TIFF render/export workflow benchmark.
