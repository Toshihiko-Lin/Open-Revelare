using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class InteractiveRenderTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Busy_render_lane_keeps_input_live_coalesces_moves_and_cancels_on_release(bool restart, bool changedSource)
    {
        // A deterministic UI context: the existing preview lane is deliberately busy. Input
        // callbacks must continue to run, and 100 moves must not create 100 waiting renders.
        using var context = new PumpContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var vm = new MainViewModel();
            var pixels = new ImageBuffer(16, 12);
            var encoding = new UncharacterizedPixelEncoding(CaptureKind.Synthetic, "test",
                CompatibilityPolicy.LegacyTreatNumbersAsWorking, TransferState.Unknown, NumericRange.Extended);
            var source = new WorkingFrame(pixels, WorkingSpaceId.LinearAcesCgV1,
                WorkingAdmission.LegacyUncharacterizedPassthrough, new SourceDescriptor("test", "test", encoding, "test"));
            typeof(MainViewModel).GetField("_previewWorking", Flags)!.SetValue(vm, source);
            var lane = (SemaphoreSlim)typeof(MainViewModel).GetField("_previewRenderLane", Flags)!.GetValue(vm)!;
            var request = typeof(MainViewModel).GetMethod("RenderInteractive", Flags)!;
            lane.Wait();
            try
            {
                vm.BeginInteractive();
                if (changedSource)
                {
                    typeof(MainViewModel).GetField("_dragSource", Flags)!.SetValue(vm, source.WithPixels(new ImageBuffer(16, 12)));
                    typeof(MainViewModel).GetField("_dragSmall", Flags)!.SetValue(vm,
                        new ImageBuffer(16, 12, Enumerable.Repeat(1f, 16 * 12 * 3).ToArray()));
                }
                request.Invoke(vm, null);
                Task first = vm.InteractiveRenderTask;
                bool inputRan = false;
                context.Post(_ => inputRan = true, null);
                context.Until(() => inputRan);
                context.Until(() => ReferenceEquals(source, typeof(MainViewModel).GetField("_dragSource", Flags)!.GetValue(vm)));
                var reduced = (ImageBuffer)typeof(MainViewModel).GetField("_dragSmall", Flags)!.GetValue(vm)!;
                Assert.All(reduced.Data, pixel => Assert.Equal(0f, pixel)); // no old frame's downsample
                for (int move = 0; move < 100; move++) request.Invoke(vm, null);
                Assert.Same(first, vm.InteractiveRenderTask);
                Assert.False(first.IsCompleted);
                if (restart)
                {
                    // A second grab can arrive before the previous cancelled pass has drained.
                    vm.BeginInteractive();
                    request.Invoke(vm, null);
                    context.Until(() => first.IsCompleted && !ReferenceEquals(first, vm.InteractiveRenderTask));
                    Assert.False(vm.InteractiveRenderTask.IsCompleted);
                }
                // No Avalonia platform is started in this test. Suppress the separate settling
                // render; release must still cancel the drag even when that render is refused.
                typeof(MainViewModel).GetField("_suppressRender", Flags)!.SetValue(vm, true);
                vm.EndInteractive();
                Task last = vm.InteractiveRenderTask;
                context.Until(() => last.IsCompleted);
                Assert.True(last.IsCompletedSuccessfully, last.Exception?.ToString());
                Assert.False((bool)typeof(MainViewModel).GetField("_interactiveRunning", Flags)!.GetValue(vm)!);
                Assert.False((bool)typeof(MainViewModel).GetField("_interactivePending", Flags)!.GetValue(vm)!);
                Assert.Null(typeof(MainViewModel).GetField("_dragSmall", Flags)!.GetValue(vm));
                Assert.Equal(0, lane.CurrentCount); // cancellation did not release someone else's lane
            }
            finally { lane.Release(); }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Add(() => callback(state));
        internal void Until(Func<bool> condition)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(10), "UI continuation did not complete.");
                if (_callbacks.TryTake(out var callback, 10)) callback();
            }
        }
        public void Dispose() => _callbacks.Dispose();
    }
}
