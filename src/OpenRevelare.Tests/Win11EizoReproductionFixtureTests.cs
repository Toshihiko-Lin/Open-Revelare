using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Freezes the M0a Windows 11 / EIZO reproduction without committing the reporter's
/// device-specific ICC payload. This is diagnostic evidence, not a portable display profile.
/// </summary>
public class Win11EizoReproductionFixtureTests
{
    private const string FixtureName = "reproduction-v1.json";
    private const float PreviewTolerance = 3.0f / 255.0f;

    [Fact]
    public void Schema_v1_records_the_measured_host_monitor_and_advanced_color_state()
    {
        ReproductionFixture fixture = LoadFixture();

        Assert.Equal(1, fixture.SchemaVersion);
        Assert.Equal("M0a-win11-eizo-null-destination", fixture.FixtureId);

        Assert.Equal("Windows 11 Pro", fixture.Host.Edition);
        Assert.Equal("10.0.26200", fixture.Host.Version);
        Assert.Equal(26200, fixture.Host.Build);

        Assert.Equal("EIZO", fixture.Monitor.Manufacturer);
        Assert.Equal("CG2700X", fixture.Monitor.Model);
        Assert.Equal(@"DISPLAY\ENC3292", fixture.Monitor.DeviceId);
        Assert.Equal("<redacted-before-publication>", fixture.Monitor.SerialNumber);

        AdvancedColorFixture advanced = fixture.AdvancedColor;
        Assert.Equal("measured-on-device", advanced.ProbeStatus);
        Assert.Equal("QueryDisplayConfig / DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2", advanced.ProbeApi);
        Assert.Equal(@"\\.\DISPLAY2", advanced.GdiDeviceName);
        Assert.Equal(
            @"\\?\DISPLAY#ENC3292#5&2ace3e61&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            advanced.MonitorDevicePath);
        Assert.Equal("0x0:10f21", advanced.AdapterLuid);
        Assert.Equal(4357, advanced.TargetId);
        Assert.Equal("0xD7", advanced.AdvancedColorInfo2Raw);
        Assert.True(advanced.AdvancedColorSupported);
        Assert.True(advanced.AdvancedColorActive);
        Assert.True(advanced.HdrSupported);
        Assert.False(advanced.HdrUserEnabled);
        Assert.True(advanced.WideColorSupported);
        Assert.True(advanced.WideColorUserEnabled);
        Assert.Equal("WCG", advanced.ActiveMode);
        Assert.Equal("RGB", advanced.ColorEncoding);
        Assert.Equal(10, advanced.BitsPerColorChannel);
        Assert.Equal(1000, advanced.SdrWhiteLevelRaw);
        Assert.Equal(80.0, advanced.SdrWhiteLevelNits, 6);
        Assert.Equal(advanced.SdrWhiteLevelRaw * 80.0 / 1000.0,
                     advanced.SdrWhiteLevelNits, 6);
    }

    [Fact]
    public void Device_profile_is_hash_identified_but_its_bytes_are_not_distributed()
    {
        ReproductionFixture fixture = LoadFixture();
        MonitorProfileFixture profile = fixture.MonitorProfile;

        Assert.Equal("Adobe RGB", profile.ColorSpace);
        Assert.Equal("CG2700X(<serial-redacted>)07Adobe RGB.icc", profile.FileName);
        Assert.Equal("SHA-256", profile.HashAlgorithm);
        Assert.Matches(new Regex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant), profile.Sha256);
        Assert.Equal(
            "5C021BC82247BA6F897EF4B78DEBF0AC11D685262B02DA97438170790BFBDE2A",
            profile.Sha256);
        Assert.False(profile.BytesIncluded);
        Assert.False(string.IsNullOrWhiteSpace(profile.NotDistributedReason));
        Assert.Equal(profile.FileName, profile.ProfileBytesRelativePath);
        Assert.False(Path.IsPathRooted(profile.ProfileBytesRelativePath));

        string fixtureDirectory = Path.GetDirectoryName(FixturePath())!;
        string profileBytesPath = Path.GetFullPath(
            Path.Combine(fixtureDirectory, profile.ProfileBytesRelativePath));
        Assert.Equal(fixtureDirectory, Path.GetDirectoryName(profileBytesPath));
        Assert.False(File.Exists(profileBytesPath),
            $"device-specific profile bytes must not be distributed with the fixture: {profileBytesPath}");
    }

