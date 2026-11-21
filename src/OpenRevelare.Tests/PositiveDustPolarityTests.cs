using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PositiveDustPolarityTests
{
    [Theory]
    [InlineData(0, false, "auto")]
    [InlineData(0, true, "dark")]
    [InlineData(-1, true, "dark")]
    [InlineData(1, false, "bright")]
    [InlineData(1, true, "bright")]
    [InlineData(2, false, "dark")]
    [InlineData(2, true, "dark")]
    public void Auto_on_a_slide_searches_dark_dust(int selectedIndex, bool positiveFilm, string expected)
        => Assert.Equal(expected, PureNegComponent.PolarityForFilm(selectedIndex, positiveFilm));
}
