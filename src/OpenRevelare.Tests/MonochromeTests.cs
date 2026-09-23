using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Black-and-white rolls. The claim being tested is narrow and total: on a monochrome roll the
/// output is neutral BY CONSTRUCTION — not neutral because a cast was corrected — and a colour roll
/// is not touched by any of it.
/// </summary>
public sealed class MonochromeTests
{
    private static ImageBuffer Negative(int w, int h, float r, float g, float b)
    {
        var img = new ImageBuffer(w, h);
        for (int i = 0; i < img.Data.Length; i += 3)
        {
            img.Data[i] = r; img.Data[i + 1] = g; img.Data[i + 2] = b;
        }
        return img;
    }

    private static FrameParams MonoParams() => new()
    {
        Monochrome = true,
        // Deliberately lopsided: a colour calibration left behind on a roll that was then switched.
        TBase = new[] { 0.44, 0.30, 0.10 },
        DMinPerChannel = new[] { 0.10, 0.29, 0.54 },
        DMaxPerChannel = new[] { 1.90, 2.05, 2.30 },
        WbGains = new[] { 1.30, 1.00, 0.80 },
        OutputIntent = OutputIntent.Basic,
    };

    [Fact]
    public void Folds_three_samples_into_one_by_luminance()
    {
        var data = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };

        Monochrome.FoldInPlace(data);

        Assert.Equal(0.2126f, data[0], 5);
        Assert.Equal(0.2126f, data[2], 5);   // written to all three channels
        Assert.Equal(0.7152f, data[3], 5);
        Assert.Equal(0.0722f, data[6], 5);
    }

    [Fact]
    public void Collapses_the_endpoints_onto_the_green_reading()
    {
        FrameParams mono = Monochrome.Collapse(MonoParams());

        Assert.Equal(new[] { 0.30, 0.30, 0.30 }, mono.TBase);
        Assert.Equal(new[] { 0.29, 0.29, 0.29 }, mono.DMinPerChannel);
        Assert.Equal(new[] { 2.05, 2.05, 2.05 }, mono.DMaxPerChannel);
    }

    /// <summary>
    /// Stage 2's colour controls have nothing to act on here, and a stale value in one of them
    /// would tint a neutral frame with nothing on screen to explain it.
    /// </summary>
    [Fact]
    public void Drops_the_colour_controls_that_have_nothing_to_act_on()
    {
        FrameParams source = MonoParams();
        source.CurvePointsR.Add((0.2, 0.4));

        FrameParams mono = Monochrome.Collapse(source);

        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, mono.WbGains);
        Assert.Empty(mono.CurvePointsR);
        // On a COPY: switching the roll back to colour must return the user's grade intact.
        Assert.Single(source.CurvePointsR);
        Assert.Equal(new[] { 1.30, 1.00, 0.80 }, source.WbGains);
    }

    [Fact]
    public void Leaves_a_colour_roll_exactly_as_it_was()
    {
        var colour = new FrameParams { Monochrome = false, TBase = new[] { 0.44, 0.30, 0.10 } };

        Assert.Same(colour, Monochrome.Collapse(colour));
    }

    /// <summary>
    /// The whole point, end to end: a negative whose three channels disagree — because the copy
    /// light and the sensor disagree, not because the film has colour in it — renders neutral.
    /// </summary>
    [Fact]
    public void Renders_a_neutral_positive_from_an_unbalanced_negative()
    {
        ImageBuffer positive = Pipeline.ProcessFrame(Negative(8, 8, 0.30f, 0.22f, 0.08f), MonoParams());

        for (int i = 0; i < positive.Data.Length; i += 3)
        {
            Assert.Equal(positive.Data[i], positive.Data[i + 1], 5);
            Assert.Equal(positive.Data[i + 1], positive.Data[i + 2], 5);
        }
    }

    /// <summary>
    /// The fold is in place, and the buffer it folds may be the caller's own preview — which is
    /// held for the film strip, the samplers and the next render. Writing through it would turn
    /// every later reading of that frame monochrome too.
    /// </summary>
    [Fact]
    public void Never_writes_through_to_the_callers_buffer()
    {
        ImageBuffer source = Negative(8, 8, 0.30f, 0.22f, 0.08f);
        float[] before = (float[])source.Data.Clone();

        Pipeline.ProcessFrame(source, MonoParams());

        Assert.Equal(before, source.Data);
    }

    /// <summary>A darker negative is a brighter positive, in monochrome as in colour: the fold must
    /// not flatten the picture into one value.</summary>
    [Fact]
    public void Keeps_the_tonal_order_of_the_negative()
    {
        FrameParams p = MonoParams();

        ImageBuffer thin = Pipeline.ProcessFrame(Negative(4, 4, 0.30f, 0.22f, 0.08f), p);
        ImageBuffer dense = Pipeline.ProcessFrame(Negative(4, 4, 0.08f, 0.06f, 0.02f), p);

        Assert.True(dense.Data[0] > thin.Data[0]);
    }
}
