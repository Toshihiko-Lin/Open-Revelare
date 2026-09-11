using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Presentation.Win32.Tests;

public sealed class WindowsDisplayEnvironmentTests
{
    [Fact]
    public void Legacy_contract_carries_exact_active_profile_bytes_and_hash()
    {
        byte[] profileBytes = [0x00, 0x00, 0x01, 0x80, 0x61, 0x63, 0x73, 0x70];
        var probe = new FakeProbe(LegacySnapshot(profileBytes));
        var colorManagement = new FakeColorManagement();
        using var environment = CreateEnvironment(probe, colorManagement);

        DisplayContract contract = environment.Current;

        Assert.Equal(1, contract.Revision);
        Assert.Equal(PresentationEncoding.MonitorDeviceBgra8, contract.Encoding);
        Assert.Equal(FinalTransformOwner.Application, contract.TransformOwner);
        Assert.NotNull(contract.DeviceProfile);
        Assert.Equal(ProfileRole.Monitor, contract.DeviceProfile.Role);
        Assert.Equal(profileBytes, contract.DeviceProfile.IccBytes.ToArray());
        Assert.Equal(ProfileIdentity.FromIcc(profileBytes), contract.DeviceProfile.Identity);
        Assert.Equal(contract.DeviceProfile.Identity.Sha256Hex, environment.Diagnostics.ProfileSha256);
        Assert.True(environment.Diagnostics.WysiwygGuaranteed);
        Assert.Equal(1, colorManagement.ValidateCalls);
        Assert.Equal(1, colorManagement.LeaseCalls);
        Assert.Equal(1, colorManagement.ApplyCalls);
        Assert.Equal(TransformPurpose.MonitorPresentation, colorManagement.LastRequest!.Purpose);
        Assert.Equal(PixelFormatDescriptor.XyzFloat32, colorManagement.LastRequest.SourceFormat);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, colorManagement.LastRequest.DestinationFormat);
        Assert.Equal(contract.DeviceProfile.Identity, colorManagement.LastRequest.Destination.Identity);
    }

    [Fact]
    public void Same_semantic_probe_does_not_increment_revision()
    {
        byte[] profileBytes = [1, 2, 3, 4, 5];
        var probe = new FakeProbe(LegacySnapshot(profileBytes));
        var colorManagement = new FakeColorManagement();
        using var environment = CreateEnvironment(probe, colorManagement);
        int changes = 0;
        environment.ContractChanged += (_, _) => changes++;

        probe.Current = LegacySnapshot(profileBytes.ToArray());

        Assert.False(environment.Refresh());
        Assert.Equal(1, environment.Current.Revision);
        Assert.Equal(0, changes);
        Assert.Equal(1, colorManagement.ValidateCalls);
        Assert.Equal(1, colorManagement.LeaseCalls);
    }

    [Fact]
    public void Profile_byte_change_increments_revision_and_publishes_new_identity()
    {
        var probe = new FakeProbe(LegacySnapshot([1, 2, 3, 4]));
        using var environment = CreateEnvironment(probe);
        DisplayContract? published = null;
        environment.ContractChanged += (_, contract) => published = contract;

        byte[] replacement = [1, 2, 3, 5];
        probe.Current = LegacySnapshot(replacement);

        Assert.True(environment.Refresh());
        Assert.Equal(2, environment.Current.Revision);
        Assert.Same(environment.Current, published);
        Assert.Equal(ProfileIdentity.FromIcc(replacement), environment.Current.DeviceProfile!.Identity);
        Assert.Equal(2, ((ProfileSource.Monitor)environment.Current.DeviceProfile.Source).Revision);
    }

    [Fact]
    public void Moving_to_a_second_display_publishes_a_contract_bound_to_that_display()
    {
        // The hardware half of this — dragging the window between two physically different
        // monitors — needs a second screen (PR4-HANDOFF §4.5 item 3). The state-machine half does
        // not, and used to rest on nothing but "DisplayId happens to be a member of
        // WindowsDisplaySemanticKey": no test changed it. This pins both halves of the contract
        // that a second monitor would otherwise be needed to observe — a new revision bound to the
        // new display, and the old display's buffers ceasing to validate.
        byte[] firstProfile = [1, 2, 3, 4];
        var probe = new FakeProbe(LegacySnapshot(firstProfile));
        using var environment = CreateEnvironment(probe);
        DisplayContract initial = environment.Current;
        DisplayContract? published = null;
        environment.ContractChanged += (_, contract) => published = contract;

        const string secondDisplayId = "win32:DISPLAY#OTHER|adapter:00000000:00010F21|target:9001";
        byte[] secondProfile = [9, 8, 7, 6, 5];
        probe.Current = LegacySnapshot(secondProfile) with
        {
            DisplayId = secondDisplayId,
            GdiDeviceName = @"\\.\DISPLAY3",
            MonitorFriendlyName = "Second Screen",
            MonitorProfile = new MonitorProfileData(
                secondProfile, "Second sRGB", "second.icc", "CurrentUser"),
        };

        Assert.True(environment.Refresh());

        DisplayContract moved = environment.Current;
        Assert.Same(moved, published);
        Assert.Equal(2, moved.Revision);
        Assert.Equal(secondDisplayId, moved.DisplayId);
        Assert.NotEqual(initial.DisplayId, moved.DisplayId);
        Assert.Equal(ProfileIdentity.FromIcc(secondProfile), moved.DeviceProfile!.Identity);
        Assert.Equal(secondDisplayId, environment.Diagnostics.DisplayId);
        Assert.Equal("Second Screen", environment.Diagnostics.MonitorFriendlyName);

        // A frame prepared for the display we just left must be refused, not presented on the new
        // one. Both the id and the revision have moved, so this is the invariant that keeps the
        // previous monitor's colours off the current monitor.
        var stale = new PresentationBuffer(
            new byte[4],
            new PixelSize(1, 1),
            initial.Encoding,
            initial.DisplayId,
            initial.Revision,
            initial.DeviceProfile!.Identity,
            initial.SdrReferenceWhite,
            initial.ReferenceWhiteScale,
            applicationMonitorTransformCount: 1);
        Assert.Throws<PresentationContractException>(() => moved.Validate(stale));
    }

    [Fact]
    public void Window_dpi_change_increments_contract_revision()
    {
        var probe = new FakeProbe(LegacySnapshot([1, 2, 3, 4]));
        using var environment = CreateEnvironment(probe);
        probe.Current = probe.Current with { WindowDpi = 192 };

        Assert.True(environment.Refresh());
        Assert.Equal(2, environment.Current.Revision);
        Assert.Equal(192u, environment.Diagnostics.WindowDpi);
    }

    [Fact]
    public void Eizo_wcg_state_maps_to_system_owned_fp16_without_app_profile()
    {
        var probe = new FakeProbe(AdvancedSnapshot(WindowsAdvancedColorMode.WideColorGamut, 80f, 1000));
        using var environment = CreateEnvironment(probe);

        DisplayContract contract = environment.Current;

        Assert.Equal(PresentationEncoding.LinearExtendedSrgbRgba16F, contract.Encoding);
        Assert.Equal(FinalTransformOwner.SystemCompositor, contract.TransformOwner);
        Assert.Null(contract.DeviceProfile);
        Assert.Equal(80f, contract.SdrReferenceWhite);
        Assert.Equal(1f, contract.ReferenceWhiteScale);
        Assert.Equal(0, contract.RequiredApplicationMonitorTransformCount);
        Assert.Equal(0xD7u, environment.Diagnostics.AdvancedColorRawFlags!.Value);
        Assert.Equal(WindowsMonitorProfileStatus.OwnedBySystemAdvancedColor,
            environment.Diagnostics.ProfileStatus);
    }

    [Fact]
    public void Hdr_state_is_explicit_emergency_until_reference_white_policy_is_verified()
    {
        var probe = new FakeProbe(AdvancedSnapshot(
            WindowsAdvancedColorMode.HighDynamicRange, 200f, 2500));
        using var environment = CreateEnvironment(probe);

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, environment.Current.Encoding);
        Assert.Equal(FinalTransformOwner.None, environment.Current.TransformOwner);
        Assert.Equal(200f, environment.Current.SdrReferenceWhite);
        Assert.Equal(1f, environment.Current.ReferenceWhiteScale);
        Assert.Contains("HDR/reference-white", environment.Current.VisibleWarning!);
        Assert.Equal(WindowsAdvancedColorMode.HighDynamicRange, environment.Diagnostics.ActiveColorMode);
        Assert.Equal(2500u, environment.Diagnostics.SdrWhiteRaw!.Value);
        Assert.Equal(200f, environment.Diagnostics.SdrWhiteNits);
        Assert.False(environment.Diagnostics.WysiwygGuaranteed);
    }

    [Fact]
    public void Sdr_white_fallback_reason_remains_explicit_in_diagnostics()
    {
        WindowsDisplayProbeSnapshot snapshot =
            AdvancedSnapshot(WindowsAdvancedColorMode.WideColorGamut, 80f, 1000) with
            {
                SdrWhiteRaw = null,
                SdrWhiteFailureReason = "GET_SDR_WHITE_LEVEL failed; using 80 nits",
            };
        using var environment = CreateEnvironment(new FakeProbe(snapshot));

        Assert.Null(environment.Diagnostics.SdrWhiteRaw);
        Assert.Contains("failed", environment.Diagnostics.SdrWhiteFailureReason!);
        Assert.Equal(80f, environment.Diagnostics.SdrWhiteNits);
    }

    [Fact]
    public void Missing_legacy_profile_is_an_explicit_emergency_contract()
    {
        WindowsDisplayProbeSnapshot snapshot = LegacySnapshot([1, 2, 3]) with
        {
            MonitorProfile = null,
            MonitorProfileFailureReason = "default profile unreadable",
        };
        using var environment = CreateEnvironment(new FakeProbe(snapshot));

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, environment.Current.Encoding);
        Assert.Equal(FinalTransformOwner.None, environment.Current.TransformOwner);
        Assert.Contains("unreadable", environment.Current.VisibleWarning!);
        Assert.False(environment.Diagnostics.WysiwygGuaranteed);
    }

    [Fact]
    public void Unknown_advanced_color_state_never_silently_selects_legacy()
    {
        WindowsDisplayProbeSnapshot snapshot = LegacySnapshot([1, 2, 3]) with
        {
            AdvancedColor = new AdvancedColorState(
                false, "unavailable", null, false, false, false, false, false, false,
                WindowsAdvancedColorMode.Unknown, null, null, "Info2 and Info unavailable"),
        };
        using var environment = CreateEnvironment(new FakeProbe(snapshot));

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, environment.Current.Encoding);
        Assert.Contains("Info2", environment.Current.VisibleWarning!);
    }

    [Fact]
    public void Invalid_legacy_profile_is_warning_emergency_and_never_gets_a_transform()
    {
        var colorManagement = new FakeColorManagement { ValidationSucceeds = false };
        using var environment = CreateEnvironment(
            new FakeProbe(LegacySnapshot([1, 2, 3, 4])),
            colorManagement);

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, environment.Current.Encoding);
        Assert.Equal(FinalTransformOwner.None, environment.Current.TransformOwner);
        Assert.Null(environment.Current.DeviceProfile);
        Assert.Contains("LittleCMS validation", environment.Current.VisibleWarning!);
        Assert.False(environment.Diagnostics.WysiwygGuaranteed);
        Assert.Equal(1, colorManagement.ValidateCalls);
        Assert.Equal(0, colorManagement.LeaseCalls);
    }

    [Fact]
    public void Unusable_legacy_profile_transform_is_warning_emergency()
    {
        var colorManagement = new FakeColorManagement { ThrowOnApply = true };
        using var environment = CreateEnvironment(
            new FakeProbe(LegacySnapshot([1, 2, 3, 4])),
            colorManagement);

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, environment.Current.Encoding);
        Assert.Equal(FinalTransformOwner.None, environment.Current.TransformOwner);
        Assert.Contains("not transform-viable", environment.Current.VisibleWarning!);
        Assert.Equal(1, colorManagement.LeaseCalls);
        Assert.Equal(1, colorManagement.ApplyCalls);
    }

    [Fact]
    public void Environment_borrows_and_does_not_dispose_shared_color_management_engine()
    {
        var colorManagement = new FakeColorManagement();
        var environment = CreateEnvironment(
            new FakeProbe(LegacySnapshot([1, 2, 3, 4])),
            colorManagement);

        environment.Dispose();

        Assert.False(colorManagement.Disposed);
    }

    private static WindowsDisplayEnvironment CreateEnvironment(
        FakeProbe probe,
        FakeColorManagement? colorManagement = null) =>
        new((nint)1, probe, colorManagement ?? new FakeColorManagement());

    private static WindowsDisplayProbeSnapshot LegacySnapshot(byte[] profileBytes) => new(
        "win32:DISPLAY#ENC3292|adapter:00000000:00010F21|target:4357",
        @"\\.\DISPLAY2",
        @"\\?\DISPLAY#ENC3292#fixture",
        "EIZO CG2700X",
        "0x00000000:0x00010F21",
        1,
        4357,
        144,
        new AdvancedColorState(
            true,
            "fake Info2",
            0,
            true,
            false,
            true,
            false,
            true,
            false,
            WindowsAdvancedColorMode.Sdr,
            "RGB",
            8,
            null),
        1000,
        80f,
        null,
        new MonitorProfileData(profileBytes, "CG2700X Adobe RGB", "CG2700X.icc", "CurrentUser"),
        null,
        null);

    private static WindowsDisplayProbeSnapshot AdvancedSnapshot(
        WindowsAdvancedColorMode mode,
        float whiteNits,
        uint whiteRaw) => new(
        "win32:DISPLAY#ENC3292|adapter:00000000:00010F21|target:4357",
        @"\\.\DISPLAY2",
        @"\\?\DISPLAY#ENC3292#fixture",
        "EIZO CG2700X",
        "0x00000000:0x00010F21",
        1,
        4357,
        144,
        new AdvancedColorState(
            true,
            "fake Info2",
            0xD7,
            true,
            true,
            true,
            false,
            true,
            true,
            mode,
            "RGB",
            10,
            null),
        whiteRaw,
        whiteNits,
        null,
        null,
        null,
        null);

    private sealed class FakeProbe(WindowsDisplayProbeSnapshot current) : IWindowsDisplayProbe
    {
        internal WindowsDisplayProbeSnapshot Current { get; set; } = current;
        public WindowsDisplayProbeSnapshot Probe(nint windowHwnd) => Current;
    }

    private sealed class FakeColorManagement : IColorManagementEngine
    {
        internal bool ValidationSucceeds { get; init; } = true;
        internal bool ThrowOnApply { get; init; }
        internal int ValidateCalls { get; private set; }
        internal int LeaseCalls { get; private set; }
        internal int ApplyCalls { get; private set; }
        internal ColorTransformRequest? LastRequest { get; private set; }
        internal bool Disposed { get; private set; }

        public CmmBuildIdentity Build => throw new NotSupportedException();

        public ProfileValidationResult Validate(ColorProfileRef profile)
        {
            ValidateCalls++;
            return new ProfileValidationResult(
                ValidationSucceeds,
                profile.Identity,
                profile.Description,
                null,
                null,
                null,
                null,
                ValidationSucceeds ? "valid" : "fixture profile rejected");
        }

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            LeaseCalls++;
            LastRequest = request;
            return new FakeLease(this);
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() => throw new NotSupportedException();

        public void Dispose() => Disposed = true;

        private sealed class FakeLease(FakeColorManagement owner) : IColorTransformLease
        {
            public ColorTransformKey Key => default;

            public void Apply(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
            {
                owner.ApplyCalls++;
                if (owner.ThrowOnApply)
                    throw new ColorManagementException("fixture transform creation succeeded but apply failed");
                source.CopyTo(destination);
            }

            public void Dispose() { }
        }
    }
}
