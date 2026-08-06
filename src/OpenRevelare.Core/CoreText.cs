using System;

namespace OpenRevelare.Core;

/// <summary>
/// Translation seam for the handful of Core messages that reach a user's eyes — the exception
/// texts the GUI prints into its status bar and error dialogs.
///
/// Core cannot reference the GUI's translation table (the CLI links Core too, and the dependency
/// would run the wrong way), so the host installs a <see cref="Lookup"/> instead. Unset — which is
/// what the CLI does — every message stays exactly the Chinese it has always been.
///
/// Keyed on the Chinese source string, same as the GUI's table, so one table covers both.
/// </summary>
public static class CoreText
{
    /// <summary>Returns the translated string for a Chinese source string, or null to keep it.</summary>
    public static Func<string, string?>? Lookup;

    /// <summary>Translate a plain message.</summary>
    public static string T(string zh) => Lookup?.Invoke(zh) ?? zh;

    /// <summary>Translate an interpolated message; the key is its composite format, e.g.
    /// <c>"DNG Converter 退出码 {0}"</c>.</summary>
    public static string F(FormattableString fs)
    {
        if (Lookup?.Invoke(fs.Format) is { } t)
        {
            try { return string.Format(t, fs.GetArguments()); }
            catch (FormatException) { /* bad hole in the table → keep the original */ }
        }
        return fs.ToString();
    }
}
