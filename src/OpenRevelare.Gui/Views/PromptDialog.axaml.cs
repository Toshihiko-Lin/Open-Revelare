using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// One-line text input modal — returns the entered string, or null if cancelled. Code-only, like
/// <see cref="PreferencesDialog"/>: a dialog with a label, a box and two buttons does not earn a
/// XAML file.
/// </summary>
public sealed class PromptDialog : Window
{
    private readonly TextBox _input = new();
    private string? _result;

    public PromptDialog(string title, string label, string initial)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _input.Text = initial;

        var ok = new Button { Content = "确定", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        ok.Click += (_, _) => { _result = _input.Text; Close(_result); };
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(_input);
        panel.Children.Add(buttons);
        Content = panel;

        // Land in the box with the current name selected: renaming is usually replacing.
        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }
}
