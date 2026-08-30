using System.Security.Cryptography;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// M1 contract tests. These intentionally freeze the typed boundaries while the pixel pipeline is
/// still v1: introducing truthful colour metadata must not change a single rendered float.
/// </summary>
public class ColorManagementModelTests
{
    [Fact]
    public void Profile_copies_bytes_and_derives_identity_without_losing_role_or_source()
    {
        byte[] callerOwnedBytes = Enumerable.Range(0, 160).Select(i => (byte)i).ToArray();
        byte[] expectedBytes = (byte[])callerOwnedBytes.Clone();
        string expectedIdentity = Convert.ToHexString(SHA256.HashData(expectedBytes)).ToLowerInvariant();
        var source = new ProfileSource.Embedded("frame:scanner-001", "TIFF IFD0 ICCProfile");

        ColorProfileRef profile = ColorProfileRef.Create(
            callerOwnedBytes,
            "Scanner snapshot",
            ProfileRole.Input,
            source);

        callerOwnedBytes.AsSpan().Fill(0xA5);

        Assert.Equal(expectedBytes, profile.IccBytes.ToArray());
        Assert.Equal(expectedIdentity, profile.Identity.Sha256Hex);
        Assert.Equal(ProfileIdentity.Parse(expectedIdentity.ToUpperInvariant()), profile.Identity);
        Assert.Equal(ProfileRole.Input, profile.Role);
        Assert.Same(source, profile.Source);
        ProfileSource.Embedded embedded = Assert.IsType<ProfileSource.Embedded>(profile.Source);
        Assert.Equal("frame:scanner-001", embedded.StableSourceId);
        Assert.Equal("TIFF IFD0 ICCProfile", embedded.Container);

        ColorProfileRef samePayload = ColorProfileRef.Create(
            expectedBytes,
            "Same bytes, another use",
            ProfileRole.Proof,
            new ProfileSource.ExternalSnapshot("scanner.icc"));
        Assert.Equal(profile.Identity, samePayload.Identity);
        Assert.NotEqual(profile.Role, samePayload.Role);
        Assert.NotEqual(profile.Source, samePayload.Source);
    }

    [Theory]
    [InlineData("sRGB", BuiltInProfileId.Srgb,
        "83d79da24e59ef91f912ccf1afaef88a01139dee9d252c424583083c39f85334")]
    [InlineData("DisplayP3", BuiltInProfileId.DisplayP3,
        "25dce2ac08fd9e9a0e0a9ff1b0c3b2b79e13efd722a159e645a5e0f1362506ec")]
    [InlineData("AdobeRGB", BuiltInProfileId.AdobeRgb1998,
        "fbd482e5e6d4ce34006581ae4db88d33d682d6a9a31e930a30cd5d7c1573a406")]
    [InlineData("Rec709", BuiltInProfileId.Rec709,
        "20a9a64d0df813794c4b1fbfa1245bf275e9d30d7fbbc04c88b8092bbe155043")]
    [InlineData("ACEScg", BuiltInProfileId.LinearAcesCg,
        "7ce7452a6123db84f4a3ffdb9796bab6da34c576afff7724f77f85244811beb8")]
    public void Built_in_profiles_keep_the_five_frozen_byte_identities(
        string name,
        BuiltInProfileId builtInId,
        string expectedSha256)
    {
        ColorSpaceDef space = ColorSpaces.ByName(name, default);
        ColorProfileRef profile = BuiltInColorProfiles.For(space, ProfileRole.CanonicalPresentation);

        Assert.Equal(expectedSha256, profile.Identity.Sha256Hex);
        Assert.Equal(ProfileRole.CanonicalPresentation, profile.Role);
        Assert.Equal(builtInId, Assert.IsType<ProfileSource.BuiltIn>(profile.Source).Id);
        Assert.Equal(IccProfiles.Build(space), profile.IccBytes.ToArray());
    }

    [Fact]
    public void Characterized_and_uncharacterized_encodings_enforce_distinct_invariants()
    {
        ColorProfileRef inputProfile = BuiltInColorProfiles.DisplayP3(ProfileRole.Input);

        Assert.Throws<ArgumentNullException>(() => new CharacterizedPixelEncoding(
            null!, ColorReference.DisplayReferred, TransferState.ProfileEncoded, NumericRange.Normalized));
        Assert.Throws<ArgumentException>(() => new CharacterizedPixelEncoding(
            inputProfile, ColorReference.DisplayReferred, TransferState.Unknown, NumericRange.Normalized));

        var characterized = new CharacterizedPixelEncoding(
            inputProfile,
            ColorReference.DisplayReferred,
            TransferState.ProfileEncoded,
            NumericRange.Normalized);
        Assert.Same(inputProfile, characterized.Profile);
        Assert.Equal(ColorReference.DisplayReferred, characterized.Reference);
        Assert.Equal(TransferState.ProfileEncoded, characterized.Transfer);
        Assert.Equal(NumericRange.Normalized, characterized.Range);

        Assert.Throws<ArgumentException>(() => new UncharacterizedPixelEncoding(
            CaptureKind.RawCameraNative,
            " ",
            CompatibilityPolicy.RequireExplicitOverride,
            TransferState.Unknown,
            NumericRange.Extended));

        PixelEncoding uncharacterized = new UncharacterizedPixelEncoding(
            CaptureKind.RawCameraNative,
            "raw-sha256:0123456789abcdef",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var unknown = Assert.IsType<UncharacterizedPixelEncoding>(uncharacterized);
        Assert.Equal(CaptureKind.RawCameraNative, unknown.CaptureKind);
        Assert.Equal("raw-sha256:0123456789abcdef", unknown.StableSourceId);
        Assert.Equal(CompatibilityPolicy.LegacyTreatNumbersAsWorking, unknown.Compatibility);
        Assert.Equal(TransferState.Unknown, unknown.Transfer);
        Assert.Equal(NumericRange.Extended, unknown.Range);

        var source = new SourceDescriptor(
            "source:one",
            "Synthetic one",
            uncharacterized,
            "test synthetic decode");
        var otherEncoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "source:two",
            CompatibilityPolicy.None,
            TransferState.Unknown,
            NumericRange.Extended);
        Assert.Throws<ArgumentException>(() =>
            new SourceFrame(MakeSyntheticNegative(), otherEncoding, source));
    }

