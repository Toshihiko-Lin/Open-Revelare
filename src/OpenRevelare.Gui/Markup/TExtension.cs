using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using OpenRevelare.Gui.Services;

namespace OpenRevelare.Gui.Markup;

/// <summary>
/// XAML side of <see cref="Loc"/>: <c>Text="{i18n:T '导出失败：'}"</c>.
///
/// The argument is quoted at every call site without exception. A markup-extension argument is
/// parsed before it ever reaches this class, and the UI strings here are full sentences — an
/// unquoted one containing a comma would be read as a second argument and one containing "=" as a
/// named property. Quoting sidesteps the whole question.
///
/// Returns a binding rather than the translated string, so switching language repaints windows
/// that are already open instead of only the ones opened afterwards.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    /// <summary>The Chinese source string, which is also the translation-table key.</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding(nameof(LocEntry.Value))
        {
            Source = new LocEntry(Key),
            Mode = BindingMode.OneWay,
        };
}

/// <summary>One live translation, bound to by one XAML property.
///
/// Stored with a strong reference in <see cref="Live"/> so that the binding source is never
/// garbage-collected between initialisation and the first language switch. Avalonia 11's binding
/// expression does not guarantee holding a strong reference to an INPC source, so keeping the
/// entry alive here is necessary for <see cref="RefreshAll"/> to reliably reach every live
/// binding after a language change.</summary>
public sealed class LocEntry : INotifyPropertyChanged
{
    private static readonly List<LocEntry> Live = new();

    static LocEntry() => Loc.Changed += RefreshAll;

    private readonly string _key;

    public LocEntry(string key)
    {
        _key = key;
        lock (Live) Live.Add(this);
    }

    public string Value => Loc.T(_key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private static void RefreshAll()
    {
        LocEntry[] snapshot;
        lock (Live) snapshot = Live.ToArray();

        var args = new PropertyChangedEventArgs(nameof(Value));
        foreach (var e in snapshot)
            e.PropertyChanged?.Invoke(e, args);
    }
}
