using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PreviewViewportGeometryTests
{
    [Theory]
    [InlineData(1.00, 101, 51)]
    [InlineData(1.25, 126, 64)]
    [InlineData(1.50, 152, 77)]
    [InlineData(2.00, 202, 102)]
    public void Viewport_extent_uses_one_frozen_DPI_rounding_rule(
        double renderScaling,
        int expectedWidth,
        int expectedHeight)
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(400, 200),
            new PreviewSize(101, 51),
            renderScaling);

        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), geometry.ViewportPhysicalSize);
    }

    [Fact]
    public void Fit_and_letterbox_are_computed_in_the_rounded_physical_viewport()
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(400, 200),
            new PreviewSize(300, 300),
            renderScaling: 1.5);

        AssertClose(1.125, geometry.FitScale);
        AssertRect(new PreviewRect(0, 112.5, 450, 225), geometry.LetterboxPhysical);
        AssertRect(geometry.LetterboxPhysical, geometry.DisplayedImagePhysical);
    }

    [Fact]
    public void Zoom_and_pan_are_clamped_without_exposing_gutters()
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(400, 200),
            new PreviewSize(200, 200),
            renderScaling: 1,
            zoom: 2,
            requestedPanLogical: new PreviewPoint(-75, 500));

        AssertClose(-75, geometry.PanPhysical.X);
        AssertClose(-100, geometry.PanPhysical.Y);
        AssertRect(new PreviewRect(-75, 0, 400, 200), geometry.DisplayedImagePhysical);
    }

    [Fact]
    public void Small_image_can_reach_physical_one_to_one_but_remains_centered()
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(50, 50),
            new PreviewSize(100, 100),
            renderScaling: 2,
            zoom: 0.01,
            requestedPanLogical: new PreviewPoint(50, -50));

        AssertClose(4, geometry.FitScale);
        AssertClose(0.25, geometry.MinimumZoom);
        AssertClose(0.25, geometry.Zoom);
        AssertRect(new PreviewRect(75, 75, 50, 50), geometry.DisplayedImagePhysical);
    }

    [Fact]
    public void Normalized_and_physical_transforms_round_trip_under_zoom_pan_and_DPI()
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(640, 480),
            new PreviewSize(333, 211),
            renderScaling: 1.5,
            zoom: 3,
            requestedPanLogical: new PreviewPoint(-70, -30));
        PreviewPoint normalizedPoint = new(0.1234, 0.8765);
        PreviewRect normalizedRect = new(0.1, 0.2, 0.3, 0.4);

        PreviewPoint roundTrippedPoint = geometry.PhysicalToNormalized(
            geometry.NormalizedToPhysical(normalizedPoint));
        PreviewRect roundTrippedRect = geometry.PhysicalToNormalized(
            geometry.NormalizedToPhysical(normalizedRect));

        AssertClose(normalizedPoint.X, roundTrippedPoint.X);
        AssertClose(normalizedPoint.Y, roundTrippedPoint.Y);
        AssertRect(normalizedRect, roundTrippedRect);
    }

    [Fact]
    public void Normalized_rect_rounding_clips_to_viewport_and_returns_null_when_empty()
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(100, 100),
            new PreviewSize(100, 100),
            renderScaling: 1);

        PixelRect clipped = Assert.IsType<PixelRect>(
            geometry.NormalizedToClippedPixelRect(new PreviewRect(-0.1, 0.2, 0.4, 0.6)));

        Assert.Equal(new PixelRect(0, 20, 30, 60), clipped);
        Assert.Null(geometry.NormalizedToClippedPixelRect(new PreviewRect(1.1, 0.2, 0.1, 0.1)));
    }

    [Fact]
    public void Rectangle_edges_round_independently_away_from_midpoints_without_seams()
    {
        PreviewViewportGeometry geometry = new(
            new PixelSize(100, 100),
            new PreviewSize(101, 101),
            renderScaling: 1);

        PixelRect left = Assert.IsType<PixelRect>(
            geometry.NormalizedToClippedPixelRect(new PreviewRect(0, 0, 0.25, 1)));
        PixelRect middle = Assert.IsType<PixelRect>(
            geometry.NormalizedToClippedPixelRect(new PreviewRect(0.25, 0, 0.5, 1)));
        PixelRect right = Assert.IsType<PixelRect>(
            geometry.NormalizedToClippedPixelRect(new PreviewRect(0.75, 0, 0.25, 1)));

        Assert.Equal(left.X + left.Width, middle.X);
        Assert.Equal(middle.X + middle.Width, right.X);
        Assert.Equal(101, right.X + right.Width);

        PixelRect midpoint = Assert.IsType<PixelRect>(
            geometry.PhysicalToClippedPixelRect(new PreviewRect(1.5, 2.5, 2, 2)));
        Assert.Equal(new PixelRect(2, 3, 2, 2), midpoint);
    }

    [Fact]
    public void Visible_ROI_undoes_zoom_and_pan_then_expands_and_clips()
    {
        PreviewViewportGeometry centered = new(
            new PixelSize(100, 100),
            new PreviewSize(100, 100),
            renderScaling: 1,
            zoom: 2,
            requestedPanLogical: new PreviewPoint(-50, -50));

        AssertRect(
            new PreviewRect(0.25, 0.25, 0.5, 0.5),
            Assert.IsType<PreviewRect>(centered.VisibleNormalizedRoi()));
        AssertRect(
            new PreviewRect(0.2, 0.2, 0.6, 0.6),
            Assert.IsType<PreviewRect>(centered.VisibleNormalizedRoi(marginFraction: 0.1)));

        PreviewViewportGeometry againstTopLeft = new(
            new PixelSize(100, 100),
            new PreviewSize(100, 100),
            renderScaling: 1,
            zoom: 2,
            requestedPanLogical: default);
        AssertRect(
            new PreviewRect(0, 0, 0.55, 0.55),
            Assert.IsType<PreviewRect>(againstTopLeft.VisibleNormalizedRoi(marginFraction: 0.1)));
    }

    private static void AssertRect(PreviewRect expected, PreviewRect actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Width, actual.Width);
        AssertClose(expected.Height, actual.Height);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(actual, expected - 1e-10, expected + 1e-10);
}
