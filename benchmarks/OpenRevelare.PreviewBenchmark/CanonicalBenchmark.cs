using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

internal static class CanonicalBenchmark
{
    internal static void Run(string output, string? input)
    {
        string root = Path.Combine(Path.GetTempPath(), "revelare-canonical-benchmark-" + Guid.NewGuid().ToString("N"));
        // Never load or save the real catalog/preferences; the optional original is read only.
        Environment.SetEnvironmentVariable("OPENREVELARE_CONFIG_DIR", Path.Combine(root, "config"));
        Environment.SetEnvironmentVariable("OPENREVELARE_DATA_DIR", Path.Combine(root, "data"));
        Settings.Current.DecodeBackend = RawDecode.RawBackend.LibRaw;
        Settings.Current.FbddMode = RawDecode.FbddMode.Off;
        GpuAcceleration.Mode = GpuAccelerationMode.Off;
        using var cmm = new LittleCmsEngine();
        var serial = new SerialEngine(cmm);
        const int iterations = 30;
        const ColorPipelineVersion version = ColorPipelineVersion.ManagedV2;
        var parameters = new FrameParams { ExposureEv = 0.35, Contrast = 0.12, Saturation = 0.08 };
        WorkingFrame source;
        string? sourceHash = null;
        long? sourceBytes = null;
        double? decodeMs = null;
        int sourceWidth, sourceHeight;
        if (input is not null)
        {
            sourceHash = HashFile(input);
            sourceBytes = new FileInfo(input).Length;
            RawDecode.ClearSourceCache();
            long start = Stopwatch.GetTimestamp();
            (source, sourceWidth, sourceHeight) = ImageIo.LoadWorkingPreview(input, 1600, version, cmm, TiffInputAssumption.Linear);
            decodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        else
        {
            var pixels = new ImageBuffer(1200, 800);
            var random = new Random(173);
            for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = 0.02f + random.NextSingle() * 0.8f;
            string fixture = Path.Combine(root, "fixture.tif");
            Directory.CreateDirectory(root);
            TiffIO.ExportTiff16(pixels, fixture, TiffIO.CompressionMode.None);
            (source, sourceWidth, sourceHeight) = ImageIo.LoadWorkingPreview(fixture, 1600, version, cmm, TiffInputAssumption.Linear);
            File.Delete(fixture);
        }

        long pipelineStart = Stopwatch.GetTimestamp();
        var rendered = Pipeline.Render(source, parameters, version, cmm);
        double pipelineMs = Stopwatch.GetElapsedTime(pipelineStart).TotalMilliseconds;
        // Warm both routes. The wrapper selects the converter's serial fallback without
        // changing requests, native engine, profiles, precision or pixel math.
        var reference = CanonicalPreviewConverter.Convert(rendered, serial, 1);
        _ = CanonicalPreviewConverter.Convert(rendered, cmm, 1);
        var serialMs = new List<double>();
        var parallelMs = new List<double>();
        var serialBytes = new List<long>();
        var parallelBytes = new List<long>();
        void Measure(bool parallel)
        {
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            var scene = CanonicalPreviewConverter.Convert(rendered, parallel ? cmm : serial, 1);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            (parallel ? parallelMs : serialMs).Add(elapsed);
            (parallel ? parallelBytes : serialBytes).Add(bytes);
            if (!MemoryMarshal.AsBytes(reference.LinearExtendedSrgbRgba.AsSpan()).SequenceEqual(
                MemoryMarshal.AsBytes(scene.LinearExtendedSrgbRgba.AsSpan())))
                throw new InvalidOperationException("Canonical conversion changed RGBA-half bits.");
        }
        for (int i = 0; i < iterations; i++)
        {
            Measure(i % 2 == 0);
            Measure(i % 2 != 0);
        }
        if (input is not null && HashFile(input) != sourceHash)
            throw new IOException("Source changed during the benchmark; discard this run.");
        using var process = Process.GetCurrentProcess();
        var result = new
        {
            Schema = "openrevelare-canonical-paired-v1", TimestampUtc = DateTime.UtcNow,
            Platform = RuntimeInformation.OSDescription, Runtime = Environment.Version.ToString(),
            ProcessorCount = Environment.ProcessorCount, ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            PresentationAssemblySha256 = HashFile(typeof(CanonicalPreviewConverter).Assembly.Location),
            CmmSha256 = cmm.Build.NativeSha256, SourceKind = input is null ? "generated-tiff" : Path.GetExtension(input).ToLowerInvariant(),
            SourceSha256 = sourceHash, SourceBytes = sourceBytes, SourceWidth = sourceWidth, SourceHeight = sourceHeight,
            PreviewWidth = source.Pixels.Width, PreviewHeight = source.Pixels.Height, DecodeMs = decodeMs, PipelineMs = pipelineMs,
            Iterations = iterations, PixelBitsEqual = true, InputUnchanged = input is not null,
            OutputProfile = rendered.OutputProfile.Identity, Pipeline = version.ToString(),
            Serial = Summary(serialMs, serialBytes), Parallel = Summary(parallelMs, parallelBytes),
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            Scope = "Paired exact canonical conversion, alternating route order; warmed JIT/CMM; includes allocation and scene validation, excludes decode/pipeline from paired times, UI publication/compositor and NegPy. Original hashed before decode (not cold disk); optional TIFF assumes linear if untagged. Allocations include all managed threads, not native allocation or retained cache size.",
        };
        string destination = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(destination);

        static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        static object Summary(List<double> times, List<long> bytes)
        {
            double[] sorted = times.Order().ToArray();
            return new { P50Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.5) - 1],
                P95Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
                MedianManagedAllocatedBytes = bytes.Order().ElementAt((bytes.Count - 1) / 2), RawMs = times, RawManagedAllocatedBytes = bytes };
        }
    }

    // Deliberately not LittleCmsEngine: preserves the serial path for comparison. Does not own
    // the underlying engine. All requests still go through the same exact native implementation.
    private sealed class SerialEngine(IColorManagementEngine inner) : IColorManagementEngine
    {
        public CmmBuildIdentity Build => inner.Build;
        public ProfileValidationResult Validate(ColorProfileRef profile) => inner.Validate(profile);
        public IColorTransformLease Lease(ColorTransformRequest request) => inner.Lease(request);
        public CmmDiagnosticsSnapshot GetDiagnostics() => inner.GetDiagnostics();
        public void Dispose() { }
    }
}