    [Fact]
    public void Every_recorded_non_srgb_result_exceeds_the_preview_tolerance()
    {
        ReproductionFixture fixture = LoadFixture();
        ReproductionCase reproduction = fixture.Reproduction;

        Assert.Equal("M0a", reproduction.CaseId);
        Assert.Equal("encoded RGB in each source color space", reproduction.PatchEncoding);
        Assert.Equal(new[] { 0.75f, 0.25f, 0.20f }, reproduction.Patch);
        Assert.Equal("sRGB", reproduction.ReferenceDestination);
        Assert.Equal(
            "OutputRender.Decode + OutputRender.Convert(Clip) + OutputRender.Encode",
            reproduction.ReferenceImplementation);
        Assert.Equal(
            "source-tagged Skia RGBA8888 drawn to a null-color-space destination",
            reproduction.CaptureImplementation);
        Assert.Equal(
            "round to nearest RGBA8888 code value, then divide by 255",
            reproduction.ActualQuantization);
        Assert.Equal(3, reproduction.Results.Length);
        Assert.Equal(
            new[] { "DisplayP3", "AdobeRGB", "Rec709" },
            reproduction.Results.Select(result => result.SourceSpace));

        float[] quantizedPatch = reproduction.Patch.Select(QuantizeToRgba8888).ToArray();
        foreach (ReproductionResult result in reproduction.Results)
        {
            Assert.Equal(3, result.ExpectedSrgb.Length);
            Assert.Equal(3, result.NullDestinationActual.Length);

            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(quantizedPatch[channel], result.NullDestinationActual[channel], 7);

            float maximumError = result.ExpectedSrgb
                .Zip(result.NullDestinationActual, (expected, actual) => Math.Abs(expected - actual))
                .Max();
            Assert.True(maximumError > PreviewTolerance,
                $"{result.SourceSpace}: max error {maximumError:R} did not exceed {PreviewTolerance:R}");
        }
    }

