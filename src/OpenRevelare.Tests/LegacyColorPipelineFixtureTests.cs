using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Characterisation fixtures for the two legacy spellings of the Stage-2 route: projects written
/// before <c>display_referred_stage2</c> existed omit it, while later legacy projects store an
/// explicit false. M0 freezes both spellings before a migration policy is selected.
/// </summary>
public class LegacyColorPipelineFixtureTests
{
    private const string Missing = "missing-display-referred-stage2.ncproj";
    private const string ExplicitFalse = "explicit-false-display-referred-stage2.ncproj";

    // SHA-256 over the rendered RGB after the same [0,1] -> 8-bit quantisation used by the
    // preview. This is stable across insignificant float/FMA differences while still pinning every
    // output pixel. Filled from the M0 baseline after the fixtures are first rendered.
    private const string NoLutSrgbSha256 =
        "50bb7374af4c76bd8f761aa6197dfcd0f3790e77959f7219d64c526497d4328c";
    private const string KodakAdobeSha256 =
        "d9d1fc8211895d7148cfce0f76b72415383b5325df2c8d8cd21880f757ec7630";

    [Fact]
    public void Fixtures_distinguish_an_absent_flag_from_an_explicit_false()
    {
        JsonArray missingFrames = ReadRawFrames(Missing);
        JsonArray explicitFrames = ReadRawFrames(ExplicitFalse);

        Assert.All(missingFrames, frame =>
            Assert.Null(frame!["display_referred_stage2"]));
        Assert.All(explicitFrames, frame =>
            Assert.False(frame!["display_referred_stage2"]!.GetValue<bool>()));
    }

    [Theory]
    [InlineData(Missing)]
    [InlineData(ExplicitFalse)]
    public void Project_load_routes_both_legacy_spellings_to_false(string fixture)
    {
        Project.Data project = Project.Load(FixturePath(fixture));

        Assert.False(project.NeedsRecalibration);
        Assert.Collection(project.Frames,
            noLut => AssertFrame(noLut, displayReferred: false, "sRGB", ""),
            kodak => AssertFrame(kodak, displayReferred: false, "AdobeRGB", ":kodak-2383"));
    }

    [Theory]
    [InlineData(Missing)]
    [InlineData(ExplicitFalse)]
    public void Legacy_render_pixels_match_the_frozen_preview_byte_hashes(string fixture)
    {
        Project.Data project = Project.Load(FixturePath(fixture));

        string actualNoLut = RenderHash(project.Frames[0].Params);
        string actualKodak = RenderHash(project.Frames[1].Params);
        var mismatches = new List<string>();
        if (!string.Equals(NoLutSrgbSha256, actualNoLut, StringComparison.Ordinal))
            mismatches.Add($"no-LUT sRGB: expected={NoLutSrgbSha256}, actual={actualNoLut}");
        if (!string.Equals(KodakAdobeSha256, actualKodak, StringComparison.Ordinal))
            mismatches.Add($"Kodak 2383 AdobeRGB: expected={KodakAdobeSha256}, actual={actualKodak}");

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static void AssertFrame(Project.Frame frame, bool displayReferred,
                                    string outputSpace, string printLut)
    {
        Assert.Equal(displayReferred, frame.Params.DisplayReferredStage2);
        Assert.Equal(outputSpace, frame.Params.OutputSpace);
        Assert.Equal(printLut, frame.Params.PrintLut);
    }

    private static string RenderHash(FrameParams parameters)
    {
        ImageBuffer rendered = Pipeline.ProcessFrame(MakeSyntheticNegative(), parameters);
        byte[] previewBytes = new byte[rendered.Data.Length];
        for (int i = 0; i < rendered.Data.Length; i++)
        {
            float scaled = rendered.Data[i] * 255.0f + 0.5f;
            previewBytes[i] = scaled <= 0.0f ? (byte)0
                : scaled >= 255.0f ? (byte)255
                : (byte)scaled;
        }
        return Convert.ToHexString(SHA256.HashData(previewBytes)).ToLowerInvariant();
    }

    /// <summary>A small orange-mask negative with shadows, midtones, highlights and board leak.</summary>
    private static ImageBuffer MakeSyntheticNegative() => new(4, 2, new[]
    {
        0.81f,  0.52f,  0.29f,
        0.63f,  0.31f,  0.12f,
        0.42f,  0.17f,  0.052f,
        0.24f,  0.075f, 0.015f,
        0.13f,  0.035f, 0.0035f,
        0.055f, 0.010f, 0.0008f,
        1.15f,  1.05f,  0.95f,
        0.008f, 0.0015f, 0.00012f,
    });

    private static JsonArray ReadRawFrames(string fixture)
        => JsonNode.Parse(File.ReadAllText(FixturePath(fixture)))!["frames"]!.AsArray();

    private static string FixturePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ColorManagement", "legacy", name);
}
