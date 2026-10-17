using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

internal sealed class ProjectDependenciesDialog : Window
{
    public ProjectDependenciesDialog(string rollTitle, IReadOnlyList<ProjectDependencies.Entry> entries)
    {
        Title = Loc.T("工程依赖");
        Width = 720; Height = 520; MinWidth = 380; MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = Loc.T("关闭"), IsCancel = true };
        close.Click += (_, _) => Close(false);
        var save = new Button { Content = Loc.T("导出依赖清单…"), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Close(true);
        actions.Children.Add(close); actions.Children.Add(save);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        string Role(ProjectDependencies.Kind role) => role switch
        {
            ProjectDependencies.Kind.Original => Loc.T("原片"),
            ProjectDependencies.Kind.Lcc => Loc.T("LCC 平场"),
            ProjectDependencies.Kind.CalibrationR => Loc.T("RGB 标定 · R"),
            ProjectDependencies.Kind.CalibrationG => Loc.T("RGB 标定 · G"),
            ProjectDependencies.Kind.CalibrationB => Loc.T("RGB 标定 · B"),
            ProjectDependencies.Kind.CalibrationDirectory => Loc.T("标定目录"),
            _ => Loc.T("自定义 LUT"),
        };
        string State(ProjectDependencies.Availability state) => state switch
        {
            ProjectDependencies.Availability.Present => Loc.T("文件存在"),
            ProjectDependencies.Availability.Missing => Loc.T("缺失"),
            ProjectDependencies.Availability.Unspecified => Loc.T("未指定路径"),
            _ => Loc.T("无法访问"),
        };
        int unavailable = entries.Count(e => e.Required && e.State != ProjectDependencies.Availability.Present);
        string body = rollTitle + "\n\n" + Loc.T("归档时请同时保留 .ncproj 和以下外部资源。内置 LUT 由程序提供，无须复制。清单只检查文件是否存在，不代表内容有效；导出清单不会复制文件。清单包含本机完整路径，分享前请检查。")
            + "\n\n" + Loc.F($"共 {entries.Count} 项外部资源，其中 {unavailable} 项必需资源不可用。")
            + "\n\n" + string.Join("\n\n", entries.Select(e =>
                string.Join(" / ", e.Roles.Select(Role)) + " · " + State(e.State) + " · "
                + (e.Required ? Loc.T("当前必需") : Loc.T("保留供恢复使用"))
                + (e.Frames.Length > 0 ? "\n" + Loc.T("使用帧：") + string.Join(", ", e.Frames) : "")
                + "\n" + (e.Path.Length > 0 ? e.Path : Loc.T("未指定路径"))));
        root.Children.Add(new ScrollViewer { Margin = new Thickness(0, 0, 0, 16), Content =
            new SelectableTextBlock { Text = body, TextWrapping = TextWrapping.Wrap } });
        Content = root;
    }
}
