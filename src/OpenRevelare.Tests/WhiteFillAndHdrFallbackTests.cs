using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class WhiteFillAndHdrFallbackTests
{
    [Fact]
    public void HDR_only_LUT_colour_survives_the_SDR_companion()
    {
        string path = Path.Combine(TestDataIsolation.Root, "pq-look-" + Guid.NewGuid().ToString("N") + ".cube");
        string colour = FormattableString.Invariant($"{Pq.FromNits(100):R} {Pq.FromNits(65):R} {Pq.FromNits(45):R}");
        File.WriteAllText(path, "# Input: Cineon\n# Display: Rec.2100 PQ\nLUT_3D_SIZE 2\n"
            + string.Join("\n", Enumerable.Repeat(colour, 8)));
        try
        {
            var pixels = new ImageBuffer(8, 8);
            Array.Fill(pixels.Data, 0.02f);
            var source = Working(pixels);
            var p = new FrameParams { PrintLut = path, HdrPeakNits = 800 };
            using var cmm = new LittleCmsEngine();
            Assert.Equal("", p.SdrRendition().PrintLut); // the old independently rendered base drops the look
            var master = Pipeline.Render(source, p, ColorPipelineVersion.ManagedV2, cmm);
            var fallback = Pipeline.RenderSdrRendition(source, p, ColorPipelineVersion.ManagedV2, cmm);
            float[] hdrColour = HdrGainMap.HdrInBaseGamut(master, ColorSpaces.Srgb);
            float[] sdrColour = (float[])fallback.Pixels.Data.Clone();
            OutputRender.Decode(sdrColour, ColorSpaces.Srgb);
            Assert.InRange(sdrColour[0] / sdrColour[2], hdrColour[0] / hdrColour[2] - 1e-3f,
                hdrColour[0] / hdrColour[2] + 1e-3f);
            Assert.True(sdrColour[0] > sdrColour[2]);
            Assert.Equal(master.Recipe.PrintLutIdentity, fallback.Recipe.PrintLutIdentity);
        }
        finally { PrintLuts.Forget(path); File.Delete(path); }
    }

    [Fact]
    public void White_fill_stays_neutral_through_orientation_straighten_and_crop()
    {
        var pixels = new ImageBuffer(12, 8);
        Array.Fill(pixels.Data, 1);
        var source = Working(pixels);
        var p = new FrameParams { SprocketEnabled = true, SprocketThreshold = 0.9, QuarterTurns = 1,
            FlipH = true, Rotation = 4, CropRect = (0.1, 0.1, 0.8, 0.8),
            WbGains = new[] { 1.6, 1.0, 0.6 }, ExposureEv = -2 };
        using var cmm = new LittleCmsEngine();
        var full = Pipeline.Render(source, p, ColorPipelineVersion.ManagedV2, cmm);
        var region = RegionRender.Render(source, p, new RegionRender.Roi(0, 0, 1, 1), ColorPipelineVersion.ManagedV2, cmm);
        Assert.All(full.Pixels.Data, value => Assert.Equal(1, value));
        Assert.All(region.Frame.Pixels.Data, value => Assert.Equal(1, value));
    }

    [Theory]
    [InlineData(ColorPipelineVersion.LegacyV1, false)]
    [InlineData(ColorPipelineVersion.ManagedV2, false)]
    [InlineData(ColorPipelineVersion.ManagedV2, true)]
    public void Colour_adjustments_and_LUT_do_not_tint_white_fill_in_whole_or_regional_render(ColorPipelineVersion version, bool hdr)
    {
        var pixels = new ImageBuffer(8, 4);
        for (int i = 0; i < pixels.Data.Length; i += 3)
        {
            bool hole = i / 3 % 8 < 2;
            pixels.Data[i] = hole ? 1 : 0.02f;
            pixels.Data[i + 1] = hole ? 1 : 0.015f;
            pixels.Data[i + 2] = hole ? 1 : 0.01f;
        }
        var source = Working(pixels);
        var parameters = new FrameParams
        {
            SprocketEnabled = true, SprocketThreshold = 0.9,
            WbGains = new[] { 1.6, 1.0, 0.6 }, ExposureEv = -1.5,
            CurvePointsR = new() { (0, 0), (1, 0.55) },
            Contrast = 0.2, Saturation = 0.5,
            PrintLut = ":kodak-2383", OutputSpace = "Display P3", HdrPeakNits = hdr ? 800 : 0,
        };
        using var cmm = new LittleCmsEngine();
        var rendered = Pipeline.Render(source, parameters, version, cmm);
        var region = RegionRender.Render(source, parameters, new RegionRender.Roi(0, 0, 1, 1), version, cmm);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 2; x++)
                for (int c = 0; c < 3; c++)
                {
                    Assert.Equal(1, rendered.Pixels.Data[(y * 8 + x) * 3 + c]);
                    Assert.Equal(1, region.Frame.Pixels.Data[(y * 8 + x) * 3 + c]);
                }
        var neutral = parameters.Clone();
        neutral.WbGains = new[] { 1d, 1d, 1d }; neutral.ExposureEv = 0; neutral.CurvePointsR.Clear();
        var other = Pipeline.Render(source, neutral, version, cmm);
        Assert.Contains(Enumerable.Range(6, 18), i => Math.Abs(other.Pixels.Data[i] - rendered.Pixels.Data[i]) > 1e-4f);
        if (hdr)
        {
            var companion = Pipeline.RenderSdrRendition(source, parameters, version, cmm);
            Assert.Equal(1, companion.Pixels.Data[0]);
            Assert.Equal(1, companion.Pixels.Data[1]);
            Assert.Equal(1, companion.Pixels.Data[2]);
        }
    }

    [Fact]
    public void SDR_primary_preserves_HDR_chroma_even_if_the_SDR_template_has_a_different_grade()
    {
        // A warm master and a blue template reproduce the failure of dropping an HDR-only LUT.
        var hdr = Frame(new[] { 0.7f, 0.35f, 0.14f }, hdr: true);
        var template = Frame(new[] { 0.2f, 0.3f, 0.4f }, hdr: false);
        var original = (float[])template.Pixels.Data.Clone();
        var fallback = HdrGainMap.ColourMatchedSdrBase(template, hdr, ColorSpaces.Srgb);
        float[] linear = (float[])fallback.Pixels.Data.Clone();
        OutputRender.Decode(linear, ColorSpaces.Srgb);
        Assert.InRange(linear[0] / linear[2], 4.999f, 5.001f);
        Assert.InRange(linear[1] / linear[2], 2.499f, 2.501f);
        Assert.Equal(original, template.Pixels.Data);
        var target = new FrameParams { HdrPeakNits = 800 }.ResolvedOutputTarget;
        var map = HdrGainMap.Compute(fallback, hdr, ColorSpaces.Srgb, target, GainMapChannels.PerChannel);
        for (int c = 0; c < 3; c++)
            Assert.InRange(HdrGainMap.Reconstruct(linear[c], map.Map.Data[c], c, map.Metadata, 1),
                hdr.Pixels.Data[c] - 2e-3f, hdr.Pixels.Data[c] + 2e-3f);

        string path = Path.Combine(TestDataIsolation.Root, "sdr-primary-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            JpegIO.ExportGainMapJpeg(fallback, hdr, ColorSpaces.Srgb, target, path, quality: 100);
            // ImageSharp reads the primary JPEG, with no gain-map reconstruction.
            using var decoded = Image.Load<Rgb24>(path);
            Assert.NotNull(decoded.Metadata.IccProfile);
            Rgb24 pixel = decoded[0, 0];
            Assert.True(pixel.R > pixel.G && pixel.G > pixel.B);
            Assert.InRange(pixel.R / 255f, fallback.Pixels.Data[0] - 0.015f, fallback.Pixels.Data[0] + 0.015f);
        }
        finally { File.Delete(path); }
    }

    private static WorkingFrame Working(ImageBuffer pixels)
    {
        var encoding = new UncharacterizedPixelEncoding(CaptureKind.Synthetic, "fill-test",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking, TransferState.Unknown, NumericRange.Extended);
        return new(pixels, WorkingSpaceId.LinearAcesCgV1, WorkingAdmission.LegacyUncharacterizedPassthrough,
            new SourceDescriptor("fill-test", "fill test", encoding, "synthetic"));
    }

    private static RenderedFrame Frame(float[] colour, bool hdr)
    {
        var profile = BuiltInColorProfiles.For(hdr ? ColorSpaces.LinearExtendedSrgb : ColorSpaces.Srgb, ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(profile,
            hdr ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
            hdr ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded,
            hdr ? NumericRange.Extended : NumericRange.Normalized);
        var pixels = new ImageBuffer(8, 8);
        for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = colour[i % 3];
        var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile,
            RenderingIntent.RelativeColorimetric, false, "synthetic", "none", false);
        return new(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }
}
