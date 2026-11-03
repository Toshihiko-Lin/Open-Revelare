namespace OpenRevelare.Core;

/// <summary>Native level evidence, read before black subtraction and normalized scaling.</summary>
public sealed record RawLevelMetadata(double Black, double[] ChannelBlack, double White,
    double DataMaximum, double[] ChannelWhite, double PatternMinimum, double PatternMaximum,
    double MaximumAdjustmentThreshold, int HighlightMode, uint Warnings)
{
    /// <summary>Conservative source-code spacing after black subtraction; never a pixel correction.</summary>
    public double QuantisationStep
    {
        get
        {
            if (ChannelBlack.Length != 4 || ChannelWhite.Length != 4) return 0;
            double range = double.MaxValue;
            for (int c = 0; c < 4; c++)
            {
                double white = ChannelWhite[c] > 0 ? ChannelWhite[c] : White;
                double usable = white - Black - ChannelBlack[c] - PatternMaximum;
                if (!double.IsFinite(usable) || usable <= 0) return 0;
                range = Math.Min(range, usable);
            }
            return 1d / range;
        }
    }
}

public sealed record RawColorMetadata(string Make, string Model, double[]? WhiteBalance,
    double[][]? CameraToLinearSrgb, string MatrixEvidence, RawLevelMetadata? Levels, string NativeBuild);

/// <summary>Matrix characterization for viewing camera-native linear values, never film inversion.</summary>
public static class CameraCharacterization
{
    public static double[,]? ToWorking(double[,]? cameraToSrgb)
    {
        if (cameraToSrgb is null || cameraToSrgb.GetLength(0) != 3 || cameraToSrgb.GetLength(1) != 3) return null;
        foreach (double value in cameraToSrgb)
            if (!double.IsFinite(value) || Math.Abs(value) > 64) return null;
        for (int r = 0; r < 3; r++)
            if (cameraToSrgb[r,0] + cameraToSrgb[r,1] + cameraToSrgb[r,2] <= 0) return null;
        double Det(double[,] m) => m[0,0] * (m[1,1]*m[2,2]-m[1,2]*m[2,1])
            - m[0,1] * (m[1,0]*m[2,2]-m[1,2]*m[2,0]) + m[0,2] * (m[1,0]*m[2,1]-m[1,1]*m[2,0]);
        if (Math.Abs(Det(cameraToSrgb)) < 1e-8) return null;
        var srgbToWorking = ColorSpaces.Convert(ColorSpaces.Srgb, ColorPipeline.Working);
        var result = new double[3, 3];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        for (int k = 0; k < 3; k++) result[r,c] += srgbToWorking[r,k] * cameraToSrgb[k,c];
        return result;
    }
}
