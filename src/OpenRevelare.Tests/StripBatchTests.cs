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
    [Theory]
    [InlineData("scene")]
    [InlineData("calibration")]
    [InlineData("geometry")]
    [InlineData("crop")]
    [InlineData("sprocket")]
    [InlineData("paste")]
    public async Task Missing_target_keeps_batch_parameters_selection_and_undo_unchanged(string operation)
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"atomic-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string[] paths = Enumerable.Range(1, 3).Select(i => WriteTiff(Path.Combine(dir, $"f{i}.tif"))).ToArray();
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(paths);
            await WhenSettled(vm);
            vm.ExposureEv = 1.5;
            vm.Rotation = 2.5;
            vm.SprocketEnabled = true;
            vm.SetCrop((0.1, 0.1, 0.7, 0.7));
            vm.CopyScene();
            // Close the source edit first, so a failure cannot hide behind a pending undo.
            vm.Undo();
            vm.Redo();
            await WhenSettled(vm);
            bool canUndo = vm.CanUndo, canRedo = vm.CanRedo;
            var before = vm.Frames.Select(f => f.Params.Clone()).ToArray();
            vm.Frames[1].IsSelected = vm.Frames[2].IsSelected = true;
            // Use an unavailable source without racing the import's background TIFF reader.
            // Deletion during that reader's file lock tests Windows sharing, not batch semantics.
            vm.Frames[2] = new RollFrame(Path.Combine(dir, "missing", "f3.tif"))
            {
                Params = before[2].Clone(), IsSelected = true,
            };

            switch (operation)
            {
                case "scene": vm.ApplySceneToRoll(); break;
                case "calibration": vm.ApplyCalibrationToRoll(); break;
                case "geometry": vm.ApplyGeometryToFrames(); break;
                case "crop": vm.ApplyCropToFrames(); break;
                case "sprocket": vm.ApplySprocketToFrames(); break;
                case "paste": vm.PasteSceneToSelected(); break;
            }

            Assert.Contains("f3.tif", vm.StatusText);
            Assert.True(vm.Frames[1].IsSelected);
            Assert.True(vm.Frames[2].IsSelected);
            Assert.Equal(canUndo, vm.CanUndo);
            Assert.Equal(canRedo, vm.CanRedo);
            for (int i = 0; i < vm.Frames.Count; i++)
            {
                Assert.Equal(before[i].ExposureEv, vm.Frames[i].Params.ExposureEv);
                Assert.Equal(before[i].Rotation, vm.Frames[i].Params.Rotation);
                Assert.Equal(before[i].CropRect, vm.Frames[i].Params.CropRect);
                Assert.Equal(before[i].SprocketEnabled, vm.Frames[i].Params.SprocketEnabled);
                Assert.Equal(before[i].TBase, vm.Frames[i].Params.TBase);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Geometry_batch_is_one_immediate_undo_and_current_only_selection_does_not_expand()
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"undo-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string[] paths = Enumerable.Range(1, 3).Select(i => WriteTiff(Path.Combine(dir, $"f{i}.tif"))).ToArray();
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(paths);
            await WhenSettled(vm);
            vm.Rotation = 2.5;
            vm.Frames[0].IsSelected = true;
            vm.ApplyGeometryToFrames();
            Assert.Equal(0, vm.Frames[1].Params.Rotation);
            Assert.Equal(0, vm.Frames[2].Params.Rotation);
            Assert.True(vm.Frames[0].IsSelected);

            vm.Frames[0].IsSelected = false;
            vm.ApplyGeometryToFrames();
            Assert.All(vm.Frames, f => Assert.Equal(2.5, f.Params.Rotation));
            vm.Undo();
            Assert.Equal(2.5, vm.Frames[0].Params.Rotation);
            Assert.Equal(0, vm.Frames[1].Params.Rotation);
            Assert.Equal(0, vm.Frames[2].Params.Rotation);
            vm.Redo();
            Assert.All(vm.Frames, f => Assert.Equal(2.5, f.Params.Rotation));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_keeps_mask_off_even_when_the_picture_has_a_bright_population(bool brightHighlights)
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"mask-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string[] paths = Enumerable.Range(1, 3)
            .Select(i => WriteTiff(Path.Combine(dir, $"f{i}.tif"), brightHighlights)).ToArray();
        try
        {
            using var vm = new MainViewModel();
            var config = new ImportConfig { AutoInvert = false, TiffInputAssumption = TiffInputAssumption.Linear };
            config.Paths.AddRange(paths);
            await vm.LoadRollWithConfigAsync(config);
            await WhenSettled(vm);
            await vm.PrepareImportedRollAsync();

            Assert.False(vm.SprocketEnabled);
            Assert.All(vm.Frames, f => Assert.False(f.Params.SprocketEnabled));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Sprocket_sync_is_explicit_and_respects_selection_without_copying_colour_or_crop()
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"mask-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string[] paths = Enumerable.Range(1, 3).Select(i => WriteTiff(Path.Combine(dir, $"f{i}.tif"))).ToArray();
        try
        {
            using var vm = new MainViewModel();
            await vm.LoadRollAsync(paths);
            await WhenSettled(vm);
            vm.SprocketEnabled = true;
            vm.SprocketThreshold = 0.75;
            vm.ExposureEv = 1.5;
            vm.SetCrop((0.2, 0.1, 0.6, 0.8));
            vm.Frames[1].Params.SprocketThreshold = 0.95;
            vm.Frames[2].Params.SprocketThreshold = 0.85;

            // Every Cineon route must leave the target's mask alone.
            vm.CopyCalibration();
            vm.ApplyCalibrationToRoll();
            vm.Frames[2].IsSelected = true;
            vm.PasteCalibrationToSelected();
            Assert.False(vm.Frames[1].Params.SprocketEnabled);
            Assert.Equal(0.95, vm.Frames[1].Params.SprocketThreshold);
            Assert.False(vm.Frames[2].Params.SprocketEnabled);
            Assert.Equal(0.85, vm.Frames[2].Params.SprocketThreshold);

            vm.SprocketEnabled = false;
            vm.SprocketThreshold = 0.99;
            vm.PasteCalibrationToCurrent();
            Assert.False(vm.SprocketEnabled);
            Assert.Equal(0.99, vm.SprocketThreshold);
            vm.SprocketEnabled = true;
            vm.SprocketThreshold = 0.75;

            vm.Frames[2].IsSelected = true;
            vm.ApplySprocketToFrames();
            Assert.False(vm.Frames[1].Params.SprocketEnabled);
            Assert.Equal(0.95, vm.Frames[1].Params.SprocketThreshold);
            Assert.True(vm.Frames[2].Params.SprocketEnabled);
            Assert.Equal(0.75, vm.Frames[2].Params.SprocketThreshold);
            Assert.Equal(0, vm.Frames[2].Params.ExposureEv);
            Assert.Null(vm.Frames[2].Params.CropRect);
            Assert.All(vm.Frames, f => Assert.False(f.IsSelected));

            // An explicit disable travels as well; no selection means the entire roll.
            vm.SprocketEnabled = false;
            vm.SprocketThreshold = 0.8;
            vm.ApplySprocketToFrames();
            Assert.All(vm.Frames, f =>
            {
                Assert.False(f.Params.SprocketEnabled);
                Assert.Equal(0.8, f.Params.SprocketThreshold);
            });
            AssertRect((0.2, 0.1, 0.6, 0.8), vm.CurrentFrame!.Params.CropRect);

            // Selecting only the current frame must not fall back to the whole roll.
            vm.SprocketEnabled = true;
            vm.CurrentFrame.IsSelected = true;
            vm.ApplySprocketToFrames();
            Assert.False(vm.Frames[1].Params.SprocketEnabled);
            Assert.False(vm.Frames[2].Params.SprocketEnabled);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Plain_import_clears_previous_capture_references_in_report_and_saved_project()
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"cal-provenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string refs = Path.Combine(dir, "references");
        Directory.CreateDirectory(refs);
        for (int c = 0; c < 3; c++)
            WriteTiff(Path.Combine(refs, $"{"RGB"[c]}.tif"), calibrationChannel: c);
        string first = WriteTiff(Path.Combine(dir, "first.tif"));
        string second = WriteTiff(Path.Combine(dir, "second.tif"));
        string flat = WriteTiff(Path.Combine(refs, "flat-field.tif"));
        using var vm = new MainViewModel();
        string initialProject = Path.Combine(dir, "first.ncproj");
        var initial = new Project.Data
        {
            Meta = new Project.RollMeta
            {
                InputType = "tiff", SourcePath = "A", TiffIsLinear = true,
                CalSourcePath = refs, LccPath = flat,
                CalRgbPaths = "RGB".ToDictionary(c => c.ToString(), c => Path.Combine(refs, $"{c}.tif")),
            },
            Frames = new() { new() { SourcePath = first } },
        };
        Project.Save(initialProject, initial);
        await vm.OpenProjectAsync(initialProject);
        await WhenSettled(vm);
        Assert.NotNull(vm.CurrentFrame!.Params.DecoupleMatrix);
        Assert.Contains(refs, vm.CaptureCorrectionSourceText);
        Assert.Contains(refs, vm.BuildFrameReport());

        await vm.LoadRollAsync(new[] { second });
        await WhenSettled(vm);
        Assert.Null(vm.CurrentFrame!.Params.DecoupleMatrix);
        Assert.False(vm.LccAvailable);
        Assert.DoesNotContain(refs, vm.CaptureCorrectionSourceText);
        Assert.DoesNotContain(refs, vm.BuildFrameReport());
        string project = Path.Combine(dir, "second.ncproj");
        await vm.SaveProjectAsync(project);
        var saved = Project.Load(project);
        Assert.Null(saved.Meta.CalSourcePath);
        Assert.Null(saved.Meta.CalRgbPaths);
        Assert.Null(saved.Meta.LccPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Virtual_copy_keeps_its_applied_crop_after_close_and_reopen(bool leaveCropToolOpen)
    {
        string dir = Path.Combine(TestDataIsolation.Root, $"copy-crop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = WriteTiff(Path.Combine(dir, "frame.tif"));
        var originalCrop = (0.1, 0.1, 0.7, 0.7);
        var copyCrop = (0.3, 0.2, 0.4, 0.4);
        var parent = new RollFrame(path);
        parent.Params.CropRect = originalCrop;
        var copy = RollFrame.MakeVirtualCopy(parent);
        string initial = Path.Combine(dir, "initial.ncproj");
        // Put the copy first so opening it exercises live editing without a thumbnail
        // switch, which requires a render platform unavailable in this test harness.
        Project.Save(initial, new Project.Data
        {
            Meta = new Project.RollMeta { InputType = "tiff", TiffIsLinear = true },
            Frames = new()
            {
                new() { SourcePath = path, Params = copy.Params, IsVirtual = true },
                new() { SourcePath = path, Params = parent.Params },
            },
        });
        string project;
        using (var vm = new MainViewModel())
        {
            await vm.OpenProjectAsync(initial);
            await WhenSettled(vm);
            vm.SetCrop(copyCrop);
            // Keep a smaller, off-centre square rather than a default centred box.
            // Opening the tool again suppresses it for rendering, never for storage.
            vm.CropEditing = leaveCropToolOpen;
            vm.FlushRollNow(); // Same synchronous save as closing the main window.
            project = vm.CurrentRoll!.ProjectPath;
            var saved = Project.Load(project);
            AssertRect(copyCrop, saved.Frames[0].Params.CropRect);
            AssertRect(originalCrop, saved.Frames[1].Params.CropRect);
        }
        using var reopened = new MainViewModel();
        await reopened.OpenProjectAsync(project);
        await WhenSettled(reopened);
        Assert.Equal(2, reopened.Frames.Count);
        AssertRect(copyCrop, reopened.Frames[0].Params.CropRect);
        AssertRect(originalCrop, reopened.Frames[1].Params.CropRect);
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

    private static string WriteTiff(string path, bool brightHighlights = false, int calibrationChannel = -1)
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
        if (calibrationChannel >= 0)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 3; c++)
                    row[x * 3 + c] = c == calibrationChannel ? (byte)220 : (byte)20;
        if (brightHighlights)
            for (int x = 0; x < w / 3; x++)
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = 250;
        for (int y = 0; y < h; y++)
            if (!tif.WriteScanline(row, y)) throw new IOException("could not write test TIFF row");
        return path;
    }
}
