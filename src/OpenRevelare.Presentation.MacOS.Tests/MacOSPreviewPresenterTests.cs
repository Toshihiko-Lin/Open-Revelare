using OpenRevelare.Presentation;
using OpenRevelare.Presentation.MacOS.Native;
using Xunit;

namespace OpenRevelare.Presentation.MacOS.Tests;

/// <summary>
/// The presenter's contract guarantees, with the native session faked out. None of these need
/// AppKit; all of them need to hold before a Mac is worth plugging in.
/// </summary>
public sealed class MacOSPreviewPresenterTests
{
    [Fact]
    public void Valid_frame_reaches_the_native_session_with_a_tight_row_pitch()
    {
        DisplayContract contract = SdrContract("display-a", 4);
        using var environment = new FakeEnvironment(contract);
        using var native = new FakeNativeSession(extendedRange: false);
        using var presenter = new MacOSPreviewPresenter(environment, native);
        var frame = Frame(contract, new PixelSize(2, 1));

        presenter.Present(frame);

        Assert.Equal(1, native.PresentCalls);
        Assert.Equal(16u, native.LastRowPitch);
        Assert.Equal("display-a", native.LastFrameDisplayId);
        Assert.Equal(4, native.LastFrameRevision);
        Assert.Equal(4, native.LastCurrentRevision);
    }

