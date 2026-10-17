# Export compatibility fixtures

Generate persistent, synthetic 1031×769 fixtures with gradients, colour variation and a
neutral highlight patch. These exercise odd dimensions and partial TIFF strips. They do
not assert that an 80 MP file fits a particular phone's decoder or memory limits.

```powershell
dotnet run --project benchmarks/OpenRevelare.ExportCompatibility -c Release -- scratch/compat-fixtures
# Optional dimensions after the output directory; very large fixtures use substantial RAM/disk.
python benchmarks/OpenRevelare.ExportCompatibility/verify.py scratch/compat-fixtures
powershell -NoProfile -File benchmarks/OpenRevelare.ExportCompatibility/verify-windows.ps1 -Directory scratch/compat-fixtures
```

Output directory must not contain fixtures with the same names. Files and SHA-256 manifest
are intentionally retained for transfer to phones and Macs. Each space's samples use the
same numeric gradients, not a colour-matched rendering of one photograph. Verify visual
appearance only against its corresponding profile and rendition.

Python needs Pillow, numpy, tifffile and imagecodecs. It uses LittleCMS to transform JPEG
profiles and tifffile/imagecodecs to compare TIFF pixels bit for bit against a hash of the
source quantization. DNG checks read the full-resolution LinearRaw SubIFD separately
from its preview. They are not a replacement for Adobe's DNG validator or a RAW renderer.

For an independent HDR decoder, build Google's
[libultrahdr](https://github.com/google/libultrahdr), then add
`--ultrahdr PATH_TO_ULTRAHDR_APP` to `verify.py`. The script decodes each HDR JPEG three
ways: both metadata packets, ISO only and XMP only. It disables the other namespace
without changing segment sizes, so MPF offsets remain valid. All paths must recover
finite RGBA half-floats and highlights above SDR white. This library is a verification
dependency only, not a new application dependency.

The Windows script fully copies pixels through WIC. It reports native pixel format and
whether the decoded frame has the source dimensions. Conversion to BGRA32 tests basic
decoding; it does not measure HDR display behaviour or compare colour accuracy.

For isolated builds, `dotnet run --artifacts-path` on the installed SDK can search the
wrong executable directory. Use `dotnet build --artifacts-path scratch/compat-dotnet-artifacts`
then invoke the generated DLL with `dotnet` instead.

Results and remaining platform checks are recorded in
[export compatibility](../../docs/export-compatibility.md).
