using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

// Isolates export from rendering and disk-cold import. Five generated-image samples,
// after one warmup; includes allocation/encoding/write, excludes fixture creation/hash reads.
GpuAcceleration.Mode = GpuAccelerationMode.Off;
string root = Path.Combine(Path.GetTempPath(), "revelare-export-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var sdr = Frame(2048, 1365, false);
    var hdr = Frame(2048, 1365, true);
    var dng = Frame(4096, 2048, false);
    string jpg = Path.Combine(root, "reference.jpg");
    JpegIO.ExportJpeg(sdr, jpg, 95);
    long jpegCeiling = new FileInfo(jpg).Length * 3 / 4;
    JpegIO.ExportGainMapJpeg(sdr, hdr, ColorSpaces.Srgb, OutputTarget.Hdr(1000), jpg, 95);
    long hdrCeiling = new FileInfo(jpg).Length * 3 / 4;
    Run("jpeg", "jpg", p => JpegIO.ExportJpeg(sdr, p, 95));
    Run("jpeg-limit", "jpg", p => JpegIO.ExportJpeg(sdr, p, 95, jpegCeiling));
    Run("hdr-jpeg-limit", "jpg", p => JpegIO.ExportGainMapJpeg(sdr, hdr, ColorSpaces.Srgb, OutputTarget.Hdr(1000), p, 95, hdrCeiling));
    Run("dng", "dng", p => LinearDng.Write(dng.Pixels, ColorSpaces.Srgb, p));
    foreach (var compression in Enum.GetValues<TiffIO.CompressionMode>())
    {
        Run("tiff16-" + compression, "tif", p => TiffIO.ExportTiff(sdr, p, compression));
        Run("tiff32-" + compression, "tif", p => TiffIO.ExportTiff(hdr, p, compression));
    }

    void Run(string name, string extension, Action<string> export)
    {
        if (args.Length > 0 && !args.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return;
        string path = Path.Combine(root, name + "." + extension);
        var times = new List<double>(); var allocations = new List<long>();
        for (int run = -1; run < 5; run++)
        {
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            long start = Stopwatch.GetTimestamp();
            export(path);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            if (run >= 0) { times.Add(ms); allocations.Add(bytes); }
        }
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        string? pixels = null;
        if (extension == "tif")
        {
            using var tiff = Tiff.Open(path, "r")!;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var row = new byte[tiff.ScanlineSize()];
            int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            for (int y = 0; y < height; y++)
            {
                if (!tiff.ReadScanline(row, y)) throw new IOException("TIFF roundtrip failed");
                digest.AppendData(row);
            }
            pixels = Convert.ToHexString(digest.GetHashAndReset());
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Name = name, Runs = 5,
            MedianMs = times.Order().ElementAt(2), AllocatedBytes = allocations.Order().ElementAt(2),
            Bytes = new FileInfo(path).Length, Sha256 = hash, PixelSha256 = pixels }));
    }
}
finally
{
    foreach (string path in Directory.EnumerateFiles(root)) File.Delete(path);
    Directory.Delete(root);
}

static RenderedFrame Frame(int width, int height, bool hdr)
{
    var image = new ImageBuffer(width, height);
    var random = new Random(42);
    for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                float encoded = Math.Clamp(0.5f + 0.4f * MathF.Sin(x * 0.015f + c)
                    * MathF.Cos(y * 0.008f) + (random.NextSingle() - 0.5f) * 0.07f, 0f, 1f);
                image.Data[(y * width + x) * 3 + c] = hdr ? Srgb.SrgbToLinear(encoded) * 4f : encoded;
            }
    var profile = BuiltInColorProfiles.For(hdr ? ColorSpaces.LinearExtendedSrgb : ColorSpaces.Srgb, ProfileRole.Output);
    var encoding = new CharacterizedPixelEncoding(profile,
        hdr ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
        hdr ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded,
        hdr ? NumericRange.Extended : NumericRange.Normalized);
    var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile,
        RenderingIntent.RelativeColorimetric, false, "export benchmark", "", false);
    return new RenderedFrame(image, encoding, recipe, RenderFingerprint.ComputeManaged(image, encoding, recipe));
}
