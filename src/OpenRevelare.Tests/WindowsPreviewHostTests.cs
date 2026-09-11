using Avalonia;
using OpenRevelare.ColorManagement;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Presentation;
using OpenRevelare.Presentation.Win32;
using OpenRevelare.Presentation.Win32.Native;
using Xunit;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Tests;

public sealed class WindowsPreviewHostTests
{
    [Fact]
    public void Physical_size_rounds_midpoints_away_and_promotes_empty_bounds()
    {
        PresentationPixelSize size = WindowsPreviewPhysicalSize.FromBounds(new Size(1d, 0d), 2.5d);

        Assert.Equal(new PresentationPixelSize(3, 1), size);
    }

    [Fact]
    public void Physical_size_rejects_scaled_int32_overflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsPreviewPhysicalSize.FromBounds(new Size(int.MaxValue, 1d), 2d));
    }

    [Fact]
    public void Mailbox_presents_only_newest_frame_waiting_for_dispatch()
    {
        var sink = new FakeSink(Contract(revision: 4));
        var dispatcher = new ManualDispatcher();
        var rejected = new List<Exception>();
        using var mailbox = new WindowsPreviewFrameMailbox(sink, dispatcher, rejected.Add);
        PresentationBuffer first = Frame(fill: 0x11, revision: 4);
        PresentationBuffer newest = Frame(fill: 0x22, revision: 4);

        mailbox.Enqueue(first);
        mailbox.Enqueue(newest);

        Assert.Equal(1, dispatcher.Count);
        dispatcher.RunAll();
        Assert.Empty(rejected);
        Assert.Same(newest, Assert.Single(sink.Presented));
    }

    [Fact]
    public void Mailbox_rejects_stale_frame_before_dispatch()
    {
        var sink = new FakeSink(Contract(revision: 8));
        var dispatcher = new ManualDispatcher();
        using var mailbox = new WindowsPreviewFrameMailbox(sink, dispatcher, _ => { });

        Assert.Throws<PresentationContractException>(() =>
            mailbox.Enqueue(Frame(fill: 0x33, revision: 7)));
        Assert.Equal(0, dispatcher.Count);
        Assert.Empty(sink.Presented);
    }

    [Fact]
    public void Mailbox_rechecks_contract_on_ui_thread_and_drops_frame_that_became_stale()
    {
        var sink = new FakeSink(Contract(revision: 12));
        var dispatcher = new ManualDispatcher();
        var rejected = new List<Exception>();
        using var mailbox = new WindowsPreviewFrameMailbox(sink, dispatcher, rejected.Add);
        mailbox.Enqueue(Frame(fill: 0x44, revision: 12));

        sink.Contract = Contract(revision: 13);
        dispatcher.RunAll();

        Assert.Empty(sink.Presented);
        Assert.IsType<PresentationContractException>(Assert.Single(rejected));
    }

    [Fact]
    public void Mailbox_dispose_discards_queued_frame()
    {
        var sink = new FakeSink(Contract(revision: 20));
        var dispatcher = new ManualDispatcher();
        var mailbox = new WindowsPreviewFrameMailbox(sink, dispatcher, _ => { });
        mailbox.Enqueue(Frame(fill: 0x55, revision: 20));

        mailbox.Dispose();
        dispatcher.RunAll();

        Assert.Empty(sink.Presented);
        Assert.Throws<ObjectDisposedException>(() =>
            mailbox.Enqueue(Frame(fill: 0x66, revision: 20)));
    }

    [Fact]
    public void Present_failure_disposes_presenter_and_same_contract_refresh_recovers()
    {
        var environment = new FakeEnvironment(Contract(revision: 4));
        var failed = new FakePresenter(PresentationEncoding.LinearExtendedSrgbRgba16F)
        {
            PresentError = new InvalidOperationException("DXGI_ERROR_DEVICE_REMOVED"),
        };
        var recovered = new FakePresenter(PresentationEncoding.LinearExtendedSrgbRgba16F);
        var factory = new FakePresenterFactory(failed, recovered);
        using var backend = new WindowsPreviewHostBackend(
            (nint)0x111,
            new PresentationPixelSize(1, 1),
            1d,
            environment,
            factory);
        int recoveries = 0;
        backend.PresenterRecoveryRequested += (_, _) => recoveries++;
        Assert.True(WindowsPresentationGuarantee.IsEffective(
            backend.Current,
            backend.IsPresenterAvailable));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            backend.Present(Frame(fill: 0x77, revision: 4)));

        Assert.Contains("DEVICE_REMOVED", error.Message);
        Assert.True(failed.Disposed);
        Assert.False(backend.IsPresenterAvailable);
        Assert.Contains("present failed", backend.PresenterFailure!);
        Assert.False(WindowsPresentationGuarantee.IsEffective(
            backend.Current,
            backend.IsPresenterAvailable));

        backend.Refresh();

        Assert.True(backend.IsPresenterAvailable);
        Assert.Null(backend.PresenterFailure);
        Assert.Equal(1, environment.RefreshCalls);
        Assert.Equal(1, recoveries);
        Assert.True(WindowsPresentationGuarantee.IsEffective(
            backend.Current,
            backend.IsPresenterAvailable));
        backend.Present(Frame(fill: 0x78, revision: 4));
        Assert.Equal(1, recovered.PresentCalls);
    }

    [Fact]
    public void Resize_failure_disposes_presenter_and_recovery_uses_latest_metrics()
    {
        var environment = new FakeEnvironment(Contract(revision: 7));
        var failed = new FakePresenter(PresentationEncoding.LinearExtendedSrgbRgba16F)
        {
            FailResizeOnCall = 2,
        };
        var recovered = new FakePresenter(PresentationEncoding.LinearExtendedSrgbRgba16F);
        var factory = new FakePresenterFactory(failed, recovered);
        using var backend = new WindowsPreviewHostBackend(
            (nint)0x111,
            new PresentationPixelSize(1, 1),
            1d,
            environment,
            factory);

        Assert.Throws<InvalidOperationException>(() =>
            backend.Resize(new PresentationPixelSize(9, 5), 1.5d));

        Assert.True(failed.Disposed);
        Assert.False(backend.IsPresenterAvailable);

        backend.Refresh();

        Assert.True(backend.IsPresenterAvailable);
        Assert.Equal(new PresentationPixelSize(9, 5), recovered.LastSize);
        Assert.Equal(1.5d, recovered.LastScale);
    }

    [Fact]
    public void Environment_and_presenter_factories_receive_color_critical_container_handle()
    {
        nint containerHwnd = (nint)0x111;
        nint topLevelHwnd = (nint)0x222;
        var environment = new FakeEnvironment(Contract(revision: 1));
        var environmentFactory = new FakeEnvironmentFactory(environment);
        var presenterFactory = new FakePresenterFactory(
            new FakePresenter(PresentationEncoding.LinearExtendedSrgbRgba16F));
        var colorManagement = new NeverCalledColorManagement();

        using var backend = new WindowsPreviewHostBackend(
            containerHwnd,
            topLevelHwnd,
            new PresentationPixelSize(1, 1),
            1d,
            colorManagement,
            environmentFactory,
            presenterFactory);

        Assert.Equal(containerHwnd, environmentFactory.SurfaceHwnd);
        Assert.NotEqual(topLevelHwnd, environmentFactory.SurfaceHwnd);
        Assert.Same(colorManagement, environmentFactory.ColorManagement);
        Assert.Equal(containerHwnd, presenterFactory.ParentHwnd);
        Assert.Same(environment, presenterFactory.Environment);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Native_airspace_is_visible_only_with_an_available_presenter(
        bool presenterAvailable,
        bool expectedVisible)
    {
        Assert.Equal(
            expectedVisible,
            WindowsPreviewNativeAirspace.ShouldBeVisible(presenterAvailable));
    }

    [Fact]
    public void Airspace_state_rehides_framework_reshow_after_failure_and_shows_after_recovery()
    {
        var native = new RecordingNativeVisibility();
        var state = new WindowsPreviewAirspaceState(native);
        nint containerHwnd = (nint)0x111;

        state.SetPresenterAvailable(false);
        native.SimulateFrameworkShowInBounds();
        state.Reapply(containerHwnd, effectivelyVisible: true); // Bounds update after base handler.
        Assert.False(native.LastVisible);

        native.SimulateFrameworkShowInBounds();
        state.Reapply(containerHwnd, effectivelyVisible: false); // Ancestor visibility update.
        Assert.False(native.LastVisible);

        state.SetPresenterAvailable(true);
        state.Reapply(containerHwnd, effectivelyVisible: true);
        Assert.True(native.LastVisible);
        Assert.Equal([true, false, true, false, true], native.VisibilityCalls);
    }

    private static DisplayContract Contract(long revision) => new(
        "win32:test-display",
        revision,
        PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor,
        null,
        80f,
        1f,
        1f,
        "test Windows Advanced Color");

    private static PresentationBuffer Frame(byte fill, long revision)
    {
        byte[] bytes = new byte[8];
        Array.Fill(bytes, fill);
        return new PresentationBuffer(
            bytes,
            new PresentationPixelSize(1, 1),
            PresentationEncoding.LinearExtendedSrgbRgba16F,
            "win32:test-display",
            revision,
            null,
            80f,
            1f,
            0);
    }

    private sealed class FakeSink : IWindowsPreviewFrameSink
    {
        internal FakeSink(DisplayContract contract) => Contract = contract;

        internal DisplayContract Contract { get; set; }
        internal List<PresentationBuffer> Presented { get; } = [];

        DisplayContract IWindowsPreviewFrameSink.Current => Contract;

        public void Present(PresentationBuffer frame)
        {
            Contract.Validate(frame);
            Presented.Add(frame);
        }
    }

    private sealed class ManualDispatcher : IWindowsPreviewDispatcher
    {
        private readonly Queue<Action> _work = new();

        internal int Count => _work.Count;

        public void Post(Action action) => _work.Enqueue(action);

        internal void RunAll()
        {
            while (_work.Count > 0) _work.Dequeue()();
        }
    }

    private sealed class FakeEnvironment(DisplayContract contract) : IWindowsDisplayEnvironment
    {
        internal int RefreshCalls { get; private set; }
        public DisplayContract Current { get; private set; } = contract;
        public WindowsDisplayDiagnostics Diagnostics => throw new NotSupportedException();
        public event EventHandler<DisplayContract>? ContractChanged;

        public bool Refresh()
        {
            RefreshCalls++;
            return false;
        }

        public void Dispose() { }

        internal void Publish(DisplayContract replacement)
        {
            Current = replacement;
            ContractChanged?.Invoke(this, replacement);
        }
    }

    private sealed class FakePresenterFactory(params FakePresenter[] presenters)
        : IWindowsPreviewPresenterFactory
    {
        private readonly Queue<FakePresenter> _presenters = new(presenters);

        internal nint ParentHwnd { get; private set; }
        internal IDisplayEnvironment? Environment { get; private set; }

        public IWindowsPreviewPresenter Create(
            IDisplayEnvironment environment,
            nint parentHwnd,
            PresentationPixelSize initialSize)
        {
            Environment = environment;
            ParentHwnd = parentHwnd;
            return _presenters.Dequeue();
        }
    }

    private sealed class FakePresenter(PresentationEncoding acceptedEncoding)
        : IWindowsPreviewPresenter
    {
        internal Exception? PresentError { get; init; }
        internal int FailResizeOnCall { get; init; }
        internal int ResizeCalls { get; private set; }
        internal int PresentCalls { get; private set; }
        internal PresentationPixelSize LastSize { get; private set; }
        internal double LastScale { get; private set; }
        internal bool Disposed { get; private set; }

        public PresentationEncoding AcceptedEncoding { get; } = acceptedEncoding;

        public void Resize(PresentationPixelSize pixels, double scale)
        {
            ResizeCalls++;
            LastSize = pixels;
            LastScale = scale;
            if (ResizeCalls == FailResizeOnCall)
                throw new InvalidOperationException("fixture resize failed after device removal");
        }

        public void Present(PresentationBuffer frame)
        {
            PresentCalls++;
            if (PresentError is not null) throw PresentError;
        }

        public WindowsNativePresenterDiagnostics QueryNativeDiagnostics() =>
            throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEnvironmentFactory(FakeEnvironment environment)
        : IWindowsDisplayEnvironmentFactory
    {
        internal nint SurfaceHwnd { get; private set; }
        internal IColorManagementEngine? ColorManagement { get; private set; }

        public IWindowsDisplayEnvironment Create(
            nint surfaceHwnd,
            IColorManagementEngine colorManagement)
        {
            SurfaceHwnd = surfaceHwnd;
            ColorManagement = colorManagement;
            return environment;
        }
    }

    private sealed class NeverCalledColorManagement : IColorManagementEngine
    {
        public CmmBuildIdentity Build => throw new NotSupportedException();
        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new NotSupportedException();
        public IColorTransformLease Lease(ColorTransformRequest request) =>
            throw new NotSupportedException();
        public CmmDiagnosticsSnapshot GetDiagnostics() => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class RecordingNativeVisibility : IWindowsPreviewNativeVisibility
    {
        internal List<bool> VisibilityCalls { get; } = [];
        internal bool LastVisible => VisibilityCalls[^1];

        public void SetVisible(nint hwnd, bool visible) => VisibilityCalls.Add(visible);

        internal void SimulateFrameworkShowInBounds() => VisibilityCalls.Add(true);
    }
}
