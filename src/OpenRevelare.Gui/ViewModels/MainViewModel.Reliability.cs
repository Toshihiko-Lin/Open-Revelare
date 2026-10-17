using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.ViewModels;

public enum ProjectSaveState { None, Pending, Saving, Saved, Failed }

public partial class MainViewModel
{
    private bool _declaredPathA;
    private long _projectRevision;
    private string? _savedProjectRevision;
    [ObservableProperty] private ProjectSaveState _saveState;
    private string _saveError = "";
    private DateTime? _lastSaved;
    public bool HasSaveStatus => SaveState != ProjectSaveState.None;
    public bool SaveNeedsAttention => SaveState == ProjectSaveState.Failed;

    public string SaveStatusText => SaveState switch
    {
        ProjectSaveState.Pending => Loc.T("待保存"),
        ProjectSaveState.Saving => Loc.T("保存中 …"),
        ProjectSaveState.Saved => Loc.T("已保存"),
        ProjectSaveState.Failed => Loc.T("保存失败 · 点击重试"),
        _ => "",
    };
    public string SaveStatusDetail => string.Join("\n", new[]
    {
        _roll?.ProjectPath ?? "", _lastSaved is { } time ? Loc.F($"最后保存：{time:HH:mm:ss}") : "", _saveError,
    }.Where(s => s.Length > 0));

