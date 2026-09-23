using OpenRevelare.Gui.Controls;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="WaveformData"/>: the binning, not the drawing. What has to hold is that a column of
/// the plot really is that column of the picture — the whole reason the waveform is worth having
/// next to the histogram is that it keeps the horizontal position a histogram throws away.
/// </summary>
public sealed class WaveformDataTests
{
    private static float[] Fill(int w, int h, Func<int, int, (float R, float G, float B)> f)
    {
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                int i = (y * w + x) * 3;
                data[i] = r; data[i + 1] = g; data[i + 2] = b;
            }
        return data;
    }

    private static int Busiest(float[] channel, int column, int levels)
    {
        int best = 0;
        for (int level = 1; level < levels; level++)
            if (channel[column * levels + level] > channel[column * levels + best]) best = level;
        return best;
    }

    [Fact]
    public void Puts_every_sample_of_a_flat_field_on_one_level()
    {
        WaveformData d = WaveformData.FromBuffer(Fill(64, 32, (_, _) => (0.5f, 0.5f, 0.5f)), 64, 32);

        int level = Busiest(d.G, column: 10, d.Levels);
        Assert.Equal(d.Levels / 2, level);
        // Everything in that column landed there: 64 px over 64 columns is one source column of 32.
        Assert.Equal(32f, d.G[10 * d.Levels + level]);
    }

    /// <summary>The property a histogram cannot show: WHERE in the frame a level sits.</summary>
    [Fact]
    public void Keeps_the_horizontal_position()
    {
        // Black on the left, white on the right.
        WaveformData d = WaveformData.FromBuffer(
            Fill(128, 16, (x, _) => x < 64 ? (0f, 0f, 0f) : (1f, 1f, 1f)), 128, 16);

        Assert.Equal(0, Busiest(d.R, column: 4, d.Levels));
        Assert.Equal(d.Levels - 1, Busiest(d.R, column: d.Columns - 4, d.Levels));
    }

    /// <summary>A cast is the channels separating vertically; a neutral is the three on top of each
    /// other. That is what makes it readable at a glance, so it is worth an assertion.</summary>
    [Fact]
    public void Separates_the_channels_of_a_tinted_frame()
    {
        WaveformData d = WaveformData.FromBuffer(
            Fill(64, 16, (_, _) => (0.75f, 0.5f, 0.25f)), 64, 16);

        Assert.True(Busiest(d.R, 8, d.Levels) > Busiest(d.G, 8, d.Levels));
        Assert.True(Busiest(d.G, 8, d.Levels) > Busiest(d.B, 8, d.Levels));
    }

    [Theory]
    [InlineData(2f)]       // an extended render's highlight
    [InlineData(float.PositiveInfinity)]
    public void Pins_values_above_white_to_the_top_row(float value)
    {
        WaveformData d = WaveformData.FromBuffer(Fill(32, 8, (_, _) => (value, value, value)), 32, 8);

        Assert.Equal(d.Levels - 1, Busiest(d.B, 4, d.Levels));
    }

    [Fact]
    public void Pins_negatives_and_nan_to_the_bottom_row()
    {
        WaveformData d = WaveformData.FromBuffer(
            Fill(32, 8, (_, _) => (-0.5f, float.NaN, 0f)), 32, 8);

        Assert.Equal(0, Busiest(d.R, 4, d.Levels));
        Assert.Equal(0, Busiest(d.G, 4, d.Levels));
    }

    /// <summary>A picture narrower than the plot must not leave empty columns between its samples,
    /// which would read as gaps in the frame.</summary>
    [Fact]
    public void Never_asks_for_more_columns_than_the_picture_has()
    {
        WaveformData d = WaveformData.FromBuffer(Fill(40, 8, (_, _) => (0.5f, 0.5f, 0.5f)), 40, 8);

        Assert.Equal(40, d.Columns);
        for (int column = 0; column < d.Columns; column++)
            Assert.True(d.G[column * d.Levels + Busiest(d.G, column, d.Levels)] > 0f);
    }
}
