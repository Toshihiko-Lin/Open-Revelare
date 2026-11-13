using Avalonia;
using Avalonia.Controls;
using OpenRevelare.Gui.Controls;
using Xunit;

namespace OpenRevelare.Tests;

public class EmbeddedOverlayHostTests
{
    [Fact]
    public void Non_windows_overlay_uses_managed_child_with_inherited_context_and_layout()
    {
        if (OperatingSystem.IsWindows()) return;
        var context = new object();
        var card = new Border { Width = 120, Height = 40 };
        var host = new EmbeddedOverlayHost { DataContext = context, Content = card };
        Assert.Same(card, host.Child);
        Assert.Same(context, card.DataContext);
        host.Measure(new Size(420, 200));
        Assert.Equal(new Size(120, 40), host.DesiredSize);
        host.EnsureAboveNativePreview(); // No native window API is needed on this path.

        var replacement = new Border();
        host.Content = replacement;
        Assert.Same(replacement, host.Child);
        Assert.Null(card.Parent);
        host.Content = null;
        Assert.Null(host.Child);
    }
}
