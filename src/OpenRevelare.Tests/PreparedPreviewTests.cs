using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;
using OpenRevelare.Gui.ViewModels;
using System.Reflection;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PreparedPreviewTests
{
    [Theory]
    [InlineData(2L << 30, true)]
    [InlineData(0L, false)]
    public async Task Idle_warmup_respects_memory_and_uses_resident_neighbour_parameters(long availableBytes, bool expectedWarmup)
    {
        string root = Path.Combine(TestDataIsolation.Root, "warm-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] paths = Enumerable.Range(0, 3).Select(i => Path.Combine(root, $"f{i}.tif")).ToArray();
        foreach (string path in paths) SourceRevisionTests.WriteTiff(path, 120);
        try
        {
            // Exercise warmup independently of the runner's current free memory.
            using var vm = new MainViewModel(() => availableBytes);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (string path in paths) vm.Frames.Add(new RollFrame(path));
            typeof(MainViewModel).GetField("_currentFrame", flags)!.SetValue(vm, vm.Frames[0]);
            typeof(MainViewModel).GetMethod("LoadParams", flags)!.Invoke(vm, new object[] { vm.Frames[0].Params });
            typeof(MainViewModel).GetField("_paramsLoaded", flags)!.SetValue(vm, true);
            var previewMethod = typeof(MainViewModel).GetMethod("PreviewAsync", flags,
                new[] { typeof(string), typeof((double X, double Y, double W, double H)?) })!;
            var entry = await (Task<PreviewCache.Entry>)previewMethod.Invoke(vm, new object?[] { paths[1], null })!;
            var keyMethod = typeof(MainViewModel).GetMethod("PreviewKey", flags,
                new[] { typeof(string), typeof((double X, double Y, double W, double H)?) })!;
            string region = (string)keyMethod.Invoke(vm, new object?[] { paths[1], null })!;
            var p = vm.Frames[1].Params.Clone();
            var currentControls = (FrameParams)typeof(MainViewModel).GetMethod("BuildParams", flags)!.Invoke(vm, null)!;
            Assert.Equal(PreviewParametersKey.Create(p), PreviewParametersKey.Create(currentControls));
            var key = (PreparedPreviewKey)typeof(MainViewModel).GetMethod("PreparedKey", flags)!
                .Invoke(vm, new object?[] { vm.Frames[1], entry.Source, region, p })!;
            var cache = (PreparedPreviewCache)typeof(MainViewModel).GetField("_preparedPreviews", flags)!.GetValue(vm)!;
            typeof(MainViewModel).GetMethod("SchedulePreviewWarmup", flags)!.Invoke(vm, null);
            await vm.PreviewWarmupTask.WaitAsync(TimeSpan.FromSeconds(30));
            CachedPreviewData? hit = cache.Get(key);
            if (!expectedWarmup)
            {
                Assert.Null(hit);
                Assert.Equal(0, cache.ResidentBytes);
                return;
            }
            Assert.NotNull(hit);
            using var cmm = new LittleCmsEngine();
            var fresh = Pipeline.Render(entry.Working, p, key.Pipeline, cmm);
            Assert.Equal(fresh.Pixels.Data, hit.Rendered.Pixels.Data);
            Assert.Equal(CanonicalPreviewConverter.Convert(fresh, cmm, 1).LinearExtendedSrgbRgba.ToArray(),
                hit.Scene.LinearExtendedSrgbRgba.ToArray());
            // The other unvisited source is never decoded speculatively.
            var decoded = (PreviewCache)typeof(MainViewModel).GetField("_previews", flags)!.GetValue(vm)!;
            string distant = (string)keyMethod.Invoke(vm, new object?[] { paths[2], null })!;
            Assert.Null(decoded.Get(distant));
        }
        finally
        {
            foreach (string path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void Parameters_freeze_curves_geometry_matrices_dust_and_field_identity()
    {
        var p = new FrameParams { CurvePointsM = new() { (0, 0), (1, 1) }, DecoupleMatrix = new double[3, 3] };
        string original = PreviewParametersKey.Create(p);
        Assert.Equal(original, PreviewParametersKey.Create(p.Clone()));
        p.CurvePointsM[1] = (1, 0.7);
        Assert.NotEqual(original, PreviewParametersKey.Create(p));
        string curved = PreviewParametersKey.Create(p);
        p.DecoupleMatrix[1, 2] = 0.2;
        Assert.NotEqual(curved, PreviewParametersKey.Create(p));
        string matrix = PreviewParametersKey.Create(p);
        p.CropRect = (0.1, 0.2, 0.5, 0.6);
        Assert.NotEqual(matrix, PreviewParametersKey.Create(p));
        string cropped = PreviewParametersKey.Create(p);
        p.DustSpots.Add(new DustSpot(0.3, 0.4, 0.01));
        Assert.NotEqual(cropped, PreviewParametersKey.Create(p));
        p.LccFlatField = new ImageBuffer(2, 2);
        string flat = PreviewParametersKey.Create(p);
        Assert.Equal(flat, PreviewParametersKey.Create(p.Clone()));
        p.LccFlatField = new ImageBuffer(2, 2);
        Assert.NotEqual(flat, PreviewParametersKey.Create(p));
    }

    [Fact]
    public void Prepared_cache_is_bounded_and_rejects_old_generation_changed_source_and_pressure()
    {
        string file = Path.Combine(TestDataIsolation.Root, "prepared-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "source");
        try
        {
            var stamp = SourceStamp.Capture(file);
            var key = new PreparedPreviewKey(new RollFrame(file), stamp, "whole", "params", ColorPipelineVersion.LegacyV1, null);
            var data = Data();
            long? free = 2L << 30;
            var cache = new PreparedPreviewCache(data.Bytes * 2, 2, () => free);
            Assert.True(cache.Put(key, data, cache.Generation));
            Assert.Same(data, cache.Get(key));
            var next = key with { Frame = new RollFrame(file) };
            var third = key with { Frame = new RollFrame(file) };
            Assert.True(cache.Put(next, data, cache.Generation));
            Assert.True(cache.Put(third, data, cache.Generation));
            Assert.Null(cache.Get(key));
            Assert.Equal(data.Bytes * 2, cache.ResidentBytes);
            Assert.Null(cache.Get(next with { Parameters = "changed" }));
            long generation = cache.Generation;
            cache.Clear();
            Assert.False(cache.Put(key, data, generation));
            Assert.True(cache.Put(key, data, cache.Generation));
            free = 0;
            Assert.Null(cache.Get(key));
            Assert.Equal(0, cache.ResidentBytes);
            Assert.False(cache.Put(key, data, cache.Generation));
            free = 2L << 30;
            File.AppendAllText(file, "replacement");
            Assert.False(cache.Put(key, data, cache.Generation));
        }
        finally { File.Delete(file); }
    }

    private static CachedPreviewData Data()
    {
        using var cmm = new LittleCmsEngine();
        var pixels = new ImageBuffer(2, 2);
        var encoding = new UncharacterizedPixelEncoding(CaptureKind.Synthetic, "test", CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown, NumericRange.Extended);
        var source = new SourceDescriptor("test", "test", encoding, "test");
        var frame = new WorkingFrame(pixels, WorkingSpaceId.LinearAcesCgV1, WorkingAdmission.LegacyUncharacterizedPassthrough, source);
        var rendered = Pipeline.Render(frame, new FrameParams());
        return new(rendered, CanonicalPreviewConverter.Convert(rendered, cmm, 1), HistogramData.FromFrame(rendered, 1));
    }

    [Fact]
    public void Performance_percentiles_are_bounded_and_counters_are_separate_from_durations()
    {
        var statistics = new PerformanceStatistics(4);
        foreach (double ms in new[] { 100d, 1, 2, 3, 4 }) statistics.Observe("render", ms);
        statistics.Observe("render", double.NaN);
        statistics.Increment("hit"); statistics.Increment("hit");
        var summary = statistics.Snapshot()["render"];
        Assert.Equal(5, summary.Count); Assert.Equal(4, summary.SampleCount);
        Assert.Equal(110, summary.TotalMs); Assert.Equal(100, summary.MaximumMs);
        Assert.Equal(2, summary.P50Ms); Assert.Equal(4, summary.P95Ms);
        Assert.Equal(2, statistics.Counters()["hit"]);
    }
}
