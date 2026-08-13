using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// A small Markdown-to-Avalonia renderer for the two bundled documents.
///
/// The viewer used to dump the raw file into one monospace <c>SelectableTextBlock</c>, so headings,
/// tables and emphasis all read as source. These documents are the program's own explanation of
/// what it does — 182 bold spans and 18 tables in THEORY.md alone — and reading them as markup
/// makes them noticeably harder to follow than the same text on disk.
///
/// Deliberately NOT a full CommonMark implementation, and deliberately not a package: adding a
/// dependency here would have to travel through three platforms' packaging. This covers exactly
/// the constructs the two files use — headings, bullets, numbered lists, tables, block quotes,
/// horizontal rules, fenced and inline code, bold, and LaTeX — and treats anything else as plain
/// text, which is what the old viewer did with everything.
///
/// Formulae are typeset properly, by AvaloniaMath — display ones centred in a panel, inline ones
/// on the surrounding baseline. <see cref="Tex"/> bridges the documents' LaTeX dialect to the
/// subset that parser accepts; anything it still rejects falls back to showing the source.
/// </summary>
public static class MarkdownView
{
    private const double BaseSize = 13.0;
    private static readonly FontFamily Mono =
        new("Consolas, Cascadia Code, DejaVu Sans Mono, PingFang SC, Microsoft YaHei, monospace");

