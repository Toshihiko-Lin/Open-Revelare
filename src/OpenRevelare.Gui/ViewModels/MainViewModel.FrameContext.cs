using System.ComponentModel;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    private readonly HashSet<RollFrame> _scopeFrames = new();
    public string CurrentFrameIdentity => CurrentFrame?.Label ?? Loc.T("未选择底片");
    public string CurrentFramePosition => CurrentFrame is { } frame ? $"{Frames.IndexOf(frame) + 1:00} / {Frames.Count:00}" : "";
    public int SelectedFrameCount => Frames.Count(f => f.IsSelected);
    public bool HasFrameSelection => SelectedFrameCount > 0;
    public string SelectionContext => HasFrameSelection ? Loc.F($"勾选 {SelectedFrameCount} 帧") : "";
    public string FrameSelectionSummary => HasFrameSelection
        ? Loc.F($"已选 {SelectedFrameCount} / {Frames.Count} 帧")
        : Loc.F($"共 {Frames.Count} 帧");
    public bool HasOtherFrames => CurrentFrame is not null && Frames.Count > 1;
    public bool CanApplySceneToRoll => HasOtherFrames && HasSceneSyncGroups;
    public int ContextTargetCount => FrameScope.OtherFrames(Frames, CurrentFrame, HasFrameSelection).Count;
    public bool CanApplyContextTargets => ContextTargetCount > 0;
    public string BroadcastSourceText => CurrentFrame is { } source ? Loc.F($"来源：{source.FileName}") : "";
    public string BroadcastTargetText => HasFrameSelection
        ? Loc.F($"目标：勾选 {ContextTargetCount} 帧")
        : Loc.F($"目标：其余 {ContextTargetCount} 帧");
    public string CalibrationApplyLabel => Loc.F($"应用底片校准到 {ContextTargetCount} 帧");
    public string SceneApplyLabel => Loc.F($"应用成片调整到 {ContextTargetCount} 帧");
    public string CompositionTargetLabel => Frames.Any(f => f.IsSelected)
        ? Loc.F($"勾选 {FrameScope.OtherFrames(Frames, CurrentFrame, true).Count} 帧")
        : Loc.F($"其余 {FrameScope.OtherFrames(Frames, CurrentFrame, false).Count} 帧");
    public string CropSyncLabel => Loc.F($"同步裁切到{CompositionTargetLabel}");
    public string GeometrySyncLabel => Loc.F($"同步旋转、翻转和拉直到{CompositionTargetLabel}");
    public string SprocketSyncLabel => Loc.F($"同步齿孔遮罩到{CompositionTargetLabel}");

    private void RefreshScopeSubscriptions()
    {
        foreach (var old in _scopeFrames.Where(f => !Frames.Contains(f)).ToArray())
        { old.PropertyChanged -= OnScopeFrameChanged; _scopeFrames.Remove(old); }
        foreach (var frame in Frames)
            if (_scopeFrames.Add(frame)) frame.PropertyChanged += OnScopeFrameChanged;
    }

    private void OnScopeFrameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RollFrame.IsSelected)
            || e.PropertyName == nameof(RollFrame.Params)
            || e.PropertyName == nameof(RollFrame.HasCalibration)
            || e.PropertyName == nameof(RollFrame.HasSceneEdits))
            NotifyRollBroadcastHints();
    }

    private void DisposeScopeSubscriptions()
    {
        foreach (var frame in _scopeFrames) frame.PropertyChanged -= OnScopeFrameChanged;
        _scopeFrames.Clear();
    }
}
