using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="NeutralPatch"/>: the mean the white-balance eyedropper reads. The rule with teeth is
/// that clipped samples are excluded — a patch catching a blown highlight or the white the sprocket
/// mask paints in would otherwise answer with a cast made of the clipping.
/// </summary>
public sealed class NeutralPatchTests
{
    /// <summary>A patch of <paramref name="count"/> samples of one encoded colour.</summary>
    private static float[] Patch(int count, float r, float g, float b)
    {
        var patch = new float[count * 3];
        for (int i = 0; i < count; i++)
        {
            patch[i * 3] = r; patch[i * 3 + 1] = g; patch[i * 3 + 2] = b;
        }
        return patch;
    }

    private static float[] Concat(params float[][] parts)
        => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void Reads_the_linear_mean_of_a_clean_patch()
    {
        var reading = NeutralPatch.MeanOfUnclipped(Patch(64, 0.5f, 0.5f, 0.5f), ColorSpaces.Srgb);

        Assert.NotNull(reading);
        Assert.Equal(0, reading!.Value.Clipped);
        Assert.Equal(64, reading.Value.Used);
        // The mean is LINEAR light, not the encoded 0.5 that went in. Four places, not more: the
        // render's decode goes through the shared LUT and the analytic curve here is its reference,
        // so they agree to about 1e-5 by construction rather than exactly.
        Assert.Equal(Srgb.SrgbToLinear(0.5f), reading.Value.Mean[0], 4);
    }

    /// <summary>
    /// The case this class exists for: half the patch is blown white. Including it would drag the
    /// mean up and — because the channels clip at different scene levels — sideways.
    /// </summary>
    [Fact]
    public void Excludes_clipped_samples_from_the_mean()
    {
        float[] patch = Concat(Patch(32, 0.4f, 0.5f, 0.6f), Patch(32, 1f, 1f, 1f));

        var reading = NeutralPatch.MeanOfUnclipped(patch, ColorSpaces.Srgb);

        Assert.NotNull(reading);
        Assert.Equal(32, reading!.Value.Clipped);
        Assert.Equal(32, reading.Value.Used);
        Assert.Equal(Srgb.SrgbToLinear(0.4f), reading.Value.Mean[0], 4);
        Assert.Equal(Srgb.SrgbToLinear(0.6f), reading.Value.Mean[2], 4);
    }

    /// <summary>One channel at the ceiling is enough: the sample no longer states a colour.</summary>
    [Fact]
    public void Treats_a_single_pinned_channel_as_clipped()
    {
        float[] patch = Concat(Patch(32, 0.5f, 0.5f, 0.5f), Patch(32, 0.5f, 0.5f, 1f));

        var reading = NeutralPatch.MeanOfUnclipped(patch, ColorSpaces.Srgb);

        Assert.Equal(32, reading!.Value.Clipped);
        Assert.Equal(Srgb.SrgbToLinear(0.5f), reading.Value.Mean[2], 4);
    }

    [Fact]
    public void Refuses_a_patch_with_too_little_left_to_read()
    {
        float[] patch = Concat(Patch(8, 0.5f, 0.5f, 0.5f), Patch(56, 1f, 1f, 1f));

        Assert.Null(NeutralPatch.MeanOfUnclipped(patch, ColorSpaces.Srgb));
    }

    [Fact]
    public void Refuses_a_patch_that_is_entirely_clipped()
    {
        Assert.Null(NeutralPatch.MeanOfUnclipped(Patch(64, 1f, 1f, 1f), ColorSpaces.Srgb));
    }

    /// <summary>
    /// The threshold is applied to the ENCODED values, so it means the same thing in every output
    /// space; the decode that follows is the space's own.
    /// </summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("AdobeRGB")]
    [InlineData("DisplayP3")]
    public void Applies_the_same_ceiling_in_every_output_space(string spaceName)
    {
        ColorSpaceDef space = ColorSpaces.ByName(spaceName, ColorSpaces.Srgb);
        float[] patch = Concat(Patch(32, 0.5f, 0.5f, 0.5f), Patch(32, 0.999f, 0.999f, 0.999f));

        var reading = NeutralPatch.MeanOfUnclipped(patch, space);

        Assert.Equal(32, reading!.Value.Clipped);
    }
}
