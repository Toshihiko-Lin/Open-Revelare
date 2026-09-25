using System.Collections.Concurrent;
using Avalonia.Media;

namespace OpenRevelare.Gui.Services;

/// <summary>Which of the two contact-sheet looks to print.</summary>
public enum SheetStyle
{
    /// <summary>Graphite paper, black rebate — matches the app and shows on a screen.</summary>
    Dark,

    /// <summary>Warm off-white paper — the lab print.</summary>
    Light,
}

/// <summary>
/// What proportion the FINISHED sheet — paper, header and info strip included, not just the
/// thumbnail grid — should come out at. The column count is solved for the target rather than
/// fixed at ceil(sqrt(n)), which is why a 36-frame roll of 3:2 frames no longer has to print
/// as a 3:2 sheet.
/// </summary>
public enum SheetAspect
{
    /// <summary>Whatever lands inside the square…4:3 band with the fewest empty cells — the
    /// shape a sheet gets pinned to a wall or dropped into a post at.</summary>
    Auto,

    /// <summary>1:1.</summary>
    Square,

    /// <summary>4:3.</summary>
    FourThree,

    /// <summary>3:2, the shape of the frames themselves.</summary>
    ThreeTwo,
}

/// <summary>
/// Which way round <see cref="SheetAspect"/>'s proportion is read — 4:3 wide, or 4:3 tall.
///
/// No effect on <see cref="SheetAspect.Square"/>, which is the same page either way round; the
/// dialog greys the choice out there rather than pretending it does something.
/// </summary>
public enum SheetOrientation
{
    /// <summary>Wide — the long side runs across the page.</summary>
    Landscape,

    /// <summary>Tall — the long side runs down the page.</summary>
    Portrait,
}

/// <summary>
/// Every colour the sheet is printed with. One record so the composer and the info bar cannot
/// drift apart, and so adding a third look later is a matter of adding a preset, not of hunting
/// literals through two files.
///
/// The palette is stored as plain 0xRRGGBB numbers and the brushes are made ON DEMAND. A
/// <see cref="SolidColorBrush"/> is an AvaloniaObject and can only be CONSTRUCTED on the UI
/// thread, so building them in the static initialiser made the whole type unusable from a worker
/// — and the sheet's grid pass is a worker that only needs <see cref="GapRgb"/>, a plain array.
/// Brushes are still touched exclusively by the composing pass, which is on the UI thread.
/// </summary>
public sealed record SheetTheme
{
    public required uint PaperRgb { get; init; }         // the sheet's ground
    public required uint KeylineRgb { get; init; }       // hairline around a frame
    public required uint FrameNumberRgb { get; init; }   // 1, 2, 3 … under each frame
    public required uint RuleRgb { get; init; }          // structural hairlines
    public required uint BarBgRgb { get; init; }         // info-strip ground, logo area included
    public required uint BarLabelRgb { get; init; }
    public required uint BarValueRgb { get; init; }
    public required uint WordmarkRgb { get; init; }

    /// <summary>Fill between frames, sRGB [0,1]. Both looks keep this equal to
    /// <see cref="Paper"/> — frames are separated by their keylines, not by a lattice — but it
    /// stays a separate value because the grid is filled in Core, which has no brushes.</summary>
    public required float[] GapRgb { get; init; }

    public IBrush Paper => Brush(PaperRgb);
    public IBrush Keyline => Brush(KeylineRgb);
    public IBrush FrameNumber => Brush(FrameNumberRgb);
    public IBrush Rule => Brush(RuleRgb);
    public IBrush BarBg => Brush(BarBgRgb);
    public IBrush BarLabel => Brush(BarLabelRgb);
    public IBrush BarValue => Brush(BarValueRgb);
    public IBrush Wordmark => Brush(WordmarkRgb);

    // Shared by colour value rather than held per theme: a brush is immutable here, and keeping
    // the cache out of the record leaves its value equality untouched.
    private static readonly ConcurrentDictionary<uint, IBrush> Brushes = new();

    private static IBrush Brush(uint rgb) => Brushes.GetOrAdd(rgb, static v =>
        new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)));

    /// <summary>Graphite. Gaps are the paper colour, not black — the same construction as the
    /// light look, so the two are one design in two palettes rather than two designs.</summary>
    public static readonly SheetTheme Dark = new()
    {
        PaperRgb = 0x1A1C1E,
        GapRgb = new[] { 0.102f, 0.110f, 0.118f },   // == Paper
        KeylineRgb = 0x34373C,
        FrameNumberRgb = 0x8A9098,
        RuleRgb = 0x34373C,
        BarBgRgb = 0x212327,
        BarLabelRgb = 0x7E848B,
        BarValueRgb = 0xDCE0E4,
        WordmarkRgb = 0xA8AEB5,
    };

    /// <summary>Warm off-white — photographic paper is never pure #FFF, and a neutral white
    /// reads as a screenshot rather than as a print.</summary>
    public static readonly SheetTheme Light = new()
    {
        PaperRgb = 0xF4F2ED,
        GapRgb = new[] { 0.957f, 0.949f, 0.929f },   // == Paper
        KeylineRgb = 0xD3CFC6,
        FrameNumberRgb = 0x6B6862,
        RuleRgb = 0xD3CFC6,
        BarBgRgb = 0xE9E6DF,
        BarLabelRgb = 0x86827A,
        BarValueRgb = 0x23211E,
        WordmarkRgb = 0x5C594F,
    };

    public static SheetTheme For(SheetStyle style) => style == SheetStyle.Light ? Light : Dark;
}
