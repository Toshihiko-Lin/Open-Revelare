using OpenRevelare.Gui.Models;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class CropAspectPresetTests
{
    [Theory]
    [InlineData(3000, 2000, 1, 1.5)]
    [InlineData(2000, 3000, 1, 2.0 / 3)]
    [InlineData(3500, 3700, 2, 35.0 / 37)]
    [InlineData(3000, 4000, 3, 0.75)]
    [InlineData(4000, 3000, 5, 4.0 / 3)]
    [InlineData(2000, 2000, 6, 1.0)]
    [InlineData(2999, 2000, 1, 1.5)]
    public void Saved_pixel_dimensions_select_the_matching_format_in_either_orientation(
        double width, double height, int index, double aspect)
    {
        var match = CropAspectPresets.Match(width, height);
        Assert.Equal(index, match.Index);
        Assert.Equal(aspect, match.Aspect!.Value, 10);
    }

    [Theory]
    [InlineData(3500, 3600)]
    [InlineData(0, 2000)]
    [InlineData(double.NaN, 2000)]
    public void Custom_or_unknown_dimensions_show_free(double width, double height)
        => Assert.Equal((0, (double?)null), CropAspectPresets.Match(width, height));
}
