using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public class AtomicIccTiffInputTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ColorManagement");

    private static string AdobeRgbTiff =>
        Path.Combine(FixtureDirectory, "win11-eizo", "adobe-rgb-patches.tif");

    [Fact]
    public void Legacy_versioned_overload_is_bit_identical_and_does_not_touch_the_CMM()
    {
        WorkingFrame frozen = TiffIO.LoadWorkingFrame(AdobeRgbTiff, inputIsSrgb: false);
        using var neverCalled = StubEngine.NeverCalled();

        WorkingFrame versioned = TiffIO.LoadWorkingFrame(
            AdobeRgbTiff,
            inputIsSrgb: false,
            ColorPipelineVersion.LegacyV1,
            neverCalled);

        Assert.Equal(frozen.Pixels.Width, versioned.Pixels.Width);
        Assert.Equal(frozen.Pixels.Height, versioned.Pixels.Height);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(frozen.Pixels.SourceQuantisationStep),
            BitConverter.DoubleToInt64Bits(versioned.Pixels.SourceQuantisationStep));
        AssertFloatBitsEqual(frozen.Pixels.Data, versioned.Pixels.Data);
        Assert.Equal(frozen.Space, versioned.Space);
        Assert.Equal(frozen.Admission, versioned.Admission);
        Assert.Equal(frozen.Source.StableSourceId, versioned.Source.StableSourceId);
        Assert.Equal(frozen.Source.DisplayName, versioned.Source.DisplayName);
        Assert.Equal(frozen.Source.DecodeRecipe, versioned.Source.DecodeRecipe);
        CharacterizedPixelEncoding frozenEncoding = Assert.IsType<CharacterizedPixelEncoding>(
            frozen.Source.OriginalEncoding);
        CharacterizedPixelEncoding versionedEncoding = Assert.IsType<CharacterizedPixelEncoding>(
            versioned.Source.OriginalEncoding);
        Assert.Equal(frozenEncoding.Profile.Identity, versionedEncoding.Profile.Identity);
        Assert.Equal(
            frozenEncoding.Profile.IccBytes.ToArray(),
            versionedEncoding.Profile.IccBytes.ToArray());
        Assert.Equal(0, neverCalled.ValidationCalls);
        Assert.Equal(0, neverCalled.LeaseCalls);
    }

    [Fact]
    public void Embedded_AdobeRGB_is_transformed_directly_and_retains_the_exact_profile()
    {
        (float[] encoded, byte[] embedded) = ReadEncodedFixture(AdobeRgbTiff);
        var sourceProfile = ColorProfileRef.Create(
            embedded,
            "direct fixture profile",
            ProfileRole.Input,
            new ProfileSource.Embedded("direct-fixture", "TIFF"));
        using var engine = new LittleCmsEngine();
        float[] expected = DirectTransform(engine, encoded, sourceProfile);

        WorkingFrame actual = TiffIO.LoadWorkingFrame(
            AdobeRgbTiff,
            inputIsSrgb: false,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.Equal(WorkingAdmission.ConvertedFromCharacterized, actual.Admission);
        Assert.Equal(WorkingSpaceId.LinearAcesCgV1, actual.Space);
        CharacterizedPixelEncoding original = Assert.IsType<CharacterizedPixelEncoding>(
            actual.Source.OriginalEncoding);
        Assert.Equal(sourceProfile.Identity, original.Profile.Identity);
        Assert.Equal(embedded, original.Profile.IccBytes.ToArray());
        Assert.IsType<ProfileSource.Embedded>(original.Profile.Source);
        Assert.Equal(TransferState.ProfileEncoded, original.Transfer);
        AssertFloatBitsEqual(expected, actual.Pixels.Data);
        Assert.True(double.IsFinite(actual.Pixels.SourceQuantisationStep));
        Assert.True(actual.Pixels.SourceQuantisationStep > 0.0);
    }

    [Fact]
    public void Explicit_sRGB_fallback_does_not_override_a_usable_embedded_profile()
    {
        (float[] encoded, byte[] embedded) = ReadEncodedFixture(AdobeRgbTiff);
        ColorProfileRef embeddedProfile = ColorProfileRef.Create(
            embedded,
            "embedded fixture profile",
            ProfileRole.Input,
            new ProfileSource.Embedded("embedded-priority", "TIFF"));
        using var engine = new LittleCmsEngine();
        float[] expected = DirectTransform(engine, encoded, embeddedProfile);

        WorkingFrame actual = TiffIO.LoadWorkingFrame(
            AdobeRgbTiff,
            TiffInputAssumption.Srgb,
            ColorPipelineVersion.ManagedV2,
            engine);

        CharacterizedPixelEncoding original = Assert.IsType<CharacterizedPixelEncoding>(
            actual.Source.OriginalEncoding);
        Assert.Equal(embeddedProfile.Identity, original.Profile.Identity);
        Assert.IsType<ProfileSource.Embedded>(original.Profile.Source);
        Assert.Equal(ColorReference.SceneReferred, original.Reference);
        Assert.Contains("exact embedded ICC", actual.Source.DecodeRecipe, StringComparison.Ordinal);
        AssertFloatBitsEqual(expected, actual.Pixels.Data);
    }

    [Fact]
    public void Truncated_embedded_profile_is_rejected_without_legacy_guess_or_transform()
    {
        string path = WriteTinyTiff(embeddedProfile: Enumerable.Repeat((byte)0xA5, 64).ToArray());
        try
        {
            // The frozen v1 reader treated a sub-132-byte payload as absent. Pin that historical
            // behaviour, then prove the managed path does not silently take it.
            WorkingFrame legacy = TiffIO.LoadWorkingFrame(path, inputIsSrgb: false);
            Assert.IsType<UncharacterizedPixelEncoding>(legacy.Source.OriginalEncoding);

            using var engine = new StubEngine(
                validate: profile => profile.IccBytes.Length < 128
                    ? Invalid(profile, $"ICC payload is truncated ({profile.IccBytes.Length} bytes).")
                    : Valid(profile),
                lease: _ => throw new InvalidOperationException("a rejected profile must not reach Lease"));

            ColorManagementException error = Assert.Throws<ColorManagementException>(() =>
                TiffIO.LoadWorkingFrame(
                    path,
                    inputIsSrgb: false,
                    ColorPipelineVersion.ManagedV2,
                    engine));

            Assert.Contains("rejected", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, engine.ValidationCalls);
            Assert.Equal(0, engine.LeaseCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Transform_creation_failure_is_an_explicit_all_or_nothing_input_error()
    {
        using var engine = new StubEngine(
            validate: Valid,
            lease: _ => throw new ColorManagementException("synthetic transform creation failure"));

        // A valid profile that the CMM cannot build a transform from is an engine fault, so it
        // stays fatal instead of quietly becoming a different colour space.
        ColorTransformCreationException error = Assert.Throws<ColorTransformCreationException>(() =>
            TiffIO.LoadWorkingFrame(
                AdobeRgbTiff,
                inputIsSrgb: false,
                ColorPipelineVersion.ManagedV2,
                engine));

        Assert.Contains("complete ICC transform", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic transform creation failure", error.InnerException?.Message ?? "");
        Assert.Equal(2, engine.ValidationCalls);
        Assert.Equal(1, engine.LeaseCalls);
    }

    [Fact]
    public void Untagged_managed_input_stays_explicitly_uncharacterized_on_compatibility_admission()
    {
        string path = WriteTinyTiff(embeddedProfile: null);
        try
        {
            WorkingFrame legacy = TiffIO.LoadWorkingFrame(path, inputIsSrgb: false);
            using var neverCalled = StubEngine.NeverCalled();

            WorkingFrame managed = TiffIO.LoadWorkingFrame(
                path,
                TiffInputAssumption.LegacyByBitDepthCompatibility,
                ColorPipelineVersion.ManagedV2,
                neverCalled);

            var encoding = Assert.IsType<UncharacterizedPixelEncoding>(
                managed.Source.OriginalEncoding);
            Assert.Equal(CaptureKind.TiffUntagged8Bit, encoding.CaptureKind);
            Assert.Equal(
                CompatibilityPolicy.LegacyDecodeSrgbTransferThenTreatAsWorking,
                encoding.Compatibility);
            Assert.Equal(WorkingAdmission.LegacyUncharacterizedPassthrough, managed.Admission);
            Assert.Contains("legacy-by-bit-depth compatibility", managed.Source.DecodeRecipe);
            AssertFloatBitsEqual(legacy.Pixels.Data, managed.Pixels.Data);
            Assert.Equal(0, neverCalled.ValidationCalls);
            Assert.Equal(0, neverCalled.LeaseCalls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float[] DirectTransform(
        IColorManagementEngine engine,
        float[] encoded,
        ColorProfileRef sourceProfile)
    {
        var request = new ColorTransformRequest(
            sourceProfile,
            BuiltInColorProfiles.LinearAcesCg(ProfileRole.Working),
            TransformPurpose.InputToWorking,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            adaptationState: 1.0);
        float[] converted = new float[encoded.Length];
        using IColorTransformLease lease = engine.Lease(request);
        lease.Apply(encoded, converted, encoded.Length / 3);
        return converted;
    }

    private static (float[] Encoded, byte[] Embedded) ReadEncodedFixture(string path)
    {
        using Tiff tif = Tiff.Open(path, "r")
            ?? throw new IOException($"could not open test TIFF: {path}");
        int width = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int bps = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
        int spp = tif.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();
        Assert.True(bps is 8 or 16);
        Assert.True(spp >= 3);
        byte[] embedded = Assert.IsType<byte[]>(TiffIO.ReadIccBytes(tif));

        float inverse = bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;
        float[] encoded = new float[width * height * 3];
        byte[] row = new byte[tif.ScanlineSize()];
        for (int y = 0; y < height; y++)
        {
            Assert.True(tif.ReadScanline(row, y));
            for (int x = 0; x < width; x++)
            {
                int source = x * spp;
                int destination = (y * width + x) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    float sample = bps == 16
                        ? (ushort)(row[(source + channel) * 2] |
                            (row[(source + channel) * 2 + 1] << 8))
                        : row[source + channel];
                    encoded[destination + channel] = sample * inverse;
                }
            }
        }
        return (encoded, embedded);
    }

    private static string WriteTinyTiff(byte[]? embeddedProfile)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"openrevelare-atomic-icc-{Guid.NewGuid():N}.tif");
        using Tiff tif = Tiff.Open(path, "w")
            ?? throw new IOException($"could not create test TIFF: {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, 1);
        tif.SetField(TiffTag.IMAGELENGTH, 1);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, 8);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1);
        if (embeddedProfile is not null)
            tif.SetField(TiffTag.ICCPROFILE, embeddedProfile.Length, embeddedProfile);
        Assert.True(tif.WriteScanline(new byte[] { 206, 138, 81 }, 0));
        return path;
    }

    private static void AssertFloatBitsEqual(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]),
                $"float {i} differs: expected={expected[i]:R}, actual={actual[i]:R}");
        }
    }

    private static ProfileValidationResult Valid(ColorProfileRef profile) => new(
        true,
        profile.Identity,
        profile.Description,
        0x52474220u,
        0x58595A20u,
        0x04300000u,
        null,
        "valid RGB profile");

    private static ProfileValidationResult Invalid(ColorProfileRef profile, string message) => new(
        false,
        profile.Identity,
        profile.Description,
        null,
        null,
        null,
        null,
        message);

    private sealed class StubEngine : IColorManagementEngine
    {
        private readonly Func<ColorProfileRef, ProfileValidationResult> _validate;
        private readonly Func<ColorTransformRequest, IColorTransformLease> _lease;

        public int ValidationCalls { get; private set; }
        public int LeaseCalls { get; private set; }

        public CmmBuildIdentity Build { get; } = new(
            "test CMM",
            "test",
            0,
            "test",
            "test",
            CmmTransformFlags.None,
            new string('0', 64),
            new string('0', 64),
            "{}",
            "test");

        public StubEngine(
            Func<ColorProfileRef, ProfileValidationResult> validate,
            Func<ColorTransformRequest, IColorTransformLease> lease)
        {
            _validate = validate;
            _lease = lease;
        }

        public static StubEngine NeverCalled() => new(
            _ => throw new InvalidOperationException("the CMM must not be used"),
            _ => throw new InvalidOperationException("the CMM must not be used"));

        public ProfileValidationResult Validate(ColorProfileRef profile)
        {
            ValidationCalls++;
            return _validate(profile);
        }

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            LeaseCalls++;
            return _lease(request);
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() => new(
            Build,
            ValidationCalls,
            0,
            LeaseCalls,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

        public void Dispose() { }
    }
}
