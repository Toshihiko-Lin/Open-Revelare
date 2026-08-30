using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Interop;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>
/// Typed handoff from the shared render boundary to the platform-neutral canonical preview.
/// The Avalonia bitmap remains a non-Windows/emergency shell fallback; Windows native
/// presentation consumes <see cref="PreviewScene"/> instead and therefore never starts from the
/// already-clipped BGRA8 bitmap.
/// </summary>
public partial class MainViewModel
{
    // M4's Windows SDR contracts use canonical 1.0 as SDR white. The contract owns the final
    // scale and PresentationBufferBuilder applies it exactly once.
    internal const float CanonicalPreviewReferenceWhiteScale = 1f;

    [ObservableProperty] private PresentationScene? _previewScene;
    [ObservableProperty] private PresentationScene? _sprocketMaskScene;
    [ObservableProperty] private PresentationScene? _clippingScene;
    [ObservableProperty] private long _presentationRevision;

    private RenderedFrame? _previewRenderedFrame;
    private RenderedFrame? _savedPositiveRenderedFrame;
    private readonly PresentationRevisionCoordinator _presentationRevisions = new();

    private sealed record PreparedPreview(
        RenderedFrame Rendered,
        PresentationScene Scene,
        Bitmap Fallback,
        HistogramData Histogram,
        Bitmap? ClippingOverlay,
        PresentationScene? ClippingScene);

    partial void OnPreviewSceneChanged(PresentationScene? value) => InvalidatePresentation();
    partial void OnSprocketMaskSceneChanged(PresentationScene? value) => InvalidatePresentation();
    partial void OnClippingSceneChanged(PresentationScene? value) => InvalidatePresentation();

    private void InvalidatePresentation() => _presentationRevisions.Invalidate();

    private void UpdatePresentation(Action mutation) => _presentationRevisions.Update(mutation);

    private PresentationScene ConvertPreviewScene(RenderedFrame rendered) =>
        CanonicalPreviewConverter.Convert(
            rendered,
            ColorManagement,
            CanonicalPreviewReferenceWhiteScale);

    /// <summary>
    /// Builds the shell fallback from the rendered frame's ACTUAL output profile. In particular,
    /// LegacyV1 compatibility pixels must not be relabelled as the user's requested output space.
    /// The CMM first admits them to canonical linear extended-sRGB; only then is the explicit sRGB
    /// fallback encoded and quantized to BGRA8.
    /// </summary>
    private Bitmap BuildFallbackBitmap(RenderedFrame rendered)
    {
        PresentationScene scene = ConvertPreviewScene(rendered);
        return BuildFallbackBitmap(rendered, scene);
    }

