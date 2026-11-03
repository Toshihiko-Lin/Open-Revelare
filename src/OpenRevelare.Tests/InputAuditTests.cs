using System.Text.Json;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class InputAuditTests
{
    [Fact]
    public async Task Original_tiff_evidence_repeatability_and_exact_comparison_need_no_chart()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"audit-{Guid.NewGuid():N}.tif");
        try
        {
            var image = new ImageBuffer(97, 63);
            for (int i = 0; i < image.Data.Length; i++) image.Data[i] = (i % 173) / 172f;
            TiffIO.ExportTiff16(image, path, TiffIO.CompressionMode.Deflate, ColorSpaces.Srgb);
            var report = await InputAudit.RunAsync(new[] { path }, new(Repeats: 2, PreviewEdge: 37, CompareEdge: 19, CompareFull: true));
            var file = Assert.Single(report.Files);
            Assert.Null(file.Error); Assert.False(file.IsRaw); Assert.True(file.TiffCharacterized);
            Assert.True(file.RepeatPixelsMatch); Assert.Equal(2, file.Runs.Length);
            Assert.Equal((97, 63), (file.Width, file.Height));
            Assert.NotNull(file.FullComparison);
            Assert.True(file.FullComparison.SameDimensions);
            Assert.InRange(file.FullComparison.PixelRmse!.Value, 0, 2e-4);
            Assert.Contains("no absolute colour-error claim", report.Limitation);
            Assert.NotEmpty(JsonSerializer.Serialize(report));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Raw_audit_records_native_levels_cache_and_phase_timings_and_isolates_bad_sources()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"audit-{Guid.NewGuid():N}.dng");
        try
        {
            LibRawRuntimeTests.WriteBayerDng(path, width: 256, height: 192, textured: true, blackLevel: 512);
            RawDecode.ClearSourceCache();
            var report = await InputAudit.RunAsync(new[] { path, path + ".missing" },
                new(Repeats: 2, PreviewEdge: 80, CompareEdge: 40, CompareFull: true));
            var file = report.Files[0];
            Assert.Null(file.Error); Assert.True(file.IsRaw); Assert.True(file.RepeatPixelsMatch);
            Assert.NotNull(file.RawMetadata); Assert.NotNull(file.RawMetadata.Levels);
            Assert.Contains("cpu=portable", file.RawMetadata.NativeBuild);
            Assert.Contains(file.Runs[0].Phases, p => p.Name == "raw.unpack");
            Assert.Contains(file.Runs[0].Phases, p => p.Name == "raw.levels" && p.Tags.ContainsKey("raw.source-step"));
            Assert.Contains(file.Runs[1].Phases, p => p.Name == "raw.read" && p.Tags.ContainsKey("raw.source-cache"));
            Assert.NotNull(file.FullComparison); Assert.NotNull(report.Files[1].Error);
            Assert.NotEmpty(JsonSerializer.Serialize(report));
        }
        finally { RawDecode.ClearSourceCache(); File.Delete(path); }
    }

    [Fact]
    public void Sample_statistics_do_not_confuse_extended_values_with_clipping_or_assume_neutrality()
    {
        var stats = InputAudit.Statistics(new ImageBuffer(2, 1, new[] { -0.1f, 0.2f, 2f, float.NaN, 0.4f, float.PositiveInfinity }));
        Assert.Equal(2, stats.NonFiniteSamples); Assert.Equal(1, stats.AtOrBelowZeroSamples); Assert.Equal(1, stats.AtOrAboveOneSamples);
        Assert.Equal(2, stats.Mean[2]);
    }
}
