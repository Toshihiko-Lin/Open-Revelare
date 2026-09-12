using OpenRevelare.Gui.Controls;
using OpenRevelare.Presentation;
using OpenRevelare.Presentation.MacOS;
using OpenRevelare.Presentation.MacOS.Native;
using Xunit;
using PresentationPixelSize = OpenRevelare.Presentation.PixelSize;

namespace OpenRevelare.Tests;

/// <summary>
/// The macOS host backend's reconcile/recovery machine, with the environment and presenter faked.
/// Same guarantees as <see cref="WindowsPreviewHostTests"/>, plus the one that is new here: the
/// EDR request is part of the presenter's identity, so a headroom appearing recreates it.
/// </summary>
public sealed class MacOSPreviewHostTests
{
    [Fact]
    public void Present_failure_disposes_presenter_and_same_contract_refresh_recovers()
    {
        var environment = new FakeEnvironment(Contract(revision: 4));
        var failed = new FakePresenter { PresentError = new InvalidOperationException("MTLCommandBuffer error") };
        var recovered = new FakePresenter();
        var factory = new FakePresenterFactory(failed, recovered);
        using var backend = new MacOSPreviewHostBackend((nint)0x222, new PresentationPixelSize(1, 1), 2d, environment, factory);
        int recoveries = 0;
        backend.PresenterRecoveryRequested += (_, _) => recoveries++;
        Assert.True(PresentationGuarantee.IsEffective(backend.Current, backend.IsPresenterAvailable));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            backend.Present(Frame(revision: 4)));

        Assert.Contains("MTLCommandBuffer", error.Message);
        Assert.True(failed.Disposed);
        Assert.False(backend.IsPresenterAvailable);
        Assert.Contains("present failed", backend.PresenterFailure!);

        backend.Refresh();

