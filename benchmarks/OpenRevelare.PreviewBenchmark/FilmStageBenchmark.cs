using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

internal static class FilmStageBenchmark
{
    internal static void Run(string output, string? input)
    {
        string root = Path.Combine(Path.GetTempPath(), "revelare-film-benchmark-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("OPENREVELARE_CONFIG_DIR", Path.Combine(root, "config"));
        Environment.SetEnvironmentVariable("OPENREVELARE_DATA_DIR", Path.Combine(root, "data"));
        Settings.Current.DecodeBackend = RawDecode.RawBackend.LibRaw;
        Settings.Current.FbddMode = RawDecode.FbddMode.Off;
        GpuAcceleration.Mode = GpuAccelerationMode.Off;
        using var cmm = new LittleCmsEngine();
        const ColorPipelineVersion version = ColorPipelineVersion.ManagedV2;
        string? inputHash = input is null ? null : HashFile(input);
        string path;
        if (input is null)
        {
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "fixture.tif");
            var pixels = new ImageBuffer(1200, 800);
            var random = new Random(173);
            for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = 0.02f + random.NextSingle() * 0.8f;
            TiffIO.ExportTiff16(pixels, path, TiffIO.CompressionMode.None);
        }
        else path = input;
        var (source, width, height) = ImageIo.LoadWorkingPreview(path, 1600, version, cmm, TiffInputAssumption.Linear);
        if (input is null) File.Delete(path);
        var p = new FrameParams
        {
            Contrast = 0.12, Saturation = 0.08, VignetteAmount = 0.15, Rotation = 0.7,
            CropRect = (0.03, 0.03, 0.94, 0.94), SprocketEnabled = true, SprocketThreshold = 0.9,
        };
        var cache = new FilmPreviewCache(() => null);
        var renderTimes = new[] { new List<double>(), new List<double>() };
        var prepareTimes = new[] { new List<double>(), new List<double>() };
        var allocated = new[] { new List<long>(), new List<long>() };
        var encode = typeof(OpenRevelare.Gui.ViewModels.MainViewModel).GetMethod("EncodeCanonicalFallbackSrgb",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        (RenderedFrame Frame, PresentationScene Scene) Measure(int route, bool record)
        {
            long bytes = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            var rendered = route == 0 ? Pipeline.Render(source, p, version, cmm)
                : cache.Render(source, p, version, cmm, default);
            double renderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var scene = CanonicalPreviewConverter.Convert(rendered, cmm, 1);
            _ = HistogramData.FromFrame(rendered, 1);
            _ = encode.Invoke(null, [scene]);
            double prepareMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long totalBytes = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            if (record)
            {
                renderTimes[route].Add(renderMs);
                prepareTimes[route].Add(prepareMs);
                allocated[route].Add(totalBytes);
            }
            return (rendered, scene);
        }
        Measure(0, false); Measure(1, false);
        const int iterations = 30;
        for (int i = 0; i < iterations; i++)
        {
            p.ExposureEv = -0.3 + i * 0.02;
            var first = Measure(i % 2, true);
            var second = Measure(1 - i % 2, true);
            if (!MemoryMarshal.AsBytes(first.Frame.Pixels.Data.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(second.Frame.Pixels.Data.AsSpan()))
                || !MemoryMarshal.AsBytes(first.Scene.LinearExtendedSrgbRgba.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(second.Scene.LinearExtendedSrgbRgba.AsSpan()))
                || first.Frame.Fingerprint != second.Frame.Fingerprint)
                throw new InvalidOperationException("Film-stage reuse changed pixels or render identity.");
        }
        if (cache.Preparations != 1 || cache.Hits != iterations)
            throw new InvalidOperationException("Creative edits did not reuse the physical preparation.");
        var warmCache = new { cache.Preparations, cache.Hits, cache.ResidentBytes };
        var warmFull = new { Render = Summary(renderTimes[0]), Prepare = Summary(prepareTimes[0]), RawAllocatedBytes = allocated[0] };
        var warmCached = new { Render = Summary(renderTimes[1]), Prepare = Summary(prepareTimes[1]), RawAllocatedBytes = allocated[1] };
        renderTimes = [new(), new()]; prepareTimes = [new(), new()]; allocated = [new(), new()];
        p.ExposureEv = 0.1;
        for (int i = 0; i < iterations; i++)
        {
            // Upstream edits cannot hit. Measure the preparation + retained-copy cost too,
            // rather than extrapolating warm scene-edit wins to calibration/first frames.
            cache.Clear();
            p.DMinPerChannel = [0.01 + i * 0.001, 0.02, 0.03];
            var first = Measure(i % 2, true);
            var second = Measure(1 - i % 2, true);
            if (!MemoryMarshal.AsBytes(first.Frame.Pixels.Data.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(second.Frame.Pixels.Data.AsSpan()))
                || !MemoryMarshal.AsBytes(first.Scene.LinearExtendedSrgbRgba.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(second.Scene.LinearExtendedSrgbRgba.AsSpan()))
                || first.Frame.Fingerprint != second.Frame.Fingerprint)
                throw new InvalidOperationException("Film-stage cache miss changed pixels or render identity.");
        }
        if (cache.Preparations - warmCache.Preparations != iterations || cache.Hits != warmCache.Hits)
            throw new InvalidOperationException("Upstream changes unexpectedly reused a physical preparation.");
        if (input is not null && HashFile(input) != inputHash) throw new IOException("Original changed during benchmark.");
        string destination = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(new
        {
            Schema = "openrevelare-film-stage-paired-v2", TimestampUtc = DateTime.UtcNow,
            Platform = RuntimeInformation.OSDescription, Runtime = Environment.Version.ToString(), ProcessorCount = Environment.ProcessorCount,
            SourceKind = input is null ? "generated-tiff" : Path.GetExtension(input).ToLowerInvariant(), SourceSha256 = inputHash,
            SourceWidth = width, SourceHeight = height, PreviewWidth = source.Pixels.Width, PreviewHeight = source.Pixels.Height,
            CoreAssemblySha256 = HashFile(typeof(Pipeline).Assembly.Location),
            GuiAssemblySha256 = HashFile(typeof(FilmPreviewCache).Assembly.Location), CmmSha256 = cmm.Build.NativeSha256,
            Iterations = iterations, PixelBitsEqual = true, SourceUnchanged = input is null ? (bool?)null : true,
            warmCache.Preparations, warmCache.Hits, warmCache.ResidentBytes,
            Full = warmFull, Cached = warmCached,
            Miss = new { Iterations = iterations, PixelBitsEqual = true,
                Preparations = cache.Preparations - warmCache.Preparations, Hits = cache.Hits - warmCache.Hits,
                Full = new { Render = Summary(renderTimes[0]), Prepare = Summary(prepareTimes[0]), RawAllocatedBytes = allocated[0] },
                Cached = new { Render = Summary(renderTimes[1]), Prepare = Summary(prepareTimes[1]), RawAllocatedBytes = allocated[1] } },
            Scope = "Paired exposure edits followed by a forced-miss phase varying calibration endpoints; full pipeline versus GUI cache including key/lookup/finish copy. Clear is outside miss timing. Prepare includes canonical scene, histogram and CPU fallback encoding, excludes native bitmap, UI/display, decode and NegPy. GPU off, warmed CMM/JIT. Source hashed before decode, not cold disk. Fixture adds vignette, rotation, crop, sprocket fill; no AI dust. Managed allocations cover all threads, not native/retained memory. Miss is not a cold decode/CMM measurement.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(destination);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static object Summary(List<double> times)
    {
        var sorted = times.Order().ToArray();
        return new { P50Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.5) - 1],
            P95Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], RawMs = times };
    }
}
