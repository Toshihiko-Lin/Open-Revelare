using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OpenRevelare.Gui.ViewModels;
using OpenRevelare.Gui.Views;

namespace OpenRevelare.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Apply the persisted light/dark theme preference to the whole app.</summary>
    public static void ApplyTheme(string? theme = null)
    {
        theme ??= Services.Settings.Current.Theme;
        if (Current is not null)
            Current.RequestedThemeVariant = theme == "light"
                ? Avalonia.Styling.ThemeVariant.Light
                : Avalonia.Styling.ThemeVariant.Dark;
    }

    /// <summary>
    /// Repaint the photo backdrop (预览区右键 → 背景色). Unlike every other colour this one is
    /// NOT in the theme dictionaries — the surround a negative is judged against is a viewing
    /// condition the user picks, so it must survive a light/dark switch. Overwriting the
    /// app-level <c>ViewerBrush</c> works because the preview binds it with DynamicResource.
    /// </summary>
    public static void ApplyViewerBackground(string? hex = null)
    {
        hex ??= Services.Settings.Current.ViewerBackground;
        if (Current is null) return;
        try
        {
            Current.Resources["ViewerBrush"] =
                new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex));
        }
        catch (FormatException) { /* a hand-edited settings.json → keep the current backdrop */ }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services.Loc.Apply();       // language first: the main window resolves its text as it loads
        ApplyTheme();               // honour the saved theme before the window shows
        ApplyViewerBackground();    // …and the saved photo backdrop
        // Let Core reach the session DNG cache without depending on it (see
        // RawDecode.LinearDngCache) — the Adobe round trip then happens once per frame instead
        // of once per decode, and a cached linear DNG is what makes region decoding possible on
        // that backend at all.
        OpenRevelare.Core.RawDecode.LinearDngCache = Services.DngCache.GetOrConvert;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The cache is session-scoped: take it with us on the way out. ShutdownRequested
            // fires for a normal close; a crash leaves an orphan the NEXT run identifies by its
            // dead process id and sweeps.
            var vm = new MainViewModel();
            // Also flush the open roll here, not only in MainWindow.OnClosing: a shutdown driven
            // by the OS or by Exit() never closes the window through the normal path.
            desktop.ShutdownRequested += (_, _) => { vm.FlushRollNow(); Services.DngCache.Cleanup(); };
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Optional startup files: `OpenRevelare.Gui <path> [<path> …]` opens them as a roll
            // (double-click association, or headless verification). Deferred so the window shows first.
            // A .ncproj argument REOPENS that roll instead — it is a project, not a negative, and
            // handing it to the decoder would just fail.
            var startFiles = (desktop.Args ?? Array.Empty<string>()).Where(File.Exists).ToList();
            string? startProject = startFiles.FirstOrDefault(
                f => string.Equals(Path.GetExtension(f), ".ncproj", StringComparison.OrdinalIgnoreCase));
            // A roll named on the command line goes straight to 修片; otherwise the session starts
            // on the wall (the view model's default), which only needs its cards loaded.
            if (startProject is not null)
                Dispatcher.UIThread.Post(async () =>
                {
                    await vm.OpenProjectAsync(startProject);
                    vm.EnterDevelop();
                });
            else if (startFiles.Count > 0)
                Dispatcher.UIThread.Post(async () =>
                {
                    await vm.LoadRollAsync(startFiles);
                    vm.EnterDevelop();
                });
            else
                Dispatcher.UIThread.Post(async () => await vm.EnterLibraryAsync());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
