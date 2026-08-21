using OpenRevelare.Core;
using SkiaSharp;

namespace OpenRevelare.Gui.Interop;

/// <summary>
/// Translates a Core <see cref="ColorSpaceDef"/> into the Skia colour space that describes the
/// same thing, so a preview bitmap can carry its space instead of being handed over untagged.
///
/// WHY THIS EXISTS. Avalonia has no way to say what colour space a bitmap is in: every surface
/// and every <c>SKImageInfo</c> it builds omits the parameter, so Skia falls back to sRGB
/// (AvaloniaUI/Avalonia#8450 and #14599, both open and unassigned since 2022; still true on
/// master). An untagged buffer is then read as sRGB by every consumer — which is CORRECT for an
/// sRGB roll and WRONG for every other one. On macOS, where the compositor actually acts on that
/// assumption and converts to the panel profile, a Display P3 render came out visibly
/// oversaturated, because P3 numbers were read as sRGB and then expanded a second time.
///
/// Describing the space to Skia lets Skia do the conversion it already knows how to do. See
/// <see cref="ColorManagedImage"/> for the draw path that consumes this.
///
/// WHAT THIS DOES NOT FIX. The destination surface is still untagged, i.e. still sRGB, so Skia
/// converts INTO sRGB and the preview cannot show colour outside sRGB's gamut on a wide-gamut
/// panel. That ceiling is Avalonia's, not ours, and it cannot be lifted from here. What this does
/// buy is that the preview is no longer WRONG: in-gamut colour lands where it should, the three
/// platforms agree, and the preview matches the export.
/// </summary>
internal static class SkiaColorSpace
{
    /// <summary>
    /// The Skia colour space for <paramref name="space"/>, cached per space.
    ///
    /// Cached because a preview render builds one per frame and an <see cref="SKColorSpace"/> is a
    /// native handle; the set of spaces is fixed and tiny, so a dictionary that never evicts is
    /// the whole story. Never dispose what comes back — the cache owns it for the process
    /// lifetime, and the objects are immutable and thread-safe.
    /// </summary>
    public static SKColorSpace For(ColorSpaceDef space)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(space.Name, out SKColorSpace? hit)) return hit;
            SKColorSpace made = Build(space);
            Cache[space.Name] = made;
            return made;
        }
    }

    private static readonly Dictionary<string, SKColorSpace> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static SKColorSpace Build(ColorSpaceDef space)
        => SKColorSpace.CreateRgb(Transfer(space), Primaries(space));

    /// <summary>
    /// The space's encoding curve as Skia's seven-parameter form.
    ///
    /// The parameters come from <see cref="ColorSpaces.TransferParameters"/> — the SAME source the
    /// ICC writer uses — so the preview and the embedded profile cannot describe one space's curve
    /// two different ways. Skia's parameterisation is the ICC 'para' type-4 one in the
    /// encoded→linear direction, which is exactly what that method returns.
    /// </summary>
    private static SKColorSpaceTransferFn Transfer(ColorSpaceDef space)
    {
        double[] p = ColorSpaces.TransferParameters(space);
        return new SKColorSpaceTransferFn(
            (float)p[0], (float)p[1], (float)p[2], (float)p[3],
            (float)p[4], (float)p[5], (float)p[6]);
    }

    /// <summary>
    /// The space's RGB→XYZ matrix, Bradford-adapted to D50 because that is the space Skia's
    /// matrices live in — the same adaptation the embedded ICC profile carries, from the same
    /// helper, so the preview and the exported file describe the primaries identically.
    /// </summary>
    private static SKColorSpaceXyz Primaries(ColorSpaceDef space)
    {
        double[,] m = ColorSpaces.ToXyzD50(space);
        return new SKColorSpaceXyz(
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2],
            (float)m[1, 0], (float)m[1, 1], (float)m[1, 2],
            (float)m[2, 0], (float)m[2, 1], (float)m[2, 2]);
    }
}
