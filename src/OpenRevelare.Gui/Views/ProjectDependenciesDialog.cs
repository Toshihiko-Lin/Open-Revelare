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
        var save = new Button { Content = Loc.T("导出依赖清单…"), Margin = new Thickness(8, 0, 0, 0), Classes = { "primary" } };
        save.Click += (_, _) => Close(true);
        var copy = new Button { Content = Loc.T("复制全部"), Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(close); actions.Children.Add(copy); actions.Children.Add(save);
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
        copy.Click += async (_, _) => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(body); };
        var list = new StackPanel { Spacing = 12 };
        list.Children.Add(new TextBlock { Text = rollTitle, Classes = { "dialogTitle" } });
        list.Children.Add(new TextBlock { Text = Loc.F($"共 {entries.Count} 项外部资源，其中 {unavailable} 项必需资源不可用。"), TextWrapping = TextWrapping.Wrap });
        list.Children.Add(new TextBlock { Text = Loc.T("仅检查文件存在性；清单不复制资源，也不保证内容有效。"), Classes = { "dialogNote" } });
        foreach (var entry in entries.OrderBy(e => e.Required && e.State != ProjectDependencies.Availability.Present ? 0 : 1)
                     .ThenBy(e => e.Roles.Contains(ProjectDependencies.Kind.Original) ? 0 : 1).ThenBy(e => e.Path))
        {
            bool missing = entry.Required && entry.State != ProjectDependencies.Availability.Present;
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 8) };
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            heading.Children.Add(new TextBlock { Text = Path.GetFileName(entry.Path), FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
            var status = new TextBlock { Text = State(entry.State), Classes = { "dialogNote" } };
            if (missing) status.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WarnBrush"));
            Grid.SetColumn(status, 1); heading.Children.Add(status); row.Children.Add(heading);
            row.Children.Add(new TextBlock { Text = string.Join(" / ", entry.Roles.Select(Role))
                + (entry.Frames.Length > 0 ? " · " + Loc.T("使用帧：") + string.Join(", ", entry.Frames) : ""), Classes = { "dialogNote" } });
            row.Children.Add(new SelectableTextBlock { Text = entry.Path.Length > 0 ? entry.Path : Loc.T("未指定路径"), TextWrapping = TextWrapping.Wrap });
            list.Children.Add(row);
            list.Children.Add(new Separator());
        }
        root.Children.Add(new ScrollViewer { Margin = new Thickness(0, 0, 0, 16), Content = list });
        Content = root;
    }
}
