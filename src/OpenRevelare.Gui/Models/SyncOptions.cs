using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenRevelare.Gui.Models;

/// <summary>
/// Colour field groups carried by roll-wide and copy/paste operations.
/// Masking and composition have explicit sync actions in their own panels, so a colour sync
/// cannot enable a destructive white fill or erase a crop.
/// </summary>
public sealed partial class SyncOptions : ObservableObject
{
    // Stage-1 calibration groups
    [ObservableProperty] private bool _calFilmBase = true;
    [ObservableProperty] private bool _calChroma = true;
    [ObservableProperty] private bool _calLens = true;

    // Stage-2 scene groups
    [ObservableProperty] private bool _sceneWb = true;
    [ObservableProperty] private bool _sceneExposure = true;
    [ObservableProperty] private bool _sceneTone = true;
    [ObservableProperty] private bool _sceneCurves = true;
}
