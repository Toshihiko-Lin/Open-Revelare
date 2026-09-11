using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ExportIccUiPolicyTests
{
    [Fact]
    public void Exact_display_referred_srgb_preserves_an_explicit_omit_or_embed_choice()
    {
        ExportIccUiState omitted = ExportIccUiPolicy.Resolve(
            ColorSpaces.Srgb,
            exportLinear: false,
            requestedEmbedIcc: false);
        ExportIccUiState embedded = ExportIccUiPolicy.Resolve(
            ColorSpaces.Srgb,
            exportLinear: false,
            requestedEmbedIcc: true);

        Assert.True(omitted.CanChange);
        Assert.False(omitted.IsForced);
        Assert.False(omitted.EmbedIcc);
        Assert.True(embedded.CanChange);
        Assert.True(embedded.EmbedIcc);
    }

    [Theory]
    [MemberData(nameof(NonSrgbDisplaySpaces))]
    public void Non_exact_srgb_display_output_normalizes_a_stale_false_preset_to_embed(
        ColorSpaceDef outputSpace)
    {
        ExportIccUiState state = ExportIccUiPolicy.Resolve(
            outputSpace,
            exportLinear: false,
            requestedEmbedIcc: false);

        Assert.True(state.IsForced);
        Assert.False(state.CanChange);
        Assert.True(state.EmbedIcc);
    }

    [Fact]
    public void Scene_linear_export_forces_embed_even_when_the_display_preference_is_srgb_omit()
    {
        ExportIccUiState state = ExportIccUiPolicy.Resolve(
            ColorSpaces.Srgb,
            exportLinear: true,
            requestedEmbedIcc: false);

        Assert.True(state.IsForced);
        Assert.False(state.CanChange);
        Assert.True(state.EmbedIcc);
    }

    [Fact]
    public void A_space_merely_named_srgb_is_not_treated_as_the_exact_builtin()
    {
        ColorSpaceDef lookalike = ColorSpaces.Srgb with { Gamma = 2.4 };

        ExportIccUiState state = ExportIccUiPolicy.Resolve(
            lookalike,
            exportLinear: false,
            requestedEmbedIcc: false);

        Assert.True(state.IsForced);
        Assert.True(state.EmbedIcc);
    }

    [Fact]
    public void Wide_gamut_summary_reports_the_forced_exact_profile_even_for_a_stale_false_value()
    {
        var options = new ExportOptions
        {
            ColorSpace = ColorSpaces.DisplayP3.Name,
            EmbedIcc = false,
        };

        string summary = options.Summary();

        Assert.Contains("DisplayP3", summary, StringComparison.Ordinal);
        Assert.Contains("强制嵌入 exact DisplayP3 ICC", summary, StringComparison.Ordinal);
    }

    public static TheoryData<ColorSpaceDef> NonSrgbDisplaySpaces => new()
    {
        ColorSpaces.DisplayP3,
        ColorSpaces.AdobeRgb,
        ColorSpaces.Rec709,
    };
}
