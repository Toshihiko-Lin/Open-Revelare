using Avalonia.Collections;
using Avalonia.Media;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// The one description of what the crop/selection overlay looks like.
///
/// <para>
/// The overlay is drawn twice. On macOS and Linux — and on Windows whenever the native presenter
/// is unavailable — Avalonia draws it from <c>MainWindow.axaml</c> and the handle shapes built in
/// <c>MainWindow.axaml.cs</c>. On Windows with the presenter active the Avalonia layer is hidden
/// behind the child HWND and the same geometry is rasterized again, in physical pixels, by
/// <c>MainWindow.WindowsPresentation.cs</c> into the shared CPU compositor. Neither renderer can
/// be deleted while macOS and Linux have no presenter of their own (docs §15, M5/M6), so the two
/// must agree forever — and they had already drifted once before this type existed: the marquee
/// is dashed in Avalonia and solid in the native path, because
/// <c>PresentationRectOutline</c> has no dash pattern at all.
/// </para>
///
/// <para>
/// Every value here is in LOGICAL pixels. The Avalonia side applies 1/zoom so the stroke stays a
/// constant size on screen; the native side multiplies by the render scaling to reach physical
/// pixels. <c>MainWindow.HandleScreenSize</c> is the same idea and already lives in one place.
/// </para>
/// </summary>
public static class PreviewOverlayStyle
{
    /// <summary>Everything outside the crop frame. Reads as "excluded", not as a shape.</summary>
    public static readonly Color CropDim = Color.Parse("#99101214");

    /// <summary>Crop frame and marquee stroke — the neutral "you are selecting" grey.</summary>
    public static readonly Color Marquee = Color.Parse("#E6E9EC");

    /// <summary>Marquee interior. The same grey at low alpha, so the fill cannot drift off it.</summary>
    public static readonly Color MarqueeFill = Color.Parse("#22E6E9EC");

    /// <summary>Rule-of-thirds guides inside the crop frame.</summary>
    public static readonly Color Guide = Color.Parse("#66E6E9EC");

    /// <summary>
    /// Straighten reference line. Warm, not the neutral marquee grey: it is a transient guide laid
    /// over the picture, not a selection.
    /// </summary>
    public static readonly Color Straighten = Color.Parse("#FF5A50");

    /// <summary>Crop handle fill.</summary>
    public static readonly Color HandleFill = Color.Parse("#F2F5F7");

    /// <summary>Crop handle outline, so a handle stays visible over a blown highlight.</summary>
    public static readonly Color HandleOutline = Color.Parse("#1C1E20");

    public const double FrameStrokeThickness = 1.5d;
    public const double GuideStrokeThickness = 1d;
    public const double StraightenStrokeThickness = 2d;
    public const double HandleOutlineThickness = 1d;

    /// <summary>
    /// Marquee dash, on/off in logical pixels. Only the Avalonia renderer honours it today; see the
    /// type remarks.
    /// </summary>
    public static readonly AvaloniaList<double> MarqueeDash = new() { 4d, 2d };

    public static readonly IBrush CropDimBrush = new SolidColorBrush(CropDim);
    public static readonly IBrush MarqueeBrush = new SolidColorBrush(Marquee);
    public static readonly IBrush MarqueeFillBrush = new SolidColorBrush(MarqueeFill);
    public static readonly IBrush GuideBrush = new SolidColorBrush(Guide);
    public static readonly IBrush StraightenBrush = new SolidColorBrush(Straighten);
    public static readonly IBrush HandleFillBrush = new SolidColorBrush(HandleFill);
    public static readonly IBrush HandleOutlineBrush = new SolidColorBrush(HandleOutline);
}
