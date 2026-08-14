using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// macOS 原生菜单栏（P4）。
///
/// 在 mac 上，窗口里画一条菜单条是"这不是个 mac 应用"最直接的信号：屏幕顶端那条系统菜单栏
/// 归应用所有，空着就是错的。Avalonia 的做法是把一棵 <see cref="NativeMenu"/> 挂到对象上，
/// 由 Avalonia.Native 后端翻译成真正的 NSMenu：
///
/// <list type="bullet">
///   <item>挂在 <c>Application.Current</c> 上的那棵 → 首个（粗体应用名）菜单。</item>
///   <item>挂在 <see cref="Window"/> 上的那棵 → 它右边的 File / Edit / View / … 各栏。</item>
/// </list>
///
/// ══ 为什么用 C# 建而不是写在 XAML 里 ══
///
/// 因为窗口内的 <c>Menu</c> 在其余平台仍然要用，两份菜单必须点出同一件事。写成两段 XAML
/// 就是两份独立的清单，改一处忘一处是迟早的；这里让原生菜单复用**同一批 Click 处理器**和
/// 同一批 VM 属性绑定，行为上不可能漂移。这正是 <c>OnKeyDown</c> 那条注释在意的同一件事
/// （"菜单里显示了却没接上的手势就是在骗用户"），只是换到了菜单与菜单之间。
///
/// ══ 不要自己加 Quit ══
///
/// Avalonia 的 <c>AvaloniaNativeMenuExporter</c> 会在应用菜单**尾部自动补上**
/// Services / Hide / Hide Others / Show All / Quit ⌘Q 这一套标准项（除非
/// <c>MacOSPlatformOptions.DisableDefaultApplicationMenuItems</c>）。所以这里只放
/// 关于 / 偏好设置，Quit 交给它——自己再加一条就是两个 Quit。
///
/// ⌘W（关闭窗口）同理不在这里：mac 上它由 NSWindow 自己处理。
/// </summary>
public partial class MainWindow
{
    private bool _nativeMenuReady;

    /// <summary>
    /// 装上原生菜单栏。非 macOS 平台直接返回——窗口内那条 <c>Menu</c> 继续用，
    /// 它在 Windows / Linux 上本来就是对的。
    /// </summary>
    private void SetUpNativeMenu()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // DataContextChanged 理论上可以再响（见构造函数里那条注释）。真响了就只重建菜单，
        // 不能把 Loc.Changed 再订一遍——那会让每次切语言都重建成倍的次数。
        if (_nativeMenuReady) { RebuildNativeMenu(); return; }
        _nativeMenuReady = true;

        // 窗口内的菜单条让位：mac 上它和顶端系统菜单栏是同一份东西，留着就是画两遍。
        // 只藏 Menu 自己，不藏那圈 Border——标题和右侧的撤销/重做按钮还在里面。
        WindowMenu.IsVisible = false;

        RebuildNativeMenu();

