using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Platform;
using Avalonia.Threading;

namespace OpenRevelare.Gui.Controls;

/// <summary>A themed Avalonia card rendered in an embedded child surface. Unlike a PopupRoot,
/// it belongs to the main window and can sit above a native preview without changing its HDR path.</summary>
public sealed class EmbeddedOverlayHost : NativeControlHost
{
    public static readonly StyledProperty<Control?> ContentProperty =
        AvaloniaProperty.Register<EmbeddedOverlayHost, Control?>(nameof(Content));
    public Control? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    private EmbeddableControlRoot? _root;
    private INotifyPropertyChanged? _context;

    private bool _raiseQueued;
    public EmbeddedOverlayHost() => LayoutUpdated += (_, _) => EnsureAboveNativePreview();

    internal void EnsureAboveNativePreview()
    {
        if (!OperatingSystem.IsWindows() || !IsEffectivelyVisible || _root is null) return;
        RaiseNativeChild();
        if (_raiseQueued) return;
        _raiseQueued = true;
        // NativeControlHost updates its child attachments after layout. Reassert sibling order
        // afterward; Avalonia ZIndex alone cannot order HWNDs created by different hosts.
        Dispatcher.UIThread.Post(() =>
        {
            _raiseQueued = false;
            RaiseNativeChild();
        }, DispatcherPriority.Background);
    }

    private void RaiseNativeChild()
    {
        if (!IsEffectivelyVisible || _root?.TryGetPlatformHandle() is not { } child
            || TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is not { } owner) return;
        EmbeddedOverlayZOrder.BringToFront(child.Handle, owner.Handle);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _root = new EmbeddableControlRoot { Content = Content, DataContext = DataContext,
            Width = Bounds.Width, Height = Bounds.Height };
        _root.Prepare();
        _root.SizeChanged += (_, _) => RoundNativeSurface();
        ObserveContext();
        _root.StartRendering();
        InvalidateMeasure(); // Re-measure after the child root has supplied binding inheritance.
        RoundNativeSurface();
        EnsureAboveNativePreview();
        return _root.TryGetPlatformHandle()
            ?? throw new InvalidOperationException("Embedded overlay has no native child surface.");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_context is not null) _context.PropertyChanged -= OnContextChanged;
        _context = null;
        if (_root is { } root)
        {
            root.Content = null;
            root.StopRendering();
            root.Dispose();
            _root = null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Content is not { } content) return default;
        content.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return content.DesiredSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) EnsureAboveNativePreview();
        if (_root is not { } root) return;
        if (change.Property == BoundsProperty)
        {
            root.Width = Bounds.Width;
            root.Height = Bounds.Height;
        }
        else if (change.Property == DataContextProperty)
        {
            root.DataContext = DataContext;
            ObserveContext();
        }
        else if (change.Property == ContentProperty) { root.Content = Content; InvalidateMeasure(); }
    }

    private void RoundNativeSurface()
    {
        if (!OperatingSystem.IsWindows() || _root is not { } root
            || root.TryGetPlatformHandle() is not { } handle) return;
        double scale = root.RenderScaling;
        int width = (int)Math.Ceiling(Bounds.Width * scale);
        int height = (int)Math.Ceiling(Bounds.Height * scale);
        if (width <= 0 || height <= 0) return;
        double radius = Content is Border border ? border.CornerRadius.TopLeft : 0;
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1,
            (int)Math.Ceiling(radius * 2 * scale), (int)Math.Ceiling(radius * 2 * scale));
        // The surface owns its silhouette; Windows takes ownership of an installed HRGN.
        if (region != 0 && SetWindowRgn(handle.Handle, region, true) == 0) DeleteObject(region);
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);

    private void ObserveContext()
    {
        if (_context is not null) _context.PropertyChanged -= OnContextChanged;
        _context = DataContext as INotifyPropertyChanged;
        if (_context is not null) _context.PropertyChanged += OnContextChanged;
    }

    private void OnContextChanged(object? sender, PropertyChangedEventArgs args)
    {
        // Text can add or remove a progress row while the task is running.
        if (Dispatcher.UIThread.CheckAccess()) InvalidateMeasure();
        else Dispatcher.UIThread.Post(InvalidateMeasure);
        if (Dispatcher.UIThread.CheckAccess()) EnsureAboveNativePreview();
    }
}
