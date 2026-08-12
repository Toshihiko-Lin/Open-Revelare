using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace OpenRevelare.Gui.Views;

/// <summary>
/// 文档查看器 — shows the bundled GUIDE.md (操作指南) and THEORY.md (技术原理) in scrollable,
/// selectable panes. Port of Python's <c>gui/help_viewer.py</c>.
///
/// Rendered through <see cref="Services.MarkdownView"/> rather than shown as source: these files
/// are the program's own account of what it does, and reading 182 bold spans and 18 tables as raw
/// markup is materially harder than reading the same text on disk. LaTeX still stays as source —
/// Avalonia has no math renderer — but is set apart as a formula block instead of running inline.
/// </summary>
public sealed class DocDialog : Window
{
    public DocDialog(int tab = 0)
    {
        Title = Loc.T("文档");
        Width = 720;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var tabs = new TabControl { SelectedIndex = tab };
        tabs.Items.Add(new TabItem { Header = Loc.T("操作指南"), Content = DocPane("GUIDE.md") });
        tabs.Items.Add(new TabItem { Header = Loc.T("技术原理"), Content = DocPane("THEORY.md") });

        var close = new Button { Content = Loc.T("关闭"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close();

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(tabs);
        Content = root;
    }

    /// <summary>
    /// Read a bundled document, preferring the current language's edition — GUIDE.en.md before
    /// GUIDE.md. The Chinese file is the fallback and always exists, so a language whose
    /// translation has not been written yet still gets a readable document rather than an error.
    /// </summary>
    private static Control DocPane(string asset)
    {
        string text;
        try
        {
            using Stream s = OpenDoc(asset);
            using var r = new StreamReader(s);
            text = r.ReadToEnd();
        }
        catch (Exception ex) { text = Loc.T("无法载入文档：") + ex.Message; }

        return Services.MarkdownView.Render(text);
    }

    private static Stream OpenDoc(string asset)
    {
        if (Services.Loc.Current != "zh")
        {
            string localised = Path.GetFileNameWithoutExtension(asset)
                               + "." + Services.Loc.Current + Path.GetExtension(asset);
            try { return AssetLoader.Open(new Uri($"avares://OpenRevelare/Assets/docs/{localised}")); }
            catch { /* no edition in this language — the Chinese original stands in */ }
        }
        return AssetLoader.Open(new Uri($"avares://OpenRevelare/Assets/docs/{asset}"));
    }
}
