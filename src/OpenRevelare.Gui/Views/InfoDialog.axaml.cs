using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

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
        // button would otherwise swallow Enter on 使用帮助 / 关于.
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
    // line by line would ask a translator to render "· 胶片条 ——" with no idea what follows it,
    // and would forbid an English version from breaking its lines anywhere else.
    public static InfoDialog Help() => new(Loc.T("使用帮助"), Loc.T(
        "OpenRevelare — 彩色负片去色罩工具（复刻 NegativeConvert）。\n\n" +
        "基本流程：\n" +
        "1. 文件 → 新建卷…（Ctrl+N）选择一张或多张负片（多选 = 整卷），左侧胶片条切换帧。\n" +
        "2. 整卷校准（Stage 1）：\n" +
        "   · 框选片基去掉橙色罩；框选/自动 D_max 定白点端。\n" +
        "   · 白平衡 wb_offset（暗部加性）/ wb_high（亮部乘性），或用「最亮点=白 / 智能白平衡」。\n" +
        "   · 反差 = 相纸号数（软/标准/硬/手动 grade+pivot）。\n" +
        "   · 镜头校正（畸变/暗角/LCC 平场）、齿孔遮罩。\n" +
        "3. 帧编辑（Stage 2）：色温/色调、曝光、黑白场、阴影/高光、反差、饱和度、色调曲线。\n" +
        "4. 应用到整卷（面板底部按钮）／复制·应用到勾选帧（编辑 菜单或胶片条右键）；\n" +
        "   用「选择同步项」控制携带哪些字段。\n" +
        "5. 文件 → 导出当前帧…（Ctrl+E）／导出整卷…／导出印样…\n" +
        "   （16-bit TIFF，含 ICC；导出按钮右侧的箭头也能开这三项）\n" +
        "   印样窗口右侧填「卷信息」（相机/胶卷/ISO/卷号/冲洗店/工艺/日期/地点/备注），\n" +
        "   会作为一条标识条烧在印样底部，和印样合成一张图。不写入 EXIF。\n\n" +
        "右键菜单：\n" +
        "· 预览区 —— 缩放、查看负片、前后对比、裁切、旋转翻转、预览背景色。\n" +
        "· 胶片条 —— 添加图像、创建虚拟副本、从卷中移除、复制/应用标定与场景。\n\n" +
        "快捷键：\n" +
        "F 适合窗口 · Ctrl+1 实际像素 · N 查看负片 · K 前后对比 · Esc 取消采样\n" +
        "Ctrl+Z/Y 撤销·重做 · Ctrl+N 新建卷 · Ctrl+O 添加图像 · Ctrl+E 导出\n" +
        "Ctrl+Shift+T 切换浅色/深色主题 · Ctrl+, 偏好设置\n\n" +
        "采样操作：点亮采样按钮（带虚线方框图标）后在预览上拖框；按 Esc 取消。\n" +
        "滑条：双击标签重置为默认值。\n" +
        "缩放后左键拖动可平移；滚轮缩放。"));

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
