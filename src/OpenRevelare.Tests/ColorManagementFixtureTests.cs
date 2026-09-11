using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public class ColorManagementFixtureTests
{
    private const string FixtureName = "built-in-profiles-and-patches.json";

    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ColorManagement");

    private static ColorManagementFixture LoadFixture()
    {
        string path = Path.Combine(FixtureDirectory, FixtureName);
        Assert.True(File.Exists(path), $"missing copied fixture: {path}");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        ColorManagementFixture? fixture = JsonSerializer.Deserialize<ColorManagementFixture>(
            File.ReadAllText(path), options);
        return Assert.IsType<ColorManagementFixture>(fixture);
    }

    [Fact]
    public void Built_in_profiles_match_frozen_hashes_and_metadata()
    {
        ColorManagementFixture fixture = LoadFixture();
        Assert.Equal(1, fixture.SchemaVersion);
        Assert.Equal("SHA-256", fixture.ProfileHashAlgorithm);

        ColorSpaceDef[] spaces =
        {
            ColorSpaces.Srgb,
            ColorSpaces.DisplayP3,
            ColorSpaces.AdobeRgb,
            ColorSpaces.Rec709,
            ColorSpaces.AcesCg,
        };
        Assert.Equal(spaces.Select(s => s.Name), fixture.Profiles.Select(p => p.Name));

        foreach ((ColorSpaceDef space, ProfileFixture expected) in spaces.Zip(fixture.Profiles))
        {
            AssertXy(expected.Red, space.Red, $"{space.Name}.red");
            AssertXy(expected.Green, space.Green, $"{space.Name}.green");
            AssertXy(expected.Blue, space.Blue, $"{space.Name}.blue");
            AssertXy(expected.White, space.White, $"{space.Name}.white");
            Assert.Equal(expected.Transfer, space.Transfer.ToString());
            Assert.Equal(expected.Gamma, space.Gamma);

            string profilePath = Path.Combine(FixtureDirectory, expected.File);
            Assert.True(File.Exists(profilePath),
                $"missing frozen ICC for {space.Name}: {profilePath}");

            byte[] frozenBytes = File.ReadAllBytes(profilePath);
            Assert.True(frozenBytes.Length >= 128,
                $"frozen ICC for {space.Name} is shorter than the 128-byte header: " +
                $"{frozenBytes.Length} bytes");
            Assert.Equal("acsp", Encoding.ASCII.GetString(frozenBytes, 36, 4));

            string actualHash = Convert.ToHexString(SHA256.HashData(frozenBytes))
                .ToLowerInvariant();
            Assert.Equal(expected.IccSha256, actualHash);

            byte[] generatedBytes = IccProfiles.Build(space);
            Assert.Equal(frozenBytes, generatedBytes);
        }
    }

    [Fact]
    public void Canonical_synthetic_patches_are_frozen_and_keep_extended_range()
    {
        ColorManagementFixture fixture = LoadFixture();
        Assert.Equal("linear extended-sRGB D65 RGBA float32", fixture.CanonicalEncoding);

        Dictionary<string, PatchFixture> patches = fixture.SyntheticPatches
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
        Assert.Equal(
            new[] { "neutral-018", "neutral-100", "display-p3-red", "negative-component", "extended-over-one" },
            fixture.SyntheticPatches.Select(p => p.Id));

        Assert.Equal(new[] { 0.18f, 0.18f, 0.18f, 1.0f }, patches["neutral-018"].Rgba);
        Assert.Equal(new[] { 1.0f, 1.0f, 1.0f, 1.0f }, patches["neutral-100"].Rgba);
        Assert.Equal(new[] { -0.125f, 0.25f, 0.5f, 1.0f }, patches["negative-component"].Rgba);
        Assert.Equal(new[] { 1.25f, 0.5f, 0.125f, 1.0f }, patches["extended-over-one"].Rgba);

        double[] converted = ColorSpaces.Apply(
            ColorSpaces.Convert(ColorSpaces.DisplayP3, ColorSpaces.Srgb),
            new[] { 1.0, 0.0, 0.0 });
        float[] actualP3Red = { (float)converted[0], (float)converted[1], (float)converted[2], 1.0f };
        float[] frozenP3Red = patches["display-p3-red"].Rgba;
        Assert.True(frozenP3Red.Length == 4,
            $"display-p3-red needs four RGBA components, got {frozenP3Red.Length}");
        var patchMismatches = new List<string>();
        for (int i = 0; i < 4; i++)
            if (Math.Abs(frozenP3Red[i] - actualP3Red[i]) > 1e-7f)
                patchMismatches.Add(
                    $"display-p3-red[{i}]: fixture={frozenP3Red[i]:R}, actual={actualP3Red[i]:R}");
        Assert.True(patchMismatches.Count == 0, string.Join("\n", patchMismatches));

        Assert.Contains(fixture.SyntheticPatches, p => p.Rgba.Take(3).Any(v => v < 0.0f));
        Assert.Contains(fixture.SyntheticPatches, p => p.Rgba.Take(3).Any(v => v > 1.0f));
    }

    private static void AssertXy(double[] actual, (double X, double Y) expected, string label)
    {
        Assert.True(actual.Length == 2, $"{label} needs two xy components, got {actual.Length}");
        Assert.Equal(expected.X, actual[0]);
        Assert.Equal(expected.Y, actual[1]);
    }

    private sealed class ColorManagementFixture
    {
        public int SchemaVersion { get; set; }
        public string ProfileHashAlgorithm { get; set; } = "";
        public ProfileFixture[] Profiles { get; set; } = Array.Empty<ProfileFixture>();
        public string CanonicalEncoding { get; set; } = "";
        public PatchFixture[] SyntheticPatches { get; set; } = Array.Empty<PatchFixture>();
    }

    private sealed class ProfileFixture
    {
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public string IccSha256 { get; set; } = "";
        public double[] Red { get; set; } = Array.Empty<double>();
        public double[] Green { get; set; } = Array.Empty<double>();
        public double[] Blue { get; set; } = Array.Empty<double>();
        public double[] White { get; set; } = Array.Empty<double>();
        public string Transfer { get; set; } = "";
        public double Gamma { get; set; }
    }

    private sealed class PatchFixture
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public float[] Rgba { get; set; } = Array.Empty<float>();
    }
}
