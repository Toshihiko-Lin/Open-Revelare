using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class BoardCutAnalysisTests
{
    [Fact]
    public async Task Full_resolution_measurement_does_not_run_on_the_calling_thread()
    {
        int caller = Environment.CurrentManagedThreadId;
        int worker = caller;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<double?> pending = BoardCutAnalysis.MeasureAsync(new Func<double?>[]
        {
            () =>
            {
                worker = Environment.CurrentManagedThreadId;
                started.Set();
                release.Wait();
                return 0.7;
            },
        });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotEqual(caller, worker);
            Assert.False(pending.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(0.7, await pending);
    }

    [Fact]
    public async Task Samples_are_measured_once_in_order_and_the_highest_cut_wins()
    {
        var order = new List<int>();
        double? cut = await BoardCutAnalysis.MeasureAsync(new Func<double?>[]
        {
            () => { order.Add(0); return 0.65; },
            () => { order.Add(1); return null; },
            () => { order.Add(2); return 0.82; },
        });
        Assert.Equal(new[] { 0, 1, 2 }, order);
        Assert.Equal(0.82, cut);
    }

    [Fact]
    public async Task No_board_stays_null()
    {
        Assert.Null(await BoardCutAnalysis.MeasureAsync(new Func<double?>[] { () => null, () => null }));
        Assert.Null(await BoardCutAnalysis.MeasureAsync(Array.Empty<Func<double?>>()));
    }

    [Fact]
    public async Task Parallel_measurements_respect_the_requested_worker_limit()
    {
        using var twoStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int active = 0, peak = 0, calls = 0;
        var gate = new object();
        Func<double?> sample = () =>
        {
            lock (gate)
            {
                calls++;
                peak = Math.Max(peak, ++active);
                if (active == 2) twoStarted.Set();
            }
            release.Wait();
            lock (gate) active--;
            return 0.8;
        };
        Task<double?> pending = BoardCutAnalysis.MeasureAsync(
            Enumerable.Repeat(sample, 4).ToArray(), maxWorkers: 2);
        try { Assert.True(twoStarted.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }
        Assert.Equal(0.8, await pending);
        Assert.Equal(2, peak);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Cancellation_does_not_start_the_next_full_decode()
    {
        using var cancellation = new CancellationTokenSource();
        int secondDecodes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoardCutAnalysis.MeasureAsync(
            new Func<double?>[]
            {
                () => { cancellation.Cancel(); return 0.7; },
                () => { secondDecodes++; return 0.8; },
            }, cancellation.Token));
        Assert.Equal(0, secondDecodes);
    }
}
