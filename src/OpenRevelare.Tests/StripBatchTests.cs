using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Gui.Views;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The film strip's tick boxes drive the structural commands and the geometry sync: ticked frames
/// are the targets when any are ticked, the current frame otherwise.
/// </summary>
public sealed class StripBatchTests
{
    [Fact]
    public void Modifier_selection_toggles_individual_frames_and_selects_ranges()
    {
        var frames = Enumerable.Range(1, 5).Select(i => new RollFrame($"/x/f{i}.tif")).ToList();

        MainWindow.ApplyFilmStripMultiSelection(frames, frames[1], frames[1], range: false, additive: true);
        MainWindow.ApplyFilmStripMultiSelection(frames, frames[3], frames[3], range: false, additive: true);
        Assert.Equal(new[] { false, true, false, true, false }, frames.Select(f => f.IsSelected));

        // Plain Shift replaces the old set with the inclusive anchored range.
        MainWindow.ApplyFilmStripMultiSelection(frames, frames[1], frames[2], range: true, additive: false);
        Assert.Equal(new[] { false, true, true, false, false }, frames.Select(f => f.IsSelected));

        // Ctrl+Shift adds a range instead of replacing what is already ticked.
        MainWindow.ApplyFilmStripMultiSelection(frames, frames[3], frames[4], range: true, additive: true);
        Assert.Equal(new[] { false, true, true, true, true }, frames.Select(f => f.IsSelected));
    }

    [Fact]
    public void A_virtual_copy_carries_both_stages_and_the_crop()
    {
        var parent = new RollFrame("/x/a.tif");
        parent.Params.ExposureEv = 0.7;
        parent.Params.Contrast = 0.3;
        parent.Params.WbGains = new[] { 1.2, 1.0, 0.9 };
        parent.Params.CropRect = (0.1, 0.1, 0.5, 0.5);
        parent.Params.QuarterTurns = 1;

        RollFrame copy = RollFrame.MakeVirtualCopy(parent);

        Assert.True(copy.IsVirtual);
        Assert.Equal(0.7, copy.Params.ExposureEv);
        Assert.Equal(0.3, copy.Params.Contrast);
        Assert.Equal(new[] { 1.2, 1.0, 0.9 }, copy.Params.WbGains);
        Assert.Equal((0.1, 0.1, 0.5, 0.5), copy.Params.CropRect);
        Assert.Equal(1, copy.Params.QuarterTurns);
        Assert.NotSame(parent.Params, copy.Params);
    }

    [Fact]
    public async Task Copies_and_removal_follow_the_ticked_frames()
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string[] paths = Enumerable.Range(1, 3).Select(i => WriteTiff(Path.Combine(dir, $"f{i}.tif"))).ToArray();
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(paths);
            await WhenSettled(vm);
            Assert.Equal(3, vm.Frames.Count);

            // Two ticked → each gets a copy right after itself; the selection stays put. (The
            // single-copy case selects the new copy, which needs a render platform this harness
            // does not have, so it is not driven here.)
            vm.Frames[1].IsSelected = true;   // f2
            vm.Frames[2].IsSelected = true;   // f3
            vm.CreateVirtualCopies();
            Assert.Equal(5, vm.Frames.Count);
            Assert.Equal(new[] { "f1.tif", "f2.tif", "f2.tif", "f3.tif", "f3.tif" },
                         vm.Frames.Select(f => f.FileName).ToArray());
            Assert.Equal(new[] { false, false, true, false, true },
                         vm.Frames.Select(f => f.IsVirtual).ToArray());
            Assert.Same(vm.Frames[0], vm.CurrentFrame);
            Assert.Equal(vm.Frames[1].Params.ExposureEv, vm.Frames[2].Params.ExposureEv);
            Assert.All(vm.Frames, f => Assert.False(f.IsSelected));

            // A batch action consumes its one-shot selection. Tick the real frames again, then
            // removing them takes their copies and clears the selection again.
            vm.Frames[1].IsSelected = true;
            vm.Frames[3].IsSelected = true;
            vm.RemoveFrames();
            Assert.Equal(new[] { "f1.tif" }, vm.Frames.Select(f => f.FileName).ToArray());
            Assert.All(vm.Frames, f => Assert.False(f.IsSelected));

            // Never down to nothing.
            foreach (RollFrame f in vm.Frames) f.IsSelected = true;
            vm.RemoveFrames();
            Assert.Single(vm.Frames);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Geometry_sync_targets_ticked_frames_or_the_whole_roll()
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"geom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string[] paths = Enumerable.Range(1, 3).Select(i => WriteTiff(Path.Combine(dir, $"f{i}.tif"))).ToArray();
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(paths);
            await WhenSettled(vm);

            vm.RotateCw();
            vm.Rotation = 2.5;
            vm.SetCrop((0.2, 0.1, 0.6, 0.8));

            vm.Frames[2].IsSelected = true;
            vm.ApplyGeometryToFrames();
            Assert.Equal(0, vm.Frames[1].Params.QuarterTurns);          // not ticked → untouched
            Assert.Equal(1, vm.Frames[2].Params.QuarterTurns);
            Assert.Equal(2.5, vm.Frames[2].Params.Rotation);
            Assert.Null(vm.Frames[2].Params.CropRect);                  // the crop stays with its picture
            Assert.All(vm.Frames, f => Assert.False(f.IsSelected));

            vm.ApplyGeometryToFrames();                                  // nothing ticked → whole roll
            Assert.All(vm.Frames, f =>
            {
                Assert.Equal(1, f.Params.QuarterTurns);
                Assert.Equal(2.5, f.Params.Rotation);
            });
            Assert.Null(vm.Frames[1].Params.CropRect);
            AssertRect((0.2, 0.1, 0.6, 0.8), vm.Frames[0].Params.CropRect);   // only the source keeps it
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void AssertRect((double X, double Y, double W, double H) expected,
                                   (double X, double Y, double W, double H)? actual)
    {
        Assert.NotNull(actual);
        var a = actual.Value;
        Assert.Equal(expected.X, a.X, 1e-9); Assert.Equal(expected.Y, a.Y, 1e-9);
        Assert.Equal(expected.W, a.W, 1e-9); Assert.Equal(expected.H, a.H, 1e-9);
    }

    private static async Task WhenSettled(MainViewModel vm)
    {
        for (int i = 0; i < 400 && vm.IsBusy; i++) await Task.Delay(25);
        Assert.False(vm.IsBusy, "the frame never finished decoding");
    }

    private static string WriteTiff(string path)
    {
        using Tiff tif = Tiff.Open(path, "w") ?? throw new IOException($"could not create test TIFF: {path}");
        const int w = 32, h = 24;
        tif.SetField(TiffTag.IMAGEWIDTH, w);
        tif.SetField(TiffTag.IMAGELENGTH, h);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, 8);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1);
        byte[] row = new byte[w * 3];
        for (int x = 0; x < w; x++) { row[x * 3] = 206; row[x * 3 + 1] = 138; row[x * 3 + 2] = 81; }
        for (int y = 0; y < h; y++)
            if (!tif.WriteScanline(row, y)) throw new IOException("could not write test TIFF row");
        return path;
    }
}
