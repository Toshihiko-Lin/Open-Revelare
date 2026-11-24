using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PhysicalMemoryTests
{
    [Theory]
    [InlineData(true, 1_342_177_280L)]
    [InlineData(false, 268_435_456L)]
    public void MacOS_estimate_counts_half_the_file_cache_when_available(bool hasFileCache, long expectedBytes)
    {
        var counters = new Dictionary<string, ulong>
        {
            ["hw.pagesize"] = 16_384,
            ["vm.page_free_count"] = 12_288,
            ["vm.page_speculative_count"] = 2_048,
            ["vm.page_purgeable_count"] = 4_096,
        };
        if (hasFileCache)
            counters["vm.page_pageable_external_count"] = 131_072;

        Assert.True(PhysicalMemory.TryMacOS(counters.TryGetValue, out long availableBytes));
        Assert.Equal(expectedBytes, availableBytes);
    }
}