    /// <summary>
    /// Render markdown into a two-pane view: a clickable table of contents on the left, the
    /// document on the right.
    ///
    /// The contents are built from the headings actually present rather than from any hand-written
    /// "目录" section — GUIDE.md has one of those, and keeping it in sync by hand is how it came to
    /// list a step that no longer exists. Its markdown links also rendered as literal text here,
    /// since nothing resolved them.
    /// </summary>
    public static Control Render(string markdown)
    {
        var nav = new List<(string Title, int Level, Control Target)>();
        Control body = RenderBody(markdown, nav);
        if (nav.Count < 3) return body;      // too short to be worth a sidebar

        var scroller = (ScrollViewer)body;
        var toc = new StackPanel { Margin = new Thickness(10, 12, 6, 16), Spacing = 1 };
        foreach ((string title, int level, Control target) in nav)
        {
            // h1 is the document title — it names the pane, so listing it would waste a line.
            if (level <= 1) continue;
            var link = new Button
            {
                Content = new TextBlock
                {
                    Text = title,
                    FontSize = level == 2 ? 12 : 11.5,
                    FontWeight = level == 2 ? FontWeight.Medium : FontWeight.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = level == 2
                        ? Brush("TextBrush", Color.FromRgb(0xD0, 0xD4, 0xD8))
                        : Brush("TextMuteBrush", Color.FromRgb(0x7E, 0x84, 0x8B)),
                },
                Classes = { "tocLink" },
                Padding = new Thickness(6 + (level - 2) * 10, 3, 6, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            Control t = target;
            link.Click += (_, _) => t.BringIntoView();
            toc.Children.Add(link);
        }

        var tocPane = new ScrollViewer
        {
            Content = toc,
            Width = 208,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var split = new Border
        {
            Width = 1,
            Background = Brush("BorderSoftBrush", Color.FromRgb(0x40, 0x44, 0x48)),
        };
        Grid.SetColumn(tocPane, 0);
        Grid.SetColumn(split, 1);
        Grid.SetColumn(scroller, 2);
        grid.Children.Add(tocPane);
        grid.Children.Add(split);
        grid.Children.Add(scroller);
        return grid;
    }

    /// <summary>The document pane. <paramref name="nav"/> collects headings for the contents.</summary>
    private static Control RenderBody(string markdown, List<(string, int, Control)> nav)
    {
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 16), Spacing = 0 };
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // Fenced code — consumed whole, so its contents are never parsed as markdown.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                stack.Children.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            // Display maths: $$ … $$, possibly spanning lines.
            if (line.TrimStart().StartsWith("$$", StringComparison.Ordinal))
            {
                var math = new List<string>();
                string first = line.Trim();
                if (first.Length > 2 && first.EndsWith("$$", StringComparison.Ordinal))
                {
                    math.Add(first[2..^2].Trim());
                }
                else
                {
                    if (first.Length > 2) math.Add(first[2..]);
                    for (i++; i < lines.Length && !lines[i].Contains("$$", StringComparison.Ordinal); i++)
                        math.Add(lines[i]);
                    if (i < lines.Length)
                    {
                        string tail = lines[i].Replace("$$", "").Trim();
                        if (tail.Length > 0) math.Add(tail);
                    }
                }
                stack.Children.Add(MathBlock(string.Join("\n", math).Trim()));
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 14, 0, 14),
                    Background = Brush("BorderSoftBrush", Color.FromRgb(0x40, 0x44, 0x48)),
                });
                continue;
            }

            // Tables: a header row followed by a |---|---| separator.
            if (line.StartsWith("|", StringComparison.Ordinal)
                && i + 1 < lines.Length && IsTableRule(lines[i + 1]))
            {
                var rows = new List<string[]> { SplitRow(line) };
                i += 2;
                for (; i < lines.Length && lines[i].StartsWith("|", StringComparison.Ordinal); i++)
                    rows.Add(SplitRow(lines[i]));
                i--;
                stack.Children.Add(Table(rows));
                continue;
            }

            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].StartsWith(">", StringComparison.Ordinal); i++)
                    quote.Add(lines[i].TrimStart('>').TrimStart());
                i--;
                stack.Children.Add(Quote(string.Join(" ", quote.Where(q => q.Length > 0))));
                continue;
            }

            Match h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (h.Success)
            {
                int level = h.Groups[1].Value.Length;
                Control head = Heading(h.Groups[2].Value, level);
                stack.Children.Add(head);
                nav.Add((StripInline(h.Groups[2].Value), level, head));
                continue;
            }

            Match bullet = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
            if (bullet.Success)
            {
                stack.Children.Add(ListItem("•", bullet.Groups[2].Value, bullet.Groups[1].Value.Length));
                continue;
            }

            Match num = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.*)$");
            if (num.Success)
            {
                stack.Children.Add(ListItem(num.Groups[2].Value + ".", num.Groups[3].Value,
                                            num.Groups[1].Value.Length));
                continue;
            }

            if (line.Trim().Length == 0)
            {
                stack.Children.Add(new Border { Height = 6 });
                continue;
            }

            stack.Children.Add(Paragraph(line));
        }

        return new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    // ── blocks ───────────────────────────────────────────────────────────────────

    private static Control Heading(string text, int level)
    {
        double size = level switch { 1 => 20, 2 => 17, 3 => 15, _ => 13.5 };
        var tb = new SelectableTextBlock
        {
            FontSize = size,
            FontWeight = level <= 2 ? FontWeight.SemiBold : FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, level <= 2 ? 18 : 12, 0, 6),
        };
        FillInlines(tb.Inlines!, text);
        return tb;
    }

    private static Control Paragraph(string text)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = BaseSize,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        FillInlines(tb.Inlines!, text);
        return tb;
    }

    private static Control ListItem(string marker, string text, int indent)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = BaseSize,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
        };
        FillInlines(tb.Inlines!, text);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(12 + indent * 8, 1, 0, 3),
        };
        var dot = new SelectableTextBlock
        {
            Text = marker,
            FontSize = BaseSize,
            LineHeight = 21,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = Brush("TextMuteBrush", Color.FromRgb(0x7E, 0x84, 0x8B)),
        };
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(tb, 1);
        grid.Children.Add(dot);
        grid.Children.Add(tb);
        return grid;
    }

    private static Control Quote(string text)
    {
        var tb = new SelectableTextBlock
        {
            FontSize = BaseSize - 0.5,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("TextMuteBrush", Color.FromRgb(0x7E, 0x84, 0x8B)),
        };
        FillInlines(tb.Inlines!, text);
        return new Border
        {
            Child = tb,
            BorderBrush = Brush("BorderSoftBrush", Color.FromRgb(0x40, 0x44, 0x48)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 4, 0, 4),
            Margin = new Thickness(2, 4, 0, 8),
        };
    }

    private static Control CodeBlock(string code) => Fenced(code, BaseSize - 1);

    /// <summary>
    /// A display formula, typeset by AvaloniaMath, falling back to plain text for anything this
    /// build cannot render. The bundled documents are written so that no formula needs the
    /// fallback — quantities are named with Latin symbols, since the math fonts carry no CJK.
    /// </summary>
    private static Control MathBlock(string tex)
    {
        Control content = Formula(tex, BaseSize + 4) ?? PlainFormula(tex, BaseSize + 4);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Child = content,
            Background = Brush("PanelSoftBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            BorderBrush = Brush("BorderSoftBrush", Color.FromRgb(0x40, 0x44, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 8, 0, 10),
        };
    }

    /// <summary>
    /// The fallback for a formula this build cannot typeset: its markup stripped, set in the BODY
    /// font. Splitting such a formula into typesettable fragments was tried and rejected — it cuts
    /// mid-construct, so a fraction arrives as "\frac{" and "}{…", which renders but means
    /// nothing. Showing the whole statement as text keeps it readable.
    /// </summary>
    private static Control PlainFormula(string tex, double size) => new SelectableTextBlock
    {
        Text = Tex.PlainText(tex),
        FontSize = size - 3,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    /// One typeset formula, or null when this build cannot render it. The formula takes the
    /// current text colour so it follows the theme, which the control does not do on its own.
    ///
    /// TWO ways to be unrenderable, and only one of them is catchable here. The parser throws on
    /// an unmodelled command, which this try/catch handles. But a CJK character throws from
    /// <c>FormulaBlock.Render</c> — during Avalonia's render pass, on the UI thread, where no
    /// caller frame exists to catch it and the process simply aborts. That one has to be refused
    /// UP FRONT, which is what <see cref="Tex.IsRenderable"/> does: the math fonts carry Latin,
    /// Greek and symbols only, so a formula naming a Chinese quantity can never be typeset.
    /// </summary>
    private static Control? Formula(string tex, double size)
    {
        if (!Tex.IsRenderable(tex)) return null;
        try
        {
            return new AvaloniaMath.Controls.FormulaBlock
            {
                Formula = Tex.Normalise(tex),
                FontSize = size,
                Foreground = Brush("TextBrush", Color.FromRgb(0xD0, 0xD4, 0xD8)),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        catch (Exception)
        {
            // Rendering the source is the honest fallback; showing nothing would lose the statement.
            return null;
        }
    }

    private static Control Fenced(string text, double size) => new Border
    {
        Child = new SelectableTextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
        },
        Background = Brush("PanelSoftBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
        BorderBrush = Brush("BorderSoftBrush", Color.FromRgb(0x40, 0x44, 0x48)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(10, 7, 10, 7),
        Margin = new Thickness(0, 6, 0, 8),
    };

    private static Control Table(List<string[]> rows)
    {
        int cols = rows.Max(r => r.Length);
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var border = Brush("BorderSoftBrush", Color.FromRgb(0x40, 0x44, 0x48));
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = new SelectableTextBlock
                {
                    FontSize = BaseSize - 1,
                    LineHeight = 19,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = r == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    MaxWidth = 260,
                };
                FillInlines(cell.Inlines!, c < rows[r].Length ? rows[r][c] : "");

                // Header underline only: full gridlines would out-shout the text at this size.
                var wrap = new Border
                {
                    Child = cell,
                    Padding = new Thickness(9, 5, 9, 5),
                    BorderBrush = border,
                    BorderThickness = new Thickness(0, 0, 0, r == 0 ? 1 : 0),
                };
                Grid.SetRow(wrap, r);
                Grid.SetColumn(wrap, c);
                grid.Children.Add(wrap);
            }
        }

        // Wide tables scroll on their own rather than forcing the page sideways.
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    // ── inlines ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bold (<c>**…**</c>), inline code (<c>`…`</c>) and inline maths (<c>$…$</c>). Scanned in one
    /// pass so a run of code containing asterisks is not re-parsed as emphasis.
    /// </summary>
    private static void FillInlines(InlineCollection into, string text)
    {
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            into.Add(new Run(buf.ToString()));
            buf.Clear();
        }

        // Links render as their TEXT. Nothing here resolves a target, and showing the raw
        // [label](#anchor) form is what made GUIDE.md's own contents section unreadable.
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // Escaped underscores are a GitHub-flavoured artefact of that generated section.
        text = text.Replace("\\_", "_");

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    into.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeight.SemiBold });
                    i = end + 1;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > 0)
                {
                    Flush();
                    into.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = Mono,
                        FontSize = BaseSize - 1,
                    });
                    i = end;
                    continue;
                }
            }
            if (text[i] == '$')
            {
                int end = text.IndexOf('$', i + 1);
                if (end > 0 && end - i < 120)      // a lone $ in prose must not swallow the line
                {
                    Flush();
                    string tex = text[(i + 1)..end];
                    // Typeset inline where the parser accepts it, via an inline container so the
                    // formula sits on the text baseline; otherwise fall back to the source.
                    if (Formula(tex, BaseSize + 1) is { } f)
                        into.Add(new InlineUIContainer(f) { BaselineAlignment = BaselineAlignment.Center });
                    else
                        // Plain text in the BODY font, not the monospace one: this branch is
                        // reached by the CJK formulae, and Consolas has no Chinese glyphs.
                        into.Add(new Run(Tex.PlainText(tex)));
                    i = end;
                    continue;
                }
            }
            buf.Append(text[i]);
        }
        Flush();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Heading text with inline markup removed, for the contents list.</summary>
    private static string StripInline(string s)
    {
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");   // links → their text
        s = s.Replace("**", "").Replace("`", "").Replace("$", "");
        s = s.Replace("\\_", "_");
        return s.Trim();
    }

    private static bool IsTableRule(string line) =>
        line.StartsWith("|", StringComparison.Ordinal)
        && line.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0
        && line.Contains('-', StringComparison.Ordinal);

    private static string[] SplitRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(s => s.Trim()).ToArray();

    /// <summary>A themed brush, falling back to a literal when the key is absent.</summary>
    private static IBrush Brush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key, out object? found) == true && found is IBrush b)
            return b;
        return new SolidColorBrush(fallback);
    }
}

