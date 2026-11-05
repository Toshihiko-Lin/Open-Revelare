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

`--raw-import [path-to-raw]` compares four independent crop preview decodes (serial and
parallel) with one shared source decode. Without a path it generates a 4000×3000 linear
DNG. It verifies exact pixels, uses the LibRaw backend with FBDD off and a warm source
cache, and reports seven-sample medians after warmup. These measurements cover shared
RAW crops, not imports of distinct files, cold storage, UI publication or thumbnail rendering.

`--raw-roll <folder-or-file> [workers=2] [limit=0] [full|shared]` decodes original RAW files,
records per-file pixel SHA-256 and native phase timings, and reports wall time, CPU time
and peak working set. The optional `full` mode hashes every full-resolution AHD pixel.
The default measures 1600-edge half-size previews. Source-byte cache is cleared at entry;
the OS disk cache is unspecified. Use different processes/native libraries for before/after
comparisons and match hashes by filename, as parallel completion order varies.
`shared` produces the usual half-size preview and full AHD image from one source unpack,
and reports both pixel hashes so each recipe can be checked against independent decodes.