    [Fact]
    public void Stale_revision_is_rejected_before_the_native_call()
    {
        DisplayContract contract = SdrContract("display-a", 5);
        using var environment = new FakeEnvironment(contract);
        using var native = new FakeNativeSession(extendedRange: false);
        using var presenter = new MacOSPreviewPresenter(environment, native);
        var stale = new PresentationBuffer(
            new byte[8], new PixelSize(1, 1), contract.Encoding, contract.DisplayId, 4,
            null, contract.SdrReferenceWhite, 1f, 0);

        Assert.Throws<PresentationContractException>(() => presenter.Present(stale));
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Production_refresh_lease_closes_the_last_read_race()
    {
        DisplayContract captured = SdrContract("display-a", 1);
        DisplayContract stable = SdrContract("display-a", 2);
        using var environment = new LeaseEnvironment(captured, stable);
        using var native = new FakeNativeSession(extendedRange: false);
        using var presenter = new MacOSPreviewPresenter(environment, native);

        Assert.Throws<PresentationContractException>(() => presenter.Present(Frame(captured, new PixelSize(1, 1))));
        Assert.True(environment.LeaseWasUsed);
        Assert.Equal(0, native.PresentCalls);
    }

    /// <summary>
    /// The Windows presenter recreates on display or mode change. Here the mode is the EDR request,
    /// derived from headroom (D-012's candidate rule), so a brightness change that takes the
    /// screen from "no headroom" to "some headroom" must recreate the layer rather than keep
    /// presenting into one that was never asked for extended range.
    /// </summary>
    [Fact]
    public void Headroom_appearing_flips_the_edr_request_and_requires_recreation()
    {
        using var environment = new FakeEnvironment(SdrContract("display-a", 1));
        using var native = new FakeNativeSession(extendedRange: false);
        using var presenter = new MacOSPreviewPresenter(environment, native);

        environment.Change(EdrContract("display-a", 2, headroom: 2.5f));

        Assert.Throws<MacOSPresenterRecreationRequiredException>(
            () => presenter.Present(Frame(environment.Current, new PixelSize(1, 1))));
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Headroom_changing_within_edr_does_not_require_recreation()
    {
        using var environment = new FakeEnvironment(EdrContract("display-a", 1, headroom: 2f));
        using var native = new FakeNativeSession(extendedRange: true);
        using var presenter = new MacOSPreviewPresenter(environment, native);

        environment.Change(EdrContract("display-a", 2, headroom: 4f));
        presenter.Present(Frame(environment.Current, new PixelSize(1, 1)));

        Assert.Equal(1, native.PresentCalls);
        Assert.Equal(2, native.LastCurrentRevision);
    }

    [Fact]
    public void Screen_change_requires_recreation()
    {
        using var environment = new FakeEnvironment(SdrContract("display-a", 1));
        using var native = new FakeNativeSession(extendedRange: false);
        using var presenter = new MacOSPreviewPresenter(environment, native);

        environment.Change(SdrContract("display-b", 2));

        Assert.Throws<MacOSPresenterRecreationRequiredException>(
            () => presenter.Resize(new PixelSize(4, 4), 2.0));
    }

    [Fact]
    public void Native_edr_mode_mismatch_disposes_the_rejected_session()
    {
        using var environment = new FakeEnvironment(EdrContract("display-a", 1, headroom: 3f));
        var native = new FakeNativeSession(extendedRange: false);

        Assert.Throws<ArgumentException>(() => new MacOSPreviewPresenter(environment, native));
        Assert.True(native.IsDisposed);
    }

    [Fact]
    public void Emergency_contract_cannot_create_a_presenter()
    {
        var emergency = new DisplayContract(
            "display-a", 1, PresentationEncoding.UnmanagedEmergencySrgb8, FinalTransformOwner.None,
            null, 80f, 1f, 1f, "emergency", "no screen");
        using var environment = new FakeEnvironment(emergency);
        var native = new FakeNativeSession(extendedRange: false);

        Assert.Throws<ArgumentException>(() => new MacOSPreviewPresenter(environment, native));
        Assert.True(native.IsDisposed);
    }

    [Fact]
    public void Screen_change_during_creation_disposes_the_session()
    {
        using var environment = new SwitchingEnvironment(SdrContract("display-a", 1), SdrContract("display-b", 2));
        var native = new FakeNativeSession(extendedRange: false);

        Assert.Throws<MacOSPresenterRecreationRequiredException>(
            () => new MacOSPreviewPresenter(environment, native));
        Assert.True(native.IsDisposed);
    }

    private static DisplayContract SdrContract(string displayId, long revision) => new(
        displayId, revision, PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor, null, 80f, 1f, 1f, "macOS · SDR");

    private static DisplayContract EdrContract(string displayId, long revision, float headroom) => new(
        displayId, revision, PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor, null, 80f, 1f, headroom, "macOS · EDR");

    private static PresentationBuffer Frame(DisplayContract contract, PixelSize size) => new(
        new byte[size.Width * size.Height * 8], size, contract.Encoding, contract.DisplayId,
        contract.Revision, null, contract.SdrReferenceWhite, contract.ReferenceWhiteScale, 0);

    private sealed class FakeEnvironment(DisplayContract current) : IDisplayEnvironment
    {
        public DisplayContract Current { get; private set; } = current;
        public event EventHandler<DisplayContract>? ContractChanged;
        internal void Change(DisplayContract contract)
        {
            Current = contract;
            ContractChanged?.Invoke(this, contract);
        }
        public void Dispose() { }
    }

    private sealed class SwitchingEnvironment(DisplayContract first, DisplayContract afterSubscription)
        : IDisplayEnvironment
    {
        private int _reads;
        public DisplayContract Current => Interlocked.Increment(ref _reads) == 1 ? first : afterSubscription;
        public event EventHandler<DisplayContract>? ContractChanged { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class LeaseEnvironment(DisplayContract captured, DisplayContract stable)
        : IDisplayEnvironment, IMacOSDisplayContractLeaseProvider
    {
        public DisplayContract Current => captured;
        internal bool LeaseWasUsed { get; private set; }
        public event EventHandler<DisplayContract>? ContractChanged { add { } remove { } }
        public void WithStableContract(Action<DisplayContract> action)
        {
            LeaseWasUsed = true;
            action(stable);
        }
        public void Dispose() { }
    }

    private sealed class FakeNativeSession(bool extendedRange) : IMacOSNativePresenterSession
    {
        public bool ExtendedRangeRequested { get; } = extendedRange;
        internal int PresentCalls { get; private set; }
        internal uint LastRowPitch { get; private set; }
        internal string? LastFrameDisplayId { get; private set; }
        internal long LastFrameRevision { get; private set; }
        internal long LastCurrentRevision { get; private set; }
        internal bool IsDisposed { get; private set; }

        public void Resize(PixelSize size, double scale) { }

        public void Present(
            ReadOnlySpan<byte> bytes, uint rowPitch, PixelSize size,
            string frameDisplayId, long frameRevision, string currentDisplayId, long currentRevision)
        {
            PresentCalls++;
            LastRowPitch = rowPitch;
            LastFrameDisplayId = frameDisplayId;
            LastFrameRevision = frameRevision;
            LastCurrentRevision = currentRevision;
        }

        public MacOSNativePresenterDiagnostics QueryDiagnostics() => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }
}
