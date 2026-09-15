using System.Collections.Immutable;

namespace OpenRevelare.Presentation;

/// <summary>
/// Remembers the last composed background-plus-image-overlays scene and re-runs only the raster
/// primitives on top of it when nothing underneath has changed.
///
/// <para>
/// A viewport composition has two very different halves. Fitting the preview into the viewport
/// (bilinear over ~2 M output pixels) is the expensive one and changes only when the picture,
/// the zoom, the pan or the viewport does. The crop frame, its dimming panes, the straighten
/// line and the selection marquee are cheap — and they are the ONLY thing that changes on a
/// pointer move while one of those tools is being dragged. Recomposing the picture under them on
/// every move made the crop handle and the straighten line trail the pointer by a full compose;
/// with the base held here a move costs one copy of the base plus the primitives.
/// </para>
///
/// <para>
/// The key is by IDENTITY, not by pixel comparison: a scene matches when it shares the previous
/// scene's pixel storage (<see cref="PresentationScene.WithReferenceWhiteScale"/> re-tags by
/// sharing storage, so a re-tagged frame still matches), its size and its reference-white scale;
/// an overlay matches when its scene matches and its destination is equal. The 1×1 solid
/// background is compared by value, because it is rebuilt on every snapshot. A new render, a zoom
/// or a pan changes an overlay's storage or destination and misses; a viewport resize misses on
/// size. Nothing here can serve a stale picture.
/// </para>
///
/// <para>
/// Not thread-safe, and the scene <see cref="Compose"/> returns is valid only until the next
/// <see cref="Compose"/>: when primitives are present it is rasterised into storage the cache
/// reuses. Intended for one coalescing presentation worker that consumes each frame before
/// asking for the next.
/// </para>
/// </summary>
public sealed class ComposedBaseCache
{
    private PresentationScene? _base;
    private PresentationScene? _background;
    private PixelSize _viewport;
    private PresentationOverlay[] _overlays = Array.Empty<PresentationOverlay>();
    // Storage for the primitives pass, reused frame to frame (see ComposePrimitivesInto). The
    // scene returned from Compose aliases it, so it is valid only until the next Compose.
    private Half[]? _scratch;

    /// <summary>True when the last call was served from the cached base — for diagnostics and tests.</summary>
    public bool LastComposeReusedBase { get; private set; }

    public PresentationScene Compose(
        PresentationScene background,
        PixelSize viewport,
        IReadOnlyList<PresentationOverlay> overlays,
        IReadOnlyList<PresentationRasterPrimitive> primitives)
    {
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(overlays);
        ArgumentNullException.ThrowIfNull(primitives);

        bool reuse = _base is not null && Matches(background, viewport, overlays);
        if (!reuse)
        {
            _base = CpuPresentationCompositor.Compose(background, viewport, overlays, null);
            _background = background;
            _viewport = viewport;
            _overlays = overlays.ToArray();
        }
        LastComposeReusedBase = reuse;

        // The primitives are rasterised over a COPY of the base (the cached scene is immutable and
        // must stay so) — into the same scratch array every frame rather than a new one. With no
        // primitives the base itself is returned.
        if (primitives.Count == 0) return _base!;
        int length = _base!.LinearExtendedSrgbRgba.Length;
        if (_scratch is null || _scratch.Length != length) _scratch = new Half[length];
        return CpuPresentationCompositor.ComposePrimitivesInto(_scratch, _base, primitives);
    }

    /// <summary>Drops the held base — e.g. when the roll closes, so its viewport-sized pixels can be collected.</summary>
    public void Clear()
    {
        _base = null;
        _background = null;
        _overlays = Array.Empty<PresentationOverlay>();
        _scratch = null;
    }

    private bool Matches(PresentationScene background, PixelSize viewport, IReadOnlyList<PresentationOverlay> overlays)
    {
        if (_background is null || viewport != _viewport || overlays.Count != _overlays.Length) return false;
        if (!SameBackground(background, _background)) return false;
        for (int i = 0; i < _overlays.Length; i++)
        {
            PresentationOverlay a = overlays[i], b = _overlays[i];
            if (a.Destination != b.Destination || !SameStorage(a.Scene, b.Scene)) return false;
        }
        return true;
    }

    private static bool SameStorage(PresentationScene a, PresentationScene b) =>
        ReferenceEquals(a, b)
        || (a.Size == b.Size
            && a.ReferenceWhiteScale == b.ReferenceWhiteScale
            && a.LinearExtendedSrgbRgba == b.LinearExtendedSrgbRgba);   // ImmutableArray: same underlying array

    private static bool SameBackground(PresentationScene a, PresentationScene b)
    {
        if (SameStorage(a, b)) return true;
        if (a.Size != b.Size || a.ReferenceWhiteScale != b.ReferenceWhiteScale) return false;
        if (a.Size.Width != 1 || a.Size.Height != 1) return false;
        ImmutableArray<Half> pa = a.LinearExtendedSrgbRgba, pb = b.LinearExtendedSrgbRgba;
        for (int i = 0; i < 4; i++)
            if (BitConverter.HalfToUInt16Bits(pa[i]) != BitConverter.HalfToUInt16Bits(pb[i])) return false;
        return true;
    }
}
