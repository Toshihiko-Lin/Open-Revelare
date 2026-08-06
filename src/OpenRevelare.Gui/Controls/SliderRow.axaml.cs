using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using static Avalonia.Input.InputElement;

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

    public SliderRow()
    {
        InitializeComponent();
        // Resolved by name, not through the generated field: this control loads its XAML with a
        // hand-written InitializeComponent, which bypasses the generated name assignments — the
        // field compiles but is still null here.
        if (this.FindControl<Slider>("Sld") is not { } sld) return;
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
            if (SpinValue is { } v) Value = (double)v;
            _syncing = false;
        }
    }

    private void OnLabelDoubleTapped(object? sender, TappedEventArgs e) => Value = DefaultValue;
}
