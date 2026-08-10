namespace OpenRevelare.Core;

/// <summary>
/// Colour matrices for cameras the bundled LibRaw does not know.
///
/// LibRaw 0.21.x carries 1181 cameras, and its table stops before several bodies people are
/// actually shooting — the OM System OM-5 (October 2022) among them. For those, LibRaw parses the
/// file fine but returns an identity matrix, i.e. "no colorimetry", and the pipeline falls back to
/// treating camera-native RGB as though it were sRGB. That is exactly the uncharacterised state
/// chroma_grade grew out of (see docs/CALIBRATION.md), so it is worth filling in by hand.
///
/// ENTRIES MUST BE MEASURED, NOT GUESSED. Each one is the camera's published DNG ColorMatrix2
/// (XYZ D65 → camera), the same quantity LibRaw stores as cam_xyz and Adobe ships in DNG
/// Converter. A plausible-looking invented matrix is worse than no entry at all: it would make
/// the UI claim the input is characterised while feeding the pipeline a fiction. The recorded
/// provenance on each entry is what makes it checkable.
/// </summary>
public static class CameraMatrixFallback
{
    /// <summary>
    /// XYZ(D65) → camera-native RGB, row-major 3×3, keyed by "MAKE MODEL" as LibRaw normalises it.
    /// This is the DNG ColorMatrix2 convention, which is the direction Adobe publishes.
    /// </summary>
    private static readonly Dictionary<string, double[,]> ColorMatrix2 =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // OM System OM-5 — from rawspeed's cameras.xml (darktable), which publishes the same
            // ColorMatrix Adobe ships, as integers scaled by 10000. Extraction verified against a
            // body LibRaw DOES know: feeding rawspeed's E-M5 Mark III entry through the derivation
            // below reproduces LibRaw's own rgb_cam to 1.03e-4, i.e. float32 storage precision.
            //
            // LibRaw reports this body as make "OM Digital" (not the file's own
            // "OM Digital Solutions"), so that is the key that has to match.
            ["OM Digital OM-5"] = new[,]
            {
                {  1.1896, -0.5110, -0.1076 },
                { -0.3181,  1.1378,  0.2048 },
                { -0.0519,  0.1224,  0.5166 },
            },
        };

    /// <summary>D65 white as XYZ — the illuminant DNG's ColorMatrix2 is defined against.</summary>
    private static readonly double[] D65Xyz = { 0.9504559, 1.0, 1.0890578 };

    /// <summary>
    /// The camera → linear sRGB matrix for <paramref name="make"/>/<paramref name="model"/>, or
    /// null when there is no entry.
    ///
    /// This reproduces dcraw's <c>cam_xyz_coeff</c>, and the order of operations is the whole
    /// substance of it. The stored matrix maps XYZ → camera, but at an arbitrary per-channel
    /// scale; it has to be normalised so the camera sees the D65 white as (1,1,1) BEFORE being
    /// inverted. Normalising the finished camera→sRGB matrix instead — the obvious-looking
    /// shortcut — appears to work (its rows do sum to 1, and neutrals do survive) while silently
    /// producing badly wrong off-diagonals: on the E-M1 III values it gives a green row of
    /// (-1.51, 5.31, -2.80) against a true (-0.19, 1.79, -0.60).
    ///
    /// Rows of the result then sum to 1 as a CONSEQUENCE, not by fiat — which is the same
    /// white-preserving property LibRaw's own rgb_cam has, and what lets the conversion be
    /// applied after the inversion has already set the white point.
    /// </summary>
    public static double[,]? CameraToSrgb(string? make, string? model)
    {
        string key = $"{make} {model}".Trim();
        if (key.Length == 0 || !ColorMatrix2.TryGetValue(key, out double[,]? stored)) return null;

        // Normalise each camera channel against the D65 white.
        var xyzToCam = (double[,])stored.Clone();
        for (int i = 0; i < 3; i++)
        {
            double num = xyzToCam[i, 0] * D65Xyz[0]
                       + xyzToCam[i, 1] * D65Xyz[1]
                       + xyzToCam[i, 2] * D65Xyz[2];
            if (Math.Abs(num) < 1e-9) return null;   // degenerate entry; refuse rather than skew
            for (int j = 0; j < 3; j++) xyzToCam[i, j] /= num;
        }

        return ColorSpaces.Mul(ColorSpaces.Invert3(ColorSpaces.Srgb.ToXyz()),
                               ColorSpaces.Invert3(xyzToCam));
    }

    /// <summary>True when a fallback exists for this body.</summary>
    public static bool Has(string? make, string? model) =>
        ColorMatrix2.ContainsKey($"{make} {model}".Trim());
}
