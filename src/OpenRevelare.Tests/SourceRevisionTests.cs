using System.Reflection;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class SourceRevisionTests : IDisposable
{
    private readonly string _root = Path.Combine(TestDataIsolation.Root, "source-revision-" + Guid.NewGuid().ToString("N"));
    public SourceRevisionTests() => Directory.CreateDirectory(_root);
    private string FilePath(string name = "source.tif") => Path.Combine(_root, name);

    [Fact]
    public void Same_path_same_size_replacement_invalidates_a_stamp()
    {
        string path = FilePath();
        File.WriteAllText(path, "old");
        SourceStamp before = SourceStamp.Capture(path);
        File.WriteAllText(path, "new");
        File.SetLastWriteTimeUtc(path, new DateTime(before.ModifiedTicks, DateTimeKind.Utc).AddSeconds(2));
        Assert.False(before.IsCurrent);
        Assert.NotEqual(before, SourceStamp.Capture(path));
        Assert.Throws<IOException>(before.RequireCurrent);
    }

    [Fact]
    public void A_change_during_read_cannot_be_returned_as_a_stable_decode()
    {
        string path = FilePath();
        File.WriteAllText(path, "old");
        Assert.Throws<IOException>(() => SourceStamp.ReadStable(path, () =>
        {
            File.AppendAllText(path, "changed");
            return 42;
        }));
    }

    [Fact]
    public void Snapshot_rejects_deleted_recreated_and_newly_available_contributors()
    {
        string first = FilePath(), missing = FilePath("missing.tif");
        File.WriteAllText(first, "first");
        var snapshot = new SourceSnapshot(new[] { first, missing });
        Assert.True(snapshot.IsCurrent);
        File.Delete(first);
        Assert.False(snapshot.IsCurrent);
        File.WriteAllText(first, "replacement");
        Assert.Throws<IOException>(snapshot.RequireCurrent);
        var withMissing = new SourceSnapshot(new[] { first, missing });
        File.WriteAllText(missing, "now present");
        Assert.False(withMissing.IsCurrent);
    }

    [Fact]
    public void Loaded_calibration_rejects_changed_reference_instead_of_silently_reusing_it()
    {
        string path = FilePath(); File.WriteAllText(path, "flat");
        var field = CalibrationSources.Track(new ImageBuffer(2, 2), new SourceSnapshot(new[] { path }));
        var p = new FrameParams { LccFlatField = field };
        CalibrationSources.RequireCurrent(p);
        File.AppendAllText(path, "changed");
        Assert.Throws<IOException>(() => CalibrationSources.RequireCurrent(p));
    }

    [Fact]
    public async Task Preview_inflight_tiles_and_full_resolution_follow_the_source_revision()
    {
        string path = FilePath(); WriteTiff(path, 100);
        using var vm = new MainViewModel();
        var previewMethod = typeof(MainViewModel).GetMethod("PreviewAsync", BindingFlags.NonPublic | BindingFlags.Instance,
            new[] { typeof(string), typeof((double X, double Y, double W, double H)?) })!;
        Task<PreviewCache.Entry> Preview((double, double, double, double)? region = null) =>
            (Task<PreviewCache.Entry>)previewMethod.Invoke(vm, new object?[] { path, region })!;
        var tileMethod = typeof(MainViewModel).GetMethod("TileFor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        WorkingFrame? Tile() => (WorkingFrame?)tileMethod.Invoke(vm, new object[] { new RollFrame(path) });
        var fullMethod = typeof(MainViewModel).GetMethod("LoadFullWorking", BindingFlags.NonPublic | BindingFlags.Instance)!;
        WorkingFrame Full() => (WorkingFrame)fullMethod.Invoke(vm,
            new object[] { path, ColorPipelineVersion.ManagedV2, TiffInputAssumption.Linear })!;

        var task = Preview();
        var concurrent = Preview();
        var first = await task;
        Assert.Same(first.Working, (await concurrent).Working); // join or already-completed cache hit
        Assert.Same(first.Working, (await Preview()).Working);
        var oldTile = Tile();
        Assert.NotNull(oldTile);
        var full = Full();
        Assert.Same(full, Full());

        WriteTiff(path, 200);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Null(Tile());
        var next = await Preview();
        Assert.NotSame(first.Working, next.Working);
        Assert.NotEqual(first.Preview.Data[0], next.Preview.Data[0]);
        Assert.NotSame(oldTile, Tile());
        Assert.NotSame(full, Full());
        Assert.NotSame(next.Working, (await Preview((0, 0, 0.5, 1))).Working);
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Preview());
        Assert.Null(Tile());
    }

    internal static void WriteTiff(string path, byte value)
    {
        using Tiff tif = Tiff.Open(path, "w")!;
        tif.SetField(TiffTag.IMAGEWIDTH, 16); tif.SetField(TiffTag.IMAGELENGTH, 12);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3); tif.SetField(TiffTag.BITSPERSAMPLE, 8);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB); tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1);
        for (int y = 0; y < 12; y++) Assert.True(tif.WriteScanline(Enumerable.Repeat(value, 48).ToArray(), y));
    }
    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_root)) File.Delete(file);
        Directory.Delete(_root);
    }
}
