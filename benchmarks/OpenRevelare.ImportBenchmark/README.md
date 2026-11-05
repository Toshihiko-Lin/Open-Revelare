# Full desktop import benchmark

Runs the actual Avalonia application, native preview host, full-resolution board detection,
roll auto inversion, thumbnail rendering and cache coordination. It records first-preview
and settled import times, UI progress transitions, peak working set and the native phase
summary. Completion requires all thumbnails, completed auto analysis and one second idle.
The desktop app closes after measurement. App startup is included; the file picker is not.

```powershell
dotnet run --project benchmarks/OpenRevelare.ImportBenchmark -c Release -- <staged-input-folder> <new-output.jsonl> [decode-concurrency=0]
```

Use a disposable input directory containing copies or hard links of the original RAWs.
The application writes new `.ncproj` files beside its input, so do not pass an original
photo directory. Settings, the catalog and regenerable data are redirected to a directory
named after the output file. Source RAW bytes are read only. Each run needs a new output
filename. OS disk cache is unspecified; do not run other benchmarks concurrently.

The import path can reuse a board sample's unpack for its normal preview. Early preview
publication is measured independently of full AHD completion. On machines with at least
18 GiB free and six decode slots, roll preview warm-up can overlap board analysis after
a board sample publishes its preview; lower-memory imports retain staged processing.
The native summary records shared-preview and overlap counters, plus separate board
decode and detection timings. Do not add overlapping phase durations to get wall time.

Settings match the default Auto backend, FBDD off, GPU Auto, automatic inversion enabled,
without optional LCC/Path A calibration. Native decode concurrency can be overridden by
the final argument. A `.performance.json` sidecar contains the raw stage summary.
