namespace OpenRevelare.Core;

/// <summary>
/// How out-of-gamut colour is brought inside the destination gamut.
/// </summary>
public enum GamutMapping
{
    /// <summary>
    /// Per-channel clamp to [0,1]. Fast, and what an unmanaged pipeline does implicitly, but it
    /// moves each channel independently: a colour outside the gamut has its offending channel
    /// truncated while the others stay, which slides the hue. Kept because it is the honest name
    /// for "what happened before", and because it is the right choice when the data is already
    /// known to be in gamut.
    /// </summary>
    Clip,

    /// <summary>
    /// Desaturate toward the luminance-matched neutral until the colour just fits, per pixel.
    /// Hue and luminance are preserved; only chroma gives way, and only for pixels that need it.
    /// This is the default: rendering into a narrower gamut (paper, sRGB) is precisely the case
    /// where clipping shifts hues most visibly.
    /// </summary>
    Desaturate,
}

/// <summary>
/// The output stage: linear working-space RGB → linear destination RGB → encoded destination RGB.
///
/// This is the step the pipeline never had. Inversion output used to be implicitly "sRGB because
/// that is what we export", which left no place to express a gamut relationship and made a scalar
/// on the density-domain chroma vector (chroma_grade) the only available lever for "the colours
/// are wrong". A scalar cannot express what is anisotropic and hue-dependent; a matrix plus a
/// gamut map can. See docs/CALIBRATION.md.
/// </summary>
public static class OutputRender
{
    /// <summary>
    /// Converts linear RGB in <paramref name="from"/> to linear RGB in <paramref name="to"/>,
    /// in place, applying <paramref name="mapping"/> to colours that fall outside the
    /// destination gamut. Data is interleaved RGB triples.
    ///
    /// A no-op when the two spaces are the same — same primaries, same white point, so the
    /// matrix would be the identity and every pixel is in gamut by construction.
    /// </summary>
    public static void Convert(float[] data, ColorSpaceDef from, ColorSpaceDef to,
                               GamutMapping mapping = GamutMapping.Desaturate)
    {
        if (from == to) return;
        ApplyMatrix(data, ColorSpaces.Convert(from, to), to, mapping);
    }

    /// <summary>
    /// Applies an arbitrary 3×3 to interleaved linear RGB, gamut-mapping the result into
    /// <paramref name="destination"/>'s range. Shared by <see cref="Convert"/> and by the input
    /// characterisation step (<see cref="ColorMatrix.ApplyInPlace"/>), which needs exactly the
    /// same out-of-range treatment for exactly the same reason — its matrix also has large
    /// negative off-diagonals and also throws colour outside [0,1].
    /// </summary>
    public static void ApplyMatrix(float[] data, double[,] m, ColorSpaceDef destination,
                                   GamutMapping mapping = GamutMapping.Desaturate)
    {
        float m00 = (float)m[0, 0], m01 = (float)m[0, 1], m02 = (float)m[0, 2];
        float m10 = (float)m[1, 0], m11 = (float)m[1, 1], m12 = (float)m[1, 2];
        float m20 = (float)m[2, 0], m21 = (float)m[2, 1], m22 = (float)m[2, 2];

        // Luminance weights OF THE DESTINATION space — the Y row of its RGB→XYZ matrix. Using
        // sRGB's familiar 0.2126/0.7152/0.0722 here would be wrong for any other destination:
        // the whole point is that these primaries are not sRGB's.
        double[,] toXyz = destination.ToXyz();
        float ly = (float)toXyz[1, 0], lg = (float)toXyz[1, 1], lb = (float)toXyz[1, 2];

        Parallel.For(0, data.Length / 3, i =>
        {
            int p = i * 3;
            float r = data[p], g = data[p + 1], b = data[p + 2];

            float nr = m00 * r + m01 * g + m02 * b;
            float ng = m10 * r + m11 * g + m12 * b;
            float nb = m20 * r + m21 * g + m22 * b;

            if (mapping == GamutMapping.Desaturate)
                Desaturate(ref nr, ref ng, ref nb, ly, lg, lb);

            data[p] = Math.Clamp(nr, 0.0f, 1.0f);
            data[p + 1] = Math.Clamp(ng, 0.0f, 1.0f);
            data[p + 2] = Math.Clamp(nb, 0.0f, 1.0f);
        });
    }

