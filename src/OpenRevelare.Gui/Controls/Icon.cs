using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace OpenRevelare.Gui.Controls;

/// <summary>
/// A 16×16 stroked line glyph — the single icon primitive for the whole UI.
///
/// The geometries live in <c>Styles/Icons.axaml</c> and are authored directly on a 16-unit
/// grid, so the template can draw with <c>Stretch="None"</c>: the 1.5 px stroke then stays
/// exactly 1.5 px at every size and DPI. (Authoring at 24 and scaling down would make the
/// stroke thinner than the rest of the set, which is what kills the "one icon family" look.)
///
/// <see cref="Stroke"/> defaults to the inherited <see cref="TemplatedControl.Foreground"/>,
/// so an icon inside a Button lights up together with the button's text on hover — no
/// per-state icon styling needed anywhere.
/// </summary>
public sealed class Icon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    /// <summary>The glyph outline, on the 16-unit authoring grid.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.5);

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}
