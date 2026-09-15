using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The cache may reuse the fitted picture only when nothing under the primitives changed. Each
/// case here is one thing that must MISS (a new render, a pan, a resize) or one that must HIT (a
/// primitive-only change, a re-tagged scene sharing storage), and the composed pixels must be
/// identical to a fresh composition either way.
/// </summary>
public class ComposedBaseCacheTests
{
    private static PresentationScene Scene(int w, int h, float value, float scale = 1f)
    {
        var px = new Half[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = (Half)value; px[i + 1] = (Half)(value / 2); px[i + 2] = (Half)(value / 4); px[i + 3] = (Half)1f;
        }
        return new PresentationScene(px, new PixelSize(w, h), scale);
    }

    private static readonly PixelSize Viewport = new(40, 30);
    private static PresentationScene Background() => PresentationSceneFactory.SolidSrgb(20, 20, 20, 255, 1f);

    private static PresentationRasterPrimitive[] Frame(double x) => new PresentationRasterPrimitive[]
    {
        new PresentationSolidRect(new PreviewRect(x, 5, 10, 10), new PremultipliedLinearRgba(0, 0, 0, 0.5f)),
        new PresentationRectOutline(new PreviewRect(x, 5, 10, 10), 1.0, new PremultipliedLinearRgba(1, 1, 1, 1)),
    };

    private static void AssertSamePixels(PresentationScene expected, PresentationScene actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        Assert.True(expected.LinearExtendedSrgbRgba.AsSpan().SequenceEqual(actual.LinearExtendedSrgbRgba.AsSpan()),
            "cached composition differs from a fresh one");
    }

    [Fact]
    public void Primitive_only_change_reuses_the_base_and_matches_a_fresh_compose()
    {
        var cache = new ComposedBaseCache();
        PresentationScene image = Scene(20, 15, 0.8f);
        var overlays = new[] { new PresentationOverlay(image, new PixelRect(5, 0, 30, 30)) };

        PresentationScene first = cache.Compose(Background(), Viewport, overlays, Frame(8));
        Assert.False(cache.LastComposeReusedBase);
        AssertSamePixels(CpuPresentationCompositor.Compose(Background(), Viewport, overlays, Frame(8)), first);
        // The returned scene is valid only until the next Compose (it is rasterised into storage
        // the cache reuses), so anything to be compared later is copied out now.
        Half[] firstPixels = first.LinearExtendedSrgbRgba.AsSpan().ToArray();

        // Same picture, the crop frame moved: the background is a NEW 1×1 scene (as every snapshot
        // builds one) and the overlay list is a new array with the same scene and destination.
        var sameOverlays = new[] { new PresentationOverlay(image, new PixelRect(5, 0, 30, 30)) };
        PresentationScene second = cache.Compose(Background(), Viewport, sameOverlays, Frame(12));
        Assert.True(cache.LastComposeReusedBase);

        AssertSamePixels(CpuPresentationCompositor.Compose(Background(), Viewport, sameOverlays, Frame(12)), second);
        Assert.NotEqual(firstPixels, second.LinearExtendedSrgbRgba.AsSpan().ToArray());
        // And the reuse is real: the second frame landed in the first frame's storage.
        Assert.True(first.LinearExtendedSrgbRgba == second.LinearExtendedSrgbRgba);
    }

    [Fact]
    public void Retagged_scene_sharing_storage_still_hits()
    {
        var cache = new ComposedBaseCache();
        PresentationScene image = Scene(20, 15, 0.8f, scale: 1f);
        cache.Compose(Background(), Viewport, new[] { new PresentationOverlay(image, new PixelRect(5, 0, 30, 30)) }, Frame(8));

        // WithReferenceWhiteScale to the SAME scale returns the same object; to a different one it
        // shares storage but the scale differs, and the background's scale must differ with it —
        // that is a contract change and must miss.
        PresentationScene sameTag = image.WithReferenceWhiteScale(1f);
        cache.Compose(Background(), Viewport, new[] { new PresentationOverlay(sameTag, new PixelRect(5, 0, 30, 30)) }, Frame(9));
        Assert.True(cache.LastComposeReusedBase);

        PresentationScene hdrTag = image.WithReferenceWhiteScale(1.5f);
        PresentationScene hdrBackground = PresentationSceneFactory.SolidSrgb(20, 20, 20, 255, 1.5f);
        cache.Compose(hdrBackground, Viewport, new[] { new PresentationOverlay(hdrTag, new PixelRect(5, 0, 30, 30)) }, Frame(9));
        Assert.False(cache.LastComposeReusedBase);
    }

    [Fact]
    public void New_render_pan_resize_and_background_change_all_miss()
    {
        var cache = new ComposedBaseCache();
        PresentationScene image = Scene(20, 15, 0.8f);
        var dest = new PixelRect(5, 0, 30, 30);
        cache.Compose(Background(), Viewport, new[] { new PresentationOverlay(image, dest) }, Frame(8));

        // A new render: same size and values, different storage.
        cache.Compose(Background(), Viewport, new[] { new PresentationOverlay(Scene(20, 15, 0.8f), dest) }, Frame(8));
        Assert.False(cache.LastComposeReusedBase);

        // A pan: same scene, moved destination.
        cache.Compose(Background(), Viewport, new[] { new PresentationOverlay(image, new PixelRect(6, 0, 30, 30)) }, Frame(8));
        Assert.False(cache.LastComposeReusedBase);

        // A viewport resize.
        cache.Compose(Background(), new PixelSize(41, 30), new[] { new PresentationOverlay(image, new PixelRect(6, 0, 30, 30)) }, Frame(8));
        Assert.False(cache.LastComposeReusedBase);

        // A different viewer background colour.
        cache.Compose(PresentationSceneFactory.SolidSrgb(90, 90, 90, 255, 1f), new PixelSize(41, 30),
            new[] { new PresentationOverlay(image, new PixelRect(6, 0, 30, 30)) }, Frame(8));
        Assert.False(cache.LastComposeReusedBase);

        // An extra overlay (clipping tint switched on).
        cache.Compose(PresentationSceneFactory.SolidSrgb(90, 90, 90, 255, 1f), new PixelSize(41, 30),
            new[] { new PresentationOverlay(image, new PixelRect(6, 0, 30, 30)), new PresentationOverlay(image, new PixelRect(6, 0, 30, 30)) }, Frame(8));
        Assert.False(cache.LastComposeReusedBase);
    }

    [Fact]
    public void No_primitives_returns_the_base_itself_without_copying()
    {
        var cache = new ComposedBaseCache();
        var overlays = new[] { new PresentationOverlay(Scene(20, 15, 0.8f), new PixelRect(5, 0, 30, 30)) };
        PresentationScene a = cache.Compose(Background(), Viewport, overlays, Array.Empty<PresentationRasterPrimitive>());
        PresentationScene b = cache.Compose(Background(), Viewport, overlays, Array.Empty<PresentationRasterPrimitive>());
        Assert.Same(a, b);
    }
}
