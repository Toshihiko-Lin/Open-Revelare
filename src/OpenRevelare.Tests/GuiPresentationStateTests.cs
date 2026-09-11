using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class GuiPresentationStateTests
{
    [Fact]
    public void Presentation_revision_coalesces_nested_component_changes()
    {
        var coordinator = new PresentationRevisionCoordinator();
        var published = new List<long>();
        coordinator.Published += published.Add;

        coordinator.Update(() =>
        {
            coordinator.Invalidate();
            coordinator.Invalidate();
            coordinator.Update(coordinator.Invalidate);
        });

        Assert.Equal(new long[] { 1 }, published);
        Assert.Equal(1, coordinator.Revision);

        coordinator.Invalidate();
        Assert.Equal(new long[] { 1, 2 }, published);
        Assert.Equal(2, coordinator.Revision);
    }

    [Fact]
    public void Failed_presentation_update_never_publishes_a_partial_generation()
    {
        var coordinator = new PresentationRevisionCoordinator();
        var published = new List<long>();
        coordinator.Published += published.Add;

        Assert.Throws<InvalidOperationException>(() => coordinator.Update(() =>
        {
            coordinator.Invalidate();
            throw new InvalidOperationException("synthetic incomplete presentation");
        }));

        Assert.Empty(published);
        Assert.Equal(0, coordinator.Revision);

        coordinator.Invalidate();
        Assert.Equal(new long[] { 1 }, published);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Full_resolution_patch_is_hidden_while_preview_resolution_diagnostics_are_visible(
        bool showClipping,
        bool showSprocketMask,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainViewModel.ShouldPresentSharpPatch(showClipping, showSprocketMask));
    }

    [Fact]
    public void Fallback_srgb_conversion_uses_the_actual_rendered_profile_and_preserves_source()
    {
        ColorProfileRef requested = BuiltInColorProfiles.DisplayP3(ProfileRole.Output);
        ColorProfileRef actual = BuiltInColorProfiles.LegacyLinearStage2Output(
            ColorSpaces.DisplayP3);
        var pixels = new ImageBuffer(2, 1, new[]
        {
            0.15f, 0.35f, 0.55f,
            0.85f, 0.65f, 0.45f,
        });
        float[] original = (float[])pixels.Data.Clone();
        var encoding = new CharacterizedPixelEncoding(
            actual,
            ColorReference.DisplayReferred,
            TransferState.ProfileEncoded,
            NumericRange.Normalized);
        var recipe = new OutputRecipe(
            ColorPipelineVersion.LegacyV1,
            requested,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            gamutPolicy: "legacy compatibility test",
            printLutIdentity: string.Empty,
            pixelProfileMismatch: true);
        var rendered = new RenderedFrame(
            pixels,
            encoding,
            recipe,
            new RenderFingerprint.Unavailable(
                FingerprintUnavailableReason.LegacyPipelineHasNoVersionedRecipe));
        using var cmm = new RecordingD50Engine();

        ImageBuffer fallback = MainViewModel.BuildFallbackSrgbPixels(rendered, cmm);

        ColorTransformRequest request = Assert.Single(cmm.Requests);
        Assert.Equal(actual.Identity, request.Source.Identity);
        Assert.NotEqual(requested.Identity, request.Source.Identity);
        Assert.Equal(PcsColorProfiles.D50Xyz.Identity, request.Destination.Identity);
        Assert.Equal(TransformPurpose.CanonicalPreview, request.Purpose);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, request.SourceFormat);
        Assert.Equal(PixelFormatDescriptor.XyzFloat32, request.DestinationFormat);
        Assert.Equal(original, pixels.Data);
        Assert.Equal((2, 1), (fallback.Width, fallback.Height));
        Assert.All(fallback.Data, value => Assert.InRange(value, 0f, 1f));
    }

    private sealed class RecordingD50Engine : IColorManagementEngine
    {
        public List<ColorTransformRequest> Requests { get; } = new();

        public CmmBuildIdentity Build { get; } = new(
            "GUI presentation test CMM",
            "test",
            0,
            "test",
            "test",
            CmmTransformFlags.None,
            new string('0', 64),
            new string('0', 64),
            "{}",
            "test");

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new InvalidOperationException("Validation is not part of this orchestration test.");

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            Requests.Add(request);
            return new LeaseImpl(request, Build);
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() =>
            throw new InvalidOperationException("Diagnostics are not part of this orchestration test.");

        public void Dispose() { }

        private sealed class LeaseImpl : IColorTransformLease
        {
            public LeaseImpl(ColorTransformRequest request, CmmBuildIdentity build)
            {
                Key = ColorTransformKey.From(request, build, CmmTransformFlags.None);
            }

            public ColorTransformKey Key { get; }

            public void Apply(
                ReadOnlySpan<float> source,
                Span<float> destination,
                int pixelCount)
            {
                if (source.Length < checked(pixelCount * 3) ||
                    destination.Length < checked(pixelCount * 3))
                    throw new ArgumentException("Transform buffer is shorter than its pixel count.");

                // A stable, non-zero D50 PCS neutral. The test is about orchestration/profile
                // identity; canonical conversion and sRGB encoding still run for real.
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    int offset = pixel * 3;
                    destination[offset] = 0.241f;
                    destination[offset + 1] = 0.250f;
                    destination[offset + 2] = 0.206f;
                }
            }

            public void Dispose() { }
        }
    }
}
