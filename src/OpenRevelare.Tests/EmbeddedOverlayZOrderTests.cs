using System.Runtime.InteropServices;
using OpenRevelare.Gui.Controls;
using Xunit;

namespace OpenRevelare.Tests;

public class EmbeddedOverlayZOrderTests
{
    [Fact]
    public void Overlay_is_raised_above_preview_within_owner_without_activation_or_topmost()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint owner = Window(0), foreground = GetForegroundWindow();
        try
        {
            nint overlayContainer = Window(owner);
            nint overlay = Window(overlayContainer);
            nint preview = Window(owner);
            for (int i = 0; i < 3; i++)
            {
                Assert.True(SetWindowPos(preview, 0, 0, 0, 0, 0, 0x13));
                Assert.NotEqual(0, GetWindow(overlayContainer, 3));
                Assert.True(EmbeddedOverlayZOrder.BringToFront(overlay, owner));
                Assert.Equal(0, GetWindow(overlayContainer, 3));
                Assert.Equal(owner, GetAncestor(overlay, 2));
                Assert.Equal(0, GetWindowLong(owner, -20) & 8); // WS_EX_TOPMOST stays off
                Assert.Equal(foreground, GetForegroundWindow());
            }
        }
        finally { DestroyWindow(owner); }
    }

    [Fact]
    public void Overlay_cannot_raise_a_different_application_or_the_owner_itself()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint owner = Window(0), other = Window(0);
        try
        {
            nint overlay = Window(owner);
            Assert.False(EmbeddedOverlayZOrder.BringToFront(overlay, other));
            Assert.False(EmbeddedOverlayZOrder.BringToFront(owner, owner));
            Assert.False(EmbeddedOverlayZOrder.BringToFront(0, owner));
            Assert.Equal(owner, GetAncestor(overlay, 2));
        }
        finally { DestroyWindow(owner); DestroyWindow(other); }
    }

    private static nint Window(nint parent)
    {
        nint hwnd = CreateWindowEx(0, "STATIC", "", parent == 0 ? 0x80000000u : 0x40000000u,
            0, 0, 200, 100, parent, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        return hwnd;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
}
