using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class TiffPredictorTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(5, 7)]
    [InlineData(7, 2)]
    [InlineData(17, 5)]
    [InlineData(257, 9)]
    [InlineData(4097, 2)]
    public void Prediction_roundtrips_wraparound_vector_tails_and_row_boundaries(int width, int height)
    {
        int rowSamples = width * 3;
        var random = new Random(42);
        ushort[] original = Enumerable.Range(0, rowSamples * height)
            .Select(i => i % 3 == 0 ? (ushort)(i % 2 == 0 ? 0 : 65535) : (ushort)random.Next(65536)).ToArray();
        ushort[] actual = (ushort[])original.Clone();
        TiffPredictor.ApplyHorizontal(actual, rowSamples);
        for (int row = 0; row < height; row++)
        {
            int start = row * rowSamples;
            // The first RGB pixel is stored verbatim; decoding proceeds forwards, unlike
            // prediction, so crossing a row or overwriting a vector predecessor is detected.
            Assert.Equal(original[start..(start + 3)], actual[start..(start + 3)]);
            for (int s = start + 3; s < start + rowSamples; s++)
                actual[s] = unchecked((ushort)(actual[s] + actual[s - 3]));
        }
        Assert.Equal(original, actual);
    }
}
