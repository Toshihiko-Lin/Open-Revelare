using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenRevelare.Gui.Services;

/// <summary>Keep new windows inside the current monitor's usable area, in device-independent units.</summary>
internal static class WindowLayout
{
    public static void Register() => Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        Dispatcher.UIThread.Post(() => FitToScreen(window), DispatcherPriority.Loaded));

    private static void FitToScreen(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double width = Math.Max(1, screen.WorkingArea.Width / scale - 24);
        double height = Math.Max(1, screen.WorkingArea.Height / scale - 64);
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        // Dialog content already scrolls. Cap its auto-size as well as its initial size.
        if (window is not Views.MainWindow)
        {
            window.MaxWidth = Math.Min(window.MaxWidth, width);
            window.MaxHeight = Math.Min(window.MaxHeight, height);
        }
        if (window.Bounds.Width > width) window.Width = width;
        if (window.Bounds.Height > height) window.Height = height;
        window.UpdateLayout();
        var frame = window.FrameSize ?? window.Bounds.Size;
        var area = screen.WorkingArea;
        int x = Math.Clamp(window.Position.X, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(frame.Width * scale)));
        int y = Math.Clamp(window.Position.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(frame.Height * scale)));
        window.Position = new PixelPoint(x, y);
    }
}
