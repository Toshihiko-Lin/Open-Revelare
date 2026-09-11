using OpenRevelare.ColorManagement;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PresentationContractTests
{
    [Fact]
    public void System_managed_contract_requires_canonical_F16_and_zero_application_transforms()
    {
        DisplayContract contract = SystemContract();
        PresentationBuffer frame = CanonicalFrame("display-a", revision: 7);

        contract.Validate(frame);

        Assert.Equal(FinalTransformOwner.SystemCompositor, contract.TransformOwner);
        Assert.Equal(0, contract.RequiredApplicationMonitorTransformCount);
        Assert.Equal(0, frame.ApplicationMonitorTransformCount);
        Assert.True(contract.RequiresSystemColorManagedSurface);
        Assert.False(contract.RequiresPassthroughSurface);

        Assert.Throws<ArgumentException>(() => new DisplayContract(
            "display-a",
            7,
            PresentationEncoding.MonitorDeviceBgra8,
            FinalTransformOwner.SystemCompositor,
            null,
            80f,
            1f,
            1f,
            "invalid system mode"));
    }

    [Fact]
    public void Application_managed_contract_requires_one_transform_and_exact_monitor_profile()
    {
        ColorProfileRef profile = MonitorProfile("display-a", revision: 7, marker: 0x11);
        DisplayContract contract = ApplicationContract(profile);
        PresentationBuffer frame = DeviceFrame(profile.Identity);

        contract.Validate(frame);

        Assert.Equal(1, contract.RequiredApplicationMonitorTransformCount);
        Assert.Equal(1, frame.ApplicationMonitorTransformCount);
        Assert.True(contract.RequiresPassthroughSurface);
        Assert.False(contract.RequiresSystemColorManagedSurface);

        ColorProfileRef other = MonitorProfile("display-a", revision: 7, marker: 0x22);
        PresentationBuffer wrongProfile = DeviceFrame(other.Identity);
        PresentationContractException error = Assert.Throws<PresentationContractException>(
            () => contract.Validate(wrongProfile));
        Assert.Contains("device-profile identity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_constructor_rejects_owner_encoding_and_profile_role_mismatches()
    {
        ColorProfileRef outputProfile = ColorProfileRef.Create(
            new byte[] { 1, 2, 3, 4 },
            "not a monitor profile",
            ProfileRole.Output,
            new ProfileSource.Generated("tests", "1"));

        Assert.Throws<ArgumentNullException>(() => new DisplayContract(
            "display-a",
            7,
            PresentationEncoding.MonitorDeviceBgra8,
            FinalTransformOwner.Application,
            null,
            80f,
            1f,
            1f,
            "missing profile"));
        Assert.Throws<ArgumentException>(() => new DisplayContract(
            "display-a",
            7,
            PresentationEncoding.MonitorDeviceBgra8,
            FinalTransformOwner.Application,
            outputProfile,
            80f,
            1f,
            1f,
            "wrong profile role"));
        Assert.Throws<ArgumentException>(() => new DisplayContract(
            "display-a",
            7,
            PresentationEncoding.UnmanagedEmergencySrgb8,
            FinalTransformOwner.None,
            null,
            80f,
            1f,
            1f,
            "unmanaged",
            visibleWarning: " "));
    }

    [Fact]
    public void Contract_rejects_wrong_display_stale_revision_and_wrong_encoding()
    {
        DisplayContract contract = SystemContract();

        Assert.Contains(
            "display id",
            Assert.Throws<PresentationContractException>(
                () => contract.Validate(CanonicalFrame("other-display", revision: 7))).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "stale",
            Assert.Throws<PresentationContractException>(
                () => contract.Validate(CanonicalFrame("display-a", revision: 6))).Message,
            StringComparison.Ordinal);

        ColorProfileRef profile = MonitorProfile("display-a", revision: 7, marker: 0x44);
        PresentationBuffer wrongEncoding = DeviceFrame(profile.Identity);
        Assert.Contains(
            "encoding",
            Assert.Throws<PresentationContractException>(() => contract.Validate(wrongEncoding)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_rejects_reference_white_from_another_display_state()
    {
        DisplayContract contract = SystemContract();
        PresentationBuffer frame = new(
            new byte[8],
            new PixelSize(1, 1),
            PresentationEncoding.LinearExtendedSrgbRgba16F,
            "display-a",
            7,
            null,
            100f,
            1.25f,
            0);

        Assert.Contains(
            "reference-white",
            Assert.Throws<PresentationContractException>(() => contract.Validate(frame)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Presentation_buffer_copies_bytes_and_enforces_encoding_specific_metadata()
    {
        byte[] callerOwned = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();
        PresentationBuffer buffer = new(
            callerOwned,
            new PixelSize(1, 1),
            PresentationEncoding.LinearExtendedSrgbRgba16F,
            "display-a",
            7,
            null,
            80f,
            1f,
            0);

        callerOwned.AsSpan().Fill(0xFF);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (byte)i), buffer.Bytes);

        Assert.Throws<ArgumentException>(() => new PresentationBuffer(
            new byte[8],
            new PixelSize(1, 1),
            PresentationEncoding.LinearExtendedSrgbRgba16F,
            "display-a",
            7,
            null,
            80f,
            1f,
            1));
        Assert.Throws<ArgumentException>(() => new PresentationBuffer(
            new byte[4],
            new PixelSize(1, 1),
            PresentationEncoding.MonitorDeviceBgra8,
            "display-a",
            7,
            null,
            80f,
            1f,
            1));
    }

    [Fact]
    public void Emergency_contract_is_explicitly_unmanaged_and_requires_visible_warning()
    {
        DisplayContract contract = new(
            "display-a",
            7,
            PresentationEncoding.UnmanagedEmergencySrgb8,
            FinalTransformOwner.None,
            null,
            80f,
            1f,
            1f,
            "unmanaged emergency",
            "Wide-gamut preview unavailable");
        PresentationBuffer frame = new(
            new byte[4],
            new PixelSize(1, 1),
            PresentationEncoding.UnmanagedEmergencySrgb8,
            "display-a",
            7,
            null,
            80f,
            1f,
            0);

        contract.Validate(frame);

        Assert.False(contract.IsWysiwygGuaranteed);
        Assert.Equal(0, contract.RequiredApplicationMonitorTransformCount);
        Assert.NotNull(contract.VisibleWarning);
    }

    private static DisplayContract SystemContract() => new(
        "display-a",
        7,
        PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor,
        null,
        80f,
        1f,
        1f,
        "system managed");

    private static DisplayContract ApplicationContract(ColorProfileRef profile) => new(
        "display-a",
        7,
        PresentationEncoding.MonitorDeviceBgra8,
        FinalTransformOwner.Application,
        profile,
        80f,
        1f,
        1f,
        "application managed");

    private static PresentationBuffer CanonicalFrame(string displayId, long revision) => new(
        new byte[8],
        new PixelSize(1, 1),
        PresentationEncoding.LinearExtendedSrgbRgba16F,
        displayId,
        revision,
        null,
        80f,
        1f,
        0);

    private static PresentationBuffer DeviceFrame(ProfileIdentity identity) => new(
        new byte[4],
        new PixelSize(1, 1),
        PresentationEncoding.MonitorDeviceBgra8,
        "display-a",
        7,
        identity,
        80f,
        1f,
        1);

    private static ColorProfileRef MonitorProfile(string displayId, long revision, byte marker) =>
        ColorProfileRef.Create(
            Enumerable.Repeat(marker, 128).ToArray(),
            $"monitor {marker}",
            ProfileRole.Monitor,
            new ProfileSource.Monitor(displayId, revision));
}
