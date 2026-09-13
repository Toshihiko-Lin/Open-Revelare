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

    /// <summary>
    /// The float32 master of an HDR roll is the extended carrier, which only its own profile
    /// describes; a stale "omit" preference from an sRGB export must not reach it. This used to
    /// surface as an export failure rather than a forced checkbox.
    /// </summary>
    [Fact]
    public void Hdr_master_forces_embed_even_for_an_srgb_roll_with_an_omit_preference()
    {
        ExportIccUiState state = ExportIccUiPolicy.Resolve(
            ColorSpaces.Srgb,
            exportLinear: false,
            requestedEmbedIcc: false,
            hdrMaster: true);

        Assert.True(state.IsForced);
        Assert.True(state.EmbedIcc);
    }

    [Fact]
    public void Gain_map_jpeg_summary_names_the_limit_and_the_base_and_resolves_icc_against_the_base()
    {
        var options = new ExportOptions
        {
            Format = ExportFormat.Jpeg,
            ColorSpace = ColorSpaces.AdobeRgb.Name,   // the roll's, hidden under HDR and not the file's
            HdrLimitStops = 2.3,
            HdrBaseSpace = ColorSpaces.DisplayP3.Name,
            EmbedIcc = false,
        };

        Assert.True(options.WritesGainMap);
        Assert.Equal(ColorSpaces.DisplayP3, options.FileDisplaySpace);
        string summary = options.Summary();
        Assert.Contains("增益图 JPEG", summary, StringComparison.Ordinal);
        Assert.Contains("+2.3", summary, StringComparison.Ordinal);
        Assert.Contains("基底 DisplayP3", summary, StringComparison.Ordinal);
        Assert.Contains("强制嵌入 exact DisplayP3 ICC", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("AdobeRGB", summary, StringComparison.Ordinal);

        // An sRGB base keeps the ordinary omit choice: readers assume sRGB.
        options.HdrBaseSpace = ColorSpaces.Srgb.Name;
        Assert.Contains("省略 ICC", options.Summary(), StringComparison.Ordinal);

        // An unknown base name falls back to sRGB rather than to the roll's space.
        options.HdrBaseSpace = "Rec2020";
        Assert.Equal(ColorSpaces.Srgb, options.ResolvedHdrBaseSpace);
    }

    [Fact]
    public void Hdr_tiff_summary_is_the_float32_master_whatever_the_stale_icc_preference()
    {
        var options = new ExportOptions
        {
            Format = ExportFormat.Tiff16,
            HdrLimitStops = 1.5,
            EmbedIcc = false,
        };

        Assert.False(options.WritesGainMap);
        string summary = options.Summary();
        Assert.Contains("32-bit float TIFF", summary, StringComparison.Ordinal);
        Assert.Contains("HDR 母版 +1.5", summary, StringComparison.Ordinal);
        Assert.Contains("强制嵌入 exact ICC", summary, StringComparison.Ordinal);
    }

    public static TheoryData<ColorSpaceDef> NonSrgbDisplaySpaces => new()
    {
        ColorSpaces.DisplayP3,
        ColorSpaces.AdobeRgb,
        ColorSpaces.Rec709,
    };
}
