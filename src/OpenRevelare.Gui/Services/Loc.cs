using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// UI language. Chinese is the source language: every user-facing literal in this project stays
/// written in Chinese at its call site, and a translation table maps it to the other language.
///
/// Keying on the source string rather than on invented ids is deliberate. It keeps call sites
/// readable (<c>Loc.T("导出失败：")</c> says what it prints), it makes an untranslated string
/// degrade to correct Chinese instead of to a stray <c>Export.Error.Title</c>, and it means adding
/// a string costs one dictionary line rather than a key, a lookup and a comment explaining both.
///
/// Two entry points, because C# has two kinds of literal:
/// <list type="bullet">
///   <item><see cref="T(string)"/> for a plain literal.</item>
///   <item><see cref="F"/> for an interpolated one — <c>Loc.F($"已导出 {n} 帧")</c> looks the
///   table up by the composite format (<c>"已导出 {0} 帧"</c>) the compiler already built, so the
///   holes keep their order/format specifiers and nothing has to be rewritten as string.Format.</item>
/// </list>
/// XAML goes through <see cref="Markup.TExtension"/>, which binds instead of resolving once, so a
/// language switch repaints every window that is already open.
///
/// <see cref="T(string)"/> and <see cref="F"/> called from C# do NOT: they hand back a string, and
/// a string does not know where it came from. That is right for the text this app produces most of
/// — a status line reporting what just happened, a dialog built at the moment it is shown — but
/// wrong for text that goes on screen and STAYS there. Whoever owns such a label subscribes to
/// <see cref="Changed"/> and re-resolves it; see <c>LibraryViewModel.RetranslateText</c> and
/// <c>MainViewModel.RetranslateText</c>, which between them cover every persistent label the code
/// (as opposed to the XAML) puts up.
/// </summary>
public static class Loc
{
    /// <summary>Resolved language actually in effect: "zh" or "en".</summary>
    public static string Current { get; private set; } = "zh";

    /// <summary>Raised after <see cref="Apply"/> changes the language, on the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>zh → target-language table. Null while the source language (zh) is in effect.</summary>
    private static Dictionary<string, string>? _table;

    /// <summary>
    /// Switch language. <paramref name="setting"/> is the persisted preference:
    /// "auto" (follow the OS), "zh" or "en". Safe to call before any window exists.
    /// </summary>
    public static void Apply(string? setting = null)
    {
        setting ??= Settings.Current.Language;
        string lang = setting switch
        {
            "zh" => "zh",
            "en" => "en",
            _ => SystemLanguage(),
        };
        if (lang == Current && (lang == "zh") == (_table is null)) return;

        Current = lang;
        _table = lang == "zh" ? null : LoadTable(lang);
        OpenRevelare.Core.CoreText.Lookup = Lookup;
        Changed?.Invoke();
    }

    /// <summary>The OS UI language, collapsed to the two we ship. Anything not Chinese gets
    /// English — a half-translated Chinese UI is worse for a French user than an English one.</summary>
    private static string SystemLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh" : "en";

    private static Dictionary<string, string>? LoadTable(string lang)
    {
        try
        {
            var uri = new Uri($"avares://OpenRevelare/Assets/i18n/{lang}.json");
            using var s = AssetLoader.Open(uri);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s);
        }
        catch
        {
            // A missing or corrupt table falls back to Chinese rather than to an empty UI.
            return null;
        }
    }

    /// <summary>Translate a plain literal. Unknown strings come back unchanged (i.e. Chinese).</summary>
    public static string T(string zh) => Keys(Lookup(zh) ?? zh);

    /// <summary>
    /// Spell shortcuts with the modifier the running platform actually uses: ⌘ on macOS, Ctrl
    /// elsewhere.
    ///
    /// Applied after the table lookup, on the way out, so the translation keys stay written
    /// "Ctrl+…" — they are the Chinese source strings and must not vary by platform, or a mac
    /// would miss every entry in en.json.
    ///
    /// Only the modifier's NAME is rewritten. The gestures themselves are bound from
    /// <c>MainWindow.Accel</c> / <see cref="Markup.AccelExtension"/>; this keeps the prose that
    /// describes them honest, which is the half a user reads before pressing anything.
    ///
    /// "Cmd" is one character shorter than "Ctrl", which matters because the 快捷键 help is a
    /// monospace table whose second column is aligned by spaces: a plain replace pulls every
    /// shortcut line one column left of the plain-key lines (N / K / F / Esc) and the table
    /// visibly breaks. Each substitution therefore gives back the character it took, when there
    /// is a run of spaces after it to give it back to.
    /// </summary>
    private static string Keys(string s)
    {
        if (!OperatingSystem.IsMacOS() || !s.Contains("Ctrl", StringComparison.Ordinal)) return s;
        // "Ctrl+Z  " → "Cmd+Z   ": swap the name, then restore the column by re-padding the
        // following gap. A shortcut with no trailing gap (mid-sentence, "（Ctrl+1）") just gets
        // one character shorter, which is what prose should do.
        return System.Text.RegularExpressions.Regex.Replace(
            s, @"Ctrl(\+\S+)?( +)?",
            m => "Cmd" + m.Groups[1].Value
                 + (m.Groups[2].Success ? m.Groups[2].Value + " " : ""));
    }

    /// <summary>
    /// Translate a literal that means different things in different places. 「关闭」 is "Off" in
    /// the FBDD combo and "Close" on every dialog button — one Chinese word, two English ones,
    /// and a single table keyed on the Chinese cannot hold both.
    ///
    /// The table key is <c>context|zh</c>; a table with no such entry falls back to the plain
    /// key and then to the Chinese, so adding a context never breaks an existing string.
    /// </summary>
    public static string T(string zh, string context)
        => Lookup(context + "|" + zh) is { } v ? Keys(v) : T(zh);

    /// <summary>Raw table lookup — null when there is no entry. This is what Core's
    /// <see cref="OpenRevelare.Core.CoreText"/> is pointed at, so the exception texts the GUI
    /// prints follow the UI language without Core having to know this class exists.</summary>
    public static string? Lookup(string zh)
        => _table is not null && _table.TryGetValue(zh, out string? v) && v.Length > 0 ? v : null;

    /// <summary>
    /// Translate an interpolated literal: <c>Loc.F($"已添加 {n} 帧")</c>.
    ///
    /// The lookup key is <see cref="FormattableString.Format"/> — the composite format the C#
    /// compiler derives from the interpolation, holes and all ("已添加 {0} 帧"), so the entry in
    /// the table is a format string with the same numbered holes.
    /// </summary>
    public static string F(FormattableString fs)
    {
        if (Lookup(fs.Format) is { } v)
        {
            try { return Keys(string.Format(CultureInfo.CurrentCulture, v, fs.GetArguments())); }
            catch (FormatException) { /* a bad hole in the table → fall through to Chinese */ }
        }
        return Keys(fs.ToString(CultureInfo.CurrentCulture));
    }
}
