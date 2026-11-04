using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenRevelare.Core;

internal static class RawRollBenchmark
{
    public static void Run(string folder, int workers, int limit, bool full, bool shared = false)
    {
        string[] paths = (File.Exists(folder) ? new[] { folder } : Directory.GetFiles(folder))
            .Where(RawDecode.IsRawExtension).Order(StringComparer.Ordinal).ToArray();
        if (limit > 0) paths = paths.Take(limit).ToArray();
        var phases = new ConcurrentDictionary<string, ConcurrentBag<double>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RawDecode.Diagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => phases.GetOrAdd(a.OperationName, _ => new()).Add(a.Duration.TotalMilliseconds),
        };
        ActivitySource.AddActivityListener(listener);
        RawDecode.ClearSourceCache();
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long start = Stopwatch.GetTimestamp();
        int done = 0;
        Parallel.ForEach(paths, new ParallelOptions { MaxDegreeOfParallelism = workers }, path =>
        {
            long fileStart = Stopwatch.GetTimestamp();
            ImageBuffer? sharedPreview = null;
            var preview = shared ? Shared() : full ? Full()
                : RawDecode.DecodeRawDownsampled(path, RawDecode.FbddMode.Off, new[] { 1600 }, RawDecode.Demosaic.Preview);
            (ImageBuffer[] Previews, int SourceWidth, int SourceHeight) Shared()
            {
                var result = RawDecode.DecodeRawWithPreview(path, RawDecode.RawBackend.LibRaw, RawDecode.FbddMode.Off, 1600);
                sharedPreview = result.Preview;
                return (new[] { result.Full }, result.SourceWidth, result.SourceHeight);
            }
            (ImageBuffer[] Previews, int SourceWidth, int SourceHeight) Full()
            {
                var pixels = RawDecode.DecodeRaw(path);
                return (new[] { pixels }, pixels.Width, pixels.Height);
            }
            string hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(preview.Previews[0].Data.AsSpan())));
            Console.WriteLine(JsonSerializer.Serialize(new { File = Path.GetFileName(path), Completed = Interlocked.Increment(ref done),
                Ms = Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds, preview.SourceWidth, preview.SourceHeight,
                PreviewWidth = preview.Previews[0].Width, PreviewHeight = preview.Previews[0].Height, PixelSha256 = hash,
                SharedPreviewSha256 = sharedPreview is null ? null
                    : Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(sharedPreview.Data.AsSpan()))) }));
        });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = "roll-summary", Files = paths.Length, Workers = workers, ProcessorCount = Environment.ProcessorCount,
            NativeBuild = RawDecode.NativeBuildInfo, WallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            CpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds, PeakWorkingSet = process.PeakWorkingSet64,
            Phases = phases.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => new { Count = p.Value.Count, SumMs = p.Value.Sum(),
                MedianMs = p.Value.Order().ElementAt(p.Value.Count / 2), MaxMs = p.Value.Max() }),
            Scope = $"Real RAW {(shared ? "shared-unpack full AHD and preview" : full ? "full AHD" : "1600-edge preview")} decode and pixel hashing, FBDD off; no UI or inversion; source cache cleared, OS cache unspecified",
        }));
    }
}
