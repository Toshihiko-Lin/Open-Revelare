using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="ExportNaming"/>. The interesting cases are all failures of politeness rather than of
/// arithmetic: a roll with half its fields blank is the normal state of a roll, and a template that
/// answers it with "__003" or with a name the filesystem refuses is the bug.
/// </summary>
public sealed class ExportNamingTests
{
    private static ExportNaming.Fields Roll(
        string roll = "Portra400-03", string rollNumber = "03", string camera = "F3",
        string film = "Portra 400", string date = "2026-09-12", string original = "_DSC7659",
        int sequence = 1)
        => new(roll, rollNumber, camera, film, date, original, sequence);

    [Fact]
    public void Defaults_to_the_scans_own_name()
    {
        Assert.Equal("_DSC7659", ExportNaming.Expand(null, Roll()));
        Assert.Equal("_DSC7659", ExportNaming.Expand("   ", Roll()));
        Assert.Equal("_DSC7659", ExportNaming.Expand(ExportNaming.Default, Roll()));
    }

    [Fact]
    public void Spends_every_token_it_is_given()
    {
        // "…-12_" meeting "_DSC7659" is a repeated separator, and collapses to one.
        Assert.Equal("Portra400-03_03_F3_Portra 400_2026-09-12_DSC7659_007",
                     ExportNaming.Expand("{Roll}_{RollNo}_{Camera}_{Film}_{Date}_{Original}_{Seq}",
                                         Roll(sequence: 7)));
    }

    /// <summary>Three digits so a file manager sorts frame 2 before frame 10, as the film does.</summary>
    [Theory]
    [InlineData(1, "001")]
    [InlineData(12, "012")]
    [InlineData(1234, "1234")]
    public void Pads_the_frame_number(int sequence, string expected)
    {
        Assert.Equal($"roll_{expected}", ExportNaming.Expand("{Roll}_{Seq}", Roll(roll: "roll", sequence: sequence)));
    }

    /// <summary>
    /// The case that decides whether templates are usable at all: a roll where the camera was never
    /// filled in must not export as "Portra400-03__001".
    /// </summary>
    [Fact]
    public void Leaves_no_trace_of_a_field_that_is_empty()
    {
        Assert.Equal("Portra400-03_001",
                     ExportNaming.Expand("{Roll}_{Camera}_{Seq}", Roll(camera: "")));
    }

    [Fact]
    public void Reads_a_token_in_any_case()
    {
        Assert.Equal("Portra400-03_001", ExportNaming.Expand("{roll}_{SEQ}", Roll()));
    }

    /// <summary>A film stock is free text and "Kodak Gold 200/2" is a thing people type.</summary>
    [Fact]
    public void Replaces_characters_a_filename_cannot_hold()
    {
        string name = ExportNaming.Expand("{Film}", Roll(film: "Kodak/Gold:200"));

        Assert.Equal("Kodak_Gold_200", name);
        foreach (char bad in Path.GetInvalidFileNameChars()) Assert.DoesNotContain(bad, name);
    }

    /// <summary>Collapsing repeats must not eat a separator the template asked for.</summary>
    [Fact]
    public void Keeps_a_separator_pattern_that_was_meant()
    {
        Assert.Equal("Portra400-03 - 001", ExportNaming.Expand("{Roll} - {Seq}", Roll()));
    }

    [Fact]
    public void Falls_back_to_the_original_when_the_template_yields_nothing()
    {
        Assert.Equal("_DSC7659", ExportNaming.Expand("{Camera}{Film}", Roll(camera: "", film: "")));
    }

    [Fact]
    public void Falls_back_to_the_frame_number_when_even_that_is_missing()
    {
        Assert.Equal("frame-004",
                     ExportNaming.Expand("{Camera}", Roll(camera: "", original: "", sequence: 4)));
    }

    /// <summary>
    /// <c>NUL.tiff</c> cannot be created on Windows at all, and an export that writes nothing while
    /// reporting success is the worst outcome available.
    /// </summary>
    [Theory]
    [InlineData("NUL")]
    [InlineData("com1")]
    [InlineData("AUX")]
    public void Steps_around_a_reserved_device_name(string camera)
    {
        Assert.Equal(camera + "_", ExportNaming.Expand("{Camera}", Roll(camera: camera)));
    }

    /// <summary>
    /// The dialog offers the schemes by NAME, so a scheme with no name of its own would appear in
    /// the picker as a second 自定义… — indistinguishable from the escape hatch, and selecting it
    /// would then reveal the template box. Adding a template to
    /// <see cref="ExportOptions.NameTemplates"/> without naming it in
    /// <see cref="ExportOptions.NameTemplateName"/> is the way that happens.
    /// </summary>
    [Fact]
    public void Every_offered_naming_scheme_is_named()
    {
        string custom = ExportOptions.NameTemplateName(null);
        foreach (string template in ExportOptions.NameTemplates)
            Assert.NotEqual(custom, ExportOptions.NameTemplateName(template));

        Assert.Equal(ExportOptions.NameTemplates.Count,
                     ExportOptions.NameTemplates
                         .Select(ExportOptions.NameTemplateName).Distinct().Count());
    }

    /// <summary>Every scheme has to produce a usable name for an ordinary roll, and a different one
    /// per frame — two frames sharing a name is a batch that overwrites itself.</summary>
    [Fact]
    public void Every_offered_naming_scheme_expands_to_a_distinct_usable_name()
    {
        foreach (string template in ExportOptions.NameTemplates)
        {
            string first = ExportNaming.Expand(template, Roll(sequence: 1, original: "_DSC7659"));
            string second = ExportNaming.Expand(template, Roll(sequence: 2, original: "_DSC7660"));
            Assert.NotEqual("", first);
            Assert.NotEqual(first, second);
        }
    }
}
