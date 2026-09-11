using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The cube's output characterization comes from the file's own header.
///
/// <para>
/// Before this, the two built-in stocks were Rec709 because a human read their header comment and
/// hard-coded the answer, while the parser discarded that identical line for every other file — so
/// a user's Resolve export was refused for saying exactly what the built-ins say. These tests pin
/// both halves: the header is read, and a file that declares nothing is still refused.
/// </para>
/// </summary>
public sealed class LutOutputDeclarationTests
{
    [Theory]
    [InlineData(":kodak-2383")]
    [InlineData(":fujifilm-3513di")]
    public void Built_ins_declare_their_own_Rec709_output(string id)
    {
        // Deliberately against the real shipped assets: this is what lets PrintLuts stop
        // hard-coding the answer, so a parsing regression has to fail here rather than silently
        // turn every built-in stock into an unusable cube at render time.
        CubeLut lut = PrintLuts.Resolve(id)!;

        Assert.NotNull(lut);
        Assert.Equal(LutOutputEncoding.Rec709, lut.OutputEncoding);
    }

    [Fact]
    public void A_resolve_style_header_characterizes_a_user_cube()
    {
        CubeLut lut = ParseCube(
            "# Resolve Film Look LUT",
            "#   Input: Cineon Log",
            "# Display: ITU-Rec.709, Gamma 2.4");

        Assert.Equal(LutOutputEncoding.Rec709, lut.OutputEncoding);
    }

    [Fact]
    public void A_cube_that_declares_nothing_stays_unknown()
    {
        CubeLut lut = ParseCube("# just some cube");

        Assert.Equal(LutOutputEncoding.Unknown, lut.OutputEncoding);
    }

    [Theory]
    // 709 primaries with a different curve is evidence AGAINST the pair Rec709 names, not for it.
    [InlineData("# Display: ITU-Rec.709, Gamma 2.2")]
    // A gamma with no colour space named proves nothing about the primaries.
    [InlineData("# Display: Gamma 2.4")]
    // Not a display declaration at all: the OUTPUT line names the film stock, not an encoding.
    [InlineData("#  Output: Kodak 2383 film stock 'look' with D65 White Point")]
    public void A_partial_or_mismatched_declaration_is_not_forced_into_Rec709(string header)
    {
        CubeLut lut = ParseCube(header);

        Assert.Equal(LutOutputEncoding.Unknown, lut.OutputEncoding);
    }

    [Fact]
    public void A_trusted_argument_outranks_the_file()
    {
        CubeLut lut = ParseCube(
            new[] { "# nothing declared here" },
            LutOutputEncoding.Rec709);

        Assert.Equal(LutOutputEncoding.Rec709, lut.OutputEncoding);
    }

    [Fact]
    public void A_later_comment_cannot_redefine_the_header()
    {
        // The first display line decides. A file that plainly said 2.2 must not be rescued into
        // Rec709 by some unrelated later comment that happens to mention 709 and 2.4.
        CubeLut lut = ParseCube(
            "# Display: ITU-Rec.709, Gamma 2.2",
            "# Display: ITU-Rec.709, Gamma 2.4");

        Assert.Equal(LutOutputEncoding.Unknown, lut.OutputEncoding);
    }

    private static CubeLut ParseCube(params string[] header) =>
        ParseCube(header, LutOutputEncoding.Unknown);

    private static CubeLut ParseCube(string[] header, LutOutputEncoding trusted)
    {
        var text = new System.Text.StringBuilder();
        foreach (string line in header) text.AppendLine(line);
        text.AppendLine("TITLE \"probe\"");
        text.AppendLine("LUT_3D_SIZE 2");
        for (int i = 0; i < 8; i++) text.AppendLine("0.5 0.5 0.5");

        using var reader = new StringReader(text.ToString());
        return CubeLut.Parse(reader, "probe", LutInputEncoding.Cineon, trusted);
    }
}
