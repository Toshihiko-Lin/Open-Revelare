using OpenRevelare.Gui.Views;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The bottom colour bar is the only place a user is told that colour has stopped being reliable,
/// so what it says has to be something they can act on. These pin the split: the user-facing exits
/// carry the fallback and the way out, and the developer text is reachable only through the copy
/// button, only when something is actually wrong.
/// </summary>
public class ColorDiagnosticBadgeTests
{
    private const string PlatformWarning =
        "ColorProfileGetDisplayDefault failed with 0x80070002.";

    [Fact]
    public void Healthy_badge_is_unchanged_and_offers_no_hover()
    {
        string badge = ColorDiagnosticBadge.Format(
            "Windows Advanced Color · WideColorGamut",
            PresentationEncoding.LinearExtendedSrgbRgba16F,
            "VG27AQ1A",
            "WYSIWYG（显示端）",
            hasWarning: false,
            colorManagementUnavailable: false);

        Assert.Equal(
            "Windows Advanced Color · WideColorGamut · LinearExtendedSrgbRgba16F · " +
            "VG27AQ1A · WYSIWYG（显示端）",
            badge);
        Assert.Null(ColorDiagnosticBadge.FormatTooltip(
            hasWarning: false,
            colorManagementUnavailable: false));
    }

    /// <summary>
    /// The defect this class exists for: every exit used to hand back the same HRESULT. The badge
    /// must say what happened in the user's terms, the hover must say what to do — and neither may
    /// carry the platform's own error text, which now lives only behind 复制色彩诊断.
    /// </summary>
    [Fact]
    public void Unmanaged_display_states_the_fallback_and_the_remedy_and_nothing_technical()
    {
        string badge = ColorDiagnosticBadge.Format(
            "Windows unmanaged emergency",
            PresentationEncoding.UnmanagedEmergencySrgb8,
            "VG27AQ1A",
            "unmanaged",
            hasWarning: true,
            colorManagementUnavailable: true);

        Assert.DoesNotContain("0x8007", badge);
        Assert.DoesNotContain("ColorProfileGetDisplayDefault", badge);
        Assert.DoesNotContain(nameof(PresentationEncoding.UnmanagedEmergencySrgb8), badge);
        Assert.Contains("sRGB", badge);

        string tooltip = Assert.IsType<string>(ColorDiagnosticBadge.FormatTooltip(
            hasWarning: true,
            colorManagementUnavailable: true));

        Assert.Contains("如何恢复", tooltip);
        Assert.Contains("颜色配置文件", tooltip);
        Assert.DoesNotContain(PlatformWarning, tooltip);
        Assert.DoesNotContain("0x8007", tooltip);
        // The hover explains and stops; the diagnostics dump has a button of its own.
        Assert.DoesNotContain("OpenRevelare color diagnostics", tooltip);
    }

    /// <summary>
    /// A presenter that failed on an otherwise managed contract is a different fault with a
    /// different answer; assigning a display profile would not fix it, so it must not be suggested.
    /// </summary>
    [Fact]
    public void Presenter_failure_on_a_managed_contract_does_not_advise_assigning_a_profile()
    {
        string tooltip = Assert.IsType<string>(ColorDiagnosticBadge.FormatTooltip(
            hasWarning: true,
            colorManagementUnavailable: false));

        Assert.DoesNotContain("颜色配置文件", tooltip);
        Assert.Contains("重新启动", tooltip);
    }

    /// <summary>
    /// A composition failure has no display contract to name, so the badge must not open with the
    /// separator that a named mode would have left behind.
    /// </summary>
    [Fact]
    public void Composition_failure_badge_carries_no_empty_mode_prefix()
    {
        string badge = ColorDiagnosticBadge.Format(
            string.Empty,
            default,
            string.Empty,
            string.Empty,
            hasWarning: true,
            colorManagementUnavailable: false);

        Assert.StartsWith("预览显示异常", badge);
    }
}
