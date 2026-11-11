using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed class PureNegInfoDialog : Window
{
    internal const string ProjectUrl = "https://github.com/easonzhangbin/PURENeg";
    private const string FilmDustUrl = "https://github.com/magholmes/film-dust";
    internal enum DownloadSource { GitHub, Gitee }

    public PureNegInfoDialog(bool download = false)
    {
        Title = Loc.T(download ? "安装可选 PURENeg 除尘组件" : "PURE Neg · 来源与许可");
        Width = 600; MinWidth = 380; Height = 660; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Spacing = 12 };
        using var imageStream = AssetLoader.Open(new Uri("avares://OpenRevelare/Assets/pureneg/lockup.png"));
        var icon = new Bitmap(imageStream);
        body.Children.Add(new Image { Source = icon, Width = 261, Height = 68, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left });
        body.Children.Add(new TextBlock { Text = Title, Classes = { "dialogTitle" }, Margin = new Thickness(0) });
        body.Children.Add(Text(Loc.T("除尘功能来自 PURE Neg 开源除尘软件，由 easonzhangbin 与 PURE Neg 贡献者维护；底层 film-dust 引擎由 Magnus Holmes 开发。感谢原作者开放这项工作。")));
        body.Children.Add(Text(Loc.T("PURE Neg 与 film-dust 检测引擎采用 MIT 许可证。允许在保留版权和许可声明的条件下使用、修改与分发；软件按原样提供，不作担保。标志版权归原权利人所有。")));
        body.Children.Add(Text("PURE Neg: Copyright (c) 2026 Film Dust for PhotoCraft contributors\nfilm-dust: Copyright (c) 2026 Magnus Holmes"));
        body.Children.Add(ProjectLink(Loc.T("PURE Neg 原项目"), ProjectUrl));
        body.Children.Add(ProjectLink(Loc.T("film-dust 底层引擎"), FilmDustUrl));
        if (download)
            body.Children.Add(Text(Loc.T("将下载当前平台的 PURENeg 除尘引擎、模型和运行库。GitHub 不可用时自动尝试 Gitee 国内镜像。组件保存在用户数据目录，升级后保留。")));
        body.Children.Add(Text(Loc.T("模型及运行库的其他第三方许可与来源声明随组件保留，包括 Node.js、ONNX Runtime 和 torchvision 等；不改变 OpenRevelare 自身的 GPL-3.0 许可证。")));
        using var licenses = new StreamReader(AssetLoader.Open(new Uri("avares://OpenRevelare/Assets/pureneg/licenses.txt")));
        body.Children.Add(new Expander
        {
            Name = "PureNegLicenseDisclosure", Header = Loc.T("查看完整许可证与第三方声明"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new SelectableTextBlock { Name = "PureNegLicenseText", Text = licenses.ReadToEnd(), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        });
        var close = new Button { Content = Loc.T(download ? "取消" : "关闭"), IsCancel = true };
        close.Click += (_, _) => Close();
        Control actions;
        if (download)
        {
            var github = new Button { Name = "PureNegGitHubDownload", Content = Loc.T("从 GitHub 下载"), Classes = { "primary" } };
            var gitee = new Button { Name = "PureNegGiteeDownload", Content = Loc.T("从 Gitee 国内镜像下载") };
            github.Click += (_, _) => Close(DownloadSource.GitHub);
            gitee.Click += (_, _) => Close(DownloadSource.Gitee);
            actions = DialogLayout.Actions(close, gitee, github);
        }
        else actions = DialogLayout.Actions(close);
        Content = DialogLayout.Compose(body, actions);
        Closed += (_, _) => icon.Dispose();
    }

    private static SelectableTextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static Control ProjectLink(string label, string url)
    {
        var link = new Button { Content = label + " ↗", HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(link, url);
        link.Click += (_, _) => Updater.OpenUrl(url);
        return new StackPanel { Spacing = 3, Children = { link, Text(url) } };
    }
}