    private void SetSaveState(ProjectSaveState state, string error = "")
    {
        SaveState = state;
        _saveError = error;
        if (state == ProjectSaveState.Saved) _lastSaved = DateTime.Now;
        if (state == ProjectSaveState.None) _lastSaved = null;
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(SaveNeedsAttention));
        OnPropertyChanged(nameof(SaveStatusText));
        OnPropertyChanged(nameof(SaveStatusDetail));
    }

    public string ProjectDependencyWarning
    {
        get
        {
            var missing = new List<string>();
            int absent = Frames.Select(f => f.Path).Distinct().Count(p => !File.Exists(p));
            if (absent > 0) missing.Add(Loc.F($"{absent} 个原片不可用"));
            if (_declaredPathA && (_decoupleMatrix is null || !CalibrationSources.IsCurrent(_decoupleMatrix)
                || _calRgbPaths is not { Length: 3 } || _calRgbPaths.Any(p => !File.Exists(p))))
                missing.Add(Loc.T("Path A 校正参考不可用"));
            if (LccEnabled && (!LccAvailable || !File.Exists(_lccSourcePath) || !CalibrationSources.IsCurrent(_lccFlatField)))
                missing.Add(Loc.T("已启用的 LCC 平场参考不可用"));
            foreach (string path in Frames.Select(f => f.Params.PrintLut).Where(p => !string.IsNullOrEmpty(p)).Distinct())
                if (!PrintLuts.IsCurrent(path))
                    missing.Add(Loc.T("LUT 不可用：") + Path.GetFileName(path));
            return string.Join("；", missing);
        }
    }
    public bool HasProjectDependencyWarning => ProjectDependencyWarning.Length > 0;
    public bool CanRestoreProjectReferences => HasProjectDependencyWarning && !HasLongTask;
    public bool CanInspectProjectDependencies => Frames.Count > 0 && !HasLongTask;

    public async Task<string?> ArchiveProjectAsync(string destination)
    {
        if (!CanInspectProjectDependencies || IsBusy) return null;
        CommitLiveParams(CurrentFrame);
        NotifyProjectDependencies();
        if (HasProjectDependencyWarning)
        {
            StatusText = Loc.T("归档前请恢复工程参考：") + ProjectDependencyWarning;
            return null;
        }
        Project.Data snapshot = BuildProjectData();
        if (!TryBeginLongTask(Loc.T("正在归档工程 …"), out var owner)) return null;
        try
        {
            var progress = new Progress<ProjectArchive.Progress>(p =>
            {
                if (ReferenceEquals(_longTaskCts, owner)) ReportLongTask(
                    Loc.F($"归档：{p.CompletedFiles}/{p.TotalFiles} 个文件 · {p.CopiedBytes / 1048576:N0}/{p.TotalBytes / 1048576:N0} MB"));
            });
            string result = await Task.Run(() => ProjectArchive.CreateAsync(snapshot, destination, progress, owner.Token));
            StatusText = Loc.T("工程已归档：") + result;
            return result;
        }
        catch (OperationCanceledException) { StatusText = Loc.T("归档已取消，未发布归档目录。"); return null; }
        catch (Exception ex) { StatusText = Loc.T("工程归档失败：") + ex.Message; return null; }
        finally { EndLongTask(owner); }
    }

    public Task<IReadOnlyList<ProjectDependencies.Entry>> InspectProjectDependenciesAsync()
    {
        Project.Data data = BuildProjectData();
        return Task.Run(() => ProjectDependencies.Inspect(data));
    }

    private void NotifyProjectDependencies()
    {
        OnPropertyChanged(nameof(ProjectDependencyWarning));
        OnPropertyChanged(nameof(HasProjectDependencyWarning));
        OnPropertyChanged(nameof(CanRestoreProjectReferences));
        OnPropertyChanged(nameof(CanInspectProjectDependencies));
    }

    private void RememberSourceFingerprints(Project.Data data)
    {
        var fingerprints = data.Frames.GroupBy(f => f.SourcePath).ToDictionary(g => g.Key, g => g.First().SourceFingerprint);
        foreach (var frame in Frames)
            if (fingerprints.TryGetValue(frame.Path, out string? fingerprint) && fingerprint is not null)
                frame.SourceFingerprint = fingerprint;
    }

    private bool CanExportProject()
    {
        CommitLiveParams(CurrentFrame);
        NotifyProjectDependencies();
        if (!HasProjectDependencyWarning) return true;
        StatusText = Loc.T("导出前请恢复工程参考：") + ProjectDependencyWarning;
        return false;
    }

    public async Task<bool> PrepareCloseAsync()
    {
        CancelRollAnalysis();
        CancelPreviewWarmup();
        _warmCts?.Cancel();
        _thumbCts?.Cancel();
        CancelLongTask();
        await WaitForLongTaskAsync();
        while (IsBusy) await Task.Delay(25);
        // FlushAsync serializes against the in-flight save. The window disables editing while
        // this runs, but a previously queued edit can still dirty the project during an await.
        do { if (!await FlushRollAsync()) return false; } while (_rollDirty);
        return true;
    }

    public void ResumeAfterCancelledClose()
    {
        RestartThumbnails();
        StartRollWarmUp();
    }

    public async Task RestoreProjectReferencesAsync(string folder)
    {
        if (_roll is null) return;
        if (!TryBeginLongTask(Loc.T("正在恢复工程参考 …"), out var owner)) return;
        try
        {
            CancelRollAnalysis();
            if (!await FlushRollAsync()) return;
            var originalRoll = _roll;
            string projectPath = _roll.ProjectPath;
            Project.Data data = BuildProjectData();
            long revision = _projectRevision;
            int restored = await ApproveRelinkAsync(data, folder) ? Project.Relink(data, folder) : 0;
            string? Restore(string? path, bool unavailable = false)
            {
                if (string.IsNullOrEmpty(path)) return path;
                // A folder selected for one missing resource may contain unrelated, same-named
                // copies of other references. Never replace a reference that is still usable.
                if (!unavailable && File.Exists(path)) return path;
                string candidate = Path.Combine(folder, Path.GetFileName(path));
                if (!File.Exists(candidate)) return path;
                restored++;
                return candidate;
            }
            data.Meta.LccPath = Restore(data.Meta.LccPath, unavailable: !LccAvailable || !CalibrationSources.IsCurrent(_lccFlatField));
            if (data.Meta.CalRgbPaths is { } rgb)
                foreach (string channel in rgb.Keys.ToArray())
                    rgb[channel] = Restore(rgb[channel], unavailable: _decoupleMatrix is null || !CalibrationSources.IsCurrent(_decoupleMatrix))!;
            if (data.Meta.SourcePath == "A" && data.Meta.CalRgbPaths is null)
            {
                try
                {
                    var (r, g, b) = await Task.Run(() => DecoupleCalibration.FindRgbCalFiles(folder));
                    data.Meta.CalSourcePath = folder;
                    data.Meta.CalRgbPaths = new() { ["R"] = r, ["G"] = g, ["B"] = b };
                    restored++;
                }
                catch (IOException) { /* no matching RGB set; retain the original declaration */ }
                catch (InvalidOperationException) { }
            }
            var recoveredLuts = new HashSet<string>();
            foreach (Project.Frame frame in data.Frames)
                if (!PrintLuts.IsBuiltin(frame.Params.PrintLut))
                {
                    string oldPath = frame.Params.PrintLut;
                    bool unavailable = !PrintLuts.IsCurrent(oldPath);
                    frame.Params.PrintLut = Restore(oldPath, unavailable) ?? "";
                    if (frame.Params.PrintLut.Length > 0 && File.Exists(frame.Params.PrintLut)
                        && (unavailable || frame.Params.PrintLut != oldPath)) recoveredLuts.Add(frame.Params.PrintLut);
                }
            if (restored == 0) { StatusText = Loc.T("所选目录中没有找到可恢复的参考文件。"); return; }
            owner.Token.ThrowIfCancellationRequested();
            ReportLongTask(Loc.T("正在校验恢复的参考 …"));
            string? originalLccPath = _lccSourcePath;
            bool hadLcc = LccAvailable && CalibrationSources.IsCurrent(_lccFlatField);
            TiffInputAssumption inputAssumption = _tiffInputAssumption;
            await Task.Run(() =>
            {
                if (data.Meta.LccPath is { } lcc && (lcc != originalLccPath || !hadLcc) && File.Exists(lcc))
                    LoadLccField(lcc, data.ColorPipelineVersion, inputAssumption, data.Meta.InputType == "raw");
                foreach (string lut in recoveredLuts) PrintLuts.ReadDetached(lut);
            });
            owner.Token.ThrowIfCancellationRequested();
            if (_roll != originalRoll || _projectRevision != revision)
            {
                StatusText = Loc.T("恢复期间工程已更改，保留最新编辑；请重新执行参考恢复。");
                return;
            }
            string savedRevision = await Task.Run(() =>
                Project.SaveIfUnchanged(projectPath, data, _savedProjectRevision));
            _savedProjectRevision = savedRevision;
            if (_roll != originalRoll)
            {
                StatusText = Loc.T("恢复期间工程已更改，保留最新编辑；请重新执行参考恢复。");
                return;
            }
            if (_projectRevision != revision)
            {
                // A programmatic edit can still arrive even when the owning window is disabled.
                // Reassert the latest in-memory revision before returning, rather than loading the
                // earlier recovery snapshot over it or letting that snapshot win a writer race.
                MarkRollDirty();
                await FlushRollAsync();
                StatusText = Loc.T("恢复期间工程已更改，保留最新编辑；请重新执行参考恢复。");
                return;
            }
            foreach (string lut in recoveredLuts) PrintLuts.Forget(lut);
            await OpenProjectAsync(projectPath);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        { StatusText = Loc.T("参考恢复已取消，原工程保持不变。"); }
        catch (Exception ex) { StatusText = Loc.T("参考恢复失败：") + ex.Message; }
        finally { EndLongTask(owner); }
    }
}
