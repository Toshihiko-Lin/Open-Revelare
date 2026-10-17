using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Views;

public enum SaveFailureChoice { Cancel, Retry, Recovery, Discard }

internal sealed class SaveFailureDialog : Window
{
    public SaveFailureDialog(string detail)
    {
        Title = Loc.T("工程尚未保存");
        Width = 580; MinWidth = 360; MinHeight = 220; MaxHeight = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, choice) in new[]
        {
            (Loc.T("继续编辑"), SaveFailureChoice.Cancel),
            (Loc.T("另存恢复副本"), SaveFailureChoice.Recovery),
            (Loc.T("放弃未保存修改并关闭"), SaveFailureChoice.Discard),
            (Loc.T("重试保存"), SaveFailureChoice.Retry),
        })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 8, 8, 0) };
            button.IsCancel = choice == SaveFailureChoice.Cancel;
            button.IsDefault = choice == SaveFailureChoice.Retry;
            button.Click += (_, _) => Close(choice);
            actions.Children.Add(button);
        }
        var details = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Loc.T("保存失败，修改仍保留在内存中。请重试或另存恢复副本。"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new SelectableTextBlock { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            },
        };
        // Recovery actions must remain reachable even when an error contains several long paths.
        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = details });
        Content = root;
    }
}
