using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

// This harness uses generated TIFFs and managed display data. It never opens a window or
// modifies the real catalog. UI presentation, OS-cold disk reads and real RAW are separate runs.
string output = Path.GetFullPath(args.FirstOrDefault() ?? "scratch/performance/printroom-preview-baseline.json");
string root = Path.Combine(Path.GetTempPath(), "revelare-preview-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("OPENREVELARE_CONFIG_DIR", Path.Combine(root, "config"));
Environment.SetEnvironmentVariable("OPENREVELARE_DATA_DIR", Path.Combine(root, "data"));
var statistics = new PerformanceStatistics();
using var cmm = new LittleCmsEngine();
const ColorPipelineVersion version = ColorPipelineVersion.ManagedV2;
var parameters = new FrameParams { ExposureEv = 0.35, Contrast = 0.12, Saturation = 0.08 };
string path = Path.Combine(root, "synthetic-negative.tif");
var image = new ImageBuffer(2400, 1600);
for (int y = 0; y < image.Height; y++)
    for (int x = 0; x < image.Width; x++)
    {
        int i = (y * image.Width + x) * 3;
        float density = 0.15f + 1.65f * x / image.Width + 0.08f * MathF.Sin(y * 0.025f);
        image.Data[i] = MathF.Pow(10, -density);
        image.Data[i + 1] = MathF.Pow(10, -(density + 0.15f));
        image.Data[i + 2] = MathF.Pow(10, -(density + 0.35f));
    }
TiffIO.ExportTiff16(image, path, TiffIO.CompressionMode.Lzw);

T Time<T>(string phase, Func<T> work)
{
    long start = Stopwatch.GetTimestamp();
    T value = work();
    statistics.Observe(phase, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    return value;
}
WorkingFrame Decode() => ImageIo.LoadWorkingPreview(path, 1600, version, cmm, TiffInputAssumption.Linear).Preview;
CachedPreviewData Prepare(WorkingFrame source)
{
    var rendered = Time("pipeline", () => Pipeline.Render(source, parameters, version, cmm));
    var scene = Time("canonical-scene", () => CanonicalPreviewConverter.Convert(rendered, cmm, 1));
    var histogram = Time("histogram", () => HistogramData.FromFrame(rendered, 1));
    return new(rendered, scene, histogram);
}
WorkingFrame preview = Time("first-preview-decode", Decode);
// Warm JIT and CMM before comparing repeated foreground work against prepared cache hits.
var prepared = Prepare(preview);
var frame = new RollFrame(path);
PreparedPreviewKey Key() => new(frame, SourceStamp.Capture(path), "whole", PreviewParametersKey.Create(parameters), version, null);
var cache = new PreparedPreviewCache(availableBytes: () => null);
cache.Put(Key(), prepared, cache.Generation);
const int iterations = 30;
for (int i = 0; i < iterations; i++)
{
    // Interleave the two paths to reduce sensitivity to clock and thermal drift.
    var recomputed = Time("foreground-prepare-without-cache", () => Prepare(preview));
    var hit = Time("foreground-prepared-hit-with-key", () => cache.Get(Key()))
        ?? throw new InvalidOperationException("Prepared cache unexpectedly missed.");
    if (!hit.Rendered.Pixels.Data.AsSpan().SequenceEqual(recomputed.Rendered.Pixels.Data)
        || !hit.Scene.LinearExtendedSrgbRgba.AsSpan().SequenceEqual(recomputed.Scene.LinearExtendedSrgbRgba.AsSpan()))
        throw new InvalidOperationException("Prepared data differs from fresh pixels.");
}
for (int i = 0; i < 5; i++) Time("uncached-preview-decode-os-cache-unspecified", Decode);
var drag = preview.WithPixels(Resample.Box(preview.Pixels, 800));
for (int i = 0; i < 12; i++) Time("drag-pipeline-800", () => Pipeline.Render(drag, parameters, version, cmm));
for (int i = 0; i < 8; i++) Time("zoom-region-render", () => RegionRender.Render(preview, parameters,
    new RegionRender.Roi(0.25, 0.25, 0.25, 0.25), version, cmm));
var full = ImageIo.LoadWorking(path, version, cmm, TiffInputAssumption.Linear);
for (int i = 0; i < 3; i++) Time("full-tiff-render-and-write", () =>
{
    var rendered = Pipeline.Render(full, parameters, version, cmm);
    TiffIO.ExportTiff(rendered, Path.Combine(root, "export.tif"));
    return true;
});
using var process = Process.GetCurrentProcess();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    Schema = "openrevelare-preview-component-benchmark-v1", TimestampUtc = DateTime.UtcNow,
    Platform = RuntimeInformation.OSDescription, Runtime = Environment.Version.ToString(),
    ProcessorCount = Environment.ProcessorCount, SourceDimensions = "2400x1600", PreviewDimensions = $"{preview.Pixels.Width}x{preview.Pixels.Height}",
    Pipeline = version.ToString(), Iterations = iterations, PeakWorkingSetBytes = process.PeakWorkingSet64,
    PreparedResidentBytes = cache.ResidentBytes, PixelEquality = true,
    Scope = "Synthetic TIFF, in-process CPU pipeline/canonical scene/histogram; excludes Avalonia fallback bitmap, compositor and real RAW; OS file cache unspecified.",
    Phases = statistics.Snapshot(),
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);
foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file);
// Keep isolated configuration subdirectories if any were created, for diagnostics.
