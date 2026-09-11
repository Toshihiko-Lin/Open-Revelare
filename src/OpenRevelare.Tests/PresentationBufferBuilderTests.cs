using System.Buffers.Binary;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PresentationBufferBuilderTests
{
    [Fact]
    public void System_managed_buffer_applies_scale_and_packs_unclipped_little_endian_RGBA_half()
    {
        var cmm = new RecordingColorManagement();
        PresentationScene scene = Scene(
            referenceWhiteScale: 2f,
            0.5f, -0.25f, 1.5f, 1f);
        DisplayContract contract = SystemContract(referenceWhiteScale: 2f);

        PresentationBuffer buffer = PresentationBufferBuilder.Build(scene, contract, cmm);

        Assert.Equal(0, cmm.LeaseRequests);
        Assert.Equal(PresentationEncoding.LinearExtendedSrgbRgba16F, buffer.Encoding);
        Assert.Equal("display-a", buffer.TargetDisplayId);
        Assert.Equal(17, buffer.ContractRevision);
        Assert.Equal(96f, buffer.SdrReferenceWhite);
        Assert.Equal(2f, buffer.ReferenceWhiteScale);
        Assert.Null(buffer.AppliedDeviceProfileIdentity);
        Assert.Equal(0, buffer.ApplicationMonitorTransformCount);
        Assert.Equal(8, buffer.Bytes.Length);

        // Negative and >1 canonical components survive system-managed FP16. Scale is applied in
        // this shared builder, while alpha remains exactly one.
        Assert.Equal(1f, ReadHalf(buffer, component: 0));
        Assert.Equal(-0.5f, ReadHalf(buffer, component: 1));
        Assert.Equal(3f, ReadHalf(buffer, component: 2));
        Assert.Equal(1f, ReadHalf(buffer, component: 3));
        contract.Validate(buffer);
    }

    [Fact]
    public void Application_managed_buffer_requests_exact_monitor_transform_then_clips_and_quantizes_BGRA_once()
    {
        ColorProfileRef monitor = MonitorProfile(marker: "managed");
        var cmm = new RecordingColorManagement
        {
            Output = new[]
            {
                1.2f, -0.1f, 0.5f,
                0.25f, 0.75f, 1f,
            },
        };
        PresentationScene scene = Scene(
            referenceWhiteScale: 2f,
            -0.25f, 0.5f, 1.25f, 1f,
            0.125f, 0.375f, 0.5f, 1f);
        DisplayContract contract = ApplicationContract(monitor, referenceWhiteScale: 2f);

        PresentationBuffer buffer = PresentationBufferBuilder.Build(scene, contract, cmm);

        Assert.Equal(1, cmm.LeaseRequests);
        Assert.Equal(1, cmm.ApplyCalls);
        Assert.Equal(1, cmm.LeaseDisposeCalls);
        Assert.Equal(new[] { -0.5f, 1f, 2.5f, 0.25f, 0.75f, 1f }, cmm.LastSource);

        ColorTransformRequest request = Assert.IsType<ColorTransformRequest>(cmm.LastRequest);
        Assert.Equal(
            BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.CanonicalPresentation).Identity,
            request.Source.Identity);
        Assert.Equal(
            BuiltInProfileId.LinearExtendedSrgb,
            Assert.IsType<ProfileSource.BuiltIn>(request.Source.Source).Id);
        Assert.Same(monitor, request.Destination);
        Assert.Equal(TransformPurpose.MonitorPresentation, request.Purpose);
        Assert.Equal(RenderingIntent.RelativeColorimetric, request.Intent);
        Assert.True(request.BlackPointCompensation);
        Assert.Equal(1.0, request.AdaptationState);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, request.SourceFormat);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, request.DestinationFormat);

        Assert.Equal(
            new byte[]
            {
                128, 0, 255, 255,
                255, 191, 64, 255,
            },
            buffer.Bytes);
        Assert.Equal(PresentationEncoding.MonitorDeviceBgra8, buffer.Encoding);
        Assert.Equal(monitor.Identity, buffer.AppliedDeviceProfileIdentity);
        Assert.Equal(1, buffer.ApplicationMonitorTransformCount);
        Assert.Equal(2f, buffer.ReferenceWhiteScale);
        contract.Validate(buffer);
    }

    [Fact]
    public void Application_managed_buffer_uses_the_real_linear_sRGB_profile_with_the_shared_CMM()
    {
        ColorProfileRef monitor = MonitorProfile(marker: "real-cmm");
        PresentationScene scene = Scene(
            referenceWhiteScale: 2f,
            0.25f, 0.25f, 0.25f, 1f);
        DisplayContract contract = ApplicationContract(monitor, referenceWhiteScale: 2f);
        using var cmm = new LittleCmsEngine();

        PresentationBuffer buffer = PresentationBufferBuilder.Build(scene, contract, cmm);

        // 0.25 * scale 2 = 0.5 linear; IEC sRGB encodes that to ~0.735, or code 188.
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, buffer.Bytes);
        Assert.Equal(monitor.Identity, buffer.AppliedDeviceProfileIdentity);
        Assert.Equal(1, buffer.ApplicationMonitorTransformCount);
    }

    [Fact]
    public void Emergency_buffer_applies_sRGB_TRC_without_claiming_a_monitor_profile_or_transform()
    {
        var cmm = new RecordingColorManagement();
        PresentationScene scene = Scene(
            referenceWhiteScale: 1f,
            0.0031308f, 0.5f, 2f, 1f);
        DisplayContract contract = EmergencyContract(referenceWhiteScale: 1f);

        PresentationBuffer buffer = PresentationBufferBuilder.Build(scene, contract, cmm);

        Assert.Equal(0, cmm.LeaseRequests);
        Assert.Equal(new byte[] { 255, 188, 10, 255 }, buffer.Bytes);
        Assert.Equal(PresentationEncoding.UnmanagedEmergencySrgb8, buffer.Encoding);
        Assert.Null(buffer.AppliedDeviceProfileIdentity);
        Assert.Equal(0, buffer.ApplicationMonitorTransformCount);
        Assert.Equal("Color-managed preview is unavailable", contract.VisibleWarning);
        Assert.False(contract.IsWysiwygGuaranteed);
        contract.Validate(buffer);
    }

    [Fact]
    public void Builder_rejects_reference_white_mismatch_before_any_CMM_operation()
    {
        var cmm = new RecordingColorManagement();
        PresentationScene scene = Scene(
            referenceWhiteScale: 1f,
            0.1f, 0.2f, 0.3f, 1f);
        DisplayContract contract = SystemContract(referenceWhiteScale: 1.25f);

        PresentationContractException error = Assert.Throws<PresentationContractException>(
            () => PresentationBufferBuilder.Build(scene, contract, cmm));

        Assert.Contains("reference-white scale", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cmm.LeaseRequests);
    }

    [Fact]
    public void Builder_rejects_nonopaque_final_scene_before_any_CMM_operation()
    {
        ColorProfileRef monitor = MonitorProfile(marker: "alpha");
        var cmm = new RecordingColorManagement();
        PresentationScene scene = Scene(
            referenceWhiteScale: 1f,
            0.1f, 0.2f, 0.3f, 0.5f);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            PresentationBufferBuilder.Build(
                scene,
                ApplicationContract(monitor, referenceWhiteScale: 1f),
                cmm));

        Assert.Equal("finalOpaqueScene", error.ParamName);
        Assert.Contains("exactly opaque", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, cmm.LeaseRequests);
    }

    [Fact]
    public void CMM_failure_is_atomic_and_disposes_the_lease_without_publishing_a_partial_buffer()
    {
        ColorProfileRef monitor = MonitorProfile(marker: "failure");
        var cmm = new RecordingColorManagement { ThrowAfterFirstWrite = true };
        PresentationScene scene = Scene(
            referenceWhiteScale: 1f,
            -0.25f, 0.5f, 1.25f, 1f);
        Half[] originalScene = scene.LinearExtendedSrgbRgba.ToArray();
        PresentationBuffer? published = null;

        ColorManagementException error = Assert.Throws<ColorManagementException>(() =>
            published = PresentationBufferBuilder.Build(
                scene,
                ApplicationContract(monitor, referenceWhiteScale: 1f),
                cmm));

        Assert.Contains("injected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(published);
        Assert.Equal(1, cmm.LeaseRequests);
        Assert.Equal(1, cmm.ApplyCalls);
        Assert.Equal(1, cmm.LeaseDisposeCalls);
        Assert.Equal(0, cmm.ActiveLeases);
        Assert.Equal(originalScene, scene.LinearExtendedSrgbRgba);
    }

    private static PresentationScene Scene(float referenceWhiteScale, params float[] rgba) => new(
        rgba.Select(value => (Half)value).ToArray(),
        new PixelSize(rgba.Length / 4, 1),
        referenceWhiteScale);

    private static DisplayContract SystemContract(float referenceWhiteScale) => new(
        "display-a",
        17,
        PresentationEncoding.LinearExtendedSrgbRgba16F,
        FinalTransformOwner.SystemCompositor,
        null,
        96f,
        referenceWhiteScale,
        extendedHeadroom: 4f,
        diagnosticName: "system managed");

    private static DisplayContract ApplicationContract(
        ColorProfileRef monitor,
        float referenceWhiteScale) => new(
        "display-a",
        17,
        PresentationEncoding.MonitorDeviceBgra8,
        FinalTransformOwner.Application,
        monitor,
        96f,
        referenceWhiteScale,
        extendedHeadroom: 4f,
        diagnosticName: "application managed");

    private static DisplayContract EmergencyContract(float referenceWhiteScale) => new(
        "display-a",
        17,
        PresentationEncoding.UnmanagedEmergencySrgb8,
        FinalTransformOwner.None,
        null,
        96f,
        referenceWhiteScale,
        extendedHeadroom: 1f,
        diagnosticName: "emergency",
        visibleWarning: "Color-managed preview is unavailable");

    private static ColorProfileRef MonitorProfile(string marker)
    {
        ColorProfileRef srgb = BuiltInColorProfiles.Srgb(ProfileRole.Output);
        return ColorProfileRef.Create(
            srgb.IccBytes.AsSpan(),
            $"test monitor {marker}",
            ProfileRole.Monitor,
            new ProfileSource.Monitor("display-a", revision: 17));
    }

    private static float ReadHalf(PresentationBuffer buffer, int component)
    {
        byte[] bytes = buffer.Bytes.ToArray();
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(component * 2, 2));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    private sealed class RecordingColorManagement : IColorManagementEngine
    {
        public CmmBuildIdentity Build { get; } = new(
            "recording CMM",
            "test",
            0,
            "test",
            "recording",
            CmmTransformFlags.None,
            "test",
            "test",
            "{}",
            "recording");

        public float[]? Output { get; init; }
        public bool ThrowAfterFirstWrite { get; init; }
        public ColorTransformRequest? LastRequest { get; private set; }
        public float[]? LastSource { get; private set; }
        public int LeaseRequests { get; private set; }
        public int ApplyCalls { get; private set; }
        public int LeaseDisposeCalls { get; private set; }
        public int ActiveLeases { get; private set; }

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new NotSupportedException("The recording CMM does not validate profiles.");

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            LastRequest = request;
            LeaseRequests++;
            ActiveLeases++;
            CmmTransformFlags flags = request.BlackPointCompensation
                ? CmmTransformFlags.BlackPointCompensation
                : CmmTransformFlags.None;
            return new RecordingLease(this, ColorTransformKey.From(request, Build, flags));
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() => new(
            Build,
            ProfileValidations: 0,
            ProfileValidationFailures: 0,
            LeaseRequests: LeaseRequests,
            CacheHits: 0,
            CacheMisses: LeaseRequests,
            TransformsCreated: LeaseRequests,
            TransformCalls: ApplyCalls,
            PixelsTransformed: LastSource?.Length / 3 ?? 0,
            NativeErrors: 0,
            ThreadCaches: LeaseRequests == 0 ? 0 : 1,
            CachedTransforms: LeaseRequests,
            ActiveOperations: ActiveLeases);

        public void Dispose()
        {
        }

        private sealed class RecordingLease : IColorTransformLease
        {
            private readonly RecordingColorManagement _owner;
            private bool _disposed;

            public ColorTransformKey Key { get; }

            public RecordingLease(RecordingColorManagement owner, ColorTransformKey key)
            {
                _owner = owner;
                Key = key;
            }

            public void Apply(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int required = checked(pixelCount * 3);
                _owner.ApplyCalls++;
                _owner.LastSource = source.Slice(0, required).ToArray();

                if (_owner.ThrowAfterFirstWrite)
                {
                    destination[0] = 0.75f;
                    throw new ColorManagementException("Injected CMM failure after a partial scratch write.");
                }

                ReadOnlySpan<float> output = _owner.Output is { } configured
                    ? configured
                    : _owner.LastSource!;
                if (output.Length != required)
                    throw new InvalidOperationException("Recording CMM output length does not match the request.");
                output.CopyTo(destination);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.LeaseDisposeCalls++;
                _owner.ActiveLeases--;
            }
        }
    }
}
