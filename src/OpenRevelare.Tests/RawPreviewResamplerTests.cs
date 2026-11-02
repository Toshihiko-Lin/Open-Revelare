using System.Runtime.InteropServices;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class RawPreviewResamplerTests
{
    [Fact]
    public void Invalid_preview_requests_fail_before_opening_the_raw_file()
    {
        const string missing = "missing-raw-preview-file.dng";
        Assert.Throws<ArgumentNullException>(() => RawDecode.DecodeRawDownsampled(missing, RawDecode.FbddMode.Off, null!));
        Assert.Throws<ArgumentException>(() => RawDecode.DecodeRawDownsampled(missing, RawDecode.FbddMode.Off, Array.Empty<int>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => RawDecode.DecodeRawDownsampled(missing, RawDecode.FbddMode.Off, new[] { 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RawDecode.DecodeRawDownsampled(missing, RawDecode.FbddMode.Off, new[] { -1 }));
    }

    [Theory]
    [InlineData(1, 47, 35)]
    [InlineData(3, 47, 35)]
    [InlineData(4, 47, 35)]
    [InlineData(1, 1603, 257)]
    [InlineData(3, 1603, 257)]
    [InlineData(4, 1603, 257)]
    public void Shared_previews_equal_full_conversion_then_box_for_odd_sizes_and_channel_layouts(int channels, int width, int height)
    {
        var samples = new ushort[width * height * channels];
        new Random(42).NextBytes(MemoryMarshal.AsBytes(samples.AsSpan()));
        var full = new ImageBuffer(width, height) { SourceQuantisationStep = 1d / 4095 };
        const float scale = 1f / 65535f;
        for (int p = 0; p < width * height; p++)
        for (int c = 0; c < 3; c++)
            full.Data[p * 3 + c] = samples[p * channels + (channels == 1 ? 0 : c)] * scale;
        int[] edges = { width, (width + 1) / 2, (width + 2) / 3, (width + 4) / 5, (width + 1) / 2 };
        int[] factors = edges.Select(edge => Resample.BoxFactor(width, height, edge)).ToArray();
        var actual = RawPreviewResampler.TryBoxMany(() => samples, width, height, channels, factors, full.SourceQuantisationStep);
        if (channels != 3 || !System.Numerics.Vector.IsHardwareAccelerated)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        for (int k = 0; k < edges.Length; k++)
        {
            ImageBuffer expected = Resample.Box(full, edges[k]);
            Assert.Equal((expected.Width, expected.Height), (actual[k].Width, actual[k].Height));
            Assert.True(MemoryMarshal.AsBytes(expected.Data.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(actual[k].Data.AsSpan())));
            Assert.Equal(full.SourceQuantisationStep, actual[k].SourceQuantisationStep);
        }
        Assert.NotSame(actual[1].Data, actual[^1].Data);
        Assert.Equal(actual[1].Data, actual[^1].Data);
    }

    [Fact]
    public void Incompatible_box_grids_keep_the_independent_parallel_path_without_reading_source()
    {
        var result = RawPreviewResampler.TryBoxMany(() => throw new InvalidOperationException("must not read"),
            103, 101, 3, new[] { 11, 13 }, 0);
        Assert.Null(result);
    }
}
