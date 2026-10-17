using System.Buffers.Binary;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ExportOptimizationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Cached_quality_probes_match_fresh_encodes_including_after_resize(bool hdr, bool resize)
    {
        WithFile("jpg", path =>
        {
            var sdr = Frame(false, 513, 513); // also crosses the parallel quantization threshold
            var master = Frame(true, 513, 513);
            var metadata = SourceExif.ForExport(null, false, new FilmCaptureMetadata {
                Camera = new CaptureDevice { Make = "Film", Model = "Capture" }, Artist = "摄影师" });
            JpegIO.JpegFit? Export(RenderedFrame baseFrame, RenderedFrame hdrFrame, int quality, long? ceiling = null)
            {
                if (hdr)
                {
                    if (ceiling is long limit) return JpegIO.ExportGainMapJpeg(baseFrame, hdrFrame, ColorSpaces.DisplayP3,
                        OutputTarget.Hdr(1000), path, quality, limit, description: "导出测试", sourceExif: metadata);
                    JpegIO.ExportGainMapJpeg(baseFrame, hdrFrame, ColorSpaces.DisplayP3,
                        OutputTarget.Hdr(1000), path, quality, description: "导出测试", sourceExif: metadata);
                }
                else
                {
                    if (ceiling is long limit) return JpegIO.ExportJpeg(baseFrame, path, quality, limit,
                        description: "导出测试", sourceExif: metadata);
                    JpegIO.ExportJpeg(baseFrame, path, quality, description: "导出测试", sourceExif: metadata);
                }
                return null;
            }

            float[] original = (float[])sdr.Pixels.Data.Clone();
            Export(sdr, master, resize ? JpegIO.MinFitQuality : 95);
            long ceiling = new FileInfo(path).Length * 3 / 4;
            var fit = Assert.IsType<JpegIO.JpegFit>(Export(sdr, master, 95, ceiling));
            Assert.True(fit.Bytes <= ceiling);
            Assert.Equal(resize, fit.Shrunk);
            Assert.True(fit.Shrunk || fit.QualityReduced);
            byte[] fitted = File.ReadAllBytes(path);
            var baseAtSize = sdr.WithPixels(Resample.ToLongEdge(sdr.Pixels, fit.LongEdge, false));
            var hdrAtSize = master.WithPixels(Resample.ToLongEdge(master.Pixels, fit.LongEdge, false));
            Export(baseAtSize, hdrAtSize, fit.Quality);
            Assert.Equal(fitted, File.ReadAllBytes(path));
            Assert.Equal(original, sdr.Pixels.Data);
        });
    }

    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    [InlineData("Rec709")]
    [InlineData("ACEScg")]
    public void Fused_dng_payload_matches_decode_then_quantize_for_every_transfer(string name)
    {
        WithFile("dng", path =>
        {
            var space = ColorSpaces.ByName(name, ColorSpaces.Srgb);
            var image = Frame(false, 263, 131).Pixels;
            float[] special = [-0.2f, 0f, 0.5f, 1f, 1.2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                0.5f / 65535f, 1.5f / 65535f];
            special.CopyTo(image.Data, 0);
            float[] original = (float[])image.Data.Clone();
            float[] linear = (float[])image.Data.Clone();
            OutputRender.Decode(linear, space);
            LinearDng.Write(image, space, path);
            byte[] file = File.ReadAllBytes(path);
            int root = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
            int sub = ReadLong(file, root, 330);
            int data = ReadLong(file, sub, 273);
            for (int i = 0; i < linear.Length; i++)
            {
                float v = linear[i];
                ushort expected = v <= 0f ? (ushort)0 : v >= 1f ? (ushort)65535 : (ushort)MathF.Round(v * 65535f);
                Assert.Equal(expected, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(data + i * 2)));
            }
            Assert.Equal(original, image.Data);
        });
    }

    [Theory]
    [InlineData(TiffIO.CompressionMode.None, false, 1021, 193)]
    [InlineData(TiffIO.CompressionMode.None, true, 1021, 193)]
    [InlineData(TiffIO.CompressionMode.Lzw, false, 1021, 193)]
    [InlineData(TiffIO.CompressionMode.Lzw, true, 1021, 193)]
    [InlineData(TiffIO.CompressionMode.Deflate, false, 1021, 193)]
    [InlineData(TiffIO.CompressionMode.Deflate, true, 1021, 193)]
    [InlineData(TiffIO.CompressionMode.Deflate, false, 1, 17)]
    [InlineData(TiffIO.CompressionMode.Deflate, true, 1, 17)]
    public void Tiff_strips_preserve_every_sample_across_rows_partial_strips_and_batches(
        TiffIO.CompressionMode compression, bool floating, int width, int height)
    {
        WithFile("tif", path =>
        {
            var frame = Frame(floating, width, height);
            float[] special = [-0.1f, 0f, 0.5f, 1f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
            special.CopyTo(frame.Pixels.Data, 0);
            float[] original = (float[])frame.Pixels.Data.Clone();
            TiffIO.ExportTiff(frame, path, compression);
            using var tiff = Tiff.Open(path, "r")!;
            Assert.Equal(width, tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt());
            Assert.Equal(height, tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt());
            byte[] row = new byte[tiff.ScanlineSize()];
            for (int y = 0; y < height; y++)
            {
                Assert.True(tiff.ReadScanline(row, y));
                for (int i = 0; i < width * 3; i++)
                {
                    float v = original[y * width * 3 + i];
                    if (floating) Assert.Equal(BitConverter.SingleToUInt32Bits(v), BitConverter.ToUInt32(row, i * 4));
                    else
                    {
                        float bounded = v < 0f ? 0f : v > 1f ? 1f : v;
                        Assert.Equal((ushort)(bounded * 65535f + 0.5f), BitConverter.ToUInt16(row, i * 2));
                    }
                }
            }
            Assert.Equal(original, frame.Pixels.Data);
        });
    }

    [Fact]
    public void Fitting_at_the_quality_floor_does_not_encode_a_failed_size_twice()
    {
        var probes = new List<(int, int)>();
        JpegIO.FitToSize(256, 256, 40, 100, (edge, quality) =>
        {
            probes.Add((edge, quality));
            return new byte[edge <= 128 ? 50 : 150];
        });
        Assert.Equal(new[] { (256, 40), (128, 40) }, probes);
    }

    private static int ReadLong(byte[] file, int ifd, ushort tag)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd));
        for (int i = 0; i < count; i++)
        {
            int entry = ifd + 2 + i * 12;
            if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(entry)) == tag)
                return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(entry + 8)));
        }
        throw new InvalidOperationException("Missing DNG tag " + tag);
    }

    private static RenderedFrame Frame(bool hdr, int width, int height)
    {
        var pixels = new ImageBuffer(width, height);
        var random = new Random(42);
        for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = random.NextSingle() * (hdr ? 4f : 1f);
        var profile = BuiltInColorProfiles.For(hdr ? ColorSpaces.LinearExtendedSrgb : ColorSpaces.DisplayP3, ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(profile,
            hdr ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
            hdr ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded,
            hdr ? NumericRange.Extended : NumericRange.Normalized);
        var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile,
            RenderingIntent.RelativeColorimetric, false, "export optimization fixture", "", false);
        return new RenderedFrame(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    private static void WithFile(string extension, Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"revelare-export-{Guid.NewGuid():N}.{extension}");
        try { action(path); }
        finally { File.Delete(path); }
    }
}
