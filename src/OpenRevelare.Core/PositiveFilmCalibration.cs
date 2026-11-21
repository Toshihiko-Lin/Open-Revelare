namespace OpenRevelare.Core;

/// <summary>Capture corrections for a finished positive image; no film response is reconstructed.</summary>
public static class PositiveFilmCalibration
{
    public static void Apply(float[] data, FrameParams cal)
    {
        float[] whiteGains = cal.DMinPerChannel.Select(d => (float)Math.Pow(10.0, d)).ToArray();
        void ToWorking(float[] values)
        {
            if (cal.PositiveWhiteReferenceSet)
            {
                // The measured lamp replaces as-shot WB exactly once. All measurements remain
                // camera-native; the same camera matrix still describes the corrected colours.
                for (int i = 0; i < values.Length; i++)
                    values[i] *= whiteGains[i % 3];
                if (cal.PositiveCameraToWorking is { } matrix)
                    OutputRender.ApplyMatrix(values, matrix, ColorPipeline.Working, GamutMapping.PreserveExtended);
            }
            else
                NegativeView.Prepare(values, cal.UsePositiveCameraWhiteBalance ? cal.PositiveCameraWhiteBalance : null,
                    cal.PositiveCameraToWorking);
        }

        ToWorking(data);
        if (!cal.PositiveBlackReferenceSet) return;
        var black = cal.DMaxPerChannel.Select(d => (float)Math.Pow(10.0, -d)).ToArray();
        ToWorking(black);
        double[,] xyz = ColorPipeline.Working.ToXyz();
        float level = (float)(xyz[1, 0] * black[0] + xyz[1, 1] * black[1] + xyz[1, 2] * black[2]);
        float span = 1.0f - level;
        if (!float.IsFinite(level) || span <= 1e-6f)
            throw new ArgumentException("Slide black reference must be below white.");
        // A shared linear black adjustment preserves channel differences. Film dye colour is
        // not interpreted as three independent response curves or sensor black offsets.
        for (int i = 0; i < data.Length; i++) data[i] = (data[i] - level) / span;
    }
}
