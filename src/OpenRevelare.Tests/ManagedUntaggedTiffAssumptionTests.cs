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
    public void Managed_v2_untagged_input_is_detected_and_an_explicit_choice_still_overrides(
        int bitsPerSample,
        int sample)
    {
        string path = WriteTiff(bitsPerSample, new[] { sample, sample, sample });
        try
        {
            using var engine = new LittleCmsEngine();
            // This file declares nothing at all, so detection lands on the labelled convention
            // instead of refusing to open it.
            WorkingFrame detected = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Unspecified,
                ColorPipelineVersion.ManagedV2,
                engine);
            Assert.Contains("detected input", detected.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.Contains(
                nameof(TiffInputEvidence.ConventionalDefault),
                detected.Source.DecodeRecipe,
                StringComparison.Ordinal);

            // The pre-typed boolean overload rides the same route rather than throwing.
            WorkingFrame viaBooleanOverload = TiffIO.LoadWorkingFrame(
                path,
                inputIsSrgb: false,
                ColorPipelineVersion.ManagedV2,
                engine);
            Assert.Equal(detected.Source.DecodeRecipe, viaBooleanOverload.Source.DecodeRecipe);

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

            // The convention IS sRGB, so detection and the explicit sRGB override agree on pixels
            // while staying distinguishable in the recipe.
            Assert.Equal(srgb.Pixels.Data, detected.Pixels.Data);
            Assert.NotEqual(srgb.Source.DecodeRecipe, detected.Source.DecodeRecipe);
        }
        finally
        {
            DeleteTestFile(path);
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
            WorkingFrame detected = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Unspecified,
                ColorPipelineVersion.ManagedV2,
                engine);
            Assert.Contains("embedded ICC unavailable", detected.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.Contains("detected input", detected.Source.DecodeRecipe, StringComparison.Ordinal);

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
            DeleteTestFile(path);
        }
    }

    /// <summary>
    /// THE DETECTOR MUST REPORT THE DECLARATION THE DECODER ACTS ON.
    ///
    /// TiffIO undid the Flextight gamma while the detector fell through to the conventional
    /// default, so the GUI told the user the roll had "no colour declaration, treated as sRGB" and
    /// offered Linear/sRGB — a choice the decode then ignored. Both must read the same evidence,
    /// and the answer is conclusive: nothing is left for the user to decide.
    /// </summary>
    [Fact]
    public void Flextight_declaration_is_conclusive_for_the_detector()
    {
        string path = WriteFlextightTiff();
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);
            Assert.Equal(TiffInputEvidence.VendorGammaDeclaration, detection.Evidence);
            Assert.True(detection.IsConclusive);
            Assert.Contains("gamma 2.0", detection.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestFile(path);
        }
    }

    [Fact]
    public void Flextight_gamma_is_honoured_without_an_explicit_choice()
    {
        string path = WriteFlextightTiff();
        try
        {
            using var engine = new LittleCmsEngine();
            // A vendor gamma declaration outranks anything the detector could infer, so it applies
            // without the user having been asked.
            WorkingFrame detected = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.Unspecified,
                ColorPipelineVersion.ManagedV2,
                engine);
            Assert.Contains(
                "scanner-vendor gamma declaration", detected.Source.DecodeRecipe, StringComparison.Ordinal);
            Assert.InRange(detected.Pixels.Data[0], 0.24f, 0.26f);

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
            DeleteTestFile(path);
        }
    }

    [Fact]
    public async Task Programmatic_new_tiff_roll_opens_without_an_upfront_choice()
    {
        string path = WriteTiff(8, new[] { 206, 138, 81 });
        try
        {
            using var vm = new MainViewModel();
            var config = new ImportConfig();
            config.Paths.Add(path);

            await vm.LoadRollWithConfigAsync(config);

            Assert.Single(vm.Frames);
        }
        finally
        {
            DeleteTestFile(path);
        }
    }

    [Fact]
    public async Task A_guessed_roll_shows_a_correctable_notice_and_the_correction_re_decodes()
    {
        // Nothing in this file declares a colour space, so the roll opens on the convention and
        // must say so. That notice is the whole replacement for the old blocking question.
        string path = WriteTiff(16, new[] { 32768, 24000, 16000 });
        try
        {
            using var vm = new MainViewModel();
            var config = new ImportConfig();
            config.Paths.Add(path);
            await vm.LoadRollWithConfigAsync(config);

            Assert.True(vm.ShowTiffInputNotice);
            Assert.Contains("sRGB", vm.TiffInputNoticeText, StringComparison.Ordinal);
            Assert.Contains("auto(", vm.ColorPipelineDiagnostic, StringComparison.Ordinal);

            await vm.SetTiffInputAssumptionAsync(TiffInputAssumption.Linear);

            // An override is no longer a guess, so the notice retires itself.
            Assert.False(vm.ShowTiffInputNotice);
            Assert.Contains("Linear", vm.ColorPipelineDiagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("auto(", vm.ColorPipelineDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestFile(path);
        }
    }

    [Fact]
    public async Task A_roll_opened_without_the_import_dialog_still_gets_the_notice()
    {
        // Every other test here goes through LoadRollWithConfigAsync — the import-dialog path —
        // and that is exactly how the bug this pins survived: the disclosure was wired to that
        // path alone, so command line, double-click and 添加图像 opened a roll, assumed sRGB, and
        // said nothing. The path that deliberately does NOT ask must still tell.
        string path = WriteTiff(16, new[] { 32768, 24000, 16000 });
        try
        {
            using var vm = new MainViewModel();

            await vm.LoadRollAsync(new[] { path });

            Assert.True(vm.ShowTiffInputNotice);
            Assert.Contains("sRGB", vm.TiffInputNoticeText, StringComparison.Ordinal);
            Assert.Contains("auto(", vm.ColorPipelineDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestFile(path);
        }
    }

    [Fact]
    public async Task Dismissing_one_rolls_notice_does_not_silence_the_next_roll()
    {
        // The dismissal is about ONE roll's guess. Letting it persist would mean a user who
        // dismissed it once never hears about any later roll — a silence they never chose.
        string first = WriteTiff(16, new[] { 32768, 24000, 16000 });
        string second = WriteTiff(16, new[] { 30000, 22000, 15000 });
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(new[] { first });
            vm.DismissTiffInputNotice();
            Assert.False(vm.ShowTiffInputNotice);

            await vm.LoadRollAsync(new[] { second });

            Assert.True(vm.ShowTiffInputNotice);
        }
        finally
        {
            DeleteTestFile(first);
            DeleteTestFile(second);
        }
    }

    [Fact]
    public async Task A_roll_that_declared_itself_is_not_nagged_about()
    {
        string path = WriteTiff(16, new[] { 32768, 24000, 16000 }, software: "VueScan 9.8.11");
        try
        {
            using var vm = new MainViewModel();
            var config = new ImportConfig();
            config.Paths.Add(path);
            await vm.LoadRollWithConfigAsync(config);

            Assert.False(vm.ShowTiffInputNotice);
            Assert.Contains("ScannerSoftware", vm.ColorPipelineDiagnostic, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTestFile(path);
        }
    }

    [Fact]
    public async Task The_notice_can_be_dismissed_without_changing_the_admission()
    {
        string path = WriteTiff(16, new[] { 32768, 24000, 16000 });
        try
        {
            using var vm = new MainViewModel();
            var config = new ImportConfig();
            config.Paths.Add(path);
            await vm.LoadRollWithConfigAsync(config);
            string before = vm.ColorPipelineDiagnostic;

            vm.DismissTiffInputNotice();

            Assert.False(vm.ShowTiffInputNotice);
            Assert.Equal(before, vm.ColorPipelineDiagnostic);
        }
        finally
        {
            DeleteTestFile(path);
        }
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

            // The previews are decoded at PREVIEW precision: on a route that runs a CMM
            // transform (sRGB here) that is the 16-bit optimised transform, which the recipe
            // says so, and the pixels agree with the exact decode to ~1e-4 rather than to the
            // bit. A route with no transform (linear) is untouched by the precision and stays
            // identical. Either way the ASSUMPTION and the route are the same — that is what
            // this test pins.
            bool cmm = assumption == TiffInputAssumption.Srgb;
            string previewRecipe = cmm
                ? full.Source.DecodeRecipe + " [preview precision: 16-bit optimised transform]"
                : full.Source.DecodeRecipe;
            Assert.Equal((2, 1), (width, height));
            Assert.Equal(previewRecipe, previews[0].Source.DecodeRecipe);
            Assert.Equal((1, 1), (regionWidth, regionHeight));
            Assert.Equal(previewRecipe, region.Source.DecodeRecipe);
            Assert.Equal(full.Admission, region.Admission);
            float tolerance = cmm ? 1e-3f : 0f;
            for (int i = 0; i < 6; i++)
                Assert.InRange(previews[0].Pixels.Data[i], full.Pixels.Data[i] - tolerance, full.Pixels.Data[i] + tolerance);
            for (int i = 0; i < 3; i++)
                Assert.InRange(region.Pixels.Data[i], full.Pixels.Data[i] - tolerance, full.Pixels.Data[i] + tolerance);
        }
        finally
        {
            DeleteTestFile(path);
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
                TiffInputAssumptionPolicy.FromPersistedLinearFlag(
                    reopened.Meta.TiffIsLinear, ColorPipelineVersion.ManagedV2));
        }
        finally
        {
            DeleteTestFile(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Missing_project_field_means_legacy_compatibility_or_detection_by_version()
    {
        // The same absent field means different things either side of the pipeline version: a
        // pre-v2 project must keep the frozen by-bit-depth route, while a v2 project never wrote
        // null before detection existed, so null there is the detector's state.
        Assert.Equal(
            TiffInputAssumption.LegacyByBitDepthCompatibility,
            TiffInputAssumptionPolicy.FromPersistedLinearFlag(null, ColorPipelineVersion.LegacyV1));
        Assert.Equal(
            TiffInputAssumption.Unspecified,
            TiffInputAssumptionPolicy.FromPersistedLinearFlag(null, ColorPipelineVersion.ManagedV2));
        Assert.Throws<ArgumentException>(() =>
            TiffInputAssumptionPolicy.FromExplicitChoice(linear: true, srgb: true));
        Assert.Equal(
            TiffInputAssumption.Unspecified,
            TiffInputAssumptionPolicy.FromExplicitChoice(linear: false, srgb: false));
    }

    /// <summary>
    /// Delete a test file once nothing holds it. A roll load returns as soon as its first frame is
    /// on screen and leaves the warm-up decoding in the background; disposing the view model
    /// cancels that work but does not wait for a decode already inside LibTiff to let go of the
    /// file, so a plain File.Delete in the finally block raced it (Windows refuses to delete an
    /// open file) — and the parallel band reader, which opens several handles per decode, made
    /// the race far more likely. A leftover temp file is not worth failing the test over, so the
    /// last resort is to leave it.
    /// </summary>
    private static void DeleteTestFile(string path)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try { File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    private static string WriteTiff(
        int bitsPerSample,
        int[] samples,
        byte[]? embeddedIcc = null,
        string? software = null)
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
        if (software is not null)
            tif.SetField(TiffTag.SOFTWARE, software);

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
