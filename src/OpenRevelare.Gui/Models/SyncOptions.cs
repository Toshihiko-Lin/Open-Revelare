using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenRevelare.Gui.Models;

/// <summary>
/// Which field groups a broadcast (应用到整卷 / 复制·应用到选中) carries — port of
/// Python's 选择同步项 dialog. Calibration and scene groups default ON; geometry is
/// opt-in (matches the source, where flip/rotate/crop are per-frame unless chosen).
/// </summary>
public sealed partial class SyncOptions : ObservableObject
{
    // Stage-1 calibration groups
    // 片基与两端端点一起走：端点是相对片基测的密度，分开传就是半套标定。
    [ObservableProperty] private bool _calFilmBase = true;   // T_base + D_min[3] + D_max[3]
    [ObservableProperty] private bool _calChroma = true;     // chroma channel scale (Path A decouple)
    [ObservableProperty] private bool _calLens = true;       // distortion / vignette / LCC
    [ObservableProperty] private bool _calSprocket = true;   // sprocket enable / threshold

    // Stage-2 scene groups
    [ObservableProperty] private bool _sceneWb = true;       // temp/tint gains
    [ObservableProperty] private bool _sceneExposure = true; // exposure_ev
    [ObservableProperty] private bool _sceneTone = true;     // levels / contrast / hi-sh / sat
    [ObservableProperty] private bool _sceneCurves = true;   // tone curves

    // Geometry (opt-in)
    [ObservableProperty] private bool _geomOrientation;      // flips / 90° turns
    [ObservableProperty] private bool _geomStraighten;       // rotation angle
    [ObservableProperty] private bool _geomCrop;             // crop rect
}
