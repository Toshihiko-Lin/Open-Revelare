using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="RollAutoSave.FlushAsync"/> means "on disk when it returns". Every caller replaces
/// the roll's in-memory state right after it, so a flush that returns early is a data loss.
/// </summary>
public sealed class RollAutoSaveTests
{
    [Fact]
    public async Task Flush_waits_for_the_write_in_flight_and_writes_again_for_edits_made_meanwhile()
    {
        var gate = new TaskCompletionSource();
        int writes = 0;
        RollAutoSave? save = null;
        save = new RollAutoSave(async () =>
        {
            writes++;
            if (writes == 1)
            {
                // An edit lands while the first write is in flight.
                save!.MarkDirty();
                await gate.Task;
            }
        });

        save.MarkDirty();
        Task<bool> first = save.FlushAsync();          // starts write #1, which is now blocked
        Assert.False(first.IsCompleted);
        Task<bool> second = save.FlushAsync();         // must NOT return while #1 is running
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(2, writes);                       // the mid-write edit got its own write
        Assert.False(save.IsDirty);
    }

    [Fact]
    public async Task Flush_reports_a_failed_write_and_stays_dirty()
    {
        var save = new RollAutoSave(() => throw new IOException("locked"));
        save.MarkDirty();
        Assert.False(await save.FlushAsync());
        Assert.True(save.IsDirty);
    }

    [Fact]
    public async Task Flush_with_nothing_pending_is_a_no_op()
    {
        int writes = 0;
        var save = new RollAutoSave(() => { writes++; return Task.CompletedTask; });
        Assert.True(await save.FlushAsync());
        Assert.Equal(0, writes);
    }

    /// <summary>A write that completes synchronously must not leave a finished task behind for
    /// the next flush to wait on — that wait never yields, and the UI thread spins forever.</summary>
    [Fact]
    public async Task A_synchronous_write_does_not_hang_the_next_flush()
    {
        int writes = 0;
        var save = new RollAutoSave(() => { writes++; return Task.CompletedTask; });
        save.MarkDirty();
        Assert.True(await save.FlushAsync());
        Task<bool> again = save.FlushAsync();
        Assert.True(await again.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, writes);
    }
}
