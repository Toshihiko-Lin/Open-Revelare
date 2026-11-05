using System.Diagnostics;
using System.Text.Json;
using OpenRevelare.Core;

internal static class RawImportBenchmark
{
    public static void Run(string? source)
    {
        string path = source ?? Path.Combine(Path.GetTempPath(), $"raw-import-{Guid.NewGuid():N}.dng");
        try
        {
            if (source is null)
            {
                var pixels = new ImageBuffer(4000, 3000);
                var random = new Random(42);
                for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = random.NextSingle();
                LinearDng.Write(pixels, ColorSpaces.Srgb, path);
            }
            (double X, double Y, double W, double H)?[] rects =
                { (0, 0, 0.5, 0.5), (0.5, 0, 0.5, 0.5), (0, 0.5, 0.5, 0.5), (0.5, 0.5, 0.5, 0.5) };
            (ImageBuffer Preview, int SourceWidth, int SourceHeight)[] Independent() => rects.Select(rect =>
                RawDecode.DecodeRawRegionDownsampled(path, RawDecode.RawBackend.LibRaw, RawDecode.FbddMode.Off, rect!.Value, 1600)).ToArray();
            (ImageBuffer Preview, int SourceWidth, int SourceHeight)[] Batch() =>
                RawDecode.DecodeRawPreviewBatch(path, RawDecode.RawBackend.LibRaw, RawDecode.FbddMode.Off, rects, 1600);
            (ImageBuffer Preview, int SourceWidth, int SourceHeight)[] ParallelIndependent()
            {
                var results = new (ImageBuffer, int, int)[rects.Length];
                Parallel.For(0, rects.Length, i => results[i] = RawDecode.DecodeRawRegionDownsampled(
                    path, RawDecode.RawBackend.LibRaw, RawDecode.FbddMode.Off, rects[i]!.Value, 1600));
                return results;
            }
            var expected = Independent();
            var actual = Batch();
            for (int i = 0; i < rects.Length; i++)
                if (expected[i].SourceWidth != actual[i].SourceWidth || expected[i].SourceHeight != actual[i].SourceHeight
                    || !expected[i].Preview.Data.AsSpan().SequenceEqual(actual[i].Preview.Data))
                    throw new InvalidOperationException("RAW import batch parity failed.");
            var independent = new List<double>();
            var batch = new List<double>();
            var parallel = new List<double>();
            for (int run = -2; run < 7; run++)
            {
                void Measure(bool batched)
                {
                    long start = Stopwatch.GetTimestamp();
                    var result = batched ? Batch() : Independent();
                    if (run >= 0) (batched ? batch : independent).Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    GC.KeepAlive(result);
                }
                Measure(run % 2 == 0);
                Measure(run % 2 != 0);
                long parallelStart = Stopwatch.GetTimestamp();
                var parallelResult = ParallelIndependent();
                if (run >= 0) parallel.Add(Stopwatch.GetElapsedTime(parallelStart).TotalMilliseconds);
                GC.KeepAlive(parallelResult);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Source = source is null ? "Generated 4000x3000 linear DNG" : Path.GetFileName(source),
                Crops = rects.Length, BitExact = true, Runs = 7,
                IndependentMedianMs = independent.Order().ElementAt(3), BatchMedianMs = batch.Order().ElementAt(3),
                ParallelIndependentMedianMs = parallel.Order().ElementAt(3),
                Scope = "Four crop previews from one RAW; warm source cache, LibRaw, FBDD off; excludes UI and thumbnail rendering.",
            }));
        }
        finally { if (source is null) File.Delete(path); }
    }
}