    [Theory]
    [InlineData("sRGB", "")]
    [InlineData("AdobeRGB", ":kodak-2383")]
    public void Typed_render_is_float_for_float_identical_to_ProcessFrame(
        string outputSpace,
        string printLut)
    {
        var parameters = new FrameParams
        {
            DisplayReferredStage2 = false,
            OutputSpace = outputSpace,
            PrintLut = printLut,
        };
        ImageBuffer legacySource = MakeSyntheticNegative();
        ImageBuffer typedSource = MakeSyntheticNegative();

        ImageBuffer legacy = Pipeline.ProcessFrame(legacySource, parameters);
        RenderedFrame typed = Pipeline.Render(MakeWorkingFrame(typedSource), parameters);

        Assert.Equal(legacy.Width, typed.Pixels.Width);
        Assert.Equal(legacy.Height, typed.Pixels.Height);
        Assert.Equal(legacy.SourceQuantisationStep, typed.Pixels.SourceQuantisationStep);
        AssertSameFloatBits(legacy.Data, typed.Pixels.Data, $"{outputSpace}/{printLut}");
        Assert.Equal(ColorPipelineVersion.LegacyV1, typed.Recipe.PipelineVersion);
        Assert.IsType<RenderFingerprint.Unavailable>(typed.Fingerprint);
    }

    [Fact]
    public void RenderedFrame_reports_actual_profile_and_requested_profile_mismatch_for_legacy_LUT()
    {
        RenderedFrame noLut = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            new FrameParams { OutputSpace = "AdobeRGB", PrintLut = "" });

        Assert.False(noLut.Recipe.PixelProfileMismatch);
        Assert.Equal(noLut.Recipe.RequestedOutputProfile.Identity, noLut.OutputProfile.Identity);
        Assert.Equal(ProfileRole.Output, noLut.OutputProfile.Role);
        Assert.IsType<ProfileSource.BuiltIn>(noLut.OutputProfile.Source);

        RenderedFrame withLut = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            new FrameParams { OutputSpace = "AdobeRGB", PrintLut = ":kodak-2383" });

        Assert.True(withLut.Recipe.PixelProfileMismatch);
        Assert.NotEqual(withLut.Recipe.RequestedOutputProfile.Identity, withLut.OutputProfile.Identity);
        Assert.Equal(
            BuiltInColorProfiles.AdobeRgb(ProfileRole.Output).Identity,
            withLut.Recipe.RequestedOutputProfile.Identity);
        Assert.IsType<ProfileSource.BuiltIn>(withLut.Recipe.RequestedOutputProfile.Source);
        ProfileSource.Generated generated = Assert.IsType<ProfileSource.Generated>(withLut.OutputProfile.Source);
        Assert.Equal("OpenRevelare.IccProfiles", generated.GeneratorId);
        Assert.Equal("legacy-v1", generated.GeneratorVersion);
        Assert.Equal(TransferState.ProfileEncoded, withLut.Encoding.Transfer);
        Assert.Equal(ColorReference.DisplayReferred, withLut.Encoding.Reference);
    }

    private static WorkingFrame MakeWorkingFrame(ImageBuffer pixels)
    {
        var originalEncoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:synthetic-negative-v1",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:synthetic-negative-v1",
            "M1 synthetic negative",
            originalEncoding,
            "test-generated RGB float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }

    private static ImageBuffer MakeSyntheticNegative()
    {
        var image = new ImageBuffer(4, 2, new[]
        {
            0.81f,  0.52f,  0.29f,
            0.63f,  0.31f,  0.12f,
            0.42f,  0.17f,  0.052f,
            0.24f,  0.075f, 0.015f,
            0.13f,  0.035f, 0.0035f,
            0.055f, 0.010f, 0.0008f,
            1.15f,  1.05f,  0.95f,
            0.008f, 0.0015f, 0.00012f,
        })
        {
            SourceQuantisationStep = 1.0 / 65535.0,
        };
        return image;
    }

    private static void AssertSameFloatBits(float[] expected, float[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            int expectedBits = BitConverter.SingleToInt32Bits(expected[i]);
            int actualBits = BitConverter.SingleToInt32Bits(actual[i]);
            Assert.True(
                expectedBits == actualBits,
                $"{label} sample {i}: expected={expected[i]:R} (0x{expectedBits:X8}), " +
                $"actual={actual[i]:R} (0x{actualBits:X8})");
        }
    }
}