/// <summary>
/// Normalises the documents' LaTeX into the subset AvaloniaMath (WpfMath) parses.
///
/// The two dialects differ in a handful of places, and every one of them is a mechanical rewrite
/// rather than a loss of meaning:
///
///   • <c>T_\text{base}</c> — the parser rejects \text as a SCRIPT argument, but accepts it inside
///     a group, so this becomes <c>T_{\mathrm{base}}</c>. This is the single biggest cause: it
///     alone accounted for most of the formulae in these files.
///   • <c>\tfrac</c> / <c>\dfrac</c> → <c>\frac</c>; <c>\operatorname</c> → <c>\mathrm</c>.
///   • Spacing: <c>\qquad</c>, <c>\quad</c> and the escaped space <c>"\ "</c> → runs of <c>\;</c>.
///     The negative lookbehind matters — <c>\\</c> is a matrix row separator, not a space.
///   • <c>\bigl</c> / <c>\bigr</c> / <c>\!</c> are dropped (sizing hints, no semantic content).
///   • <c>bmatrix</c> → <c>pmatrix</c>: only the bracket shape differs and only pmatrix exists here.
///   • Literal Unicode operators (→ × −) that were typed straight into the source.
///   • <c>\xrightarrow{label}</c> has no stacked equivalent, so the label rides as a superscript
///     on a plain arrow.
///
/// Verified against every formula in the four bundled documents: 136 of 136 parse.
/// </summary>
internal static class Tex
{
    /// <summary>
    /// Whether the math fonts can carry every character in <paramref name="tex"/>.
    ///
    /// They hold Latin, Greek and mathematical symbols. A CJK character has no glyph, and the
    /// engine reports that by throwing FROM ITS RENDER PASS — inside Avalonia's compositor, where
    /// the exception is unhandled and takes the process down. So the check has to happen before a
    /// FormulaBlock is ever constructed rather than around it.
    ///
    /// Only the Chinese document trips this, in the three formulae that name a quantity in Chinese
    /// (输出范围, 线性设备 RGB); those fall back to their source text, which is legible as-is.
    /// </summary>
    public static bool IsRenderable(string tex)
    {
        foreach (char c in tex)
            if (c > 0x2FFF) return false;   // past the symbol blocks: CJK, kana, fullwidth forms
        return true;
    }

