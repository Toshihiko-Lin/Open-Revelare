using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void Replaces_the_target_once_a_transient_lock_is_released()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"atomic-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "old");
        // Somebody (a scanner, an indexer) has the target open with no sharing for a moment.
        using var held = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            held.Set();
            Thread.Sleep(150);
        });
        held.Wait();

        AtomicFile.WriteAllText(path, "new");   // must outlast the 150 ms lock and land

        holder.Wait();
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Gives_up_on_a_lock_that_does_not_clear()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"atomic-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "old");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => AtomicFile.WriteAllText(path, "new"));
        Assert.Equal("old", File.ReadAllText(path));
    }

    [Fact]
    public void Creates_a_missing_target()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"atomic-{Guid.NewGuid():N}.txt");
        AtomicFile.WriteAllText(path, "first");
        Assert.Equal("first", File.ReadAllText(path));
    }
}
