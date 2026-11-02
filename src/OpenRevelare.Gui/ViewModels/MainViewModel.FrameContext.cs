using System.ComponentModel;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.ViewModels;

public partial class MainViewModel
{
    private readonly HashSet<RollFrame> _scopeFrames = new();
    public string CurrentFrameIdentity => CurrentFrame?.Label ?? Loc.T("未选择底片");
    public string CurrentFramePosition => CurrentFrame is { } frame ? $"{Frames.IndexOf(frame) + 1:00} / {Frames.Count:00}" : "";
    public string SelectionContext => Frames.Count(f => f.IsSelected) is > 0 and var n ? Loc.F($"勾选 {n} 帧") : "";
    public bool HasOtherFrames => CurrentFrame is not null && Frames.Count > 1;
    public bool CanApplySceneToRoll => HasOtherFrames && HasSceneSyncGroups;
    public string CalibrationApplyLabel => Loc.F($"同步标定到其余 {FrameScope.OtherFrames(Frames, CurrentFrame, false).Count} 帧");
    public string SceneApplyLabel => Loc.F($"同步成片到其余 {FrameScope.OtherFrames(Frames, CurrentFrame, false).Count} 帧");
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
        if (e.PropertyName == nameof(RollFrame.IsSelected)) NotifyRollBroadcastHints();
    }

    private void DisposeScopeSubscriptions()
    {
        foreach (var frame in _scopeFrames) frame.PropertyChanged -= OnScopeFrameChanged;
        _scopeFrames.Clear();
    }
}
