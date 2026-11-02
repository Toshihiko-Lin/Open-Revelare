using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace OpenRevelare.Gui.Views;

public partial class MainWindow
{
    // UniformGrid provides equal-width segments but not WrapPanel's directional navigation.
    // Handle the tab keys here, before the window's image-navigation shortcuts see them.
    private void OnInspectorTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TabStrip strip || e.KeyModifiers != KeyModifiers.None) return;
        int last = strip.ItemCount - 1;
        if (last < 0) return;
        int target = e.Key switch
        {
            Key.Left => Math.Max(0, strip.SelectedIndex - 1),
            Key.Right => Math.Min(last, strip.SelectedIndex + 1),
            Key.Home => 0,
            Key.End => last,
            _ => -1,
        };
        if (target < 0) return;
        strip.SelectedIndex = target;
        strip.ContainerFromIndex(target)?.Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
