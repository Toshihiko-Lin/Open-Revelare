namespace OpenRevelare.Core;

/// <summary>Capture corrections for a finished positive image; no film response is reconstructed.</summary>
public static class PositiveFilmCalibration
{
    private static readonly double[,] WorkingToXyz = ColorPipeline.Working.ToXyz();
    private static readonly float LumaR = (float)WorkingToXyz[1, 0];
    private static readonly float LumaG = (float)WorkingToXyz[1, 1];
    private static readonly float LumaB = (float)WorkingToXyz[1, 2];
    private const float NearBlack = 0.002f;

    private static float Luminance(float[] data, int i) =>
        LumaR * data[i] + LumaG * data[i + 1] + LumaB * data[i + 2];

    /// <summary>Render calibrated linear slide pixels for the available display range.</summary>
    internal static void ApplyViewingMap(float[] data, FrameParams cal, float peak)
    {
        float exposure = (float)Math.Pow(2, cal.ExposureEv);
        float filmBase = 0;
        if (cal.PositiveBlackReferenceSet)
        {
            // Recover the native transmission stored by the picker and follow the same
            // colour transforms as the image. The stored sample itself stays linear in meaning.
            float[] sample = cal.DMaxPerChannel.Select(d => (float)Math.Pow(10, -d)).ToArray();
            Apply(sample, cal);
            if (InputTransform.ToWorking(cal.InputPrimaries, cal.InputWhitePoint) is { } matrix)
                InputTransform.Apply(sample, matrix);
            if (cal.Monochrome) Monochrome.FoldInPlace(sample);
            float level = Luminance(sample, 0);
            if (level > NearBlack && level < 0.12f) filmBase = level;
        }

        // The toe follows viewing exposure and rejoins the identity below the midtones.
        // A smoothstep in gain keeps both joins smooth and preserves the sample's chromaticity.
        float toe = filmBase * exposure;
        float toeEnd = MathF.Min(2f * filmBase, 0.18f) * exposure;
        // A bright reference may include flare or exposed film. Taper its influence rather
        // than making a bright patch near-black and crushing legitimate image shadows.
        float strength = Math.Clamp((0.12f - filmBase) / 0.09f, 0, 1);
        float baseGain = filmBase > 0 ? 1 - strength * (1 - NearBlack / filmBase) : 1f;
        float knee = peak > 1f ? MathF.Max(1f, 0.85f * peak) : 0.85f * peak;
        float headroom = peak - knee;
        ParallelSweep.OverPixels(data.Length / 3, (from, to) =>
        {
            for (int i = from; i < to; i += 3)
            {
                float y = Luminance(data, i) * exposure;
                float gain = 1f;
                if (toe > 0 && y < toeEnd)
                {
                    float t = MathF.Max(0, (y - toe) / (toeEnd - toe));
                    gain = baseGain + (1f - baseGain) * t * t * (3f - 2f * t);
                }
                float viewed = y * gain;
                if (viewed > knee)
                {
                    float excess = viewed - knee;
                    float compressed = knee + headroom * (1f - headroom / (excess + headroom));
                    gain = compressed / y;
                }
                gain *= exposure;
                data[i] *= gain;
                data[i + 1] *= gain;
                data[i + 2] *= gain;
            }
        });
    }

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
        // Film-base density is not a sensor offset. Preserve its measured brightness and colour.
    }
}
