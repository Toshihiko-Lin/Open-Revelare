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
