using System.Threading.Tasks;

namespace OpenRevelare.Core;

/// <summary>
/// Detects over-exposed and under-exposed pixels in a display-encoded [0,1] RGB buffer
/// using Rec.709 luminance.
/// </summary>
public static class ClippingDetect
{
    public static void Detect(float[] data, int pixelCount,
                              float shadowThreshold, float highlightThreshold,
                              out bool[] shadows, out bool[] highlights)
    {
        var sh = new bool[pixelCount];
        var hi = new bool[pixelCount];

        Parallel.For(0, pixelCount, i =>
        {
            int p = i * 3;
            float luma = 0.2126f * data[p] + 0.7152f * data[p + 1] + 0.0722f * data[p + 2];
            if (luma <= shadowThreshold) sh[i] = true;
            else if (luma >= highlightThreshold) hi[i] = true;
        });

        shadows = sh;
        highlights = hi;
    }
}
