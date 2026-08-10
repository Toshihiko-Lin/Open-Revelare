namespace OpenRevelare.Core;

/// <summary>
/// Interprets the decoded negative in a declared colour space, before the density inversion.
///
/// This is the step the pipeline never had. The decoder hands over the sensor's own linear
/// numbers — which is correct, and must stay that way: the camera's raw light IS the primary
/// data for the density maths. But nothing said what those numbers MEAN colourimetrically, so
/// the inversion treated them as sRGB by default. chroma_grade then existed to patch the colour
/// that assumption gets wrong, from downstream, with a scalar.
///
/// The fix belongs here instead, and it is what DiVERE does: solve the input primaries jointly
/// with the density parameters against a chart, so the transform and the inversion are
/// consistent by construction. That consistency is the whole point — three earlier attempts to
/// insert an EXTERNAL matrix (the camera's own ColorMatrix) into this pipeline all failed on
/// real film, because a matrix that did not participate in the calibration cannot agree with it.
/// See docs/CALIBRATION.md.
/// </summary>
public static class InputTransform
{
    /// <summary>
    /// The matrix taking the declared input space to sRGB's primaries, or null when the
    /// declaration is absent or already sRGB — in which case there is nothing to do and the
    /// caller skips the pass entirely.
    /// </summary>
    public static double[,]? ToSrgb(double[,]? primaries, double[]? whitePoint)
    {
        if (primaries is null) return null;
        if (primaries.GetLength(0) != 3 || primaries.GetLength(1) != 2) return null;

        var wp = whitePoint is { Length: 2 }
            ? (whitePoint[0], whitePoint[1])
            : (ColorSpaces.Srgb.White.X, ColorSpaces.Srgb.White.Y);

        var input = new ColorSpaceDef("input",
            (primaries[0, 0], primaries[0, 1]),
            (primaries[1, 0], primaries[1, 1]),
            (primaries[2, 0], primaries[2, 1]),
            wp);

        // Degenerate primaries (collinear chromaticities) have no usable matrix. Refuse rather
        // than throw from inside a render: an unusable calibration should leave the frame
        // rendering as it did, not fail the whole pass.
        double[,] m;
        try { m = ColorSpaces.Convert(input, ColorSpaces.Srgb); }
        catch (InvalidOperationException) { return null; }

        // An identity means the declaration matches what the pipeline already assumed; skipping
        // keeps that case bit-for-bit unchanged rather than passing it through a float multiply.
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (Math.Abs(m[r, c] - (r == c ? 1.0 : 0.0)) > 1e-9) return m;
        return null;
    }

    /// <summary>
    /// Applies <see cref="ToSrgb"/> to a linear negative in place.
    ///
    /// Uses Path A's gamut-mapped linear path, not a bare multiply. A primaries change has
    /// negative off-diagonals like any colour matrix, so it can push a near-zero transmittance
    /// below zero — and the very next step takes -log10, where a negative or near-zero value
    /// explodes. Decouple's per-pixel alpha blend is exactly the safety net for that: it exists
    /// because the Path A matrix has the same problem, and only the pixels that need it retreat.
    /// </summary>
    public static void Apply(float[] data, double[,] toSrgb)
        => Decouple.Apply(data, toSrgb, DecoupleMode.Linear);
}
