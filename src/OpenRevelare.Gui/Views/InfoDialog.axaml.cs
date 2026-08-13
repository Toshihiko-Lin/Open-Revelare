using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenRevelare.Gui.Views;

/// <summary>Reusable modal text dialog — hosts the Help / About(License) / onboarding content.</summary>
public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
    }

    public InfoDialog(string title, string body) : this()
    {
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private Action? _action;

    private void OnActionClick(object? sender, RoutedEventArgs e)
    {
        _action?.Invoke();
        Close();
    }

    /// <summary>
    /// Adds a primary button beside 关闭 and relabels the latter — e.g. 前往下载 / 稍后再说 on the
    /// update notice, matching the Python dialog's two-button layout.
    /// </summary>
    public InfoDialog WithAction(string label, string closeLabel, Action action)
    {
        _action = action;
        ActionButton.Content = label;
        ActionButton.IsVisible = true;
        // Enter = the primary action, but only on dialogs that have one — a hidden default
        // button would otherwise swallow Enter on 快捷键 / 关于.
        ActionButton.IsDefault = true;
        CloseButton.Content = closeLabel;
        return this;
    }

    private Action? _secondaryAction;

    private void OnSecondaryActionClick(object? sender, RoutedEventArgs e)
    {
        _secondaryAction?.Invoke();
        Close();
    }

    /// <summary>
    /// Adds a third button, between 关闭 and the primary one — the update notice's 国内镜像下载.
    /// Deliberately not IsDefault: Enter stays with <see cref="WithAction"/>'s button.
    /// </summary>
    public InfoDialog WithSecondaryAction(string label, Action action)
    {
        _secondaryAction = action;
        SecondaryActionButton.Content = label;
        SecondaryActionButton.IsVisible = true;
        return this;
    }

    private static string Version => Services.AppInfo.Version;

    // ONE translation entry, not thirty: the compiler folds these adjacent literals into a single
    // constant before Loc.T ever sees them, so the table key is the whole page. Wrapping them
    // line by line would ask a translator to render a fragment with no idea what follows it,
    // and would forbid an English version from breaking its lines anywhere else.
    //
    // Shortcuts ONLY. The workflow walkthrough that used to sit here restated GUIDE.md from
    // memory, and drifted from it — it still described 反差 = 相纸号数 after the endpoint model
    // replaced grade. One account of the workflow, in the document that is rendered with its
    // headings and tables; this dialog answers the one question a document is bad at, which is
    // "what is the key for this".
    public static InfoDialog Help() => Monospace(new(Loc.T("快捷键"), Loc.T(
        "Ctrl+N / Ctrl+O    新建卷 / 添加图像\n" +
        "Ctrl+E             导出当前帧\n" +
        "Ctrl+Z / Ctrl+Y    撤销 / 重做\n" +
        "N                  临时查看负片（对准片基用）\n" +
        "K                  前后对比（不含 Stage 2）\n" +
        "F / Ctrl+1         适合窗口 / 实际像素 100%\n" +
        "G / D              图库 / 修片 切换\n" +
        "Esc                取消当前采样\n" +
        "Ctrl+Shift+T       切换浅色 / 深色主题\n" +
        "Ctrl+,             偏好设置\n\n" +
        "采样：点亮采样按钮（带虚线方框图标）后在预览上拖框，Esc 取消。\n" +
        "滑块：双击标签重置为默认值。\n" +
        "预览：缩放后左键拖动平移，滚轮缩放。\n\n" +
        "完整操作说明见 帮助 → 操作指引 / 技术原理。")));

    /// <summary>
    /// Sets the body in a monospace face — the key column is aligned with spaces, which only lines
    /// up in a fixed-pitch font. Applied here rather than in the XAML because the same BodyText
    /// carries 关于 and the update notices, which are prose and read better proportional.
    /// </summary>
    private static InfoDialog Monospace(InfoDialog dlg)
    {
        dlg.BodyText.FontFamily =
            new FontFamily("Consolas, Cascadia Code, DejaVu Sans Mono, PingFang SC, Microsoft YaHei, monospace");
        return dlg;
    }

    /// <summary>Reveals the app mark in the header — About only.</summary>
    private InfoDialog WithLogo()
    {
        Logo.IsVisible = true;
        return this;
    }

    public static InfoDialog About() => new InfoDialog(Loc.T("关于 OpenRevelare"),
        $"OpenRevelare v{Version}\n\n" + Loc.T(
        "彩色负片去色罩工具，C# / .NET 8 + Avalonia。\n\n" +
        "自由软件，以 GNU GPL v3 授权发布，随包附有完整许可文本。\n" +
        "源码：https://github.com/Toshihiko-Lin/Open-Revelare\n\n" +
        "开源组件：\n" +
        "· Avalonia UI（MIT）\n" +
        "· BitMiracle.LibTiff.NET（BSD）— 16-bit TIFF\n" +
        "· SixLabors.ImageSharp（Six Labors Split License）— JPEG\n" +
        "· Sdcb.LibRaw / LibRaw（LGPL/CDDL）— RAW 解码\n" +
        "· Microsoft.ML.OnnxRuntime（MIT）— 智能白平衡\n" +
        "· CommunityToolkit.Mvvm（MIT）\n\n" +
        // LGPL-2.1 的 LibRaw 要求随二进制给出许可声明，光在这里列个名字不够，
        // 完整文本随包分发（Windows 在安装目录、Linux 在 AppImage 内、mac 在
        // OpenRevelare.app/Contents/Resources）。这一行是指路牌，别删。
        "完整第三方声明见随附的 THIRD_PARTY_NOTICES.txt。\n\n" +
        "「智能白平衡」用到的 net_awb.onnx 权重（Deep White-Balance Editing,\n" +
        "CVPR 2020）按 CC BY-NC-SA 4.0 单独授权，不在本程序的 GPL-3.0 范围内。")).WithLogo();
}
