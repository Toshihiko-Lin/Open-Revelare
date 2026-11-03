using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class RawSourceCacheTests
{
    [Fact]
    public void Cache_reuses_only_the_same_revision_and_releases_on_pressure()
    {
        string path = FilePath();
        try
        {
            long free = 2L << 30;
            var cache = new RawSourceCache(32, () => free);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            byte[] first = cache.Read(path, File.ReadAllBytes, out string miss);
            Assert.Equal("miss", miss);
            Assert.Same(first, cache.Read(path, File.ReadAllBytes, out string hit));
            Assert.Equal("hit", hit);
            File.WriteAllBytes(path, new byte[] { 4, 5, 6 });
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            byte[] replacement = cache.Read(path, File.ReadAllBytes, out _);
            Assert.NotSame(first, replacement);
            Assert.Equal(new byte[] { 4, 5, 6 }, replacement);
            free = 0;
            cache.Read(path, File.ReadAllBytes, out _);
            Assert.Equal(0, cache.Snapshot().ResidentBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Concurrent_readers_share_bytes_and_failed_reads_do_not_poison_retries()
    {
        string path = FilePath();
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var cache = new RawSourceCache(32, () => null);
            Assert.Throws<IOException>(() => cache.Read(path, _ => throw new IOException("read failed"), out _));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int reads = 0;
            byte[] Load(string file)
            {
                Interlocked.Increment(ref reads); entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return File.ReadAllBytes(file);
            }
            var tasks = Enumerable.Range(0, 4).Select(jobIndex => Task.Factory.StartNew(
                () => cache.Read(path, Load, out _), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            release.Set();
            byte[][] values = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, reads);
            Assert.All(values, value => Assert.Same(values[0], value));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Clear_during_read_prevents_repopulation_and_oversized_files_are_not_retained()
    {
        string path = FilePath();
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var cache = new RawSourceCache(32, () => null);
            cache.Read(path, file => { cache.Clear(); return File.ReadAllBytes(file); }, out _);
            Assert.Equal(0, cache.Snapshot().Entries);
            var tiny = new RawSourceCache(2, () => null);
            Assert.Equal(3, tiny.Read(path, File.ReadAllBytes, out _).Length);
            Assert.Equal(0, tiny.Snapshot().Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Byte_budget_evicts_least_recently_used_and_rejects_mid_read_replacements()
    {
        string a = FilePath(), b = FilePath();
        try
        {
            File.WriteAllBytes(a, new byte[] { 1, 2, 3 }); File.WriteAllBytes(b, new byte[] { 4, 5, 6 });
            var cache = new RawSourceCache(4, () => null);
            cache.Read(a, File.ReadAllBytes, out _); cache.Read(b, File.ReadAllBytes, out _);
            Assert.Equal(1, cache.Snapshot().Entries);
            cache.Read(a, File.ReadAllBytes, out string status);
            Assert.Equal("miss", status);
            cache.Clear();
            Assert.Throws<IOException>(() => cache.Read(a, file =>
            {
                var bytes = File.ReadAllBytes(file);
                File.WriteAllBytes(file, new byte[] { 9, 9, 9, 9 });
                return bytes;
            }, out _));
            Assert.Equal(0, cache.Snapshot().Entries);
        }
        finally { File.Delete(a); File.Delete(b); }
    }

    private static string FilePath() => Path.Combine(TestDataIsolation.Root, $"raw-cache-{Guid.NewGuid():N}.raw");
}
