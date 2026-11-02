using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

internal static class TiffRegionBenchmark
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"revelare-region-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var cmm = new LittleCmsEngine();
            const int width = 1200, height = 4800;
            var requests = Enumerable.Range(0, 4)
                .Select(i => new TiffIO.RegionRequest((0, i / 4.0, 1, 0.25), 800)).ToArray();
            foreach (Compression compression in new[] { Compression.NONE, Compression.LZW, Compression.DEFLATE })
            {
                string path = Path.Combine(root, $"{compression}.fff");
                using (Tiff tif = Tiff.Open(path, "w"))
                {
                    tif.SetField(TiffTag.IMAGEWIDTH, width);
                    tif.SetField(TiffTag.IMAGELENGTH, height);
                    tif.SetField(TiffTag.BITSPERSAMPLE, 16);
                    tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
                    tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
                    tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                    tif.SetField(TiffTag.COMPRESSION, compression);
                    tif.SetField(TiffTag.ROWSPERSTRIP, height);
                    byte[] row = new byte[width * 3 * 2];
                    var random = new Random(42);
                    for (int y = 0; y < height; y++)
                    {
                        random.NextBytes(row);
                        if (!tif.WriteScanline(row, y)) throw new IOException("Fixture write failed.");
                    }
                }
                WorkingFrame[] Separate() => requests.Select(r => TiffIO.LoadWorkingRegion(path, r.Rect, r.MaxEdges[0],
                    TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, cmm)).ToArray();
                WorkingFrame[] Batch() => TiffIO.LoadWorkingRegionBatch(path, requests,
                    TiffInputAssumption.Linear, ColorPipelineVersion.ManagedV2, cmm);
                var expected = Separate();
                var actual = Batch();
                for (int i = 0; i < expected.Length; i++)
                    if (!MemoryMarshal.AsBytes(expected[i].Pixels.Data.AsSpan())
                        .SequenceEqual(MemoryMarshal.AsBytes(actual[i].Pixels.Data.AsSpan())))
                        throw new InvalidOperationException("Region parity failed.");
                var separate = new List<double>();
                var batch = new List<double>();
                for (int run = 0; run < 5; run++)
                {
                    void Measure(Func<WorkingFrame[]> action, List<double> timings)
                    {
                        long start = Stopwatch.GetTimestamp();
                        var frames = action();
                        timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                        GC.KeepAlive(frames);
                    }
                    if (run % 2 == 0) { Measure(Separate, separate); Measure(Batch, batch); }
                    else { Measure(Batch, batch); Measure(Separate, separate); }
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Width = width, Height = height, Regions = requests.Length,
                    Compression = compression.ToString(), RowsPerStrip = height, WarmOsCache = true,
                    SeparateMs = separate.Order().ElementAt(2), BatchMs = batch.Order().ElementAt(2), BitExact = true,
                }));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
