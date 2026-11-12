namespace OpenRevelare.Core;

/// <summary>
/// Owned, unexposed Stage-1 pixels after source corrections, inversion and geometry, plus the
/// matching layout-fill mask. Finishing always copies the pixels, so repeated creative edits
/// cannot mutate the preparation. Rebuild after any source, calibration or geometry change.
/// </summary>
public sealed class PreparedFilmFrame
{
    internal ImageBuffer Pixels { get; }
    internal bool[]? Fill { get; }
    public long ResidentBytes => (long)Pixels.Data.Length * sizeof(float) + (Fill?.LongLength ?? 0);

    internal PreparedFilmFrame(ImageBuffer pixels, bool[]? fill)
    {
        Pixels = pixels;
        Fill = fill;
    }
}
