using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The arithmetic behind the Display white-balance eyedropper: from a patch that ought to be
/// neutral, to the two slider values that make it so.
///
/// The view-model method around this renders a frame and averages a rectangle, which a unit test
/// cannot reach; what it does with the average is entirely here, and it is where the ways to get it
/// wrong live — a cast solved in the wrong direction, or a correction that silently changes the
/// exposure along with the colour.
/// </summary>
public sealed class NeutralEyedropperTests
{
    /// <summary>What <c>SampleDisplayNeutral</c> computes from the patch mean.</summary>
    private static (double Temp, double Tint, double[] Gains) Solve(double r, double g, double b)
    {
        double geomean = Math.Cbrt(r * g * b);
        double[] gains = { geomean / r, geomean / g, geomean / b };
        var (temp, tint, _) = WbMath.GainsToTempTint(gains);
        return (temp, tint, gains);
    }

    [Fact]
    public void Turns_the_sampled_patch_neutral()
    {
        var (temp, tint, _) = Solve(0.42, 0.36, 0.30);          // a warm patch

        double[] applied = WbMath.TempTintToGains(temp, tint);
        double[] corrected = { 0.42 * applied[0], 0.36 * applied[1], 0.30 * applied[2] };

        Assert.Equal(corrected[0], corrected[1], 6);
        Assert.Equal(corrected[1], corrected[2], 6);
    }

    /// <summary>
    /// Brightness belongs to 曝光. A correction that also lifted or dropped the patch would make
    /// the eyedropper an exposure tool by accident, and the two sliders it writes cannot express
    /// that anyway — so the gains it derives must have a geometric mean of one.
    /// </summary>
    [Theory]
    [InlineData(0.42, 0.36, 0.30)]
    [InlineData(0.10, 0.12, 0.19)]
    [InlineData(0.80, 0.80, 0.80)]
    public void Changes_the_colour_without_changing_the_brightness(double r, double g, double b)
    {
        var (_, _, gains) = Solve(r, g, b);

        Assert.Equal(1.0, Math.Cbrt(gains[0] * gains[1] * gains[2]), 9);
    }

    [Fact]
    public void Leaves_a_patch_that_is_already_neutral_alone()
    {
        var (temp, tint, _) = Solve(0.5, 0.5, 0.5);

        Assert.Equal(0.0, temp, 9);
        Assert.Equal(0.0, tint, 9);
    }

    /// <summary>
    /// The signs, stated once: a patch that came out too warm (red high, blue low) has to be
    /// COOLED, which is a negative 色温 on this slider's basis. A sign flip here would move the
    /// slider the wrong way and look like the control being inverted.
    /// </summary>
    [Fact]
    public void Cools_a_warm_patch_and_warms_a_cool_one()
    {
        Assert.True(Solve(0.50, 0.40, 0.30).Temp < 0);
        Assert.True(Solve(0.30, 0.40, 0.50).Temp > 0);
    }

    /// <summary>Green高 must pull 色调 towards magenta, i.e. positive on this basis (the Lightroom
    /// sign, which the slider's own track paints).</summary>
    [Fact]
    public void Moves_tint_away_from_the_cast_it_found()
    {
        Assert.True(Solve(0.40, 0.50, 0.40).Tint > 0);     // green cast → towards magenta
        Assert.True(Solve(0.50, 0.40, 0.50).Tint < 0);     // magenta cast → towards green
    }
}