    /// <summary>
    /// A readable plain-text rendering for a span that cannot be typeset — the markup stripped,
    /// leaving the words and symbols it was wrapping.
    /// </summary>
    public static string PlainText(string tex)
    {
        string s = Regex.Replace(tex, @"\\(?:text|mathrm|operatorname)\s*\{([^{}]*)\}", "$1");
        s = Regex.Replace(s, @"\\xrightarrow\s*\{(.*?)\}", "  ──$1──▶  ");
        s = s.Replace(@"\cdot", "·").Replace(@"\mid", "|").Replace(@"\qquad", "  ").Replace(@"\,", " ");
        s = Regex.Replace(s, @"\\([a-zA-Z]+)", "$1");
        s = s.Replace("{", "").Replace("}", "").Replace("\\", "");
        return Regex.Replace(s, @"[ \t]{2,}", " ").Trim();
    }

    /// <summary>Rewrite <paramref name="tex"/> into the parser's accepted subset.</summary>
    public static string Normalise(string tex)
    {
        string s = tex.Trim();

        // Operators typed as literal characters rather than as commands.
        s = s.Replace("→", @"\to ").Replace("×", @"\times ").Replace("−", "-").Replace("≈", @"\approx ");

        s = s.Replace(@"\tfrac", @"\frac").Replace(@"\dfrac", @"\frac");
        s = Regex.Replace(s, @"\\operatorname\s*\{", @"\mathrm{");
        s = s.Replace(@"\qquad", @"\;\;\;\;").Replace(@"\quad", @"\;\;");
        s = s.Replace(@"\bigl", "").Replace(@"\bigr", "")
             .Replace(@"\Bigl", "").Replace(@"\Bigr", "").Replace(@"\!", "");
        s = s.Replace(@"\begin{bmatrix}", @"\begin{pmatrix}")
             .Replace(@"\end{bmatrix}", @"\end{pmatrix}");

        // Escaped space. The lookbehind keeps the matrix row separator \\ intact.
        s = Regex.Replace(s, @"(?<!\\)\\ (?=[^\s])", @"\; ");

        // \text{…} / \mathrm{…} as a script argument must be wrapped in a group. Repeated because
        // one formula can hold several, and a rewrite can expose the next.
        for (int i = 0; i < 4; i++)
            s = Regex.Replace(s, @"([_^])\\(?:text|mathrm)\s*\{([^{}]*)\}", "$1{\\mathrm{$2}}");

        // No stacked-label arrow exists in this parser; the label becomes a superscript.
        s = Regex.Replace(s, @"\\xrightarrow\s*\{(.*?)\}(?=\s|$)",
                          m => @"\;\to^{\;" + m.Groups[1].Value + @"\;}\;");
        return s;
    }
}
