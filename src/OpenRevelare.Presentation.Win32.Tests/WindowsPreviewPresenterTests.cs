using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using OpenRevelare.Presentation.Win32.Native;
using Xunit;

namespace OpenRevelare.Presentation.Win32.Tests;

public sealed class WindowsPreviewPresenterTests
{
    [Fact]
    public void Valid_advanced_frame_maps_to_tight_native_contract()
    {
        DisplayContract contract = AdvancedContract("display-a", 4);
        using var environment = new FakeEnvironment(contract);
        using var native = new FakeNativeSession(NativePresenterMode.AdvancedColor);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        var size = new PixelSize(2, 1);
        var frame = new PresentationBuffer(
            new byte[16], size, contract.Encoding, contract.DisplayId, contract.Revision,
            null, contract.SdrReferenceWhite, contract.ReferenceWhiteScale, 0);

        presenter.Present(frame);

        Assert.Equal(1, native.PresentCalls);
        Assert.Equal(16u, native.LastRowPitch);
        Assert.Equal("display-a", native.LastFrameDisplayId);
        Assert.Equal(4, native.LastFrameRevision);
        Assert.Equal(4, native.LastCurrentRevision);
    }

    [Fact]
    public void Stale_revision_is_rejected_before_native_call()
    {
        DisplayContract contract = AdvancedContract("display-a", 5);
        using var environment = new FakeEnvironment(contract);
        using var native = new FakeNativeSession(NativePresenterMode.AdvancedColor);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        var frame = new PresentationBuffer(
            new byte[8], new PixelSize(1, 1), contract.Encoding, contract.DisplayId, 4,
            null, 80, 1, 0);

        Assert.Throws<PresentationContractException>(() => presenter.Present(frame));
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Production_refresh_lease_closes_last_read_before_native_race()
    {
        DisplayContract captured = AdvancedContract("display-a", 1);
        DisplayContract stable = AdvancedContract("display-a", 2);
        using var environment = new LeaseEnvironment(captured, stable);
        using var native = new FakeNativeSession(NativePresenterMode.AdvancedColor);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        var staleFrame = new PresentationBuffer(
            new byte[8], new PixelSize(1, 1), captured.Encoding, captured.DisplayId,
            captured.Revision, null, 80, 1, 0);

        Assert.Throws<PresentationContractException>(() => presenter.Present(staleFrame));
        Assert.True(environment.LeaseWasUsed);
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Wrong_legacy_profile_identity_is_rejected_before_native_call()
    {
        ColorProfileRef expected = Profile([1, 2, 3], "display-a", 1);
        ColorProfileRef wrong = Profile([1, 2, 4], "display-a", 1);
        DisplayContract contract = LegacyContract("display-a", 1, expected);
        using var environment = new FakeEnvironment(contract);
        using var native = new FakeNativeSession(NativePresenterMode.Legacy);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        var frame = new PresentationBuffer(
            new byte[4], new PixelSize(1, 1), contract.Encoding, contract.DisplayId, 1,
            wrong.Identity, 80, 1, 1);

        Assert.Throws<PresentationContractException>(() => presenter.Present(frame));
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Higher_revision_for_same_display_and_mode_does_not_require_recreation()
    {
        ColorProfileRef firstProfile = Profile([1, 2, 3], "display-a", 1);
        using var environment = new FakeEnvironment(LegacyContract("display-a", 1, firstProfile));
        using var native = new FakeNativeSession(NativePresenterMode.Legacy);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        ColorProfileRef nextProfile = Profile([1, 2, 4], "display-a", 2);
        DisplayContract next = LegacyContract("display-a", 2, nextProfile);
        environment.Change(next);
        var frame = new PresentationBuffer(
            new byte[4], new PixelSize(1, 1), next.Encoding, next.DisplayId, next.Revision,
            nextProfile.Identity, 80, 1, 1);

        presenter.Present(frame);

        Assert.Equal(1, native.PresentCalls);
        Assert.Equal(2, native.LastCurrentRevision);
    }

    [Fact]
    public void Display_change_requires_native_presenter_recreation()
    {
        using var environment = new FakeEnvironment(AdvancedContract("display-a", 1));
        using var native = new FakeNativeSession(NativePresenterMode.AdvancedColor);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        DisplayContract next = AdvancedContract("display-b", 2);
        environment.Change(next);
        var frame = new PresentationBuffer(
            new byte[8], new PixelSize(1, 1), next.Encoding, next.DisplayId, next.Revision,
            null, 80, 1, 0);

        Assert.Throws<WindowsPresenterRecreationRequiredException>(() => presenter.Present(frame));
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Emergency_srgb8_uses_fixed_copy_bgra8_surface_without_app_transform()
    {
        var contract = new DisplayContract(
            "display-a", 1, PresentationEncoding.UnmanagedEmergencySrgb8,
            FinalTransformOwner.None, null, 80, 1, 1, "fallback", "unmanaged");
        using var environment = new FakeEnvironment(contract);
        using var native = new FakeNativeSession(NativePresenterMode.Legacy);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        var frame = new PresentationBuffer(
            new byte[4], new PixelSize(1, 1), contract.Encoding, contract.DisplayId,
            contract.Revision, null, 80, 1, 0);

        presenter.Present(frame);

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, presenter.AcceptedEncoding);
        Assert.Equal(1, native.PresentCalls);
        Assert.Equal(4u, native.LastRowPitch);
        Assert.False(native.IsDisposed);
    }

    [Fact]
    public void Legacy_monitor_pixels_and_emergency_srgb_require_distinct_presenter_generation()
    {
        ColorProfileRef profile = Profile([1, 2, 3], "display-a", 1);
        using var environment = new FakeEnvironment(LegacyContract("display-a", 1, profile));
        using var native = new FakeNativeSession(NativePresenterMode.Legacy);
        using var presenter = new WindowsPreviewPresenter(environment, native);
        var emergency = new DisplayContract(
            "display-a", 2, PresentationEncoding.UnmanagedEmergencySrgb8,
            FinalTransformOwner.None, null, 80, 1, 1, "fallback", "unmanaged");
        environment.Change(emergency);
        var frame = new PresentationBuffer(
            new byte[4], new PixelSize(1, 1), emergency.Encoding, emergency.DisplayId,
            emergency.Revision, null, 80, 1, 0);

        Assert.Throws<WindowsPresenterRecreationRequiredException>(() => presenter.Present(frame));
        Assert.Equal(0, native.PresentCalls);
    }

    [Fact]
    public void Native_mode_mismatch_disposes_rejected_session()
    {
        using var environment = new FakeEnvironment(AdvancedContract("display-a", 1));
        using var native = new FakeNativeSession(NativePresenterMode.Legacy);

        Assert.Throws<ArgumentException>(() => new WindowsPreviewPresenter(environment, native));
        Assert.True(native.IsDisposed);
    }

    [Fact]
    public void Display_change_during_creation_disposes_session_and_requires_recreation()
    {
        using var environment = new SwitchingEnvironment(
            AdvancedContract("display-a", 1),
            AdvancedContract("display-b", 2));
        using var native = new FakeNativeSession(NativePresenterMode.AdvancedColor);

        Assert.Throws<WindowsPresenterRecreationRequiredException>(
            () => new WindowsPreviewPresenter(environment, native));
        Assert.True(native.IsDisposed);
    }

    private static DisplayContract AdvancedContract(string displayId, long revision) => new(
        displayId, revision, PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor, null, 80, 1, 1, "Advanced Color");

    private static DisplayContract LegacyContract(
        string displayId,
        long revision,
        ColorProfileRef profile) => new(
        displayId, revision, PresentationEncoding.MonitorDeviceBgra8,
        FinalTransformOwner.Application, profile, 80, 1, 1, "Legacy ICC");

    private static ColorProfileRef Profile(byte[] bytes, string displayId, long revision) =>
        ColorProfileRef.Create(
            bytes, "monitor", ProfileRole.Monitor, new ProfileSource.Monitor(displayId, revision));

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

    private sealed class SwitchingEnvironment(
        DisplayContract first,
        DisplayContract afterSubscription) : IDisplayEnvironment
    {
        private int _reads;

        public DisplayContract Current =>
            Interlocked.Increment(ref _reads) == 1 ? first : afterSubscription;

        public event EventHandler<DisplayContract>? ContractChanged
        {
            add { }
            remove { }
        }

        public void Dispose() { }
    }

    private sealed class LeaseEnvironment(
        DisplayContract captured,
        DisplayContract stable) : IDisplayEnvironment, IWindowsDisplayContractLeaseProvider
    {
        public DisplayContract Current => captured;
        internal bool LeaseWasUsed { get; private set; }
        public event EventHandler<DisplayContract>? ContractChanged
        {
            add { }
            remove { }
        }

        public void WithStableContract(Action<DisplayContract> action)
        {
            LeaseWasUsed = true;
            action(stable);
        }

        public void Dispose() { }
    }

    private sealed class FakeNativeSession(NativePresenterMode mode) : IWin32NativePresenterSession
    {
        public NativePresenterMode Mode { get; } = mode;
        internal int PresentCalls { get; private set; }
        internal uint LastRowPitch { get; private set; }
        internal string? LastFrameDisplayId { get; private set; }
        internal long LastFrameRevision { get; private set; }
        internal long LastCurrentRevision { get; private set; }
        internal bool IsDisposed { get; private set; }

        public void Resize(PixelSize size) { }

        public void Present(
            ReadOnlySpan<byte> bytes,
            uint rowPitch,
            PixelSize size,
            string frameDisplayId,
            long frameRevision,
            string currentDisplayId,
            long currentRevision)
        {
            PresentCalls++;
            LastRowPitch = rowPitch;
            LastFrameDisplayId = frameDisplayId;
            LastFrameRevision = frameRevision;
            LastCurrentRevision = currentRevision;
        }

        public WindowsNativePresenterDiagnostics QueryDiagnostics() =>
            throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
    }
}
