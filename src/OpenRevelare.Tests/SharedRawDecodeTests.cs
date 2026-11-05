using System.Diagnostics;
using System.Reflection;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class SharedRawDecodeTests
{
    [Theory]
    [InlineData(RawDecode.FbddMode.Off)]
    [InlineData(RawDecode.FbddMode.Light)]
    public void One_unpack_preserves_independent_full_and_preview_pixels_crop_and_precision(RawDecode.FbddMode fbdd)
    {
        string path = Path.Combine(TestDataIsolation.Root, $"shared-raw-{Guid.NewGuid():N}.dng");
        try
        {
            LibRawRuntimeTests.WriteBayerDng(path, 1032, 778, textured: true, blackLevel: 17);
            using var root = new Activity("shared-raw-test").SetIdFormat(ActivityIdFormat.W3C).Start();
            int unpacks = 0;
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "OpenRevelare.RawDecode",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = a => { if (a.TraceId == root.TraceId && a.OperationName == "raw.unpack") Interlocked.Increment(ref unpacks); },
            };
            ActivitySource.AddActivityListener(listener);
            var actual = RawDecode.DecodeRawWithPreview(path, RawDecode.RawBackend.LibRaw, fbdd, 170);
            Assert.Equal(1, unpacks);
            var full = RawDecode.DecodeRaw(path, fbdd);
            var preview = RawDecode.DecodeRawDownsampled(path, fbdd, new[] { 170 }, RawDecode.Demosaic.Preview);
            Assert.Equal(full.Data, actual.Full.Data);
            Assert.Equal(preview.Previews[0].Data, actual.Preview.Data);
            Assert.Equal((full.Width, full.Height), (actual.SourceWidth, actual.SourceHeight));
            Assert.Equal((preview.SourceWidth, preview.SourceHeight), (actual.SourceWidth, actual.SourceHeight));
            Assert.Equal(full.SourceQuantisationStep, actual.Full.SourceQuantisationStep);
            Assert.Equal(preview.Previews[0].SourceQuantisationStep, actual.Preview.SourceQuantisationStep);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Already_demosaiced_linear_dng_keeps_both_independent_recipes()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"shared-linear-{Guid.NewGuid():N}.dng");
        try
        {
            var pixels = new ImageBuffer(97, 73);
            for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = (i * 97 % 65536) / 65535f;
            LinearDng.Write(pixels, ColorSpaces.Srgb, path);
            var actual = RawDecode.DecodeRawWithPreview(path, RawDecode.RawBackend.LibRaw, RawDecode.FbddMode.Off, 31);
            Assert.Equal(RawDecode.DecodeRaw(path).Data, actual.Full.Data);
            Assert.Equal(RawDecode.DecodeRawDownsampled(path, RawDecode.FbddMode.Off, new[] { 31 }, RawDecode.Demosaic.Preview).Previews[0].Data,
                actual.Preview.Data);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Foreground_preview_joins_the_board_unpack_and_later_hits_its_cache()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"shared-foreground-{Guid.NewGuid():N}.dng");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fullStarted = new ManualResetEventSlim();
        using var releaseFull = new ManualResetEventSlim();
        try
        {
            LibRawRuntimeTests.WriteBayerDng(path, 1032, 778, textured: true);
            using var engine = new LittleCmsEngine();
            using var vm = new MainViewModel(() => 4L << 30);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var cache = (PreviewCache)typeof(MainViewModel).GetField("_previews", flags)!.GetValue(vm)!;
            using var root = new Activity("shared-foreground-test").SetIdFormat(ActivityIdFormat.W3C).Start();
            int unpacks = 0;
            using var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "OpenRevelare.RawDecode",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = a =>
                {
                    if (a.TraceId != root.TraceId) return;
                    if (a.OperationName == "raw.process.preview")
                    {
                        started.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Preview was not released.");
                    }
                    if (a.OperationName == "raw.process.full")
                    {
                        fullStarted.Set();
                        if (!releaseFull.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Full image was not released.");
                    }
                },
                ActivityStopped = a => { if (a.TraceId == root.TraceId && a.OperationName == "raw.unpack") Interlocked.Increment(ref unpacks); },
            };
            ActivitySource.AddActivityListener(listener);
            var load = typeof(MainViewModel).GetMethod("LoadBoardRawWithPreview", flags)!;
            var board = Task.Run(() => (ImageBuffer)load.Invoke(vm, new object[]
                { path, ColorPipelineVersion.ManagedV2, engine, TiffInputAssumption.Unspecified, SourceStamp.Capture(path), cache.Generation, null! })!);
            Task<PreviewCache.Entry> Request() => (Task<PreviewCache.Entry>)typeof(MainViewModel).GetMethod("PreviewAsync", flags,
                new[] { typeof(string), typeof((double, double, double, double)?), typeof(ColorPipelineVersion), typeof(TiffInputAssumption) })!
                .Invoke(vm, new object?[] { path, null, ColorPipelineVersion.ManagedV2, TiffInputAssumption.Unspecified })!;
            Task<PreviewCache.Entry> foreground;
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
                foreground = Request();
                Assert.False(foreground.IsCompleted);
            }
            finally { release.Set(); }
            try
            {
                Assert.True(fullStarted.Wait(TimeSpan.FromSeconds(10)));
                var entry = await foreground.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Same(entry.Working, (await Request()).Working);
                Assert.False(board.IsCompleted); // Preview must not wait for full AHD.
            }
            finally { releaseFull.Set(); }
            await board.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, unpacks);
        }
        finally { release.Set(); releaseFull.Set(); File.Delete(path); }
    }
}
