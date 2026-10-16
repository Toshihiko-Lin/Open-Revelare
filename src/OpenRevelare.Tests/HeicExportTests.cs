using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class HeicExportTests
{
    [Theory]
    [InlineData("sRGB")]
    [InlineData("AdobeRGB")]
    [InlineData("DisplayP3")]
    [InlineData("Rec709")]
    public void Sdr_file_is_main10_hevc_and_preserves_exact_output_profile(string spaceName)
    {
        RenderedFrame frame = Frame(ColorSpaces.ByName(spaceName, ColorSpaces.Srgb), hdr: false);
        WithFile(path =>
        {
            HeicIO.ExportHeic(frame, path);
            byte[] file = File.ReadAllBytes(path);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(file, 4, 4));
            Assert.Equal("heix", Encoding.ASCII.GetString(file, 8, 4)); // HEVC Main10 brand.
            var properties = Properties(file);
            byte[] aperture = Assert.Single(properties, p => p.Type == "clap").Data;
            Assert.Equal(33u, BinaryPrimitives.ReadUInt32BigEndian(aperture));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(aperture.AsSpan(4)));
            Assert.Equal(17u, BinaryPrimitives.ReadUInt32BigEndian(aperture.AsSpan(8)));
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(aperture.AsSpan(12)));
            byte[] config = Assert.Single(properties, p => p.Type == "hvcC").Data;
            Assert.Equal(2, config[17] & 7); // Main10 really carries 10 bits, not an 8-bit fallback.
            byte[] icc = Assert.Single(properties, p => p.Type == "colr"
                && Encoding.ASCII.GetString(p.Data, 0, 4) == "prof").Data;
            Assert.Equal(frame.OutputProfile.IccBytes.ToArray(), icc[4..]);
            byte[] nclx = Assert.Single(properties, p => p.Type == "colr"
                && Encoding.ASCII.GetString(p.Data, 0, 4) == "nclx").Data;
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(nclx.AsSpan(4)));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(nclx.AsSpan(6)));
        });
    }

    [Fact]
    public void Hdr_file_declares_bt2020_pq_without_a_contradictory_linear_icc()
    {
        RenderedFrame frame = Frame(ColorSpaces.LinearExtendedSrgb, hdr: true);
        WithFile(path =>
        {
            HeicIO.ExportHeic(frame, path);
            var properties = Properties(File.ReadAllBytes(path));
            byte[] color = Assert.Single(properties, p => p.Type == "colr").Data;
            Assert.Equal("nclx", Encoding.ASCII.GetString(color, 0, 4));
            Assert.Equal(9, BinaryPrimitives.ReadUInt16BigEndian(color.AsSpan(4)));
            Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(color.AsSpan(6)));
            Assert.Equal(9, BinaryPrimitives.ReadUInt16BigEndian(color.AsSpan(8)));
            Assert.Equal(0x80, color[10]);
            byte[] config = Assert.Single(properties, p => p.Type == "hvcC").Data;
            Assert.Equal(2, config[17] & 7);
        });
    }

    [Fact]
    public void Pq_conversion_retains_highlights_and_uses_203_nit_reference_white()
    {
        RenderedFrame frame = Frame(ColorSpaces.LinearExtendedSrgb, hdr: true);
        frame.Pixels.Data[0] = frame.Pixels.Data[1] = frame.Pixels.Data[2] = 1f;
        frame.Pixels.Data[3] = frame.Pixels.Data[4] = frame.Pixels.Data[5] = 4f;
        float[] before = (float[])frame.Pixels.Data.Clone();
        ushort[] codes = HeicIO.PreparePixels(frame);
        Assert.InRange(Pq.ToNits(codes[0] / 1023f), 201f, 205f);
        Assert.InRange(Pq.ToNits(codes[3] / 1023f), 807f, 817f);
        Assert.True(codes[3] > codes[0]);
        Assert.Equal(before, frame.Pixels.Data);
    }

    [Fact]
    public void Scene_linear_acescg_is_rejected_without_replacing_an_existing_export()
    {
        RenderedFrame frame = Frame(ColorSpaces.AcesCg, hdr: true);
        WithFile(path =>
        {
            File.WriteAllText(path, "previous export");
            Assert.Throws<NotSupportedException>(() => HeicIO.ExportHeic(frame, path));
            Assert.Equal("previous export", File.ReadAllText(path));
        });
    }

    [Fact]
    public void Heic_preset_roundtrips_and_reports_hdr_without_claiming_tiff_or_gain_map()
    {
        var options = new ExportOptions { Format = ExportFormat.Heic, HeicQuality = 82,
            HdrLimitStops = 2, LimitFileSize = true, MaxFileSizeMb = 2 };
        Assert.Equal("heic", options.Extension);
        Assert.False(options.WritesGainMap);
        Assert.Equal(2 * 1024 * 1024, options.MaxFileBytes);
        Assert.Contains("HEIC", options.Summary());
        Assert.Contains("PQ", options.Summary());
        Assert.DoesNotContain("TIFF", options.Summary());
        var restored = JsonSerializer.Deserialize<ExportOptions>(JsonSerializer.Serialize(options))!;
        Assert.Equal(ExportFormat.Heic, restored.Format);
        Assert.Equal(82, restored.HeicQuality);
        Assert.Equal(95, restored.JpegQuality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Size_ceiling_reduces_quality_and_counts_the_complete_heic_file(bool hdr)
    {
        WithFile(path =>
        {
            var frame = Frame(hdr ? ColorSpaces.LinearExtendedSrgb : ColorSpaces.Srgb, hdr, 257, 129);
            var random = new Random(42);
            for (int i = 0; i < frame.Pixels.Data.Length; i++)
                frame.Pixels.Data[i] = random.NextSingle() * (hdr ? 4f : 1f);
            HeicIO.ExportHeic(frame, path, 90);
            long ceiling = new FileInfo(path).Length * 3 / 4;
            var fit = HeicIO.ExportHeic(frame, path, 90, maxBytes: ceiling);
            Assert.NotNull(fit);
            Assert.Equal(new FileInfo(path).Length, fit.Bytes);
            Assert.True(fit.Bytes <= ceiling);
            Assert.True(fit.QualityReduced || fit.Shrunk);
            byte[] color = Assert.Single(Properties(File.ReadAllBytes(path)), p => p.Type == "colr"
                && Encoding.ASCII.GetString(p.Data, 0, 4) == "nclx").Data;
            Assert.Equal(hdr ? 16 : 2, BinaryPrimitives.ReadUInt16BigEndian(color.AsSpan(6)));
        });
    }

    private static RenderedFrame Frame(ColorSpaceDef space, bool hdr, int width = 33, int height = 17)
    {
        // Odd sizes catch HEVC padding accidentally becoming the delivered image dimensions.
        var pixels = new ImageBuffer(width, height);
        for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = (i % 97) / 97f * (hdr ? 4f : 1f);
        var profile = BuiltInColorProfiles.For(space, ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(profile,
            hdr ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
            hdr ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded,
            hdr ? NumericRange.Extended : NumericRange.Normalized);
        var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile,
            RenderingIntent.RelativeColorimetric, false, "HEIC fixture", "", false);
        return new RenderedFrame(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    private static List<(string Type, byte[] Data)> Properties(byte[] file)
    {
        var result = new List<(string, byte[])>();
        void Walk(int start, int end)
        {
            for (int offset = start; offset < end;)
            {
                int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset)));
                Assert.True(size >= 8 && size <= end - offset);
                string type = Encoding.ASCII.GetString(file, offset + 4, 4);
                if (type == "meta") Walk(offset + 12, offset + size);
                else if (type is "iprp" or "ipco") Walk(offset + 8, offset + size);
                else result.Add((type, file[(offset + 8)..(offset + size)]));
                offset += size;
            }
        }
        Walk(0, file.Length);
        return result;
    }

    private static void WithFile(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"正片-heic-{Guid.NewGuid():N}.heic");
        try { action(path); }
        finally { File.Delete(path); }
    }
}
