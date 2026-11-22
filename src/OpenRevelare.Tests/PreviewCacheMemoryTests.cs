using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PreviewCacheMemoryTests
{
    [Fact]
    public void Memory_pressure_evicts_old_previews_but_keeps_a_stable_working_set()
    {
        long? free = 2L << 30;
        var cache = new PreviewCache(10_000, () => free);
        cache.Put("first", Frame(), 10, 10);
        cache.Put("second", Frame(), 10, 10);
        cache.Put("third", Frame(), 10, 10);
        Assert.Equal(3_600, cache.ResidentBytes);

        free = 0;
        Assert.NotNull(cache.Get("third"));
        Assert.Equal(2_400, cache.ResidentBytes);
        Assert.Null(cache.Get("first"));
        Assert.NotNull(cache.Get("second"));
        Assert.NotNull(cache.Get("third"));
        Assert.Equal(2_400, cache.ResidentBytes);
    }

    [Fact]
    public void Cache_hits_reuse_the_recent_pressure_sample()
    {
        int probes = 0;
        var cache = new PreviewCache(10_000, () => { probes++; return 2L << 30; },
            probeIntervalMs: 60_000);
        cache.Put("first", Frame(), 10, 10);

        for (int i = 0; i < 100; i++) Assert.NotNull(cache.Get("first"));

        Assert.Equal(1, probes);
    }

    [Fact]
    public void Current_and_adjacent_previews_survive_background_cache_churn()
    {
        var cache = new PreviewCache(2_400, () => null);
        cache.Protect(["current", "next"]);
        cache.Put("current", Frame(), 10, 10);
        cache.Put("next", Frame(), 10, 10);
        cache.Put("distant-a", Frame(), 10, 10);
        cache.Put("distant-b", Frame(), 10, 10);

        Assert.NotNull(cache.Get("current"));
        Assert.NotNull(cache.Get("next"));
        Assert.Null(cache.Get("distant-a"));
        Assert.Null(cache.Get("distant-b"));
        Assert.Equal(2_400, cache.ResidentBytes);
    }

    [Fact]
    public void Export_trim_releases_distant_previews_without_invalidating_the_generation()
    {
        var cache = new PreviewCache(10_000, () => null);
        cache.Protect(["current"]);
        cache.Put("current", Frame(), 10, 10);
        cache.Put("distant", Frame(), 10, 10);
        long generation = cache.Generation;

        cache.TrimToProtected();

        Assert.Equal(generation, cache.Generation);
        Assert.NotNull(cache.Get("current"));
        Assert.Null(cache.Get("distant"));
        Assert.Equal(1_200, cache.ResidentBytes);
    }

    [Fact]
    public void Roll_tiles_keep_exact_pixels_on_disk_after_memory_eviction()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"tile-{Guid.NewGuid():N}.tif");
        File.WriteAllText(path, "source revision for tile cache");
        using var tiles = new RollTileCache(1_200, () => null);
        SourceStamp stamp = SourceStamp.Capture(path);
        long generation = tiles.Generation;
        var first = Frame();
        first.Pixels.SourceQuantisationStep = 1d / 65535;
        first.Pixels.Data[0] = -0f;
        first.Pixels.Data[1] = 3.125f;
        first.Pixels.Data[2] = -0.3125f;
        tiles.Put("left", first, stamp, generation);
        tiles.Put("right", Frame(), stamp, generation);

        Assert.Equal(1_200, tiles.ResidentBytes);
        Assert.True(tiles.Contains("left"));
        var hot = tiles.Get("right", generation);
        Assert.NotNull(tiles.Get("left", generation, promote: false));
        Assert.Same(hot, tiles.Get("right", generation));
        var restored = Assert.IsType<WorkingFrame>(tiles.Get("left", generation));
        Assert.NotSame(first, restored);
        Assert.Equal(first.Pixels.Data.Select(BitConverter.SingleToInt32Bits),
            restored.Pixels.Data.Select(BitConverter.SingleToInt32Bits));
        Assert.Equal(first.Pixels.SourceQuantisationStep, restored.Pixels.SourceQuantisationStep);
        Assert.Same(first.Source, restored.Source);
        Assert.Equal(stamp, DecodedSources.For(restored));
        Assert.Equal(1_200, tiles.ResidentBytes);

        File.AppendAllText(path, "-changed");
        Assert.Null(tiles.Get("left", generation));
        Assert.Null(tiles.Get("right", generation));
        tiles.Clear();
        tiles.Put("old-roll", first, SourceStamp.Capture(path), generation);
        Assert.False(tiles.Contains("old-roll"));
        Assert.Equal(0, tiles.ResidentBytes);
    }

    [Fact]
    public void Raw_dimensions_and_shared_outputs_determine_memory_admission_under_manual_limit()
    {
        const long mib = 1L << 20;
        long small = ImageIo.EstimateRawDecodeBytes(24_000_000, 20 * mib, false, 16 * mib);
        long large = ImageIo.EstimateRawDecodeBytes(100_000_000, 90 * mib, false, 16 * mib);
        long batch = ImageIo.EstimateRawDecodeBytes(100_000_000, 90 * mib, false, 64 * mib);
        Assert.Equal(48 * mib, batch - large);
        Assert.True(ImageIo.CanShareDecodeSlot(1, 8, small, large, 1024 * mib));
        Assert.False(ImageIo.CanShareDecodeSlot(1, 8, large, large, 1024 * mib));
        Assert.False(ImageIo.CanShareDecodeSlot(1, 1, small, small, 1024 * mib));
        Assert.True(ImageIo.EstimateRawDecodeBytes(100_000_000, 90 * mib, true, 0) > large);
    }

    [Fact]
    public void A_decode_started_before_clear_cannot_restore_the_old_preview()
    {
        var cache = new PreviewCache(10_000, () => null);
        long oldGeneration = cache.Generation;
        cache.Clear();

        Assert.False(cache.PutIfCurrent("old-roll", Frame(), 10, 10, oldGeneration));
        Assert.Null(cache.Get("old-roll"));
        Assert.True(cache.PutIfCurrent("new-roll", Frame(), 10, 10, cache.Generation));
        Assert.NotNull(cache.Get("new-roll"));
    }

    private static WorkingFrame Frame()
    {
        var encoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:preview-cache",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:preview-cache", "cache test", encoding, "generated");
        return new WorkingFrame(
            new ImageBuffer(10, 10),
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }
}
