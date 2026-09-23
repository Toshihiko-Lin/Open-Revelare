using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// 本帧技术报告 — the numbers this frame's rendering rests on and where each came from, as text.
///
/// Deliberately a plain monospaced dump with a copy button rather than a laid-out panel: its two
/// uses are reading one line to find a wrong assumption, and pasting the whole thing into an issue.
/// Both want text that can be selected, and neither wants a design.
/// </summary>
public sealed class FrameReportDialog : Window
{
    public FrameReportDialog(string report)
    {
        Title = Loc.T("本帧技术报告");
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var text = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 12,
            // The report is read, not edited; a caret that can be placed is what makes a single
            // line selectable, which is the other thing people do with it.
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var copy = new Button { Content = Loc.T("复制全部") };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(report);
        };

        var close = new Button { Content = Loc.T("关闭") };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Content = root;
    }
}
