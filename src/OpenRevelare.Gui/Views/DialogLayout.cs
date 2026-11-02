using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OpenRevelare.Gui.Views;

/// <summary>One action edge for both code-built and XAML dialogs: content scrolls, decisions stay.</summary>
internal static class DialogLayout
{
    public static DockPanel Compose(Control body, Control actions)
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        actions.Margin = new Thickness(0, 16, 0, 0);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = body });
        return root;
    }

    public static WrapPanel Actions(params Button[] buttons)
    {
        var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(8, 0, 0, 4);
            panel.Children.Add(button);
        }
        return panel;
    }
}
