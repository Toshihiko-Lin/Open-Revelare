using System.Security.Cryptography;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class TypedTiffExportTests
{
    [Fact]
    public void Extended_scene_linear_export_uses_float32_and_preserves_exact_icc_and_range()
    {
        float[] samples =
        {
            -0.25f, 0.18f, 1.50f,
            2.25f, -0.03125f, 0.75f,
        };
        RenderedFrame frame = LinearFrame(new ImageBuffer(2, 1, (float[])samples.Clone()));

        WithTempTiff(path =>
        {
            TiffIO.ExportTiff(frame, path, TiffIO.CompressionMode.Lzw);

            using (Tiff tif = Assert.IsType<Tiff>(Tiff.Open(path, "r")))
            {
                Assert.Equal(32, RequiredInt(tif, TiffTag.BITSPERSAMPLE));
                Assert.Equal((int)SampleFormat.IEEEFP, RequiredInt(tif, TiffTag.SAMPLEFORMAT));

                FieldValue[] icc = Assert.IsType<FieldValue[]>(tif.GetField(TiffTag.ICCPROFILE));
                byte[] embedded = icc[1].ToByteArray();
                Assert.Equal(frame.OutputProfile.IccBytes.ToArray(), embedded);
                Assert.Equal(
                    frame.OutputProfile.Identity.Sha256Hex,
                    Convert.ToHexString(SHA256.HashData(embedded)).ToLowerInvariant());

                byte[] row = new byte[tif.ScanlineSize()];
                Assert.True(tif.ReadScanline(row, 0));
                for (int i = 0; i < samples.Length; i++)
                    Assert.Equal(samples[i], BitConverter.ToSingle(row, i * sizeof(float)));
            }

            // Exercise the application reader too, not just LibTiff's raw scanline API. The
            // embedded profile is linear ACEScg, so its legacy matrix admission is an identity
            // apart from ICC matrix quantisation and must retain both sides of nominal [0,1].
            ImageBuffer reopened = TiffIO.LoadTiff(path, inputIsSrgb: false);
            Assert.Equal(samples.Length, reopened.Data.Length);
            for (int i = 0; i < samples.Length; i++)
                Assert.InRange(Math.Abs(reopened.Data[i] - samples[i]), 0.0f, 3e-4f);
            Assert.True(reopened.Data.Min() < 0.0f);
            Assert.True(reopened.Data.Max() > 1.0f);

            ImageBuffer region = TiffIO.LoadTiffRegion(
                path,
                (X: 0.0, Y: 0.0, W: 1.0, H: 1.0),
                inputIsSrgb: false,
                maxEdge: 0);
            Assert.True(region.Data.Min() < 0.0f);
            Assert.True(region.Data.Max() > 1.0f);
            for (int i = 0; i < samples.Length; i++)
                Assert.InRange(Math.Abs(region.Data[i] - samples[i]), 0.0f, 3e-4f);

            // ManagedV2 uses one RGB-float LittleCMS transform, with NoOptimize and without the
            // cmsFLAGS_NONEGATIVES clamp. Reopening the exact linear ACEScg export must therefore
            // preserve extended values through the production admission boundary too.
            using var colorManagement = new LittleCmsEngine();
            WorkingFrame managed = TiffIO.LoadWorkingFrame(
                path,
                inputIsSrgb: false,
                ColorPipelineVersion.ManagedV2,
                colorManagement);
            Assert.Equal(WorkingAdmission.ConvertedFromCharacterized, managed.Admission);
            Assert.IsType<CharacterizedPixelEncoding>(managed.Source.OriginalEncoding);
            Assert.Equal(NumericRange.Extended, managed.Source.OriginalEncoding.Range);
            Assert.True(managed.Pixels.Data.Min() < 0.0f);
            Assert.True(managed.Pixels.Data.Max() > 1.0f);
            for (int i = 0; i < samples.Length; i++)
                Assert.InRange(Math.Abs(managed.Pixels.Data[i] - samples[i]), 0.0f, 1e-5f);
        });
    }

    [Fact]
    public void Linear_export_summary_reports_only_float32_tiff_depth()
    {
        var options = new ExportOptions
        {
            Format = ExportFormat.Tiff16,
            ExportLinear = true,
            TiffCompression = TiffIO.CompressionMode.Lzw,
        };

        string summary = options.Summary();

        Assert.Contains("32-bit float TIFF", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("16-bit TIFF", summary, StringComparison.Ordinal);
        Assert.Contains("嵌入 ICC", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalized_typed_export_stays_unsigned_tiff16()
    {
        RenderedFrame frame = DisplayFrame(new ImageBuffer(1, 1, new[] { 0.0f, 0.5f, 1.0f }));

        WithTempTiff(path =>
        {
            TiffIO.ExportTiff(frame, path, TiffIO.CompressionMode.None);

            using Tiff tif = Assert.IsType<Tiff>(Tiff.Open(path, "r"));
            Assert.Equal(16, RequiredInt(tif, TiffTag.BITSPERSAMPLE));
            Assert.Null(tif.GetField(TiffTag.SAMPLEFORMAT));
            byte[] row = new byte[tif.ScanlineSize()];
            Assert.True(tif.ReadScanline(row, 0));
            Assert.Equal((ushort)0, ReadU16(row, 0));
            Assert.Equal((ushort)32768, ReadU16(row, 1));
            Assert.Equal(ushort.MaxValue, ReadU16(row, 2));
        });
    }

    [Fact]
    public void Exact_display_referred_srgb_may_be_exported_without_an_icc()
    {
        RenderedFrame frame = DisplayFrame(new ImageBuffer(1, 1, new[] { 0.0f, 0.5f, 1.0f }));

        WithTempTiff(path =>
        {
            TiffIO.ExportTiff(
                frame,
                path,
                TiffIO.CompressionMode.None,
                profilePolicy: ExportProfilePolicy.OmitExactSrgb);

            using Tiff tif = Assert.IsType<Tiff>(Tiff.Open(path, "r"));
            Assert.Null(tif.GetField(TiffTag.ICCPROFILE));
        });
    }

    [Theory]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    [InlineData("Rec709")]
    public void Non_exact_srgb_omit_is_rejected_before_a_file_is_created(string spaceName)
    {
        ColorSpaceDef space = ColorSpaces.ByName(spaceName, ColorSpaces.Srgb);
        RenderedFrame frame = DisplayFrame(
            new ImageBuffer(1, 1, new[] { 0.0f, 0.5f, 1.0f }),
            space);

        WithTempTiff(path =>
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                TiffIO.ExportTiff(
                    frame,
                    path,
                    TiffIO.CompressionMode.None,
                    profilePolicy: ExportProfilePolicy.OmitExactSrgb));

            Assert.Contains("only for exact display-referred sRGB", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void Scene_linear_icc_omit_is_rejected_before_a_file_is_created()
    {
        RenderedFrame frame = LinearFrame(
            new ImageBuffer(1, 1, new[] { -0.1f, 0.5f, 1.1f }));

        WithTempTiff(path =>
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                TiffIO.ExportTiff(
                    frame,
                    path,
                    TiffIO.CompressionMode.None,
                    profilePolicy: ExportProfilePolicy.OmitExactSrgb));

            Assert.Contains("only for exact display-referred sRGB", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void Explicit_tiff16_rejects_extended_frame_before_creating_a_file()
    {
        RenderedFrame frame = LinearFrame(new ImageBuffer(1, 1, new[] { -0.1f, 0.5f, 1.1f }));

        WithTempTiff(path =>
        {
            Assert.Throws<NotSupportedException>(() =>
                TiffIO.ExportTiff16(frame, path, TiffIO.CompressionMode.None));
            Assert.False(File.Exists(path));
        });
    }

    private static RenderedFrame LinearFrame(ImageBuffer pixels)
    {
        ColorProfileRef profile = BuiltInColorProfiles.LinearAcesCg(ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(
            profile,
            ColorReference.SceneReferred,
            TransferState.LinearInProfilePrimaries,
            NumericRange.Extended);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2,
            profile,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            "none (scene-linear ACEScg)",
            printLutIdentity: string.Empty,
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    private static RenderedFrame DisplayFrame(ImageBuffer pixels, ColorSpaceDef? outputSpace = null)
    {
        ColorSpaceDef space = outputSpace ?? ColorSpaces.Srgb;
        ColorProfileRef profile = BuiltInColorProfiles.For(space, ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(
            profile,
            ColorReference.DisplayReferred,
            TransferState.ProfileEncoded,
            NumericRange.Normalized);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.ManagedV2,
            profile,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            $"test exact {space.Name}",
            printLutIdentity: string.Empty,
            pixelProfileMismatch: false);
        return new RenderedFrame(
            pixels,
            encoding,
            recipe,
            RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }

    private static int RequiredInt(Tiff tif, TiffTag tag) =>
        Assert.IsType<FieldValue[]>(tif.GetField(tag))[0].ToInt();

    private static ushort ReadU16(byte[] bytes, int sampleIndex)
    {
        int offset = sampleIndex * sizeof(ushort);
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    private static void WithTempTiff(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), $"open-revelare-{Guid.NewGuid():N}.tiff");
        try
        {
            action(path);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
