using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

public sealed record InputAuditOptions(int Repeats = 3, int Workers = 1, int PreviewEdge = 1600,
    int CompareEdge = 320, bool CompareFull = false, long MaxFullPixels = 32_000_000,
    TiffInputAssumption TiffAssumption = TiffInputAssumption.Unspecified);
public sealed record InputPhase(string Name, double Milliseconds, IReadOnlyDictionary<string, object?> Tags);
public sealed record InputSampleStatistics(double[] Mean, double[] Minimum, double[] Maximum,
    long NonFiniteSamples, long AtOrBelowZeroSamples, long AtOrAboveOneSamples, long Samples);
public sealed record InputAuditRun(double Milliseconds, string PixelSha256, InputSampleStatistics Samples, InputPhase[] Phases);
public sealed record InputAuditComparison(double[] MeanDifference, double? PixelRmse, bool SameDimensions, string Meaning);
public sealed record InputFileAudit(string Path, bool IsRaw, RawColorMetadata? RawMetadata,
    string? TiffEvidence, bool? TiffCharacterized, string? DecodeRecipe, int Width, int Height,
    InputAuditRun[] Runs, bool RepeatPixelsMatch, InputAuditComparison? FullComparison,
    string? ComparisonSkipped, string? Error);
public sealed record InputAuditReport(string Schema, DateTime CreatedUtc, InputAuditOptions Options,
    int EffectiveWorkers, string Runtime, string NativeBuild, InputFileAudit[] Files, string Limitation);

