using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The cube's INPUT characterization comes from the file's own header too.
///
/// <para>
/// D-018 taught the parser to read the output declaration a Resolve export writes. The same header
/// states the input on the line above it — <c>"#   Input: Cineon Log"</c> — and the parser was
/// discarding that one as well, so every cube was fed Cineon-encoded data regardless of what it
/// was authored against. That failure is worse than the output one it mirrors: an ACEScct or Log-C
/// cube fed Cineon does not error, it renders a plausible picture with wrong colour.
/// </para>
///
/// <para>
/// These tests pin the three outcomes: a declared Cineon input is EVIDENCE (not convention), a
/// declared non-Cineon input is REFUSED, and a file that declares nothing keeps rendering exactly
/// as it did before.
/// </para>
/// </summary>
public sealed class LutInputDeclarationTests
{
    [Theory]
    [InlineData(":kodak-2383")]
    [InlineData(":fujifilm-3513di")]
    public void Built_ins_declare_their_own_Cineon_input(string id)
    {
        // Against the real shipped assets, for the reason its output-side twin gives: PrintLuts no
        // longer hard-codes the answer, so a parsing regression has to fail here rather than turn
        // every built-in stock into a conventional guess nobody notices.
        CubeLut lut = PrintLuts.Resolve(id)!;

        Assert.NotNull(lut);
        Assert.Equal(LutInputEncoding.Cineon, lut.InputEncoding);
        Assert.Equal(LutInputEncodingSource.Declared, lut.InputEncodingSource);
    }

    [Fact]
    public void A_resolve_style_header_declares_the_input()
    {
        CubeLut lut = ParseCube(
            "# Resolve Film Look LUT",
            "#   Input: Cineon Log ",
            "#        : floating point data (range 0.0 - 1.0)",
            "# Display: ITU-Rec.709, Gamma 2.4");

        Assert.Equal(LutInputEncoding.Cineon, lut.InputEncoding);
        Assert.Equal(LutInputEncodingSource.Declared, lut.InputEncodingSource);
    }

    [Fact]
    public void A_cube_that_declares_nothing_still_renders_as_Cineon()
    {
        // The one assumption that survives. Rejecting these would break every cube that works
        // today for no new information, so the behaviour is unchanged — only now it is labelled.
        CubeLut lut = ParseCube("# just some cube");

        Assert.Equal(LutInputEncoding.Cineon, lut.InputEncoding);
        Assert.Equal(LutInputEncodingSource.ConventionalDefault, lut.InputEncodingSource);
    }

    [Theory]
    [InlineData("#   Input: ACEScct")]
    [InlineData("#   Input: ARRI Log-C (LogC3 / AWG)")]
    [InlineData("# Input: Linear")]
    [InlineData("#Input:S-Log3 / S-Gamut3.Cine")]
    public void A_cube_authored_against_another_encoding_is_refused(string header)
    {
        // This is the whole point: before, each of these was silently fed Cineon and produced a
        // wrong picture that looked fine.
        var ex = Assert.Throws<InvalidDataException>(() => ParseCube(header));

        // The file's own words go back to the user — "this LUT wants ACEScct" is actionable in a
        // way that "unsupported LUT" is not.
        Assert.Contains("Cineon", ex.Message);
    }

    [Theory]
    // "Input range" is not an input declaration, and print-film cubes are full of it. Treating it
    // as one would reject a cube that works today — the regression this asymmetry exists to avoid.
    [InlineData("# Input range: 0.0 1.0")]
    [InlineData("# Generated from input.dpx")]
    [InlineData("# inputs were graded in Resolve")]
    public void A_comment_that_merely_mentions_input_is_not_a_declaration(string header)
    {
        CubeLut lut = ParseCube(header);

        Assert.Equal(LutInputEncoding.Cineon, lut.InputEncoding);
        Assert.Equal(LutInputEncodingSource.ConventionalDefault, lut.InputEncodingSource);
    }

    [Fact]
    public void A_later_comment_cannot_redefine_the_input()
    {
        // Same rule as the output side: the first declaration decides, whether or not it is one
        // this build can honour. A file that plainly said ACEScct must not be rescued by a later
        // line that happens to say Cineon.
        Assert.Throws<InvalidDataException>(() => ParseCube(
            "#   Input: ACEScct",
            "#   Input: Cineon Log"));
    }

    [Fact]
    public void A_caller_that_knows_the_stock_outranks_the_file()
    {
        // Out-of-band knowledge wins, exactly as it does for the output side — otherwise a caller
        // holding a trusted asset manifest could not load a cube with a sloppy header at all.
        using var reader = new StringReader(CubeText("#   Input: ACEScct"));
        CubeLut lut = CubeLut.Parse(reader, "probe", LutInputEncoding.Cineon);

        Assert.Equal(LutInputEncoding.Cineon, lut.InputEncoding);
        Assert.Equal(LutInputEncodingSource.CallerSpecified, lut.InputEncodingSource);
    }

    [Fact]
    public void The_render_path_degrades_a_refused_cube_to_pass_through()
    {
        // PrintLuts.Resolve must keep its contract that a bad file renders as pass-through rather
        // than throwing from inside a frame render; only Validate lets the reason through.
        string path = Path.Combine(Path.GetTempPath(), $"or-lut-{Guid.NewGuid():N}.cube");
        File.WriteAllText(path, CubeText("#   Input: ACEScct"));
        try
        {
            Assert.Null(PrintLuts.Resolve(path));
            Assert.Throws<InvalidDataException>(() => PrintLuts.Validate(path));
        }
        finally
        {
            PrintLuts.Forget(path);
            File.Delete(path);
        }
    }

    private static CubeLut ParseCube(params string[] header)
    {
        using var reader = new StringReader(CubeText(header));
        return CubeLut.Parse(reader, "probe");
    }

    private static string CubeText(params string[] header)
    {
        var text = new System.Text.StringBuilder();
        foreach (string line in header) text.AppendLine(line);
        text.AppendLine("TITLE \"probe\"");
        text.AppendLine("LUT_3D_SIZE 2");
        for (int i = 0; i < 8; i++) text.AppendLine("0.5 0.5 0.5");
        return text.ToString();
    }
}
