namespace OpenRevelare.Core;

/// <summary>
/// Where a buffer sits inside the frame it was cut from, so frame-global operators keep
/// measuring against the FRAME rather than against the piece they were handed.
///
/// Three operators in the chain are radial or centred about the whole frame — vignette,
/// distortion and the straighten rotation — plus LCC, whose flat field is a whole-frame image.
/// Hand any of them a sub-rectangle and they silently re-centre on it: the vignette's falloff
/// grows out of the middle of the crop, the rotation pivots about the crop's centre, the flat
/// field is stretched over the wrong area. The result looks plausible and is wrong, which is
/// the worst failure mode available. This struct is what lets the region renderer run the exact
/// same maths on a slice.
///
/// Coordinates are DOUBLE and in the working buffer's own scale. The region renderer box-
/// downsamples the source slice before processing it, and divides the offset and the frame
/// size by that same factor; every operator here normalises by the frame size, so the factor
/// cancels and no rounding creeps into the centre. Integers would quantise the offset to the
/// downsample factor and drift the centre by up to a pixel.
/// </summary>
/// <param name="OffsetX">Buffer's left edge within the frame.</param>
/// <param name="OffsetY">Buffer's top edge within the frame.</param>
/// <param name="FrameWidth">Full frame width in the same scale.</param>
/// <param name="FrameHeight">Full frame height in the same scale.</param>
public readonly record struct FrameRegion(double OffsetX, double OffsetY,
                                          double FrameWidth, double FrameHeight)
{
    /// <summary>The buffer IS the whole frame — what every non-region caller passes.</summary>
    public static FrameRegion Whole(int width, int height) => new(0, 0, width, height);

    /// <summary>True when this covers the entire frame, i.e. the operators can take their
    /// original fast path and index the buffer directly.</summary>
    public bool IsWhole(int width, int height)
        => OffsetX == 0 && OffsetY == 0 && FrameWidth == width && FrameHeight == height;
}