    [Fact]
    public void Adobe_rgb_patch_tiff_matches_frozen_metadata_and_embedded_profile()
    {
        PatchTiffFixture expected = LoadFixture().Reproduction.PatchTiff;

        Assert.Equal("adobe-rgb-patches.tif", expected.RelativePath);
        Assert.False(Path.IsPathRooted(expected.RelativePath));
        Assert.Matches(new Regex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant), expected.Sha256);
        Assert.Equal(
            "087F1EC24F5338C3C4CDC92F4736BDD28F572BD297EB7A97F4B4CBFA1258DCF4",
            expected.Sha256);
        Assert.Equal(5, expected.Width);
        Assert.Equal(1, expected.Height);
        Assert.Equal(16, expected.BitsPerSample);
        Assert.Equal(3, expected.SamplesPerPixel);
        Assert.Equal("AdobeRGB", expected.EmbeddedIccColorSpace);
        Assert.Matches(
            new Regex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant),
            expected.EmbeddedIccSha256);
        Assert.Equal(
            "FBD482E5E6D4CE34006581AE4DB88D33D682D6A9A31E930A30CD5D7C1573A406",
            expected.EmbeddedIccSha256);

        string fixtureDirectory = Path.GetDirectoryName(FixturePath())!;
        string tiffPath = Path.GetFullPath(Path.Combine(fixtureDirectory, expected.RelativePath));
        Assert.Equal(fixtureDirectory, Path.GetDirectoryName(tiffPath));
        Assert.True(File.Exists(tiffPath), $"missing reproduction TIFF: {tiffPath}");

        string actualFileHash;
        using (FileStream stream = File.OpenRead(tiffPath))
            actualFileHash = Convert.ToHexString(SHA256.HashData(stream));
        Assert.Equal(expected.Sha256, actualFileHash);

        using Tiff tiff = Assert.IsType<Tiff>(Tiff.Open(tiffPath, "r"));
        Assert.Equal(expected.Width, ReadRequiredIntField(tiff, TiffTag.IMAGEWIDTH));
        Assert.Equal(expected.Height, ReadRequiredIntField(tiff, TiffTag.IMAGELENGTH));
        Assert.Equal(expected.BitsPerSample, ReadRequiredIntField(tiff, TiffTag.BITSPERSAMPLE));
        Assert.Equal(expected.SamplesPerPixel, ReadRequiredIntField(tiff, TiffTag.SAMPLESPERPIXEL));

        FieldValue[]? iccField = tiff.GetField(TiffTag.ICCPROFILE);
        Assert.NotNull(iccField);
        Assert.True(iccField!.Length >= 2, "ICC profile tag must contain a byte count and payload");
        byte[] embeddedIcc = iccField[1].ToByteArray();
        Assert.Equal(iccField[0].ToInt(), embeddedIcc.Length);
        Assert.True(embeddedIcc.Length >= 132, "embedded ICC payload is too short to be a profile");
        Assert.Equal(
            expected.EmbeddedIccSha256,
            Convert.ToHexString(SHA256.HashData(embeddedIcc)));
    }

    [Fact]
    public void Frozen_srgb_references_recompute_through_OutputRender()
    {
        ReproductionCase reproduction = LoadFixture().Reproduction;

        foreach (ReproductionResult result in reproduction.Results)
        {
            ColorSpaceDef source = ColorSpaces.All[result.SourceSpace];
            float[] recomputed = (float[])reproduction.Patch.Clone();
            OutputRender.Decode(recomputed, source);
            OutputRender.Convert(recomputed, source, ColorSpaces.Srgb, GamutMapping.Clip);
            OutputRender.Encode(recomputed, ColorSpaces.Srgb);

            for (int channel = 0; channel < 3; channel++)
            {
                float difference = Math.Abs(recomputed[channel] - result.ExpectedSrgb[channel]);
                Assert.True(difference <= 2.0f / 65535.0f,
                    $"{result.SourceSpace} channel {channel}: fixture={result.ExpectedSrgb[channel]:R}, " +
                    $"OutputRender={recomputed[channel]:R}, difference={difference:R}");
            }
        }
    }

    private static float QuantizeToRgba8888(float value)
        => (byte)Math.Clamp(value * 255.0f + 0.5f, 0.0f, 255.0f) / 255.0f;

    private static int ReadRequiredIntField(Tiff tiff, TiffTag tag)
    {
        FieldValue[]? values = tiff.GetField(tag);
        Assert.NotNull(values);
        Assert.NotEmpty(values!);
        return values![0].ToInt();
    }

    private static ReproductionFixture LoadFixture()
    {
        string path = FixturePath();
        Assert.True(File.Exists(path), $"missing copied fixture: {path}");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        ReproductionFixture? fixture = JsonSerializer.Deserialize<ReproductionFixture>(
            File.ReadAllText(path), options);
        return Assert.IsType<ReproductionFixture>(fixture);
    }

    private static string FixturePath()
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ColorManagement", "win11-eizo",
                        FixtureName);

    private sealed class ReproductionFixture
    {
        public int SchemaVersion { get; set; }
        public string FixtureId { get; set; } = "";
        public HostFixture Host { get; set; } = new();
        public MonitorFixture Monitor { get; set; } = new();
        public MonitorProfileFixture MonitorProfile { get; set; } = new();
        public AdvancedColorFixture AdvancedColor { get; set; } = new();
        public ReproductionCase Reproduction { get; set; } = new();
    }

    private sealed class HostFixture
    {
        public string Edition { get; set; } = "";
        public string Version { get; set; } = "";
        public int Build { get; set; }
    }

    private sealed class MonitorFixture
    {
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string SerialNumber { get; set; } = "";
    }

    private sealed class MonitorProfileFixture
    {
        public string ColorSpace { get; set; } = "";
        public string FileName { get; set; } = "";
        public string HashAlgorithm { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public bool BytesIncluded { get; set; }
        public string ProfileBytesRelativePath { get; set; } = "";
        public string NotDistributedReason { get; set; } = "";
    }

    private sealed class AdvancedColorFixture
    {
        public string ProbeStatus { get; set; } = "";
        public string ProbeApi { get; set; } = "";
        public string GdiDeviceName { get; set; } = "";
        public string MonitorDevicePath { get; set; } = "";
        public string AdapterLuid { get; set; } = "";
        public int TargetId { get; set; }
        public string AdvancedColorInfo2Raw { get; set; } = "";
        public bool AdvancedColorSupported { get; set; }
        public bool AdvancedColorActive { get; set; }
        public bool HdrSupported { get; set; }
        public bool HdrUserEnabled { get; set; }
        public bool WideColorSupported { get; set; }
        public bool WideColorUserEnabled { get; set; }
        public string ActiveMode { get; set; } = "";
        public string ColorEncoding { get; set; } = "";
        public int BitsPerColorChannel { get; set; }
        public int SdrWhiteLevelRaw { get; set; }
        public double SdrWhiteLevelNits { get; set; }
    }

    private sealed class ReproductionCase
    {
        public string CaseId { get; set; } = "";
        public string PatchEncoding { get; set; } = "";
        public float[] Patch { get; set; } = Array.Empty<float>();
        public string ReferenceDestination { get; set; } = "";
        public string ReferenceImplementation { get; set; } = "";
        public string CaptureImplementation { get; set; } = "";
        public string ActualQuantization { get; set; } = "";
        public PatchTiffFixture PatchTiff { get; set; } = new();
        public ReproductionResult[] Results { get; set; } = Array.Empty<ReproductionResult>();
    }

    private sealed class PatchTiffFixture
    {
        public string RelativePath { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerSample { get; set; }
        public int SamplesPerPixel { get; set; }
        public string EmbeddedIccColorSpace { get; set; } = "";
        public string EmbeddedIccSha256 { get; set; } = "";
    }

    private sealed class ReproductionResult
    {
        public string SourceSpace { get; set; } = "";
        public float[] ExpectedSrgb { get; set; } = Array.Empty<float>();
        public float[] NullDestinationActual { get; set; } = Array.Empty<float>();
    }
}
