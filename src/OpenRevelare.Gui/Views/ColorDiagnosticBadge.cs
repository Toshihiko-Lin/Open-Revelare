using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.Views;

/// <summary>Only actionable display failures belong in the daily UI. Technical details live in Diagnostics.</summary>
internal static class ColorDiagnosticBadge
{
    internal static string Format(
        string diagnosticName,
        PresentationEncoding encoding,
        string monitor,
        string guarantee,
        bool hasWarning,
        bool colorManagementUnavailable)
    {
        if (!hasWarning)
            return string.Empty;

        // Deliberately short: this TextBlock is capped at MaxWidth 560 with CharacterEllipsis, and
        // a sentence that gets trimmed mid-remedy is worse than one that points at the tooltip.
        if (colorManagementUnavailable)
            return Loc.T("颜色管理不可用 · 正按 sRGB 显示，颜色可能不准 · 悬停查看如何恢复");

        // A composition failure has no contract to name, so it passes no mode.
        string problem = Loc.T("预览显示异常 · 悬停查看详情");
        return problem;
    }

    internal static string? FormatTooltip(bool hasWarning, bool colorManagementUnavailable)
        => FormatTooltip(hasWarning, colorManagementUnavailable, displaySpace: null);

    internal static string? FormatTooltip(bool hasWarning, bool colorManagementUnavailable, string? displaySpace)
    {
        if (!hasWarning) return null;

        string what = colorManagementUnavailable
            ? Loc.T("颜色管理不可用：取不到这台显示器的颜色配置文件，画面正按标准 sRGB 显示，颜色可能与实际不符。")
            : Loc.T("预览显示异常：画面可能没有按色彩管理的结果显示。");
        string how = colorManagementUnavailable
            ? Loc.T("如何恢复：在 Windows「设置 → 系统 → 显示 → 高级显示」中开启「自动管理应用的颜色」；" +
                    "或在控制面板的「颜色管理」中为这台显示器指定一个颜色配置文件。")
            : Loc.T("可先重新启动应用；若反复出现，请从【帮助 → 诊断 → 复制诊断】取得诊断后反馈。");

        return what + "\n\n" + how;
    }

    /// <summary>
    /// The display space in the user's terms: who maps the picture onto the panel, into what,
    /// and how far above SDR white the panel goes. Every number comes from the contract the
    /// presenter is bound to — nothing here is a preference, which is the point of saying it.
    /// </summary>
    internal static string DescribeDisplaySpace(DisplayContract contract, string monitor)
    {
        ArgumentNullException.ThrowIfNull(contract);
        string mapping = contract.TransformOwner switch
        {
            FinalTransformOwner.SystemCompositor =>
                Loc.T("由系统合成器把画面映射到面板原生色域：面板有多宽就显示多宽，输出空间之外的颜色不会先被裁掉。"),
            FinalTransformOwner.Application =>
                Loc.T("由本应用按显示器的颜色配置文件转换后送显：显示范围以该配置文件描述的面板色域为准。"),
            _ => Loc.T("未做显示端色彩管理：画面按 sRGB 送显。"),
        };
        string white = contract.ExtendedHeadroom > 1f
            ? Loc.F($"SDR 白 {contract.SdrReferenceWhite:0} nits，纸白之上还有 +{MathF.Log2(contract.ExtendedHeadroom):0.0} 档余量。")
            : Loc.F($"SDR 白 {contract.SdrReferenceWhite:0} nits，纸白之上没有余量（非 HDR 模式）。");
        return Loc.F($"显示空间（探测所得，不可选）：{monitor} · {contract.DiagnosticName}")
            + "\n" + mapping
            + "\n" + white
            + "\n" + Loc.T("它只决定你在这块屏上看到什么，不影响渲染与导出（I5）；导出写的是【输出空间】。");
    }
}
