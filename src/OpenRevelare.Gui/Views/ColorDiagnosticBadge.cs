using OpenRevelare.Gui.Services;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// The two strings the bottom colour bar shows. Pure, so the one rule that matters can be tested:
/// WHAT THE USER IS SHOWN MUST BE SOMETHING THE USER CAN ACT ON.
///
/// The fallback itself was already honest: an unmanaged contract cannot be constructed without a
/// visible warning (<see cref="DisplayContract"/> enforces it), and the emergency path really does
/// encode sRGB, so the picture is no worse than any unmanaged application's. What was missing is
/// the other half of a warning's job. The badge printed the platform's own failure text —
/// "WARNING: ColorProfileGetDisplayDefault failed with 0x80070002." — and the tooltip printed the
/// thirty-line diagnostics dump, so all three exits (badge, hover, 复制色彩诊断) handed back the
/// same developer-facing fact and none of them said what to do about it. Telling someone their
/// colour is unreliable while offering no route back to reliable colour is anxiety, not a warning.
///
/// The remedy is one settings dialog away and the application already knows it, so: the badge
/// states the fallback in the user's terms and the tooltip gives the way out. The HRESULT keeps
/// its place in 复制色彩诊断 — where a bug report needs it, and where nobody meets it by accident.
/// That entry point lives in the Help menu rather than in the status bar: most colour reports
/// ("the export is off", "this frame is wrong") arrive while the display chain is perfectly
/// healthy, so the channel has to exist at all times, and precisely then the status bar should
/// be showing the user nothing they would have to decode.
///
/// WHY THE REMEDY IS CHOSEN FROM THE CONTRACT AND NOT FROM THE MESSAGE. The warning string is a
/// union of two unrelated causes: an unmanaged display contract, and a presenter that failed on a
/// contract that was otherwise fine. They need different advice, and matching on the text to tell
/// them apart would bind this copy to the exact wording of a platform error. The typed owner
/// already separates them, so it is what decides which sentence is shown.
/// </summary>
internal static class ColorDiagnosticBadge
{
    /// <summary>
    /// The single line under the preview. The healthy form is unchanged — it is read by someone
    /// who wants the current mode at a glance, and its terms are the ones the diagnostics use.
    /// </summary>
    internal static string Format(
        string diagnosticName,
        PresentationEncoding encoding,
        string monitor,
        string guarantee,
        bool hasWarning,
        bool colorManagementUnavailable)
    {
        if (!hasWarning)
            return $"{diagnosticName} · {encoding} · {monitor} · {guarantee}";

        // Deliberately short: this TextBlock is capped at MaxWidth 560 with CharacterEllipsis, and
        // a sentence that gets trimmed mid-remedy is worse than one that points at the tooltip.
        if (colorManagementUnavailable)
            return Loc.T("颜色管理不可用 · 正按 sRGB 显示，颜色可能不准 · 悬停查看如何恢复");

        // A composition failure has no contract to name, so it passes no mode.
        string problem = Loc.T("预览显示异常 · 悬停查看详情");
        return string.IsNullOrEmpty(diagnosticName) ? problem : $"{diagnosticName} · {problem}";
    }

    /// <summary>
    /// Hover text: the explanation and the way out, and nothing else. It used to be the whole
    /// thirty-line diagnostics dump, which is what a hover is worst at — unskimmable, unselectable,
    /// and gone the moment the pointer moves. The diagnostics have a button; a tooltip that
    /// duplicates it only guarantees the user meets the developer text without asking for it.
    ///
    /// Null when there is nothing wrong: with no warning there is nothing here a hover could add
    /// that the badge does not already say.
    /// </summary>
    internal static string? FormatTooltip(bool hasWarning, bool colorManagementUnavailable)
        => FormatTooltip(hasWarning, colorManagementUnavailable, displaySpace: null);

    /// <summary>
    /// The healthy form carries the DISPLAY SPACE. It is the one colour fact the user cannot set
    /// anywhere — it is discovered from the contract, and I5 keeps it out of the render — so it
    /// had no home in the controls, and the output-space picker was being read as if it were
    /// this. Here it sits next to the mode it belongs to.
    /// </summary>
    internal static string? FormatTooltip(bool hasWarning, bool colorManagementUnavailable, string? displaySpace)
    {
        if (!hasWarning) return displaySpace;

        string what = colorManagementUnavailable
            ? Loc.T("颜色管理不可用：取不到这台显示器的颜色配置文件，画面正按标准 sRGB 显示，颜色可能与实际不符。")
            : Loc.T("预览显示异常：画面可能没有按色彩管理的结果显示。");
        string how = colorManagementUnavailable
            ? Loc.T("如何恢复：在 Windows「设置 → 系统 → 显示 → 高级显示」中开启「自动管理应用的颜色」；" +
                    "或在控制面板的「颜色管理」中为这台显示器指定一个颜色配置文件。")
            : Loc.T("可先重新启动应用；若反复出现，请从【帮助 → 复制色彩诊断】取得诊断后反馈。");

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
