using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// One parameter row: a fixed-width label, a slider, and a numeric spin box that
/// stay in sync — the Avalonia analogue of Python's <c>_make_row(label, slider,
/// spinbox)</c>. Double-clicking the label resets the value to
/// <see cref="DefaultValue"/> (Python's clickable-label reset).
///
/// <see cref="Value"/> is the single source of truth (TwoWay by default). The
/// spin box is bridged through <see cref="SpinValue"/> because
/// <see cref="NumericUpDown"/> works in <c>decimal?</c>.
/// </summary>
public partial class SliderRow : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<SliderRow, string>(nameof(Label), "");

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<SliderRow, double>(nameof(Minimum), 0.0);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<SliderRow, double>(nameof(Maximum), 1.0);

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SliderRow, double>(
            nameof(Value), 0.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> DefaultValueProperty =
        AvaloniaProperty.Register<SliderRow, double>(nameof(DefaultValue), 0.0);

    public static readonly StyledProperty<double> IncrementProperty =
        AvaloniaProperty.Register<SliderRow, double>(nameof(Increment), 0.01);

    public static readonly StyledProperty<int> DecimalsProperty =
        AvaloniaProperty.Register<SliderRow, int>(nameof(Decimals), 2);

    public static readonly StyledProperty<string> FormatStringProperty =
        AvaloniaProperty.Register<SliderRow, string>(nameof(FormatString), "0.00");

    public static readonly StyledProperty<decimal?> SpinValueProperty =
        AvaloniaProperty.Register<SliderRow, decimal?>(
            nameof(SpinValue), 0m, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>
    /// A brush painted along the whole track — blue→yellow under 色温, dark→light under 曝光 —
    /// so the slider says which way is which without a label. When set, the ordinary
    /// left-to-right fill is dropped (it would hide the gradient) and the track thickens so
    /// the colours can be read; the thumb alone marks the value.
    /// </summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SliderRow, IBrush?>(nameof(TrackBrush));

    /// <summary>
    /// The value's neutral point is <see cref="DefaultValue"/> rather than the left end. The
    /// fill then grows from that point in either direction, and a tick marks it, so a row at
    /// rest reads as "untouched" instead of "half full".
    /// </summary>
    public static readonly StyledProperty<bool> BipolarProperty =
        AvaloniaProperty.Register<SliderRow, bool>(nameof(Bipolar));

    /// <summary>
    /// Raised when the user grabs a slider thumb, and again when they let go.
    ///
    /// The host uses this to switch the preview into a low-latency drag mode: without it a drag is
    /// just a stream of value changes, and a debounce-and-render design shows NOTHING until the
    /// user stops moving. Bubbling, so a window subscribes once and covers every row it contains.
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> InteractionStartedEvent =
        RoutedEvent.Register<SliderRow, RoutedEventArgs>(
            nameof(InteractionStarted), RoutingStrategies.Bubble);

    /// <inheritdoc cref="InteractionStartedEvent"/>
    public static readonly RoutedEvent<RoutedEventArgs> InteractionEndedEvent =
        RoutedEvent.Register<SliderRow, RoutedEventArgs>(
            nameof(InteractionEnded), RoutingStrategies.Bubble);

    public event EventHandler<RoutedEventArgs> InteractionStarted
    {
        add => AddHandler(InteractionStartedEvent, value);
        remove => RemoveHandler(InteractionStartedEvent, value);
    }

    public event EventHandler<RoutedEventArgs> InteractionEnded
    {
        add => AddHandler(InteractionEndedEvent, value);
        remove => RemoveHandler(InteractionEndedEvent, value);
    }

    private bool _syncing;
    private bool _grabbed;
    private Slider? _sld;
    private Border? _centerFill;
    private Border? _zeroTick;

    public SliderRow()
    {
        InitializeComponent();
        // Resolved by name, not through the generated field: this control loads its XAML with a
        // hand-written InitializeComponent, which bypasses the generated name assignments — the
        // field compiles but is still null here.
        if (this.FindControl<Slider>("Sld") is not { } sld) return;
        _sld = sld;
        _centerFill = this.FindControl<Border>("CenterFill");
        _zeroTick = this.FindControl<Border>("ZeroTick");
        sld.SizeChanged += (_, _) => UpdateMarks();
        // TUNNEL, and handled events too: Avalonia's Slider hands the drag to a Thumb which
        // captures the pointer and marks the events handled, so a plain bubbling subscription on
        // this row would see neither the grab nor the release.
        sld.AddHandler(PointerPressedEvent, (_, _) => Grab(),
                       RoutingStrategies.Tunnel, handledEventsToo: true);
        sld.AddHandler(PointerReleasedEvent, (_, _) => Release(),
                       RoutingStrategies.Tunnel, handledEventsToo: true);
        // Backstop: releasing outside the window, or anything else that steals capture, would
        // otherwise strand the preview in drag mode at half resolution.
        sld.PointerCaptureLost += (_, _) => Release();
    }

    private void Grab()
    {
        if (_grabbed) return;
        _grabbed = true;
        RaiseEvent(new RoutedEventArgs(InteractionStartedEvent));
    }

    private void Release()
    {
        if (!_grabbed) return;
        _grabbed = false;
        RaiseEvent(new RoutedEventArgs(InteractionEndedEvent));
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public double Increment { get => GetValue(IncrementProperty); set => SetValue(IncrementProperty, value); }
    public int Decimals { get => GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string FormatString { get => GetValue(FormatStringProperty); set => SetValue(FormatStringProperty, value); }
    public decimal? SpinValue { get => GetValue(SpinValueProperty); set => SetValue(SpinValueProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public bool Bipolar { get => GetValue(BipolarProperty); set => SetValue(BipolarProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DecimalsProperty)
        {
            int d = Decimals;
            FormatString = d <= 0 ? "0" : "0." + new string('0', d);
        }
        else if (change.Property == ValueProperty && !_syncing)
        {
            _syncing = true;
            SpinValue = (decimal)Value;
            _syncing = false;
        }
        else if (change.Property == SpinValueProperty && !_syncing)
        {
            _syncing = true;
            // An EMPTY NumericUpDown writes null. Not an unparseable one — "abc" and "   " both
            // leave the old number alone — but an empty string does, and so does a null one, which
            // is the state the box passes through every time the user selects the digits and
            // deletes them on the way to typing a different value.
            //
            // Refusing the null protects Value, but that alone leaves the two halves of the bridge
            // disagreeing: the spin box is bound to null and renders empty while Value still holds
            // the real number, and nothing re-syncs them, because the only thing that pushes
            // Value → SpinValue is a CHANGE to Value — which never came. The row then reads as
            // cleared, and the next edit commits from an empty baseline. Push the authoritative
            // Value back out instead, so a null round-trips to where it started.
            if (SpinValue is { } v) Value = (double)v;
            else SpinValue = (decimal)Value;
            _syncing = false;
        }

        if (change.Property == ValueProperty || change.Property == DefaultValueProperty)
        {
            PseudoClasses.Set(":modified", Math.Abs(Value - DefaultValue) > 1e-9);
            UpdateMarks();
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty
                 || change.Property == BipolarProperty)
        {
            UpdateMarks();
        }
        else if (change.Property == TrackBrushProperty)
        {
            if (_sld is { } sld)
            {
                // Null falls back to the theme's groove via the slider's own style, so a row
                // that loses its brush does not keep a stale one.
                if (TrackBrush is { } b) sld.Background = b;
                else sld.ClearValue(BackgroundProperty);
                sld.Classes.Set("gradient", TrackBrush is not null);
            }
            UpdateMarks();
        }
    }

    /// <summary>
    /// Places the zero tick and the centre-growing fill under the slider. Track geometry
    /// mirrors the Slider template: the thumb is <c>ThumbSize</c> wide and the value spans the
    /// track minus the thumb, so the position of value <c>v</c> is the thumb's centre at <c>v</c>.
    /// </summary>
    private void UpdateMarks()
    {
        if (_sld is not { } sld || _centerFill is not { } fill || _zeroTick is not { } tick) return;
        bool bipolar = Bipolar;
        bool gradient = TrackBrush is not null;
        sld.Classes.Set("bipolar", bipolar);
        // The gradient already says which side is which; a fill on top of it would only hide
        // the colours. The tick still marks the neutral point.
        fill.IsVisible = bipolar && !gradient;
        tick.IsVisible = bipolar;
        if (!bipolar) return;

        double w = sld.Bounds.Width;
        double range = Maximum - Minimum;
        if (w <= ThumbSize || range <= 0) { fill.IsVisible = false; tick.IsVisible = false; return; }
        double usable = w - ThumbSize;
        double X(double v) => ThumbSize / 2 + Math.Clamp((v - Minimum) / range, 0, 1) * usable;
        double x0 = X(DefaultValue), x1 = X(Value);
        tick.Margin = new Thickness(Math.Round(x0) - 0.5, 0, 0, 0);
        fill.Margin = new Thickness(Math.Min(x0, x1), 0, 0, 0);
        fill.Width = Math.Abs(x1 - x0);
    }

    /// <summary>Thumb diameter in the app's Slider theme (App.axaml); the marks assume it.</summary>
    private const double ThumbSize = 14;

    private void OnLabelDoubleTapped(object? sender, TappedEventArgs e) => Value = DefaultValue;
}
