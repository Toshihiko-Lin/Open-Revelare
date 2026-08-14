using System;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace OpenRevelare.Gui.Markup;

/// <summary>
/// XAML side of the platform shortcut modifier: <c>InputGesture="{i18n:Accel 'N'}"</c> yields
/// ⌘N on macOS and Ctrl+N everywhere else.
///
/// Exists because the modifier is not the same key on every platform — Avalonia reports ⌘ as
/// <see cref="KeyModifiers.Meta"/> and keeps <see cref="KeyModifiers.Control"/> for the physical
/// Ctrl key. The menus used to spell every accelerator "Ctrl+…" literally, which on macOS
/// advertised a chord mac software never uses and which the window's key handler (correctly, now)
/// does not listen for.
///
/// Returns a real <see cref="KeyGesture"/> rather than a string so that the value is the same
/// kind of object the hand-written gestures are, and a typo fails at parse time instead of
/// rendering as dead text.
///
/// NOT localised: these are key names, and Loc's table is for prose. The rendering of Meta as ⌘
/// is macOS's own, done when the menu draws.
/// </summary>
public sealed class AccelExtension : MarkupExtension
{
    public AccelExtension() { }

    public AccelExtension(string key) => Key = key;

    /// <summary>The key the modifier applies to, e.g. <c>N</c>, <c>Z</c>, <c>OemComma</c>.
    /// May carry extra modifiers of its own: <c>Shift+T</c>.</summary>
    public string Key { get; set; } = "";

    /// <summary>⌘ on macOS, Ctrl elsewhere — the spelling KeyGesture.Parse accepts.</summary>
    public static string ModifierName => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => KeyGesture.Parse($"{ModifierName}+{Key}");
}