        // 语言一变，整棵树重建：NativeMenuItem.Header 是普通字符串，不像 XAML 那边的
        // {i18n:T} 会自己重取。重建比逐项改省事，也不会漏。
        Loc.Changed += RebuildNativeMenu;
    }

    /// <summary>配对的退订，由 <c>OnClosing</c> 调用。</summary>
    private void TearDownNativeMenu()
    {
        if (!_nativeMenuReady) return;
        Loc.Changed -= RebuildNativeMenu;
        _nativeMenuReady = false;
    }

    private void RebuildNativeMenu()
    {
        if (!OperatingSystem.IsMacOS()) return;
        NativeMenu.SetMenu(Application.Current!, BuildAppMenu());
        NativeMenu.SetMenu(this, BuildWindowMenu());
    }

    // ── 应用菜单（粗体 OpenRevelare 那栏）────────────────────────────────────────
    //
    // mac 的约定：关于 / 偏好设置 归这里，不归 帮助 / 设置。⌘, 是系统级习惯，
    // 每个 mac 应用的偏好设置都在这个位置、这个键上。
    private NativeMenu BuildAppMenu() => new()
    {
        Items =
        {
            Item(Loc.T("关于 OpenRevelare"), OnAboutClick),
            new NativeMenuItemSeparator(),
            Item(Loc.T("偏好设置…"), OnPreferencesClick, gesture: "OemComma"),
        },
    };

    // ── 窗口菜单（File / Edit / View / Help）─────────────────────────────────────
    //
    // 设置 那一栏在 mac 上没了：它唯一的内容是 偏好设置，已经按 mac 约定搬进应用菜单。
    // 其余四栏与窗口内菜单逐条对齐，顺序也一样——文档里"文件 → 导出印样…"那类指路
    // 两个平台都还成立。
    private NativeMenu BuildWindowMenu()
    {
        var file = new NativeMenu
        {
            Items =
            {
                Item(Loc.T("新建卷…"), OnOpenClick, gesture: "N"),
                Bind(Item(Loc.T("添加图像到当前卷…"), OnAddImagesClick, gesture: "O"), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                RecentRollsNativeItem(),
                Item(Loc.T("打开工程…"), OnOpenProjectClick),
                Item(Loc.T("扫描文件夹加入目录…"), OnScanFolderClick),
                Bind(Item(Loc.T("另存工程副本…"), OnSaveProjectClick), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                Bind(Item(Loc.T("导出当前帧…"), OnExportClick, gesture: "E"), nameof(Vm.HasImage)),
                Bind(Item(Loc.T("导出整卷…"), OnExportRollClick), nameof(Vm.HasImage)),
                Bind(Item(Loc.T("导出印样…"), OnContactSheetClick), nameof(Vm.HasImage)),
            },
        };

        var cal = new NativeMenu
        {
            Items =
            {
                Item(Loc.T("复制标定"), OnCopyCalClick),
                Bind(Item(Loc.T("粘贴标定到本帧"), OnPasteCalToCurrentClick), nameof(Vm.HasCalClipboard)),
                Bind(Item(Loc.T("应用标定到勾选帧"), OnPasteCalClick), nameof(Vm.HasCalClipboard)),
                Item(Loc.T("应用标定到整卷"), OnApplyCalToRollClick),
            },
        };

        var scene = new NativeMenu
        {
            Items =
            {
                Item(Loc.T("复制场景"), OnCopySceneClick),
                Bind(Item(Loc.T("粘贴场景到本帧"), OnPasteSceneToCurrentClick), nameof(Vm.HasSceneClipboard)),
                Bind(Item(Loc.T("应用场景到勾选帧"), OnPasteSceneClick), nameof(Vm.HasSceneClipboard)),
                Item(Loc.T("应用场景到整卷"), OnApplySceneToRollClick),
            },
        };

        var edit = new NativeMenu
        {
            Items =
            {
                Bind(Item(Loc.T("撤销"), OnUndoClick, gesture: "Z"), nameof(Vm.CanUndo)),
                Bind(Item(Loc.T("重做"), OnRedoClick, gesture: "Y"), nameof(Vm.CanRedo)),
                new NativeMenuItemSeparator(),
                Bind(Item(Loc.T("重置本帧调整"), OnResetClick), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                Bind(Item(Loc.T("复制（按当前面板）"), OnCopyActiveClick, gesture: "C"), nameof(Vm.HasImage)),
                Bind(Item(Loc.T("粘贴到本帧（按当前面板）"), OnPasteActiveClick, gesture: "V"), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                Bind(Sub(Loc.T("标定（Stage 1）"), cal), nameof(Vm.HasImage)),
                Bind(Sub(Loc.T("场景（Stage 2）"), scene), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                Bind(Item(Loc.T("选择同步项…"), OnSyncOptionsClick), nameof(Vm.HasImage)),
            },
        };

        var view = new NativeMenu
        {
            Items =
            {
                Item(Loc.T("图库（卷墙）"), OnLibraryModeClick, gesture: "G", bare: true),
                Bind(Item(Loc.T("修片"), OnDevelopModeClick, gesture: "D", bare: true), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                Bind(Item(Loc.T("查看负片"), OnMenuViewNegClick, gesture: "N", bare: true), nameof(Vm.HasImage)),
                Bind(Item(Loc.T("前后对比"), OnMenuCompareClick, gesture: "K", bare: true), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                Bind(Item(Loc.T("适合窗口"), OnFitClick, gesture: "F", bare: true), nameof(Vm.HasImage)),
                Bind(Item(Loc.T("实际像素 100%"), OnActualSizeClick, gesture: "D1"), nameof(Vm.HasImage)),
                new NativeMenuItemSeparator(),
                SprocketNativeItem(),
                new NativeMenuItemSeparator(),
                Sub(Loc.T("预览背景色"), ViewerBgNativeMenu()),
                Item(Loc.T("切换浅色/深色主题"), OnToggleThemeClick, gesture: "Shift+T"),
            },
        };

        var help = new NativeMenu
        {
            Items =
            {
                Item(Loc.T("操作指引 / 技术原理…"), OnDocsClick),
                Item(Loc.T("快捷键…"), OnHelpClick),
                new NativeMenuItemSeparator(),
                Item(Loc.T("检查更新…"), OnCheckUpdateClick),
            },
        };

        // 顶栏那五个字保持英文，理由和窗口内菜单同一条（见 MainWindow.axaml 的注释）：
        // 它们是窗口的固定地标，截图和文档都指着它们。
        return new NativeMenu
        {
            Items =
            {
                Sub("File", file),
                Sub("Edit", edit),
                Sub("View", view),
                Sub("Help", help),
            },
        };
    }

    // ── 动态项 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 「最近的卷」。和窗口内那份一样，内容是**打开时**才填的——目录随导入、改名、移除而变，
    /// 建菜单时抓一次的快照过一次导入就旧了。窗口内那份挂在 <c>SubmenuOpened</c> 上，
    /// 这份挂在 <see cref="NativeMenu.Opening"/> 上，是同一个时机。
    /// </summary>
    private NativeMenuItem RecentRollsNativeItem()
    {
        var menu = new NativeMenu();
        NativeMenuItem head = Sub(Loc.T("最近的卷"), menu);

        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            IReadOnlyList<Catalog.Roll> recent = Catalog.Recent(10);
            head.IsEnabled = recent.Count > 0;

            foreach (Catalog.Roll roll in recent)
            {
                var item = new NativeMenuItem
                {
                    Header = roll.Missing ? roll.Title + Loc.T("（文件缺失）") : roll.Title,
                    IsEnabled = !roll.Missing,
                    ToolTip = Loc.F($"{roll.Subtitle}\n{roll.FrameCount} 帧 · {roll.ProjectPath}").TrimStart(),
                };
                item.Click += async (_, _) =>
                {
                    if (Vm is null) return;
                    await Vm.OpenRollAsync(roll);
                    Vm.EnterDevelop();
                };
                menu.Items.Add(item);
            }
        };

        // 首帧就得对：Opening 要等用户点开才响，而这一项在那之前就画在菜单里了。
        // 目录是空的（第一次运行）时它应当是灰的，不能等点开才发现里面没东西。
        head.IsEnabled = Catalog.Recent(1).Count > 0;
        return head;
    }

    /// <summary>齿孔遮罩：勾选状态双向跟 VM 走，和窗口内那条 CheckBox 项是同一个属性。</summary>
    private NativeMenuItem SprocketNativeItem()
    {
        var item = new NativeMenuItem
        {
            Header = Loc.T("显示齿孔遮罩"),
            ToggleType = NativeMenuItemToggleType.CheckBox,
        };
        item.Bind(NativeMenuItem.IsCheckedProperty,
            new Binding(nameof(Vm.ShowSprocketMask)) { Source = Vm, Mode = BindingMode.TwoWay });
        return Bind(item, nameof(Vm.HasImage));
    }

    /// <summary>
    /// 预览背景色。窗口内那两份靠 <c>SyncViewerBgChecks</c> 对勾；这份自己记住实例，
    /// 由同一个函数一起更新，免得点了原生菜单而窗口内菜单还勾在旧色上。
    /// </summary>
    private NativeMenu ViewerBgNativeMenu()
    {
        var menu = new NativeMenu();
        _bgNativeItems.Clear();

        foreach ((string label, string hex) in ViewerBackgrounds)
        {
            var item = new NativeMenuItem
            {
                Header = Loc.T(label),
                ToggleType = NativeMenuItemToggleType.Radio,
            };
            item.Click += (_, _) =>
            {
                Settings.Current.ViewerBackground = hex;
                Settings.Save();
                App.ApplyViewerBackground(hex);
                SyncViewerBgChecks();
            };
            _bgNativeItems[hex] = item;
            menu.Items.Add(item);
        }

        SyncViewerBgChecks();
        return menu;
    }

    /// <summary>原生背景色项，按色值索引。SyncViewerBgChecks 更新它们。</summary>
    private readonly Dictionary<string, NativeMenuItem> _bgNativeItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 背景色清单。原生菜单和窗口内菜单本来各写一遍，颜色对不上就是两条不同的菜单；
    /// 在这里定义一次，XAML 那份的 Tag 与它逐条对应。
    /// </summary>
    private static readonly (string Label, string Hex)[] ViewerBackgrounds =
    {
        ("白", "#FFFFFF"),
        ("浅灰", "#B4B4B4"),
        ("中灰（默认）", "#5E5E5E"),
        ("18% 中性灰", "#777777"),
        ("深灰", "#323232"),
        ("黑", "#0E0F11"),
    };

    // ── 小工具 ──────────────────────────────────────────────────────────────────

    /// <summary>一条普通菜单项。<paramref name="gesture"/> 给键名（<c>"N"</c>、<c>"Shift+T"</c>），
    /// 默认加 ⌘；<paramref name="bare"/> 为真表示这是个无修饰键的单键手势（G / D / F / K / N）。</summary>
    private static NativeMenuItem Item(
        string header, EventHandler click, string? gesture = null, bool bare = false)
    {
        var item = new NativeMenuItem
        {
            Header = header,
            Gesture = gesture is null ? null
                : KeyGesture.Parse(bare ? gesture : $"{Markup.AccelExtension.ModifierName}+{gesture}"),
        };
        item.Click += click;
        return item;
    }

    /// <summary>
    /// 收窗口里那批 <c>(object?, RoutedEventArgs)</c> 处理器的重载。原生菜单的 Click 是
    /// <c>EventHandler&lt;EventArgs&gt;</c>，签名对不上，这里补一层转接，好让两份菜单共用
    /// 同一批方法——这是"两份菜单不会漂移"这件事真正落实的地方。
    ///
    /// 造一个空的 <see cref="RoutedEventArgs"/> 是安全的：这批处理器没有一个读 e，
    /// 它们要么无参调用 VM，要么读 <c>sender</c>。
    /// </summary>
    private static NativeMenuItem Item(
        string header, EventHandler<RoutedEventArgs> click, string? gesture = null, bool bare = false)
        => Item(header, (EventHandler)((s, _) => click(s, new RoutedEventArgs())), gesture, bare);

    /// <summary>带子菜单的一项。</summary>
    private static NativeMenuItem Sub(string header, NativeMenu menu)
        => new() { Header = header, Menu = menu };

    /// <summary>
    /// 把 <c>IsEnabled</c> 绑到 VM 的一个 bool 属性上——效果同 XAML 里的
    /// <c>IsEnabled="{Binding HasImage}"</c>：绑定而非赋值，状态自己跟着变。
    ///
    /// ⚠️ 必须显式给 <c>Source</c>。<see cref="NativeMenuItem"/> 派生自
    /// <see cref="AvaloniaObject"/> 而**不是** <c>StyledElement</c>：它没有 DataContext，
    /// 也不在逻辑树上，所以不写 Source 的 <see cref="Binding"/> 找不到任何东西可解析——
    /// 而且是**静默**失效，属性停在默认值 true 上。那意味着没开卷时「导出当前帧…」
    /// 照样可点，且只会在 mac 上出现。
    /// </summary>
    private NativeMenuItem Bind(NativeMenuItem item, string path)
    {
        item.Bind(NativeMenuItem.IsEnabledProperty, new Binding(path) { Source = Vm });
        return item;
    }
}
