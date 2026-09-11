using OpenRevelare.Gui.Controls;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class WindowsPreviewDisplayChangeBridgeTests
{
    [Fact]
    public void Classifier_covers_display_settings_dpi_and_cross_screen_window_movement()
    {
        uint[] refreshOnly =
        {
            WindowsPreviewDisplayMessages.WmMove,
            WindowsPreviewDisplayMessages.WmSettingChange,
            WindowsPreviewDisplayMessages.WmDisplayChange,
        };
        foreach (uint message in refreshOnly)
        {
            Assert.True(WindowsPreviewDisplayMessages.TryClassify(message, out var kind));
            Assert.Equal(WindowsPreviewDisplayRefreshKind.Refresh, kind);
        }

        uint[] refreshAndMetrics =
        {
            WindowsPreviewDisplayMessages.WmWindowPosChanged,
            WindowsPreviewDisplayMessages.WmDpiChanged,
        };
        foreach (uint message in refreshAndMetrics)
        {
            Assert.True(WindowsPreviewDisplayMessages.TryClassify(message, out var kind));
            Assert.True((kind & WindowsPreviewDisplayRefreshKind.Refresh) != 0);
            Assert.True((kind & WindowsPreviewDisplayRefreshKind.Metrics) != 0);
        }

        Assert.False(WindowsPreviewDisplayMessages.TryClassify(0x000F, out _)); // WM_PAINT
        Assert.False(WindowsPreviewDisplayMessages.TryClassify(0x0200, out _)); // WM_MOUSEMOVE
    }

    [Fact]
    public void Bridge_calls_Avalonia_first_notifies_for_relevant_messages_and_restores_once()
    {
        var native = new FakeSubclassNative();
        var observed = new List<WindowsPreviewDisplayRefreshKind>();
        using WindowsPreviewDisplayChangeBridge bridge =
            WindowsPreviewDisplayChangeBridge.Install(
                native,
                topLevelHwnd: (nint)0x101,
                kind =>
                {
                    Assert.Equal(1, native.PreviousCalls);
                    observed.Add(kind);
                });

        nint result = native.Invoke(WindowsPreviewDisplayMessages.WmDpiChanged);
        Assert.Equal(native.PreviousResult, result);
        Assert.Equal(
            WindowsPreviewDisplayRefreshKind.Refresh |
            WindowsPreviewDisplayRefreshKind.Metrics,
            Assert.Single(observed));

        observed.Clear();
        native.Invoke(0x000F); // WM_PAINT
        Assert.Empty(observed);
        Assert.Equal(2, native.PreviousCalls);

        bridge.Dispose();
        bridge.Dispose();
        Assert.False(bridge.IsInstalled);
        Assert.Equal(1, native.RestoreCalls);
        Assert.Equal((nint)0x101, native.RestoredHwnd);
        Assert.Equal(native.PreviousProcedure, native.RestoredPreviousProcedure);
        Assert.Same(native.InstalledProcedure, native.RestoredInstalledProcedure);
    }

    [Fact]
    public void Callback_or_previous_WndProc_failure_never_unwinds_through_native_callback()
    {
        var native = new FakeSubclassNative { ThrowOnPrevious = true };
        using WindowsPreviewDisplayChangeBridge bridge =
            WindowsPreviewDisplayChangeBridge.Install(
                native,
                topLevelHwnd: (nint)0x202,
                _ => throw new InvalidOperationException("Injected callback failure."));

        nint result = native.Invoke(WindowsPreviewDisplayMessages.WmDisplayChange);

        Assert.Equal(native.DefWindowResult, result);
        Assert.Equal(1, native.DefWindowCalls);
    }

    [Fact]
    public void Backend_accepts_all_three_managed_Windows_contract_encodings()
    {
        Assert.True(WindowsPreviewHostBackend.IsNativePresentationEncoding(
            PresentationEncoding.LinearExtendedSrgbRgba16F));
        Assert.True(WindowsPreviewHostBackend.IsNativePresentationEncoding(
            PresentationEncoding.MonitorDeviceBgra8));
        Assert.True(WindowsPreviewHostBackend.IsNativePresentationEncoding(
            PresentationEncoding.UnmanagedEmergencySrgb8));
        Assert.False(WindowsPreviewHostBackend.IsNativePresentationEncoding(
            (PresentationEncoding)int.MaxValue));
    }

    private sealed class FakeSubclassNative : IWindowsPreviewWindowSubclassNative
    {
        internal nint PreviousProcedure { get; } = (nint)0xBEEF;
        internal nint PreviousResult { get; } = (nint)0xCAFE;
        internal nint DefWindowResult { get; } = (nint)0xD00D;
        internal bool ThrowOnPrevious { get; set; }
        internal int PreviousCalls { get; private set; }
        internal int DefWindowCalls { get; private set; }
        internal int RestoreCalls { get; private set; }
        internal nint RestoredHwnd { get; private set; }
        internal nint RestoredPreviousProcedure { get; private set; }
        internal WindowsPreviewWindowProcedure? InstalledProcedure { get; private set; }
        internal WindowsPreviewWindowProcedure? RestoredInstalledProcedure { get; private set; }

        public nint InstallWindowProcedure(nint hwnd, WindowsPreviewWindowProcedure procedure)
        {
            InstalledProcedure = procedure;
            return PreviousProcedure;
        }

        public void RestoreWindowProcedure(
            nint hwnd,
            WindowsPreviewWindowProcedure installedProcedure,
            nint previousProcedure)
        {
            RestoreCalls++;
            RestoredHwnd = hwnd;
            RestoredInstalledProcedure = installedProcedure;
            RestoredPreviousProcedure = previousProcedure;
        }

        public nint CallPreviousWindowProcedure(
            nint previousProcedure,
            nint hwnd,
            uint message,
            nuint wParam,
            nint lParam)
        {
            PreviousCalls++;
            if (ThrowOnPrevious) throw new InvalidOperationException("Injected previous failure.");
            return PreviousResult;
        }

        public nint DefWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            DefWindowCalls++;
            return DefWindowResult;
        }

        internal nint Invoke(uint message)
        {
            WindowsPreviewWindowProcedure procedure = InstalledProcedure ??
                throw new InvalidOperationException("No test WndProc is installed.");
            return procedure((nint)0x101, message, 0, nint.Zero);
        }
    }
}