    private static Bitmap BuildFallbackBitmap(RenderedFrame rendered, PresentationScene scene)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Size.Width != rendered.Pixels.Width || scene.Size.Height != rendered.Pixels.Height)
            throw new ArgumentException("Canonical scene dimensions do not match the rendered frame.", nameof(scene));

        ImageBuffer srgb = EncodeCanonicalFallbackSrgb(scene);
        return (Bitmap)BitmapConvert.ToBitmap(srgb, ColorSpaces.Srgb);
    }

    internal static ImageBuffer BuildFallbackSrgbPixels(
        RenderedFrame rendered,
        IColorManagementEngine colorManagement)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(colorManagement);
        PresentationScene scene = CanonicalPreviewConverter.Convert(
            rendered,
            colorManagement,
            CanonicalPreviewReferenceWhiteScale);
        return EncodeCanonicalFallbackSrgb(scene);
    }

    /// <summary>
    /// Builds the un-inverted import/split preview through the same typed negative-view boundary
    /// as the editor. The caller supplies scene-linear ACEScg and receives an explicitly sRGB
    /// shell bitmap; the selected output profile is never inferred from untyped bytes.
    /// </summary>
    internal Bitmap BuildTransientNegativeFallback(ImageBuffer sceneLinearWorking)
    {
        ArgumentNullException.ThrowIfNull(sceneLinearWorking);
        var displayed = new ImageBuffer(
            sceneLinearWorking.Width,
            sceneLinearWorking.Height,
            (float[])sceneLinearWorking.Data.Clone());
        FrameParams parameters = BuildParams();
        NegativeView.ToDisplay(displayed.Data, parameters.ResolvedOutputSpace);
        RenderedFrame rendered = RegionRender.DescribeNegativeViewerPixels(
            displayed,
            parameters,
            _colorPipelineVersion);
        return BuildFallbackBitmap(rendered);
    }

    private static ImageBuffer EncodeCanonicalFallbackSrgb(PresentationScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var encoded = new ImageBuffer(scene.Size.Width, scene.Size.Height);
        for (int pixel = 0; pixel < encoded.PixelCount; pixel++)
        {
            int source = pixel * 4;
            int destination = pixel * 3;
            encoded.Data[destination] = (float)scene.LinearExtendedSrgbRgba[source];
            encoded.Data[destination + 1] = (float)scene.LinearExtendedSrgbRgba[source + 1];
            encoded.Data[destination + 2] = (float)scene.LinearExtendedSrgbRgba[source + 2];
        }
        OutputRender.Encode(encoded.Data, ColorSpaces.Srgb);
        return encoded;
    }

    private PreparedPreview PreparePreview(
        RenderedFrame rendered,
        bool clippingEnabled,
        PresentationScene? scene = null,
        Bitmap? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        scene ??= ConvertPreviewScene(rendered);
        fallback ??= BuildFallbackBitmap(rendered, scene);
        return new PreparedPreview(
            rendered,
            scene,
            fallback,
            HistogramData.FromBuffer(rendered.Pixels.Data),
            clippingEnabled ? BuildClippingOverlay(rendered.Pixels) : null,
            clippingEnabled ? BuildClippingPresentationScene(rendered.Pixels) : null);
    }

    private void PublishCompletePreview(
        PreparedPreview preview,
        bool refreshSprocketMask = false) =>
        PublishCompletePreview(
            preview.Rendered,
            preview.Scene,
            preview.Fallback,
            preview.Histogram,
            preview.ClippingOverlay,
            preview.ClippingScene,
            refreshSprocketMask);

    private void PublishCompletePreview(
        RenderedFrame rendered,
        PresentationScene scene,
        Bitmap fallback,
        HistogramData histogram,
        Bitmap? clippingOverlay,
        PresentationScene? clippingScene,
        bool refreshSprocketMask = false)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(histogram);

        UpdatePresentation(() =>
        {
            _previewRenderedFrame = rendered;
            PreviewImage = fallback;
            Histogram = histogram;
            ClippingOverlay = clippingOverlay;
            ClippingScene = clippingScene;
            if (refreshSprocketMask && ShowSprocketMask) UpdateSprocketOverlay();
            PreviewScene = scene;
            OnPropertyChanged(nameof(ColorPipelineDiagnostic));
        });
    }

    private void ClearPreviewPresentation()
    {
        UpdatePresentation(() =>
        {
            _previewRenderedFrame = null;
            PreviewScene = null;
            ClippingScene = null;
            SprocketMaskScene = null;
        });
    }

    private static PresentationScene BuildMaskPresentationScene(
        bool[] mask,
        int width,
        int height,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Length != checked(width * height))
            throw new ArgumentException("Mask dimensions do not match its pixel count.", nameof(mask));

        var rgba = new byte[checked(mask.Length * 4)];
        for (int pixel = 0; pixel < mask.Length; pixel++)
        {
            if (!mask[pixel]) continue;
            int offset = pixel * 4;
            rgba[offset] = red;
            rgba[offset + 1] = green;
            rgba[offset + 2] = blue;
            rgba[offset + 3] = alpha;
        }

        return PresentationSceneFactory.FromSrgbRgba8(
            rgba,
            new OpenRevelare.Presentation.PixelSize(width, height),
            CanonicalPreviewReferenceWhiteScale);
    }

    private static PresentationScene BuildClippingPresentationScene(ImageBuffer image)
    {
        ClippingDetect.Detect(
            image.Data,
            image.PixelCount,
            0.02f,
            0.98f,
            out bool[] shadows,
            out bool[] highlights);

        // Keep these constants identical to BitmapConvert.ToClippingOverlay. They are UI sRGB
        // colors and PresentationSceneFactory performs the required linearization/premultiply.
        var rgba = new byte[checked(image.PixelCount * 4)];
        for (int pixel = 0; pixel < image.PixelCount; pixel++)
        {
            int offset = pixel * 4;
            if (shadows[pixel])
            {
                rgba[offset] = 13;
                rgba[offset + 1] = 87;
                rgba[offset + 2] = 255;
                rgba[offset + 3] = 140;
            }
            else if (highlights[pixel])
            {
                rgba[offset] = 255;
                rgba[offset + 1] = 31;
                rgba[offset + 2] = 15;
                rgba[offset + 3] = 140;
            }
        }

        return PresentationSceneFactory.FromSrgbRgba8(
            rgba,
            new OpenRevelare.Presentation.PixelSize(image.Width, image.Height),
            CanonicalPreviewReferenceWhiteScale);
    }

    internal PresentationBuffer BuildPresentationBuffer(
        PresentationScene finalOpaqueScene,
        DisplayContract contract) =>
        PresentationBufferBuilder.Build(finalOpaqueScene, contract, ColorManagement);

    /// <summary>Render/input/CMM half of the user-copyable color diagnostics.</summary>
    internal string BuildRenderColorDiagnostics()
    {
        var text = new StringBuilder();
        WorkingFrame? working = _previewWorking;
        RenderedFrame? rendered = _previewRenderedFrame;

        if (working is null)
        {
            text.AppendLine("Source: none");
        }
        else
        {
            text.AppendLine($"Source: {working.Source.DisplayName}");
            text.AppendLine($"Source id: {working.Source.StableSourceId}");
            text.AppendLine($"Decode recipe: {working.Source.DecodeRecipe}");
            text.AppendLine($"Working: {working.Space.Name} v{working.Space.Version}; admission={working.Admission}");
            switch (working.Source.OriginalEncoding)
            {
                case CharacterizedPixelEncoding characterized:
                    text.AppendLine(
                        $"Input profile: {characterized.Profile.Description} " +
                        $"[{characterized.Profile.Identity.Sha256Hex}]");
                    text.AppendLine(
                        $"Input encoding: characterized; reference={characterized.Reference}; " +
                        $"transfer={characterized.Transfer}; range={characterized.Range}");
                    break;
                case UncharacterizedPixelEncoding uncharacterized:
                    text.AppendLine(
                        $"Input encoding: Uncharacterized; kind={uncharacterized.CaptureKind}; " +
                        $"compatibility={uncharacterized.Compatibility}; transfer={uncharacterized.Transfer}; " +
                        $"range={uncharacterized.Range}");
                    break;
            }
        }

        text.AppendLine($"Color pipeline version: {(int)_colorPipelineVersion} ({_colorPipelineVersion})");
        if (rendered is null)
        {
            text.AppendLine("Rendered frame: none");
        }
        else
        {
            text.AppendLine(
                $"Output profile: {rendered.OutputProfile.Description} " +
                $"[{rendered.OutputProfile.Identity.Sha256Hex}]");
            text.AppendLine(
                $"Output encoding: reference={rendered.Encoding.Reference}; " +
                $"transfer={rendered.Encoding.Transfer}; range={rendered.Encoding.Range}");
            text.AppendLine(
                $"Output recipe: intent={rendered.Recipe.Intent}; BPC={rendered.Recipe.BlackPointCompensation}; " +
                $"gamut={rendered.Recipe.GamutPolicy}; printLut={rendered.Recipe.PrintLutIdentity}; " +
                $"pixelProfileMismatch={rendered.Recipe.PixelProfileMismatch}");
            text.AppendLine($"Render fingerprint: {FormatFingerprint(rendered.Fingerprint)}");
        }

        if (_colorManagement.IsValueCreated)
        {
            CmmDiagnosticsSnapshot diagnostics = _colorManagement.Value.GetDiagnostics();
            text.AppendLine(
                $"CMM: {diagnostics.Build.Product} {diagnostics.Build.ReportedNativeVersion}; " +
                $"required={diagnostics.Build.RequiredRelease}; nativeSha256={diagnostics.Build.NativeSha256}");
            text.AppendLine(
                $"CMM transforms: calls={diagnostics.TransformCalls}; pixels={diagnostics.PixelsTransformed}; " +
                $"cacheHits={diagnostics.CacheHits}; cacheMisses={diagnostics.CacheMisses}; " +
                $"nativeErrors={diagnostics.NativeErrors}");
        }

        return text.ToString().TrimEnd();

        static string FormatFingerprint(RenderFingerprint fingerprint) => fingerprint switch
        {
            RenderFingerprint.Computed computed => computed.Sha256Hex,
            RenderFingerprint.Unavailable unavailable => $"unavailable ({unavailable.Reason})",
            _ => "unknown",
        };
    }
}
