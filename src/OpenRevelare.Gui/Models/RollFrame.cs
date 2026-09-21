using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Models;

/// <summary>
/// One frame of a roll: its source file, its full per-frame <see cref="FrameParams"/>
/// snapshot (calibration + adjustments + geometry + curves), and a small processed
/// thumbnail for the film strip. Decoded image buffers are NOT held here — only the
/// current frame keeps full/preview buffers in memory (see MainViewModel), so a long
/// roll of 64 MP scans does not exhaust RAM.
///
/// A <see cref="IsVirtual"/> frame shares another frame's source <see cref="Path"/>
/// but carries its own params — an alternate look on the same scan (port of Python's
/// FrameEntry virtual copies).
/// </summary>
public sealed partial class RollFrame : ObservableObject
{
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>True when this entry is a virtual copy (shares another frame's source file).</summary>
    public bool IsVirtual { get; }

    /// <summary>Film-strip caption: the file name, marked when this is a virtual copy.</summary>
    public string Label => IsVirtual ? FileName + Loc.T(" · 副本") : FileName;

    /// <summary>Re-resolve <see cref="Label"/> after a language switch — the 副本 marker is the
    /// only translated part of it, and it sits in the film strip for as long as the roll is open.
    /// Driven from MainViewModel, which is what owns the frames.</summary>
    public void RefreshText() => OnPropertyChanged(nameof(Label));

    /// <summary>Per-frame parameters — the single source of truth for this frame's edit.</summary>
    public FrameParams Params { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private Bitmap? _thumbnail;

    /// <summary>
    /// No thumbnail yet — this frame is in the roll but has not been decoded.
    ///
    /// The film strip binds a placeholder to this. Every frame of an import is listed the moment
    /// the roll is opened (so a frame can be selected, and roll-wide operations reach all of them,
    /// before the decode queue gets to it), and without a visible pending state that reads as
    /// "everything imported" while the strip sits empty.
    /// </summary>
    public bool IsPending => Thumbnail is null;

    [ObservableProperty] private bool _isSelected;   // ticked in the film strip → paste target

    public RollFrame(string path, bool isVirtual = false)
    {
        Path = path;
        IsVirtual = isVirtual;
    }

    /// <summary>
    /// Make a virtual copy of <paramref name="parent"/>: every parameter of both stages, the
    /// geometry and the crop included.
    ///
    /// The Stage-2 scene used to be reset on the copy (the Python build's FilmBase-inherited /
    /// SceneBase-reset split). In practice a copy is made to VARY a finished frame — the other
    /// half of a half-frame scan, an alternative crop, a second grade — and starting it from a
    /// flat scene meant re-doing the whole Display stage first.
    /// </summary>
    public static RollFrame MakeVirtualCopy(RollFrame parent)
        // Thumbnail left null so the strip renders it from this copy's own params.
        => new(parent.Path, isVirtual: true) { Params = parent.Params.Clone() };

    /// <summary>Reset every Stage-2 (SceneBase) field of <paramref name="p"/> to its neutral default.</summary>
    public static void ResetScene(FrameParams p)
    {
        p.WbGains = new[] { 1.0, 1.0, 1.0 };
        p.ExposureEv = 0.0;
        p.BlackPoint = 0.0;
        p.WhitePoint = 1.0;
        p.Contrast = 0.0;
        p.Highlights = 0.0;
        p.Shadows = 0.0;
        p.Saturation = 0.0;
        p.CurvePointsM = new();
        p.CurvePointsR = new();
        p.CurvePointsG = new();
        p.CurvePointsB = new();
        p.CurvePreserveHue = true;
    }
}