    /// <summary>
    /// Pulls one colour toward its own luminance-matched grey until every channel lies within
    /// [0,1], preserving hue and luminance.
    ///
    /// Writing the colour as grey + t·(colour − grey), the smallest t that brings the worst
    /// channel to the boundary is found in closed form, so this costs a handful of arithmetic
    /// ops rather than an iterative search. Pixels already in gamut take t = 1 and are untouched.
    ///
    /// The luminance itself is clamped first: a pixel brighter than the destination's white
    /// cannot be fixed by desaturating (its grey is out of range too), so it is tone-limited and
    /// then desaturated against the limited grey.
    /// </summary>
    private static void Desaturate(ref float r, ref float g, ref float b,
                                   float ly, float lg, float lb)
    {
        if (r >= 0f && r <= 1f && g >= 0f && g <= 1f && b >= 0f && b <= 1f) return;

        float y = Math.Clamp(ly * r + lg * g + lb * b, 0.0f, 1.0f);

        // Largest t in [0,1] keeping grey + t·(c − grey) inside [0,1] for all three channels.
        float t = 1.0f;
        t = Math.Min(t, Limit(r, y));
        t = Math.Min(t, Limit(g, y));
        t = Math.Min(t, Limit(b, y));
        t = Math.Clamp(t, 0.0f, 1.0f);

        r = y + t * (r - y);
        g = y + t * (g - y);
        b = y + t * (b - y);

        // How far along grey→c we may travel before c leaves [0,1].
        static float Limit(float c, float y)
        {
            float d = c - y;
            if (d > 1e-9f) return (1.0f - y) / d;     // heading for the ceiling
            if (d < -1e-9f) return y / -d;            // heading for the floor
            return 1.0f;                              // channel sits on the neutral
        }
    }

    /// <summary>
    /// Re-renders a frame the pipeline already encoded as sRGB into <paramref name="to"/>.
    ///
    /// The BASIC output intent bakes the sRGB TRC in as the last step of Stage 2, for the preview
    /// as much as for the export, so by the time an exporter sees the data it is sRGB-encoded.
    /// Rather than restructure that shared path, this undoes the TRC, converts in linear light,
    /// and applies the destination's own curve. The inverse is exact, so a destination of sRGB is
    /// a true no-op and every other destination is as correct as converting before encoding.
    ///
    /// Only valid under <see cref="OutputIntent.Basic"/>. NONE-intent output is linear and has no
    /// TRC to undo — call <see cref="Convert"/> and <see cref="Encode"/> directly for that.
    /// </summary>
    public static void FromSrgbEncoded(float[] data, ColorSpaceDef to,
                                       GamutMapping mapping = GamutMapping.Desaturate)
    {
        if (to == ColorSpaces.Srgb) return;

        Srgb.ApplyInverseInPlace(data);
        Convert(data, ColorSpaces.Srgb, to, mapping);
        Encode(data, to);
    }

    /// <summary>
    /// Applies <paramref name="space"/>'s encoding TRC in place, taking linear to encoded.
    ///
    /// sRGB gets its piecewise curve (the linear toe is the whole point of it); everything else
    /// is a pure power curve. AdobeRGB's 563/256 goes through the same path as any other gamma,
    /// which is what its profile declares.
    /// </summary>
    public static void Encode(float[] data, ColorSpaceDef space)
    {
        // sRGB and Display P3 share the same piecewise TRC — P3 differs only in its primaries.
        if (space.Name.Equals("sRGB", StringComparison.OrdinalIgnoreCase)
         || space.Name.Equals("DisplayP3", StringComparison.OrdinalIgnoreCase))
        {
            Srgb.ApplyForwardInPlace(data);
            return;
        }

        float g = 1.0f / (float)EncodingGamma(space);
        Parallel.For(0, data.Length, i => data[i] = MathF.Pow(Math.Clamp(data[i], 0.0f, 1.0f), g));
    }

    /// <summary>
    /// The display gamma each space's ICC profile declares. ACEScg is scene-linear and carries no
    /// encoding curve, so it stays linear (gamma 1). sRGB and Display P3 are absent because their
    /// TRC is piecewise, not a power curve — <see cref="Encode"/> routes them separately.
    /// </summary>
    public static double EncodingGamma(ColorSpaceDef space) => space.Name switch
    {
        "AdobeRGB" => 563.0 / 256.0,
        "ACEScg" => 1.0,
        _ => 2.2,   // the paper/print spaces are published at 2.2
    };
}
