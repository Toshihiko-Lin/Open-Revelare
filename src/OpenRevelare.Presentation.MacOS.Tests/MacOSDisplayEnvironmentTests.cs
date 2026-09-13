using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Presentation.MacOS.Tests;

/// <summary>
/// The macOS presentation policy (D-026), proven without a Mac: every case is a snapshot the
/// real probe could return, fed through the same provider production uses.
/// </summary>
public sealed class MacOSDisplayEnvironmentTests
{
    [Fact]
    public void Sdr_screen_maps_to_system_owned_fp16_with_no_scale_and_no_headroom()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 1.0, potentialEdr: 1.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);

        DisplayContract contract = environment.Current;

        Assert.Equal(PresentationEncoding.LinearExtendedSrgbRgba16F, contract.Encoding);
        Assert.Equal(FinalTransformOwner.SystemCompositor, contract.TransformOwner);
        Assert.Null(contract.DeviceProfile);
        Assert.Equal(1f, contract.ReferenceWhiteScale);
        Assert.Equal(1f, contract.ExtendedHeadroom);
        Assert.Equal(0, contract.RequiredApplicationMonitorTransformCount);
        Assert.Null(contract.VisibleWarning);
        Assert.False(environment.Diagnostics.EdrRequested);
        Assert.True(environment.Diagnostics.WysiwygGuaranteed);
        Assert.Contains("SDR", contract.DiagnosticName);
    }

    /// <summary>
    /// D-026's central claim, stated as a test so a Mac can refute it: on this platform canonical
    /// 1.0 is already SDR white, so an EDR screen changes the HEADROOM and never the scale. This
    /// is the mirror image of Windows HDR (D-020), where the scale moves and the headroom follows.
    /// </summary>
    [Fact]
    public void Edr_screen_reports_headroom_verbatim_and_leaves_reference_white_unscaled()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 2.75, potentialEdr: 16.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);

        DisplayContract contract = environment.Current;

        Assert.Equal(PresentationEncoding.LinearExtendedSrgbRgba16F, contract.Encoding);
        Assert.Equal(1f, contract.ReferenceWhiteScale);
        Assert.Equal(2.75f, contract.ExtendedHeadroom);
        Assert.True(environment.Diagnostics.EdrRequested);
        Assert.Contains("EDR", contract.DiagnosticName);
    }

    /// <summary>
    /// The potential value is what the panel could do at another brightness; the current value is
    /// what it will show. Using the potential would draw a "safe up to here" line that is not.
    /// </summary>
    [Fact]
    public void Headroom_is_the_current_edr_value_not_the_potential_one()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 1.0, potentialEdr: 16.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);

        Assert.Equal(1f, environment.Current.ExtendedHeadroom);
        Assert.False(environment.Diagnostics.EdrRequested);
        Assert.Equal(16.0, environment.Diagnostics.MaximumPotentialEdrValue);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.5)]
    [InlineData(-1.0)]
    public void Nonsense_edr_values_degrade_to_no_headroom(double maximumEdr)
    {
        Assert.Equal(1f, MacOSDisplayContractProvider.ExtendedHeadroom(maximumEdr));
    }

    [Fact]
    public void Unidentified_screen_is_an_explicit_emergency_with_a_warning()
    {
        var probe = new FakeProbe(MacOSDisplayProbeSnapshot.Failed(nint.Zero, "no screen"));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);

        DisplayContract contract = environment.Current;

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, contract.Encoding);
        Assert.Equal(FinalTransformOwner.None, contract.TransformOwner);
        Assert.Contains("no screen", contract.VisibleWarning!);
        Assert.False(environment.Diagnostics.WysiwygGuaranteed);
        Assert.False(environment.Diagnostics.EdrRequested);
    }

    [Fact]
    public void Probe_exception_becomes_an_emergency_rather_than_escaping()
    {
        var probe = new ThrowingProbe();
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);

        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, environment.Current.Encoding);
        Assert.Contains("InvalidOperationException", environment.Current.VisibleWarning!);
    }

    [Fact]
    public void Same_semantic_probe_does_not_increment_revision()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 1.0, potentialEdr: 1.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);
        int changes = 0;
        environment.ContractChanged += (_, _) => changes++;

        probe.Current = Snapshot(maximumEdr: 1.0, potentialEdr: 1.0) with { MaximumReferenceEdrValue = 3.0 };

        Assert.False(environment.Refresh());
        Assert.Equal(1, environment.Current.Revision);
        Assert.Equal(0, changes);
        // Non-semantic fields still refresh the diagnostics.
        Assert.Equal(3.0, environment.Diagnostics.MaximumReferenceEdrValue);
    }

    /// <summary>
    /// Headroom changes with the user's brightness slider on an EDR Mac — the same panel reports a
    /// different maximum at a different brightness — so it must be a new revision, or a frame
    /// built for yesterday's headroom would be validated against today's.
    /// </summary>
    [Fact]
    public void Headroom_change_publishes_a_new_revision()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 2.0, potentialEdr: 16.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);
        DisplayContract? published = null;
        environment.ContractChanged += (_, contract) => published = contract;

        probe.Current = Snapshot(maximumEdr: 4.0, potentialEdr: 16.0);

        Assert.True(environment.Refresh());
        Assert.Equal(2, environment.Current.Revision);
        Assert.Same(environment.Current, published);
        Assert.Equal(4f, environment.Current.ExtendedHeadroom);
    }

    [Fact]
    public void Moving_to_another_screen_publishes_a_contract_bound_to_that_screen()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 1.0, potentialEdr: 1.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);
        DisplayContract initial = environment.Current;

        probe.Current = Snapshot(maximumEdr: 1.0, potentialEdr: 1.0) with
        {
            DisplayId = "macos:display-2|External",
            DirectDisplayId = 2,
            LocalizedName = "External",
        };

        Assert.True(environment.Refresh());
        DisplayContract moved = environment.Current;
        Assert.NotEqual(initial.DisplayId, moved.DisplayId);

        var stale = new PresentationBuffer(
            new byte[8],
            new PixelSize(1, 1),
            initial.Encoding,
            initial.DisplayId,
            initial.Revision,
            null,
            initial.SdrReferenceWhite,
            initial.ReferenceWhiteScale,
            applicationMonitorTransformCount: 0);
        Assert.Throws<PresentationContractException>(() => moved.Validate(stale));
    }

    [Fact]
    public void Backing_scale_change_publishes_a_new_revision()
    {
        var probe = new FakeProbe(Snapshot(maximumEdr: 1.0, potentialEdr: 1.0));
        using var environment = new MacOSDisplayEnvironment(nint.Zero, probe);

        probe.Current = probe.Current with { BackingScaleFactor = 1.0 };

        Assert.True(environment.Refresh());
        Assert.Equal(2, environment.Current.Revision);
        Assert.Equal(1.0, environment.Diagnostics.BackingScaleFactor);
    }

    internal static MacOSDisplayProbeSnapshot Snapshot(double maximumEdr, double potentialEdr) => new(
        "macos:display-1|Built-in Liquid Retina XDR Display",
        1,
        "Built-in Liquid Retina XDR Display",
        "Display P3",
        2.0,
        maximumEdr,
        potentialEdr,
        1.0,
        IsReliable: true,
        null);

    internal sealed class FakeProbe(MacOSDisplayProbeSnapshot current) : IMacOSDisplayProbe
    {
        internal MacOSDisplayProbeSnapshot Current { get; set; } = current;
        public MacOSDisplayProbeSnapshot Probe(nint viewHandle) => Current;
    }

    private sealed class ThrowingProbe : IMacOSDisplayProbe
    {
        public MacOSDisplayProbeSnapshot Probe(nint viewHandle) =>
            throw new InvalidOperationException("AppKit is not here");
    }
}
