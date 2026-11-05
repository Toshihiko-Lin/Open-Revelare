using System.Runtime.InteropServices;

namespace OpenRevelare.Gui.Controls;

/// <summary>Orders embedded children only. Never promotes the application to an OS topmost window.</summary>
internal static class EmbeddedOverlayZOrder
{
    internal static bool BringToFront(nint child, nint owner)
    {
        if (!OperatingSystem.IsWindows() || child == 0 || owner == 0 || child == owner
            || GetAncestor(child, 2) != owner) return false;
        // NativeControlHost may place the embedded root inside another attachment container.
        // Raise that owner's direct child, not just the leaf inside its own hidden sibling group.
        nint current = child;
        for (int depth = 0; depth < 64; depth++)
        {
            nint parent = GetParent(current);
            if (parent == owner)
                return GetWindow(current, 3) == 0 || SetWindowPos(current, 0, 0, 0, 0, 0, 0x0213); // TOP, no move/size/activate/owner-order
            if (parent == 0 || parent == current) return false;
            current = parent;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