/// <summary>Audits original files without requiring a chart, illuminant or claimed neutral scene.</summary>
public static class InputAudit
{
    public static async Task<InputAuditReport> RunAsync(IReadOnlyList<string> paths, InputAuditOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (paths.Count == 0) throw new ArgumentException("At least one source is required.", nameof(paths));
        if (options.Repeats is < 1 or > 20 || options.Workers is < 1 or > 4 || options.PreviewEdge < 1
            || options.CompareEdge < 1 || options.MaxFullPixels < 1 || !Enum.IsDefined(options.TiffAssumption))
            throw new ArgumentOutOfRangeException(nameof(options));
        int workers = options.Workers;
        if (PhysicalMemory.TryGetAvailableBytes(out long free))
            workers = Math.Min(workers, (int)Math.Clamp((free - (1L << 30)) / (1200L << 20), 1, 4));
        var phases = new ConcurrentDictionary<ActivityTraceId, ConcurrentQueue<InputPhase>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RawDecode.Diagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (phases.TryGetValue(activity.TraceId, out var run))
                    run.Enqueue(new(activity.OperationName, activity.Duration.TotalMilliseconds,
                        activity.TagObjects.ToDictionary(t => t.Key, t => t.Value)));
            },
        };
        ActivitySource.AddActivityListener(listener);
        var files = new InputFileAudit[paths.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken,
        }, (i, token) =>
        {
            files[i] = AuditFile(paths[i], options, phases, token);
            return ValueTask.CompletedTask;
        });
        return new("openrevelare-input-audit-v1", DateTime.UtcNow, options, workers,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, RawDecode.NativeBuildInfo, files,
            "Original-file evidence and internal consistency only. No external colour reference: " +
            "no absolute colour-error claim, no inferred film/illuminant, no assumed neutral pixels. OS disk cache is unspecified.");
    }

    private static InputFileAudit AuditFile(string path, InputAuditOptions options,
        ConcurrentDictionary<ActivityTraceId, ConcurrentQueue<InputPhase>> phases, CancellationToken token)
    {
        path = System.IO.Path.GetFullPath(path);
        bool raw = false;
        try
        {
            var revision = RawSourceCache.Revision.Capture(path);
            raw = RawDecode.IsRawExtension(path);
            RawColorMetadata? metadata = raw ? RawDecode.ReadColorMetadata(path) : null;
            TiffInputDetection? tiff = raw ? null : TiffInputDetector.Detect(path);
            using var engine = new LittleCmsEngine();
            var runs = new List<InputAuditRun>();
            ImageBuffer? comparePreview = null;
            int width = 0, height = 0;
            string? recipe = null;
            bool? tiffCharacterized = null;
            for (int run = 0; run < options.Repeats; run++)
            {
                token.ThrowIfCancellationRequested();
                using var trace = new Activity("input-audit.run").SetIdFormat(ActivityIdFormat.W3C).Start();
                var timings = new ConcurrentQueue<InputPhase>();
                phases[trace.TraceId] = timings;
                try
                {
                    long start = Stopwatch.GetTimestamp();
                    ImageBuffer preview;
                    if (raw)
                    {
                        var decoded = RawDecode.DecodeRawDownsampled(path, RawDecode.FbddMode.Off,
                            new[] { options.PreviewEdge, options.CompareEdge }, RawDecode.Demosaic.Preview);
                        width = decoded.SourceWidth; height = decoded.SourceHeight;
                        preview = decoded.Previews[0]; comparePreview = decoded.Previews[1];
                        recipe = "UniWB native channels; LibRaw half-size preview; FBDD off";
                    }
                    else
                    {
                        (width, height) = TiffIO.ReadTiffSize(path);
                        var frames = TiffIO.LoadWorkingRegions(path, (0, 0, 1, 1),
                            new[] { options.PreviewEdge, options.CompareEdge }, options.TiffAssumption,
                            ColorPipelineVersion.ManagedV2, engine, TransformPrecision.Preview);
                        preview = frames[0].Pixels; comparePreview = frames[1].Pixels;
                        recipe = frames[0].Source.DecodeRecipe;
                        tiffCharacterized = frames[0].Source.OriginalEncoding is CharacterizedPixelEncoding;
                    }
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    string hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(preview.Data.AsSpan())));
                    runs.Add(new(elapsed, hash, Statistics(preview), timings.ToArray()));
                }
                finally { phases.TryRemove(trace.TraceId, out _); }
            }
            InputAuditComparison? comparison = null;
            string? skipped = options.CompareFull ? null : "Full-quality comparison not requested.";
            if (options.CompareFull && (long)width * height > options.MaxFullPixels)
                skipped = "Source exceeds the configured full-quality pixel budget.";
            else if (options.CompareFull)
            {
                token.ThrowIfCancellationRequested();
                ImageBuffer full = raw
                    ? RawDecode.DecodeRawDownsampled(path, RawDecode.FbddMode.Off, new[] { options.CompareEdge }, RawDecode.Demosaic.Full).Previews[0]
                    : TiffIO.LoadWorkingRegion(path, (0, 0, 1, 1), options.CompareEdge, options.TiffAssumption,
                        ColorPipelineVersion.ManagedV2, engine, TransformPrecision.Exact).Pixels;
                var a = Statistics(comparePreview!); var b = Statistics(full);
                bool same = comparePreview!.Width == full.Width && comparePreview.Height == full.Height;
                double? rmse = null;
                if (same && a.NonFiniteSamples == 0 && b.NonFiniteSamples == 0)
                {
                    double sum = 0;
                    for (int i = 0; i < full.Data.Length; i++) { double d = comparePreview.Data[i] - full.Data[i]; sum += d * d; }
                    rmse = Math.Sqrt(sum / full.Data.Length);
                }
                comparison = new(a.Mean.Zip(b.Mean, (x, y) => x - y).ToArray(), rmse, same,
                    "Preview minus full-quality decode; measures algorithm consistency, not absolute colour accuracy. " +
                    "Pixel RMSE is omitted when reduction grids have different dimensions or samples are non-finite.");
            }
            if (RawSourceCache.Revision.Capture(path) != revision) throw new IOException("Source changed during audit.");
            return new(path, raw, metadata, tiff is null ? null : $"{tiff.Evidence}: {tiff.Diagnostic}",
                tiffCharacterized,
                recipe, width, height, runs.ToArray(), runs.Select(r => r.PixelSha256).Distinct().Count() == 1, comparison, skipped, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(path, raw, null, null, null, null, 0, 0, [], false, null, null, ex.Message); }
    }

    public static InputSampleStatistics Statistics(ImageBuffer image)
    {
        var mean = new double[3]; var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var max = new[] { double.MinValue, double.MinValue, double.MinValue }; var count = new long[3];
        long nonfinite = 0, low = 0, high = 0;
        for (int i = 0; i < image.Data.Length; i++)
        {
            double v = image.Data[i]; int c = i % 3;
            if (!double.IsFinite(v)) { nonfinite++; continue; }
            mean[c] += v; count[c]++; min[c] = Math.Min(min[c], v); max[c] = Math.Max(max[c], v);
            if (v <= 0) low++; if (v >= 1) high++;
        }
        for (int c = 0; c < 3; c++)
            if (count[c] > 0) mean[c] /= count[c]; else min[c] = max[c] = mean[c] = 0;
        return new(mean, min, max, nonfinite, low, high, image.Data.LongLength);
    }
}