        Assert.True(backend.IsPresenterAvailable);
        Assert.Null(backend.PresenterFailure);
        Assert.Equal(1, environment.RefreshCalls);
        Assert.Equal(1, recoveries);
        backend.Present(Frame(revision: 4));
        Assert.Equal(1, recovered.PresentCalls);
    }

    [Fact]
    public void Resize_failure_disposes_presenter_and_recovery_uses_latest_metrics()
    {
        var environment = new FakeEnvironment(Contract(revision: 1));
        var failed = new FakePresenter { FailResizeOnCall = 2 };
        var recovered = new FakePresenter();
        using var backend = new MacOSPreviewHostBackend(
            (nint)0x222, new PresentationPixelSize(1, 1), 2d, environment, new FakePresenterFactory(failed, recovered));

        Assert.Throws<InvalidOperationException>(() => backend.Resize(new PresentationPixelSize(8, 6), 1d));
        Assert.True(failed.Disposed);
        Assert.False(backend.IsPresenterAvailable);

        backend.Refresh();

        Assert.True(backend.IsPresenterAvailable);
        Assert.Equal(new PresentationPixelSize(8, 6), recovered.LastSize);
        Assert.Equal(1d, recovered.LastScale);
    }

    /// <summary>
    /// Brightness slider moves on an EDR Mac change the reported headroom. Crossing from none to
    /// some flips the EDR request (D-026), and that is a property set on the layer at creation,
    /// so the presenter is replaced. Moving within EDR is a revision-only change and is not.
    /// </summary>
    [Fact]
    public void Headroom_appearing_recreates_the_presenter_but_growing_within_edr_does_not()
    {
        var environment = new FakeEnvironment(Contract(revision: 1, headroom: 1f));
        var sdr = new FakePresenter();
        var edr = new FakePresenter();
        var factory = new FakePresenterFactory(sdr, edr);
        using var backend = new MacOSPreviewHostBackend((nint)0x222, new PresentationPixelSize(1, 1), 2d, environment, factory);
        Assert.Equal(1, factory.Created);

        environment.Publish(Contract(revision: 2, headroom: 2.5f));
        Assert.True(sdr.Disposed);
        Assert.Equal(2, factory.Created);
        Assert.True(backend.IsPresenterAvailable);

        environment.Publish(Contract(revision: 3, headroom: 4f));
        Assert.False(edr.Disposed);
        Assert.Equal(2, factory.Created);
    }

    [Fact]
    public void Emergency_contract_has_no_presenter_and_a_later_managed_contract_creates_one()
    {
        var emergency = new DisplayContract(
            "display-a", 1, PresentationEncoding.UnmanagedEmergencySrgb8, FinalTransformOwner.None,
            null, 80f, 1f, 1f, "emergency", "no screen");
        var environment = new FakeEnvironment(emergency);
        var factory = new FakePresenterFactory(new FakePresenter());
        using var backend = new MacOSPreviewHostBackend((nint)0x222, new PresentationPixelSize(1, 1), 2d, environment, factory);

        Assert.False(backend.IsPresenterAvailable);
        Assert.Equal(0, factory.Created);
        Assert.Throws<InvalidOperationException>(() => backend.Present(new PresentationBuffer(
            new byte[4], new PresentationPixelSize(1, 1), emergency.Encoding, emergency.DisplayId, 1,
            null, 80f, 1f, 0)));

        environment.Publish(Contract(revision: 2));
        Assert.True(backend.IsPresenterAvailable);
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public void Presenter_factory_receives_the_container_view_and_the_environment()
    {
        var environment = new FakeEnvironment(Contract(revision: 1));
        var factory = new FakePresenterFactory(new FakePresenter());
        using var backend = new MacOSPreviewHostBackend((nint)0xABC, new PresentationPixelSize(3, 2), 2d, environment, factory);

        Assert.Equal((nint)0xABC, factory.ParentView);
        Assert.Same(environment, factory.Environment);
    }

    private static DisplayContract Contract(long revision, float headroom = 1f) => new(
        "display-a", revision, PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor, null, 80f, 1f, headroom, "macOS");

    private static PresentationBuffer Frame(long revision) => new(
        new byte[8], new PresentationPixelSize(1, 1), PresentationEncoding.LinearExtendedSrgbRgba16F,
        "display-a", revision, null, 80f, 1f, 0);

    private sealed class FakeEnvironment(DisplayContract contract) : IMacOSDisplayEnvironment
    {
        internal int RefreshCalls { get; private set; }
        public DisplayContract Current { get; private set; } = contract;
        public MacOSDisplayDiagnostics Diagnostics => throw new NotSupportedException();
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

    private sealed class FakePresenterFactory(params FakePresenter[] presenters) : IMacOSPreviewPresenterFactory
    {
        private readonly Queue<FakePresenter> _presenters = new(presenters);
        internal nint ParentView { get; private set; }
        internal IDisplayEnvironment? Environment { get; private set; }
        internal int Created { get; private set; }

        public IMacOSPreviewPresenter Create(IDisplayEnvironment environment, nint parentView, PresentationPixelSize initialSize)
        {
            Environment = environment;
            ParentView = parentView;
            Created++;
            return _presenters.Dequeue();
        }
    }

    private sealed class FakePresenter : IMacOSPreviewPresenter
    {
        internal Exception? PresentError { get; init; }
        internal int FailResizeOnCall { get; init; }
        internal int ResizeCalls { get; private set; }
        internal int PresentCalls { get; private set; }
        internal PresentationPixelSize LastSize { get; private set; }
        internal double LastScale { get; private set; }
        internal bool Disposed { get; private set; }

        public PresentationEncoding AcceptedEncoding => PresentationEncoding.LinearExtendedSrgbRgba16F;

        public void Resize(PresentationPixelSize pixels, double scale)
        {
            ResizeCalls++;
            LastSize = pixels;
            LastScale = scale;
            if (ResizeCalls == FailResizeOnCall) throw new InvalidOperationException("fixture resize failed");
        }

        public void Present(PresentationBuffer frame)
        {
            PresentCalls++;
            if (PresentError is not null) throw PresentError;
        }

        public MacOSNativePresenterDiagnostics QueryNativeDiagnostics() => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }
}
