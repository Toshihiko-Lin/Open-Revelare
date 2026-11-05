using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

[CollectionDefinition("NativeRawScheduling", DisableParallelization = true)]
public sealed class NativeRawSchedulingCollection { }

[Collection("NativeRawScheduling")]
public sealed class NativeRawSchedulingTests
{
    [Theory]
    [InlineData(1032, 778, RawDecode.FbddMode.Off)]
    [InlineData(2056, 1552, RawDecode.FbddMode.Off)]
    [InlineData(1032, 778, RawDecode.FbddMode.Light)]
    public void Parallel_ahd_matches_upstream_serial_pixels_across_tile_boundaries(
        int width, int height, RawDecode.FbddMode fbdd)
    {
        string path = Path.Combine(TestDataIsolation.Root, $"native-scheduling-{Guid.NewGuid():N}.dng");
        string? previous = Environment.GetEnvironmentVariable("OPENREVELARE_RAW_PROCESS_THREADS");
        try
        {
            LibRawRuntimeTests.WriteBayerDng(path, width, height, textured: true);
            Environment.SetEnvironmentVariable("OPENREVELARE_RAW_PROCESS_THREADS", "1");
            ImageBuffer expected = RawDecode.DecodeRaw(path, fbdd);
            Environment.SetEnvironmentVariable("OPENREVELARE_RAW_PROCESS_THREADS", "4");
            ImageBuffer actual = RawDecode.DecodeRaw(path, fbdd);
            Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
            Assert.Equal(expected.Data, actual.Data);
            Assert.Equal(expected.SourceQuantisationStep, actual.SourceQuantisationStep);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENREVELARE_RAW_PROCESS_THREADS", previous);
            File.Delete(path);
        }
    }
}
