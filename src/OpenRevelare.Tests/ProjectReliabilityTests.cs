using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ProjectReliabilityTests
{
    private static string Folder()
    {
        string dir = Path.Combine(TestDataIsolation.Root, "reliability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Image(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        using var t = Tiff.Open(path, "w")!;
        t.SetField(TiffTag.IMAGEWIDTH, 16); t.SetField(TiffTag.IMAGELENGTH, 12);
        t.SetField(TiffTag.SAMPLESPERPIXEL, 3); t.SetField(TiffTag.BITSPERSAMPLE, 8);
        t.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        t.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        t.SetField(TiffTag.ROWSPERSTRIP, 1);
        for (int y = 0; y < 12; y++) t.WriteScanline(Enumerable.Repeat((byte)128, 48).ToArray(), y);
        return path;
    }

    private static async Task Settle(MainViewModel vm)
    {
        for (int i = 0; i < 400 && vm.IsBusy; i++) await Task.Delay(25);
        Assert.False(vm.IsBusy);
    }

    private static string ProjectFile(string dir, params Project.Frame[] frames)
    {
        string path = Path.Combine(dir, "roll.ncproj");
        Project.Save(path, new Project.Data { Meta = new() { InputType = "tiff", TiffIsLinear = true }, Frames = frames.ToList() });
        return path;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lcc_choice_survives_reopening_with_available_reference(bool rollFlag, bool enabled)
    {
        string dir = Folder(), source = Image(dir, "frame.tif"), flat = Image(dir, "flat.tif");
        string project = Path.Combine(dir, "roll.ncproj");
        Project.Save(project, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, LccPath = flat, LccEnabled = rollFlag ? enabled : null },
            Frames = new() { new() { SourcePath = source, LccEnabled = enabled } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(project); await Settle(vm);
        Assert.True(vm.LccAvailable); Assert.Equal(enabled, vm.LccEnabled);
        string copy = Path.Combine(dir, "copy.ncproj");
        Assert.True(await vm.SaveProjectAsync(copy));
        Assert.Equal(enabled, Project.Load(copy).Meta.LccEnabled);
        await vm.OpenProjectAsync(copy); await Settle(vm);
        Assert.Equal(enabled, vm.LccEnabled);
        Assert.Equal(enabled, vm.Frames[0].Params.LccFlatField is not null);
        vm.LccEnabled = !enabled;
        Assert.True(await vm.SaveProjectAsync(copy));
        await vm.OpenProjectAsync(copy); await Settle(vm);
        Assert.Equal(!enabled, vm.LccEnabled);
        Assert.All(vm.Frames, f => Assert.Equal(!enabled, f.Params.LccFlatField is not null));
    }

    [Fact]
    public async Task Missing_references_remain_declared_and_block_degraded_export()
    {
        string dir = Folder(), source = Image(dir, "frame.tif");
        string missing = Path.Combine(dir, "missing", "flat.tif"), project = Path.Combine(dir, "roll.ncproj");
        Project.Save(project, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, SourcePath = "A",
                CalSourcePath = Path.Combine(dir, "missing"), LccPath = missing, LccEnabled = true },
            Frames = new() { new() { SourcePath = source } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(project); await Settle(vm);
        Assert.True(vm.HasProjectDependencyWarning); Assert.True(vm.LccEnabled); Assert.False(vm.LccAvailable);
        string copy = Path.Combine(dir, "copy.ncproj");
        Assert.True(await vm.SaveProjectAsync(copy));
        var saved = Project.Load(copy);
        Assert.Equal("A", saved.Meta.SourcePath); Assert.Equal(missing, saved.Meta.LccPath); Assert.True(saved.Meta.LccEnabled);
        string output = Path.Combine(dir, "degraded");
        await vm.ExportRollAsync(output, new ExportOptions());
        Assert.Null(vm.LastBatchExportJob); Assert.Contains("Path A", vm.StatusText);
        Assert.StartsWith("导出前请恢复工程参考", vm.StatusText);
    }

    [Fact]
    public async Task Failed_close_save_preserves_edits_for_retry()
    {
        string dir = Folder(), source = Image(dir, "frame.tif");
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        vm.Rotation = 3.25;
        vm.CurrentRoll!.ProjectPath = dir; // replacing a directory must fail on every platform
        Assert.False(await vm.PrepareCloseAsync());
        Assert.Equal(ProjectSaveState.Failed, vm.SaveState); Assert.Equal(3.25, vm.Rotation);
        vm.CurrentRoll.ProjectPath = path;
        Assert.True(await vm.PrepareCloseAsync());
        Assert.Equal(ProjectSaveState.Saved, vm.SaveState);
        Assert.Equal(3.25, Project.Load(path).Frames[0].Params.Rotation);
    }

    [Fact]
    public async Task Two_editors_do_not_silently_overwrite_the_same_project()
    {
        string dir = Folder(), source = Image(dir, "frame.tif");
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source });
        using var first = new MainViewModel();
        using var second = new MainViewModel();
        await first.OpenProjectAsync(path); await Settle(first);
        await second.OpenProjectAsync(path); await Settle(second);

        first.Rotation = 2.5;
        Assert.True(await first.FlushRollAsync());
        second.Rotation = 7.5;
        Assert.False(await second.FlushRollAsync());
        Assert.Equal(ProjectSaveState.Failed, second.SaveState);
        Assert.Contains("另一窗口", second.SaveStatusDetail);
        Assert.Equal(2.5, Project.Load(path).Frames[0].Params.Rotation);
        Assert.Equal(7.5, second.Rotation);

        string recovery = Path.Combine(dir, "recovery.ncproj");
        Assert.True(await second.SaveProjectAsync(recovery));
        Assert.Equal(7.5, Project.Load(recovery).Frames[0].Params.Rotation);
        Assert.Equal(2.5, Project.Load(path).Frames[0].Params.Rotation);
        Assert.False(await second.SaveProjectAsync(path));
    }

    [Fact]
    public async Task Simultaneous_checked_writers_commit_only_one_revision()
    {
        string dir = Folder(), source = Image(dir, "frame.tif");
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source });
        var (_, revision) = Project.LoadWithRevision(path);
        Task<Exception?> Write(string note) => Task.Run(() =>
        {
            try
            {
                Project.SaveIfUnchanged(path, new Project.Data
                {
                    Meta = new() { InputType = "tiff", TiffIsLinear = true, RollNote = note },
                    Frames = new() { new() { SourcePath = source } },
                }, revision);
                return null;
            }
            catch (Exception ex) { return ex; }
        });
        var results = await Task.WhenAll(Write("first"), Write("second"));
        Assert.Equal(1, results.Count(e => e is null));
        Assert.Single(results, e => e is IOException);
        Assert.Contains(Project.Load(path).Meta.RollNote, new[] { "first", "second" });
    }

    [Fact]
    public async Task Removing_parent_and_copies_preserves_prior_undo_history()
    {
        string dir = Folder(), a = Image(dir, "a.tif"), b = Image(dir, "b.tif");
        string path = ProjectFile(dir,
            new() { SourcePath = a },
            new() { SourcePath = a, IsVirtual = true, Params = new() { Rotation = 7, CropRect = (0.1, 0.2, 0.5, 0.6) } },
            new() { SourcePath = b });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        var parent = vm.Frames[0]; var copy = vm.Frames[1];
        vm.Rotation = 2.5; parent.IsSelected = true;
        vm.RemoveFrames(); Assert.Single(vm.Frames);
        vm.Undo(); await Settle(vm);
        Assert.Equal(3, vm.Frames.Count); Assert.Same(parent, vm.Frames[0]); Assert.Same(copy, vm.Frames[1]);
        Assert.True(parent.IsSelected); Assert.Equal(2.5, parent.Params.Rotation); Assert.Equal(7, copy.Params.Rotation);
        Assert.Equal((0.1, 0.2, 0.5, 0.6), copy.Params.CropRect);
        vm.Undo(); await Settle(vm); Assert.Equal(0, vm.Frames[0].Params.Rotation);
        vm.Redo(); await Settle(vm); Assert.Equal(2.5, vm.Frames[0].Params.Rotation);
        vm.Redo(); await Settle(vm); Assert.Single(vm.Frames);
        Assert.True(await vm.PrepareCloseAsync()); Assert.Single(Project.Load(path).Frames);
    }

    [Fact]
    public void Relocation_rejects_same_name_with_different_content()
    {
        string dir = Folder(), old = Path.Combine(dir, "old"); Directory.CreateDirectory(old);
        string source = Path.Combine(old, "frame.raw"); File.WriteAllText(source, "original");
        string project = ProjectFile(dir, new Project.Frame { SourcePath = source });
        var data = Project.Load(project); Assert.NotNull(data.Frames[0].SourceFingerprint);
        File.Delete(source); File.WriteAllText(Path.Combine(dir, "frame.raw"), "incorrect");
        Assert.Equal(0, Project.Relink(data, dir)); Assert.Equal(source, data.Frames[0].SourcePath);
        File.WriteAllText(Path.Combine(dir, "frame.raw"), "original");
        Assert.Equal(1, Project.Relink(data, dir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_lcc_can_be_relocated_without_changing_enable_intent(bool samePath)
    {
        string dir = Folder(), source = Image(dir, "frame.tif"), path = Path.Combine(dir, "roll.ncproj");
        Project.Save(path, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true,
                LccPath = samePath ? Path.Combine(dir, "flat.tif") : Path.Combine(dir, "old", "flat.tif"), LccEnabled = true },
            Frames = new() { new() { SourcePath = source } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm); Assert.True(vm.HasProjectDependencyWarning);
        Image(dir, "flat.tif");
        await vm.RestoreProjectReferencesAsync(dir); await Settle(vm);
        Assert.True(vm.LccEnabled); Assert.True(vm.LccAvailable); Assert.False(vm.HasProjectDependencyWarning);
        Assert.Equal(Path.Combine(dir, "flat.tif"), Project.Load(path).Meta.LccPath);
    }

    [Fact]
    public async Task Closing_during_a_write_waits_and_persists_the_edit_made_during_that_write()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows rename sharing is the controlled write gate.
        string dir = Folder(), source = Image(dir, "frame.tif");
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        vm.Rotation = 2;
        Task<bool> saving, closing;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            saving = vm.FlushRollAsync();
            vm.Rotation = 4;
            closing = vm.PrepareCloseAsync();
            await Task.Delay(20);
            Assert.False(closing.IsCompleted);
        }
        Assert.True(await saving);
        Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(4, Project.Load(path).Frames[0].Params.Rotation);
    }

    [Fact]
    public async Task Rendering_space_changes_can_be_undone_and_redone_for_the_whole_roll()
    {
        string dir = Folder(), a = Image(dir, "a.tif"), b = Image(dir, "b.tif");
        string path = ProjectFile(dir, new() { SourcePath = a }, new() { SourcePath = b });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        int original = vm.OutputSpaceIndex, target = (original + 1) % 3;
        vm.OutputSpaceIndex = target;
        string chosen = vm.CurrentOutputSpace.Name;
        Assert.All(vm.Frames, f => Assert.Equal(chosen, f.Params.OutputSpace));
        vm.Undo(); await Settle(vm);
        Assert.Equal(original, vm.OutputSpaceIndex);
        Assert.All(vm.Frames, f => Assert.Equal(vm.CurrentOutputSpace.Name, f.Params.OutputSpace));
        vm.Redo(); await Settle(vm);
        Assert.Equal(target, vm.OutputSpaceIndex);
    }

    [Fact]
    public async Task Batch_export_retry_reads_repaired_sources_but_keeps_snapshot_parameters_and_paths()
    {
        string dir = Folder(), a = Image(dir, "a.tif"), b = Path.Combine(dir, "b.tif");
        File.WriteAllText(b, "invalid TIFF");
        string path = ProjectFile(dir, new() { SourcePath = a }, new() { SourcePath = b });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        string output = Path.Combine(dir, "output");
        Directory.CreateDirectory(output);
        var options = new ExportOptions { Format = ExportFormat.Tiff16, KeepSourceExif = false };
        await vm.ExportRollAsync(output, options);
        var job = Assert.IsType<OpenRevelare.Gui.Services.BatchExportJob>(vm.LastBatchExportJob);
        Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Completed, job.Items[0].State);
        Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Failed, job.Items[1].State);
        byte[] original = File.ReadAllBytes(job.Items[0].OutputPath!);
        string retryPath = job.Items[1].OutputPath!;
        Image(dir, "b.tif");
        options.Format = ExportFormat.Jpeg; // caller changes must not mutate the queued task
        vm.Rotation = 8;
        await vm.RetryBatchExportAsync();
        Assert.All(job.Items, item => Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Completed, item.State));
        Assert.Equal(original, File.ReadAllBytes(job.Items[0].OutputPath!));
        Assert.Equal(retryPath, job.Items[1].OutputPath);
        Assert.Equal(".tiff", Path.GetExtension(retryPath));
        Assert.Equal(2, Directory.GetFiles(output).Length);
    }

    private static string Cube(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path);
        writer.WriteLine("# Input: Cineon Log\n# Output: sRGB\nLUT_3D_SIZE 2");
        for (int b = 0; b < 2; b++)
            for (int g = 0; g < 2; g++)
                for (int r = 0; r < 2; r++) writer.WriteLine($"{r} {g} {b}");
        return path;
    }

    [Fact]
    public async Task Restoring_a_missing_LUT_keeps_an_available_same_named_flat_field_in_its_original_folder()
    {
        string dir = Folder(), source = Image(dir, "source.tif"), originalFlat = Image(dir, "flat.tif");
        string missingLut = Path.Combine(dir, "old", "look.cube");
        string path = Path.Combine(dir, "roll.ncproj"), candidate = Path.Combine(dir, "candidates");
        Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(candidate, "flat.tif"), "unrelated broken flat field");
        Cube(Path.Combine(candidate, "look.cube"));
        Project.Save(path, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, LccPath = originalFlat, LccEnabled = true },
            Frames = new() { new() { SourcePath = source, Params = new() { PrintLut = missingLut, PrintLutOutput = "Srgb" } } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        Assert.True(vm.LccAvailable); Assert.True(vm.HasProjectDependencyWarning);
        await vm.RestoreProjectReferencesAsync(candidate); await Settle(vm);
        var restored = Project.Load(path);
        Assert.Equal(originalFlat, restored.Meta.LccPath);
        Assert.Equal(Path.Combine(candidate, "look.cube"), restored.Frames[0].Params.PrintLut);
        Assert.True(vm.LccAvailable); Assert.False(vm.HasProjectDependencyWarning);
    }

    [Fact]
    public async Task An_invalid_recovery_reference_does_not_replace_the_saved_declaration()
    {
        string dir = Folder(), source = Image(dir, "source.tif"), missing = Path.Combine(dir, "old", "flat.tif");
        string path = Path.Combine(dir, "roll.ncproj");
        Project.Save(path, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, LccPath = missing, LccEnabled = true },
            Frames = new() { new() { SourcePath = source } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        File.WriteAllText(Path.Combine(dir, "flat.tif"), "invalid reference");
        await vm.RestoreProjectReferencesAsync(dir);
        Assert.Equal(missing, Project.Load(path).Meta.LccPath);
        Assert.True(vm.HasProjectDependencyWarning); Assert.False(vm.HasLongTask);
        Assert.StartsWith("参考恢复失败", vm.StatusText);
    }

    [Fact]
    public async Task A_deleted_cached_LUT_is_reported_before_export()
    {
        string dir = Folder(), source = Image(dir, "source.tif"), lut = Cube(Path.Combine(dir, "look.cube"));
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source, Params = new() { PrintLut = lut, PrintLutOutput = "Srgb" } });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        Assert.NotNull(PrintLuts.Resolve(lut));
        File.Delete(lut);
        await vm.ExportRollAsync(Path.Combine(dir, "output"), new ExportOptions());
        Assert.Null(vm.LastBatchExportJob); Assert.True(vm.HasProjectDependencyWarning);
        Assert.StartsWith("导出前请恢复工程参考", vm.StatusText);
    }

    [Fact]
    public void Saving_an_intentionally_replaced_source_refreshes_its_relocation_evidence()
    {
        string dir = Folder(), source = Path.Combine(dir, "frame.raw");
        File.WriteAllText(source, "original capture");
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source });
        var data = Project.Load(path); string? before = data.Frames[0].SourceFingerprint;
        File.WriteAllText(source, "new replacement capture");
        Project.Save(path, data);
        Assert.NotEqual(before, Project.Load(path).Frames[0].SourceFingerprint);
        File.Delete(source);
        Project.Save(path, data); // an offline original must retain its last known evidence
        string folder = Path.Combine(dir, "relocated"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "frame.raw"), "new replacement capture");
        Assert.Equal(1, Project.Relink(Project.Load(path), folder));
    }

    [Fact]
    public void A_virtual_copy_without_evidence_does_not_bypass_the_real_frames_fingerprint()
    {
        string dir = Folder(), source = Path.Combine(dir, "frame.raw");
        File.WriteAllText(source, "actual original");
        string? fingerprint = SourceFingerprint.TryCapture(source);
        File.Delete(source);
        var data = new Project.Data { Frames = new()
        {
            new() { SourcePath = source, IsVirtual = true },
            new() { SourcePath = source, SourceFingerprint = fingerprint },
        } };
        string folder = Path.Combine(dir, "relocated"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "frame.raw"), "other photograph");
        Assert.Equal(0, Project.Relink(data, folder));
    }

    [Fact]
    public async Task Modified_cached_LUTs_require_explicit_recovery_and_failed_validation_does_not_publish_them()
    {
        string dir = Folder(), source = Image(dir, "source.tif"), lut = Cube(Path.Combine(dir, "look.cube"));
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source, Params = new() { PrintLut = lut, PrintLutOutput = "Srgb" } });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        var admitted = PrintLuts.Resolve(lut);
        Assert.NotNull(admitted); Assert.True(PrintLuts.IsCurrent(lut));
        File.WriteAllText(lut, "broken replacement LUT");
        Assert.False(PrintLuts.IsCurrent(lut)); Assert.True(vm.HasProjectDependencyWarning);
        await vm.RestoreProjectReferencesAsync(dir);
        Assert.Same(admitted, PrintLuts.Resolve(lut));
        Assert.True(vm.HasProjectDependencyWarning); Assert.StartsWith("参考恢复失败", vm.StatusText);
        Cube(lut);
        File.AppendAllText(lut, "\n# repaired revision\n");
        await vm.RestoreProjectReferencesAsync(dir); await Settle(vm);
        Assert.True(PrintLuts.IsCurrent(lut)); Assert.False(vm.HasProjectDependencyWarning);
        Assert.NotSame(admitted, PrintLuts.Resolve(lut));
    }

    [Fact]
    public async Task Reopening_explicitly_admits_a_new_LUT_revision()
    {
        string dir = Folder(), source = Image(dir, "source.tif"), lut = Cube(Path.Combine(dir, "look.cube"));
        string path = ProjectFile(dir, new Project.Frame { SourcePath = source, Params = new() { PrintLut = lut, PrintLutOutput = "Srgb" } });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        var admitted = PrintLuts.Resolve(lut);
        File.AppendAllText(lut, "\n# new explicit revision\n");
        Assert.False(PrintLuts.IsCurrent(lut));
        await vm.OpenProjectAsync(path); await Settle(vm);
        Assert.True(PrintLuts.IsCurrent(lut)); Assert.NotSame(admitted, PrintLuts.Resolve(lut));
    }

    [Fact]
    public void Successful_candidate_validation_does_not_publish_a_new_LUT_before_commit()
    {
        string dir = Folder(), lut = Cube(Path.Combine(dir, "look.cube"));
        var admitted = PrintLuts.Validate(lut);
        File.AppendAllText(lut, "\n# candidate revision\n");
        var candidate = PrintLuts.ReadDetached(lut);
        Assert.NotSame(admitted, candidate);
        Assert.Same(admitted, PrintLuts.Resolve(lut));
        Assert.False(PrintLuts.IsCurrent(lut));
    }

    [Fact]
    public async Task A_modified_flat_field_is_reported_and_can_be_reloaded_at_the_same_path()
    {
        string dir = Folder(), source = Image(dir, "source.tif"), flat = Image(dir, "flat.tif");
        string path = Path.Combine(dir, "roll.ncproj");
        Project.Save(path, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, LccPath = flat, LccEnabled = true },
            Frames = new() { new() { SourcePath = source } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        File.AppendAllText(flat, "changed revision");
        Assert.True(vm.HasProjectDependencyWarning);
        await vm.RestoreProjectReferencesAsync(dir); await Settle(vm);
        Assert.False(vm.HasProjectDependencyWarning); Assert.True(vm.LccEnabled); Assert.True(vm.LccAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_respects_cancellation_and_edits_made_while_confirming_legacy_sources(bool cancel)
    {
        string dir = Folder(), missing = Path.Combine(dir, "old", "source.tif");
        Image(dir, "source.tif"); Image(dir, "flat.tif");
        string path = Path.Combine(dir, "roll.ncproj");
        Project.Save(path, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, LccPath = Path.Combine(dir, "old", "flat.tif"), LccEnabled = true },
            Frames = new() { new() { SourcePath = missing } },
        });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        byte[] original = File.ReadAllBytes(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ConfirmUnverifiedRelink = async _ => { entered.TrySetResult(); return await decision.Task; };
        Task recovery = vm.RestoreProjectReferencesAsync(dir);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.HasLongTask); Assert.False(vm.CanRestoreProjectReferences);
        if (cancel) vm.CancelLongTask();
        else vm.Notes.RollNote = "new edit while choosing references";
        decision.SetResult(true);
        await recovery.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.HasLongTask); Assert.True(vm.CanRestoreProjectReferences);
        Assert.Equal(missing, Project.Load(path).Frames[0].SourcePath);
        if (cancel) Assert.Equal(original, File.ReadAllBytes(path));
        else
        {
            Assert.True(await vm.FlushRollAsync());
            Assert.Equal("new edit while choosing references", Project.Load(path).Meta.RollNote);
        }
    }

    [Fact]
    public async Task Retrying_after_a_LUT_change_does_not_mix_old_and_new_colour_bases_in_one_task()
    {
        string dir = Folder(), source = Image(dir, "a.tif"), corrupt = Path.Combine(dir, "b.tif");
        string lut = Cube(Path.Combine(dir, "look.cube")); File.WriteAllText(corrupt, "broken TIFF");
        string path = ProjectFile(dir,
            new() { SourcePath = source, Params = new() { PrintLut = lut, PrintLutOutput = "Srgb" } },
            new() { SourcePath = corrupt, Params = new() { PrintLut = lut, PrintLutOutput = "Srgb" } });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        await vm.ExportRollAsync(Path.Combine(dir, "output"), new ExportOptions { KeepSourceExif = false });
        var job = vm.LastBatchExportJob!;
        Assert.True(vm.CanShowBatchExportReport);
        Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Completed, job.Items[0].State);
        byte[] completed = File.ReadAllBytes(job.Items[0].OutputPath!);
        Image(dir, "b.tif"); File.AppendAllText(lut, "\n# new colour basis\n"); PrintLuts.Validate(lut);
        await vm.RetryBatchExportAsync();
        Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Failed, job.Items[1].State);
        Assert.Contains("重新创建批量导出任务", job.RecoveryBlockedReason);
        Assert.False(File.Exists(job.Items[1].OutputPath));
        Assert.Equal(completed, File.ReadAllBytes(job.Items[0].OutputPath!));
    }

    [Fact]
    public async Task Archived_roll_remains_portable_after_GUI_edit_and_autosave()
    {
        string dir = Folder(), source = Image(dir, "frame.tif"), path = ProjectFile(dir,
            new Project.Frame { SourcePath = source });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(path); await Settle(vm);
        string originalProject = vm.CurrentRoll!.ProjectPath;
        string destination = Path.Combine(dir, "archive");
        string? archiveProject = await vm.ArchiveProjectAsync(destination);
        Assert.Equal(Path.Combine(destination, "project.ncproj"), archiveProject);
        Assert.Equal(originalProject, vm.CurrentRoll!.ProjectPath);
        await vm.OpenProjectAsync(archiveProject!); await Settle(vm);
        vm.Notes.RollNote = "edited after archive";
        Assert.True(await vm.FlushRollAsync());
        string moved = Path.Combine(dir, "archive-moved"); Directory.Move(destination, moved);
        var reopened = Project.Load(Path.Combine(moved, "project.ncproj"));
        Assert.True(reopened.UseRelativePaths);
        Assert.Equal("edited after archive", reopened.Meta.RollNote);
        Assert.True(File.Exists(reopened.Frames[0].SourcePath));
    }

    [Fact]
    public async Task Failed_batch_resumes_after_a_new_view_model_opens_the_same_project()
    {
        string dir = Folder(), first = Image(dir, "first.tif"), bad = Path.Combine(dir, "bad.tif");
        File.WriteAllText(bad, "broken TIFF");
        string project = ProjectFile(dir, new() { SourcePath = first }, new() { SourcePath = bad });
        string output = Path.Combine(dir, "deliver");
        string completedPath, failedPath;
        byte[] completedBytes;
        using (var firstSession = new MainViewModel())
        {
            await firstSession.OpenProjectAsync(project); await Settle(firstSession);
            await firstSession.ExportRollAsync(output, new ExportOptions { KeepSourceExif = false });
            var job = firstSession.LastBatchExportJob!;
            Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Completed, job.Items[0].State);
            Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Failed, job.Items[1].State);
            completedPath = job.Items[0].OutputPath!; failedPath = job.Items[1].OutputPath!;
            completedBytes = File.ReadAllBytes(completedPath);
        }
        Image(dir, "bad.tif");
        using var resumed = new MainViewModel();
        await resumed.OpenProjectAsync(project); await Settle(resumed);
        var recovered = resumed.LastBatchExportJob!;
        Assert.True(recovered.CanRetry, recovered.RecoveryBlockedReason);
        Assert.Equal(completedPath, recovered.Items[0].OutputPath);
        Assert.Equal(failedPath, recovered.Items[1].OutputPath);
        await resumed.RetryBatchExportAsync();
        Assert.All(recovered.Items, i => Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Completed, i.State));
        Assert.Equal(completedBytes, File.ReadAllBytes(completedPath));
        Assert.True(File.Exists(failedPath));
    }

    [Fact]
    public async Task Recovered_batch_blocks_retry_when_project_or_completed_output_changed()
    {
        string dir = Folder(), first = Image(dir, "first.tif"), bad = Path.Combine(dir, "bad.tif");
        File.WriteAllText(bad, "broken TIFF");
        string project = ProjectFile(dir, new() { SourcePath = first }, new() { SourcePath = bad });
        using (var firstSession = new MainViewModel())
        {
            await firstSession.OpenProjectAsync(project); await Settle(firstSession);
            await firstSession.ExportRollAsync(Path.Combine(dir, "deliver"), new ExportOptions { KeepSourceExif = false });
            File.AppendAllText(firstSession.LastBatchExportJob!.Items[0].OutputPath!, "changed");
        }
        using (var resumed = new MainViewModel())
        {
            await resumed.OpenProjectAsync(project); await Settle(resumed);
            Assert.False(resumed.LastBatchExportJob!.CanRetry);
            Assert.Contains("已完成的导出文件已改变", resumed.LastBatchExportJob.RecoveryBlockedReason);
        }
        var data = Project.Load(project);
        data.Meta.RollNote = "new calibration context";
        Project.Save(project, data);
        using var changed = new MainViewModel();
        await changed.OpenProjectAsync(project); await Settle(changed);
        Assert.False(changed.LastBatchExportJob!.CanRetry);
        Assert.Contains("工程在任务创建后已改变", changed.LastBatchExportJob.RecoveryBlockedReason);
    }

    [Fact]
    public async Task Recovered_batch_blocks_retry_when_a_declared_reference_changed()
    {
        string dir = Folder(), first = Image(dir, "first.tif"), bad = Path.Combine(dir, "bad.tif");
        string flat = Image(dir, "flat.tif"), project = Path.Combine(dir, "roll.ncproj");
        File.WriteAllText(bad, "broken TIFF");
        Project.Save(project, new Project.Data
        {
            Meta = new() { InputType = "tiff", TiffIsLinear = true, LccPath = flat, LccEnabled = false },
            Frames = new() { new() { SourcePath = first }, new() { SourcePath = bad } },
        });
        using (var firstSession = new MainViewModel())
        {
            await firstSession.OpenProjectAsync(project); await Settle(firstSession);
            await firstSession.ExportRollAsync(Path.Combine(dir, "deliver"), new ExportOptions { KeepSourceExif = false });
            Assert.Equal(OpenRevelare.Gui.Services.BatchExportState.Failed,
                firstSession.LastBatchExportJob!.Items[1].State);
        }
        File.AppendAllText(flat, "changed");
        Image(dir, "bad.tif");
        using var resumed = new MainViewModel();
        await resumed.OpenProjectAsync(project); await Settle(resumed);
        Assert.False(resumed.LastBatchExportJob!.CanRetry);
        Assert.Contains("校正参考或 LUT 已改变", resumed.LastBatchExportJob.RecoveryBlockedReason);
    }

    [Fact]
    public async Task Batch_retry_rechecks_completed_outputs_after_report_was_opened()
    {
        string dir = Folder(), first = Image(dir, "first.tif"), bad = Path.Combine(dir, "bad.tif");
        File.WriteAllText(bad, "broken TIFF");
        string project = ProjectFile(dir, new() { SourcePath = first }, new() { SourcePath = bad });
        using var vm = new MainViewModel();
        await vm.OpenProjectAsync(project); await Settle(vm);
        await vm.ExportRollAsync(Path.Combine(dir, "deliver"), new ExportOptions { KeepSourceExif = false });
        var job = vm.LastBatchExportJob!;
        Assert.True(job.CanRetry);
        File.AppendAllText(job.Items[0].OutputPath!, "changed after report opened");
        Image(dir, "bad.tif");
        await vm.RetryBatchExportAsync();
        Assert.False(job.CanRetry);
        Assert.Contains("已完成的导出文件已改变", job.RecoveryBlockedReason);
        Assert.False(File.Exists(job.Items[1].OutputPath));
    }
}
