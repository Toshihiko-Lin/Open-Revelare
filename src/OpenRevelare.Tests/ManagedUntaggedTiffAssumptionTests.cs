using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using System.Text;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ManagedUntaggedTiffAssumptionTests
{
    [Theory]
    [InlineData(8, 128)]
    [InlineData(16, 32768)]
    public void Managed_v2_untagged_input_requires_and_records_an_explicit_choice(
        int bitsPerSample,
        int sample)
    {
        string path = WriteTiff(bitsPerSample, new[] { sample, sample, sample });
        try
        {
            using var engine = new LittleCmsEngine();
            ColorManagementException error = Assert.Throws<ColorManagementException>(() =>
                TiffIO.LoadWorkingFrame(
                    path,
                    TiffInputAssumption.Unspecified,
                    ColorPipelineVersion.ManagedV2,
                    engine));
            Assert.Contains("explicit roll-level Linear or sRGB", error.Message, StringComparison.Ordinal);
            Assert.Contains("bit depth is not", error.Message, StringComparison.Ordinal);
            Assert.Throws<ColorManagementException>(() => TiffIO.LoadWorkingFrame(
                path,
                inputIsSrgb: false,
                ColorPipelineVersion.ManagedV2,
                engine));

            WorkingFrame linear = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Linear,
                ColorPipelineVersion.ManagedV2,
                engine);
            WorkingFrame srgb = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Srgb,
                ColorPipelineVersion.ManagedV2,
                engine);

            float normalized = sample / (bitsPerSample == 8 ? 255.0f : 65535.0f);
            Assert.Equal(normalized, linear.Pixels.Data[0]);
            Assert.Equal(WorkingAdmission.ExplicitUncharacterizedPassthrough, linear.Admission);
            UncharacterizedPixelEncoding uncharacterized = Assert.IsType<UncharacterizedPixelEncoding>(
                linear.Source.OriginalEncoding);
            Assert.Equal(CompatibilityPolicy.None, uncharacterized.Compatibility);
            Assert.Equal(TransferState.LinearInProfilePrimaries, uncharacterized.Transfer);
            Assert.Contains("explicit roll fallback linear", linear.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.Contains("primaries uncharacterized", linear.Source.DecodeRecipe, StringComparison.Ordinal);

            CharacterizedPixelEncoding characterized = Assert.IsType<CharacterizedPixelEncoding>(
                srgb.Source.OriginalEncoding);
            Assert.Equal(BuiltInColorProfiles.Srgb(ProfileRole.Input).Identity, characterized.Profile.Identity);
            Assert.Equal(ColorReference.DisplayReferred, characterized.Reference);
            Assert.Contains("explicit roll fallback sRGB", srgb.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.NotEqual(linear.Pixels.Data[0], srgb.Pixels.Data[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Explicit_fallback_can_replace_a_malformed_profile_and_reports_why()
    {
        string path = WriteTiff(
            8,
            new[] { 206, 138, 81 },
            Enumerable.Repeat((byte)0xA5, 64).ToArray());
        try
        {
            using var engine = new LittleCmsEngine();
            Assert.Throws<ColorManagementException>(() => TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Unspecified,
                ColorPipelineVersion.ManagedV2,
                engine));

            WorkingFrame linear = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Linear,
                ColorPipelineVersion.ManagedV2,
                engine);
            WorkingFrame srgb = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Srgb,
                ColorPipelineVersion.ManagedV2,
                engine);

            Assert.InRange(Math.Abs(linear.Pixels.Data[0] - 206 / 255.0f), 0.0f, 1e-6f);
            Assert.Contains("embedded ICC unavailable", linear.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.Contains("rejected", linear.Source.DecodeRecipe, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("explicit roll fallback linear", linear.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.Contains("embedded ICC unavailable", srgb.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.Contains("explicit roll fallback sRGB", srgb.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.IsType<ProfileSource.BuiltIn>(
                Assert.IsType<CharacterizedPixelEncoding>(srgb.Source.OriginalEncoding).Profile.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Flextight_gamma_does_not_bypass_explicit_managed_v2_admission()
    {
        string path = WriteFlextightTiff();
        try
        {
            using var engine = new LittleCmsEngine();
            ColorManagementException error = Assert.Throws<ColorManagementException>(() =>
                TiffIO.LoadWorkingFrame(
                    path,
                    TiffInputAssumption.Unspecified,
                    ColorPipelineVersion.ManagedV2,
                    engine));
            Assert.Contains("explicit roll-level Linear or sRGB", error.Message, StringComparison.Ordinal);

            WorkingFrame admitted = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Linear,
                ColorPipelineVersion.ManagedV2,
                engine);
            Assert.Contains("scanner-vendor gamma declaration", admitted.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.InRange(admitted.Pixels.Data[0], 0.24f, 0.26f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Programmatic_new_tiff_roll_without_choice_is_rejected_before_adoption()
    {
        using var vm = new MainViewModel();
        string originalDiagnostic = vm.ColorPipelineDiagnostic;
        var config = new ImportConfig();
        config.Paths.Add(Path.Combine(Path.GetTempPath(), "not-opened-because-admission-fails.tif"));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            vm.LoadRollWithConfigAsync(config));

        Assert.Contains("must explicitly select Linear or sRGB", error.Message, StringComparison.Ordinal);
        Assert.Empty(vm.Frames);
        Assert.Equal(originalDiagnostic, vm.ColorPipelineDiagnostic);
    }

    [Theory]
    [InlineData(TiffInputAssumption.Linear)]
    [InlineData(TiffInputAssumption.Srgb)]
    public void Full_preview_and_region_share_one_assumption_and_recipe(
        TiffInputAssumption assumption)
    {
        string path = WriteTiff(8, new[]
        {
            206, 138, 81,
            128, 96, 64,
        });
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame full = ImageIo.LoadWorking(
                path,
                ColorPipelineVersion.ManagedV2,
                engine,
                assumption);
            var (previews, width, height) = ImageIo.LoadWorkingPreviews(
                path,
                ColorPipelineVersion.ManagedV2,
                engine,
                assumption,
                100);
            var (region, regionWidth, regionHeight) = ImageIo.LoadWorkingPreviewRegion(
                path,
                (0.0, 0.0, 0.5, 1.0),
                100,
                ColorPipelineVersion.ManagedV2,
                engine,
                assumption);

            Assert.Equal((2, 1), (width, height));
            Assert.Equal(full.Pixels.Data, previews[0].Pixels.Data);
            Assert.Equal(full.Source.DecodeRecipe, previews[0].Source.DecodeRecipe);
            Assert.Equal((1, 1), (regionWidth, regionHeight));
            Assert.Equal(full.Pixels.Data.AsSpan(0, 3).ToArray(), region.Pixels.Data);
            Assert.Equal(full.Source.DecodeRecipe, region.Source.DecodeRecipe);
            Assert.Equal(full.Admission, region.Admission);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(TiffInputAssumption.Linear, true)]
    [InlineData(TiffInputAssumption.Srgb, false)]
    public void Roll_assumption_round_trips_through_existing_project_field(
        TiffInputAssumption assumption,
        bool persistedFlag)
    {
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-tiff-assumption-{Guid.NewGuid():N}.ncproj");
        try
        {
            var project = new Project.Data();
            project.Meta.InputType = "tiff";
            project.Meta.TiffIsLinear = TiffInputAssumptionPolicy.ToPersistedLinearFlag(assumption);

            Project.Save(path, project);
            Project.Data reopened = Project.Load(path);

            Assert.Equal(persistedFlag, reopened.Meta.TiffIsLinear);
            Assert.Equal(
                assumption,
                TiffInputAssumptionPolicy.FromPersistedLinearFlag(reopened.Meta.TiffIsLinear));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Missing_project_field_and_legacy_selector_are_explicit_compatibility_only()
    {
        Assert.Equal(
            TiffInputAssumption.LegacyByBitDepthCompatibility,
            TiffInputAssumptionPolicy.FromPersistedLinearFlag(null));
        Assert.Throws<ArgumentException>(() =>
            TiffInputAssumptionPolicy.FromExplicitChoice(linear: true, srgb: true));
        Assert.Equal(
            TiffInputAssumption.Unspecified,
            TiffInputAssumptionPolicy.FromExplicitChoice(linear: false, srgb: false));
    }

    private static string WriteTiff(int bitsPerSample, int[] samples, byte[]? embeddedIcc = null)
    {
        int samplesPerPixel = 3;
        int width = samples.Length / samplesPerPixel;
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-managed-untagged-{Guid.NewGuid():N}.tif");
        using Tiff tif = Tiff.Open(path, "w")
            ?? throw new IOException($"could not create test TIFF: {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, 1);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, samplesPerPixel);
        tif.SetField(TiffTag.BITSPERSAMPLE, bitsPerSample);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1);
        if (embeddedIcc is not null)
            tif.SetField(TiffTag.ICCPROFILE, embeddedIcc.Length, embeddedIcc);

        byte[] row = new byte[samples.Length * (bitsPerSample / 8)];
        for (int index = 0; index < samples.Length; index++)
        {
            if (bitsPerSample == 8)
            {
                row[index] = checked((byte)samples[index]);
            }
            else
            {
                ushort value = checked((ushort)samples[index]);
                row[index * 2] = (byte)value;
                row[index * 2 + 1] = (byte)(value >> 8);
            }
        }
        Assert.True(tif.WriteScanline(row, 0));
        return path;
    }

    private static string WriteFlextightTiff()
    {
        byte[] plist = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CurrentIx</key><integer>0</integer>
              <key>ImageSettings</key><array><dict>
                <key>ImageCorrection</key><dict><key>Gamma</key><real>2.0</real></dict>
                <key>Name</key><string>Negative RGB standard</string>
              </dict></array>
            </dict></plist>
            """);

        const ushort entryCount = 11;
        const uint ifdOffset = 8;
        uint bitsOffset = ifdOffset + 2 + entryCount * 12 + 4;
        uint plistOffset = bitsOffset + 6;
        uint stripOffset = plistOffset + checked((uint)plist.Length);

        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)'I');
            writer.Write((byte)'I');
            writer.Write((ushort)42);
            writer.Write(ifdOffset);
            writer.Write(entryCount);

            void Entry(ushort tag, ushort type, uint count, uint value)
            {
                writer.Write(tag);
                writer.Write(type);
                writer.Write(count);
                writer.Write(value);
            }

            Entry(256, 3, 1, 1);                         // width
            Entry(257, 3, 1, 1);                         // height
            Entry(258, 3, 3, bitsOffset);                // bits/sample array
            Entry(259, 3, 1, 1);                         // no compression
            Entry(262, 3, 1, 2);                         // RGB
            Entry(273, 4, 1, stripOffset);               // strip offset
            Entry(277, 3, 1, 3);                         // samples/pixel
            Entry(278, 4, 1, 1);                         // rows/strip
            Entry(279, 4, 1, 3);                         // strip byte count
            Entry(284, 3, 1, 1);                         // contiguous
            Entry(FlextightMeta.SettingsPlistTag, 7, checked((uint)plist.Length), plistOffset);
            writer.Write(0u);                            // no next IFD
            writer.Write((ushort)8);
            writer.Write((ushort)8);
            writer.Write((ushort)8);
            writer.Write(plist);
            writer.Write(new byte[] { 128, 128, 128 });
        }

        string path = Path.Combine(
            Path.GetTempPath(),
            $"openrevelare-managed-flextight-{Guid.NewGuid():N}.fff");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }
}
