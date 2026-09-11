using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PresentationCompositorTests
{
    [Fact]
    public void Canonical_scene_is_immutable_and_preserves_negative_and_above_one_RGB()
    {
        Half[] callerOwned = Pixel(-0.25f, 1.5f, 2.25f, 1f);
        PresentationScene scene = new(callerOwned, new PixelSize(1, 1), referenceWhiteScale: 1f);
        callerOwned.AsSpan().Fill((Half)0f);

        PresentationScene composed = CpuPresentationCompositor.Compose(scene, new PixelSize(1, 1));

        AssertClose(-0.25f, composed.LinearExtendedSrgbRgba[0]);
        AssertClose(1.5f, composed.LinearExtendedSrgbRgba[1]);
        AssertClose(2.25f, composed.LinearExtendedSrgbRgba[2]);
        AssertClose(1f, composed.LinearExtendedSrgbRgba[3]);
    }

    [Fact]
    public void Premultiplied_overlay_uses_linear_source_over_without_RGB_clamp()
    {
        PresentationScene baseLayer = Scene(1, 1, Pixel(0.2f, -0.4f, 1.4f, 1f));
        PresentationScene overlayLayer = Scene(1, 1, Pixel(0.5f, 0.1f, -0.25f, 0.5f));
        var overlay = new PresentationOverlay(overlayLayer, new PixelRect(0, 0, 1, 1));

        PresentationScene result = CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(1, 1),
            new[] { overlay });

        AssertClose(0.6f, result.LinearExtendedSrgbRgba[0]);
        AssertClose(-0.1f, result.LinearExtendedSrgbRgba[1]);
        AssertClose(0.45f, result.LinearExtendedSrgbRgba[2]);
        AssertClose(1f, result.LinearExtendedSrgbRgba[3]);
    }

    [Fact]
    public void Rectangular_overlay_uses_documented_pixel_center_bilinear_sampling()
    {
        PresentationScene baseLayer = Scene(
            4,
            1,
            Pixel(0f, 0f, 0f, 1f),
            Pixel(0f, 0f, 0f, 1f),
            Pixel(0f, 0f, 0f, 1f),
            Pixel(0f, 0f, 0f, 1f));
        PresentationScene overlayLayer = Scene(
            2,
            1,
            Pixel(0f, 0f, 0f, 1f),
            Pixel(1f, 0f, 0f, 1f));

        PresentationScene result = CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(4, 1),
            new[] { new PresentationOverlay(overlayLayer, new PixelRect(0, 0, 4, 1)) });

        AssertClose(0f, result.LinearExtendedSrgbRgba[0]);
        AssertClose(0.25f, result.LinearExtendedSrgbRgba[4]);
        AssertClose(0.75f, result.LinearExtendedSrgbRgba[8]);
        AssertClose(1f, result.LinearExtendedSrgbRgba[12]);
    }

    [Fact]
    public void Overlay_rectangle_is_clipped_to_output_without_changing_sampling_coordinates()
    {
        PresentationScene baseLayer = Scene(
            2,
            1,
            Pixel(0f, 0f, 0f, 1f),
            Pixel(0f, 0f, 0f, 1f));
        PresentationScene overlayLayer = Scene(1, 1, Pixel(2f, 0f, 0f, 1f));

        PresentationScene result = CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(2, 1),
            new[] { new PresentationOverlay(overlayLayer, new PixelRect(-1, 0, 2, 1)) });

        AssertClose(2f, result.LinearExtendedSrgbRgba[0]);
        AssertClose(0f, result.LinearExtendedSrgbRgba[4]);
    }

    [Fact]
    public void Raster_primitives_use_linear_source_over_in_list_order()
    {
        PresentationScene baseLayer = Scene(1, 1, Pixel(0.2f, 0.4f, 0.6f, 1f));
        var red = new PresentationSolidRect(
            new PreviewRect(0, 0, 1, 1),
            new PremultipliedLinearRgba(0.5f, 0f, 0f, 0.5f));
        var blue = new PresentationSolidRect(
            new PreviewRect(0, 0, 1, 1),
            new PremultipliedLinearRgba(0f, 0f, 0.5f, 0.5f));

        PresentationScene redThenBlue = CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(1, 1),
            overlays: null,
            rasterPrimitives: new PresentationRasterPrimitive[] { red, blue });
        PresentationScene blueThenRed = CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(1, 1),
            overlays: null,
            rasterPrimitives: new PresentationRasterPrimitive[] { blue, red });

        AssertClose(0.3f, redThenBlue.LinearExtendedSrgbRgba[0]);
        AssertClose(0.1f, redThenBlue.LinearExtendedSrgbRgba[1]);
        AssertClose(0.65f, redThenBlue.LinearExtendedSrgbRgba[2]);
        AssertClose(0.55f, blueThenRed.LinearExtendedSrgbRgba[0]);
        AssertClose(0.1f, blueThenRed.LinearExtendedSrgbRgba[1]);
        AssertClose(0.4f, blueThenRed.LinearExtendedSrgbRgba[2]);
    }

    [Fact]
    public void Raster_primitives_are_above_image_overlays()
    {
        PresentationScene baseLayer = Scene(1, 1, Pixel(0f, 0f, 0f, 1f));
        PresentationScene greenOverlay = Scene(1, 1, Pixel(0f, 1f, 0f, 1f));
        var redPrimitive = new PresentationSolidRect(
            new PreviewRect(0, 0, 1, 1),
            new PremultipliedLinearRgba(0.5f, 0f, 0f, 0.5f));

        PresentationScene result = CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(1, 1),
            new[] { new PresentationOverlay(greenOverlay, new PixelRect(0, 0, 1, 1)) },
            new PresentationRasterPrimitive[] { redPrimitive });

        AssertClose(0.5f, result.LinearExtendedSrgbRgba[0]);
        AssertClose(0.5f, result.LinearExtendedSrgbRgba[1]);
        AssertClose(0f, result.LinearExtendedSrgbRgba[2]);
    }

    [Fact]
    public void Fractional_solid_rectangle_has_exact_area_coverage_and_clips_to_output()
    {
        PresentationScene transparent = SolidScene(2, 1, Pixel(0f, 0f, 0f, 0f));
        var clippedHalfPixel = new PresentationSolidRect(
            new PreviewRect(-0.5, 0, 1, 1),
            new PremultipliedLinearRgba(1f, 0f, 0f, 1f));

        PresentationScene result = CpuPresentationCompositor.Compose(
            transparent,
            new PixelSize(2, 1),
            overlays: null,
            rasterPrimitives: new PresentationRasterPrimitive[] { clippedHalfPixel });

        AssertClose(0.5f, result.LinearExtendedSrgbRgba[0]);
        AssertClose(0.5f, result.LinearExtendedSrgbRgba[3]);
        AssertClose(0f, result.LinearExtendedSrgbRgba[4]);
        AssertClose(0f, result.LinearExtendedSrgbRgba[7]);
    }

    [Fact]
    public void Rectangle_outline_is_one_layer_with_square_corners_and_clear_interior()
    {
        PresentationScene transparent = SolidScene(5, 5, Pixel(0f, 0f, 0f, 0f));
        var outline = new PresentationRectOutline(
            new PreviewRect(1.5, 1.5, 2, 2),
            strokeWidth: 1,
            color: new PremultipliedLinearRgba(1f, 1f, 1f, 1f));

        PresentationScene result = CpuPresentationCompositor.Compose(
            transparent,
            new PixelSize(5, 5),
            overlays: null,
            rasterPrimitives: new PresentationRasterPrimitive[] { outline });

        AssertClose(1f, AlphaAt(result, 1, 1));
        AssertClose(1f, AlphaAt(result, 3, 3));
        AssertClose(0f, AlphaAt(result, 2, 2));
        AssertClose(0f, AlphaAt(result, 0, 0));
    }

    [Fact]
    public void Optimized_outline_interior_gap_matches_full_area_coverage()
    {
        const int width = 47;
        const int height = 31;
        PresentationScene transparent = RepeatedScene(
            width,
            height,
            red: 0f,
            green: 0f,
            blue: 0f,
            alpha: 0f);
        var color = new PremultipliedLinearRgba(1f, 1f, 1f, 1f);
        var outlines = new List<PresentationRectOutline>
        {
            new(new PreviewRect(1.5, 2.25, 40.125, 25.5), 0.5, color),
            new(new PreviewRect(-8.75, -3.125, 65.5, 40.25), 3.75, color),
            new(new PreviewRect(17.2, 9.4, 2.1, 1.7), 4, color),
        };
        var random = new Random(0x0A71);
        for (int index = 0; index < 64; index++)
        {
            outlines.Add(new PresentationRectOutline(
                new PreviewRect(
                    (random.NextDouble() * (width + 20)) - 10,
                    (random.NextDouble() * (height + 20)) - 10,
                    0.1d + (random.NextDouble() * (width + 8)),
                    0.1d + (random.NextDouble() * (height + 8))),
                0.1d + (random.NextDouble() * 8d),
                color));
        }

        foreach (PresentationRectOutline outline in outlines)
        {
            PresentationScene result = CpuPresentationCompositor.Compose(
                transparent,
                transparent.Size,
                overlays: null,
                rasterPrimitives: new PresentationRasterPrimitive[] { outline });

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(
                        (Half)ReferenceOutlineCoverage(outline, x, y),
                        AlphaAt(result, x, y));
                }
            }
        }
    }

    [Fact]
    public void Line_stroke_has_constant_physical_coverage_at_different_zoom_and_subpixel_positions()
    {
        PresentationScene transparent = SolidScene(10, 5, Pixel(0f, 0f, 0f, 0f));
        PreviewViewportGeometry fit = new(
            new PixelSize(10, 10),
            new PreviewSize(10, 10),
            renderScaling: 1,
            zoom: 1);
        PreviewViewportGeometry zoomed = new(
            new PixelSize(10, 10),
            new PreviewSize(10, 10),
            renderScaling: 1,
            zoom: 2);
        double fitX = fit.NormalizedToPhysical(new PreviewPoint(0.25, 0)).X;
        double zoomedX = zoomed.NormalizedToPhysical(new PreviewPoint(0.25, 0)).X;

        PresentationScene fitLine = ComposeVerticalLine(transparent, fitX);
        PresentationScene zoomedLine = ComposeVerticalLine(transparent, zoomedX);

        AssertClose(1.5f, RowAlpha(fitLine, y: 2));
        AssertClose(1.5f, RowAlpha(zoomedLine, y: 2));
    }

    [Fact]
    public void Arbitrary_line_supports_diagonal_selection_and_straighten_geometry()
    {
        PresentationScene transparent = SolidScene(5, 5, Pixel(0f, 0f, 0f, 0f));
        var diagonal = new PresentationLine(
            new PreviewPoint(0.5, 0.5),
            new PreviewPoint(4.5, 4.5),
            strokeWidth: 1,
            color: new PremultipliedLinearRgba(1f, 1f, 1f, 1f));

        PresentationScene result = CpuPresentationCompositor.Compose(
            transparent,
            new PixelSize(5, 5),
            overlays: null,
            rasterPrimitives: new PresentationRasterPrimitive[] { diagonal });

        for (int coordinate = 0; coordinate < 5; coordinate++)
            AssertClose(1f, AlphaAt(result, coordinate, coordinate));
        AssertClose(0f, AlphaAt(result, 0, 4));
    }

    [Fact]
    public void Optimized_line_row_bounds_match_the_full_analytic_rasterizer()
    {
        const int width = 47;
        const int height = 31;
        PresentationScene transparent = RepeatedScene(
            width,
            height,
            red: 0f,
            green: 0f,
            blue: 0f,
            alpha: 0f);
        var color = new PremultipliedLinearRgba(1f, 1f, 1f, 1f);
        var lines = new List<PresentationLine>
        {
            new(new PreviewPoint(-8.25, 2.75), new PreviewPoint(55.5, 28.125), 1.5, color),
            new(new PreviewPoint(42.75, -4), new PreviewPoint(3.125, 35), 2.25, color),
            new(new PreviewPoint(23.4, -10), new PreviewPoint(23.4, 40), 0.75, color),
            new(new PreviewPoint(-10, 15.6), new PreviewPoint(60, 15.6), 3.5, color),
            new(new PreviewPoint(17.25, 12.75), new PreviewPoint(17.25, 12.75), 2, color),
        };
        var random = new Random(0x5EED);
        for (int index = 0; index < 128; index++)
        {
            PreviewPoint start = new(
                (random.NextDouble() * (width + 24)) - 12,
                (random.NextDouble() * (height + 24)) - 12);
            PreviewPoint end = new(
                (random.NextDouble() * (width + 24)) - 12,
                (random.NextDouble() * (height + 24)) - 12);
            double strokeWidth = 0.1d + (random.NextDouble() * 6d);
            lines.Add(new PresentationLine(start, end, strokeWidth, color));
        }

        foreach (PresentationLine line in lines)
        {
            PresentationScene result = CpuPresentationCompositor.Compose(
                transparent,
                transparent.Size,
                overlays: null,
                rasterPrimitives: new PresentationRasterPrimitive[] { line });

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Half expected = (Half)ReferenceLineCoverage(line, x, y);
                    Assert.Equal(expected, AlphaAt(result, x, y));
                }
            }
        }
    }

    [Fact]
    public void Parallel_row_composition_is_bitwise_deterministic()
    {
        var size = new PixelSize(700, 400); // Exceeds the compositor's parallel row threshold.
        PresentationScene background = Scene(1, 1, Pixel(0.1f, 0.2f, 0.3f, 1f));
        PresentationScene image = Scene(
            2,
            2,
            Pixel(-0.2f, 0.1f, 1.2f, 0.75f),
            Pixel(0.4f, 0.2f, 0.1f, 0.5f),
            Pixel(0.05f, 1.1f, 0.2f, 1f),
            Pixel(0f, 0f, 0f, 0f));
        var overlays = new[]
        {
            new PresentationOverlay(image, new PixelRect(0, 0, size.Width, size.Height)),
        };
        var primitives = new PresentationRasterPrimitive[]
        {
            new PresentationSolidRect(
                new PreviewRect(10.25, 12.5, 650.125, 350.75),
                new PremultipliedLinearRgba(0.08f, 0.02f, -0.01f, 0.2f)),
        };

        PresentationScene first = CpuPresentationCompositor.Compose(
            background,
            size,
            overlays,
            primitives);
        PresentationScene second = CpuPresentationCompositor.Compose(
            background,
            size,
            overlays,
            primitives);

        Assert.True(
            first.LinearExtendedSrgbRgba.AsSpan().SequenceEqual(
                second.LinearExtendedSrgbRgba.AsSpan()));
    }

    [Fact]
    public void Parallel_composition_preserves_the_documented_overflow_exception()
    {
        const int width = 600;
        const int height = 500;
        PresentationScene backdrop = RepeatedScene(
            width,
            height,
            red: 65504f,
            green: 0f,
            blue: 0f,
            alpha: 1f);
        PresentationScene source = RepeatedScene(
            width,
            height,
            red: 65504f,
            green: 0f,
            blue: 0f,
            alpha: 0.5f);

        Assert.Throws<OverflowException>(() => CpuPresentationCompositor.Compose(
            backdrop,
            backdrop.Size,
            new[] { new PresentationOverlay(source, new PixelRect(0, 0, width, height)) }));
    }

    [Fact]
    public void Compositor_rejects_layers_with_different_reference_white_scale()
    {
        PresentationScene baseLayer = Scene(1, 1, Pixel(0f, 0f, 0f, 1f));
        PresentationScene overlayLayer = new(
            Pixel(0f, 0f, 0f, 1f),
            new PixelSize(1, 1),
            referenceWhiteScale: 1.25f);

        Assert.Throws<ArgumentException>(() => CpuPresentationCompositor.Compose(
            baseLayer,
            new PixelSize(1, 1),
            new[] { new PresentationOverlay(overlayLayer, new PixelRect(0, 0, 1, 1)) }));
    }

    [Fact]
    public void Scene_rejects_non_finite_or_non_premultiplied_zero_alpha_pixels()
    {
        Assert.Throws<ArgumentException>(() => Scene(1, 1, Pixel(float.NaN, 0f, 0f, 1f)));
        Assert.Throws<ArgumentException>(() => Scene(1, 1, Pixel(0.5f, 0f, 0f, 0f)));
    }

    private static PresentationScene Scene(int width, int height, params Half[][] pixels)
    {
        Half[] flattened = pixels.SelectMany(pixel => pixel).ToArray();
        return new PresentationScene(flattened, new PixelSize(width, height), referenceWhiteScale: 1f);
    }

    private static PresentationScene SolidScene(int width, int height, Half[] pixel) =>
        Scene(width, height, Enumerable.Range(0, checked(width * height)).Select(_ => pixel).ToArray());

    private static PresentationScene RepeatedScene(
        int width,
        int height,
        float red,
        float green,
        float blue,
        float alpha)
    {
        var pixels = new Half[checked(width * height * 4)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (Half)red;
            pixels[offset + 1] = (Half)green;
            pixels[offset + 2] = (Half)blue;
            pixels[offset + 3] = (Half)alpha;
        }
        return new PresentationScene(pixels, new PixelSize(width, height), referenceWhiteScale: 1f);
    }

    private static double ReferenceLineCoverage(PresentationLine line, int pixelX, int pixelY)
    {
        double pointX = pixelX + 0.5d;
        double pointY = pixelY + 0.5d;
        double segmentX = line.End.X - line.Start.X;
        double segmentY = line.End.Y - line.Start.Y;
        double lengthSquared = (segmentX * segmentX) + (segmentY * segmentY);

        double closestX;
        double closestY;
        if (lengthSquared == 0d)
        {
            closestX = line.Start.X;
            closestY = line.Start.Y;
        }
        else
        {
            double projection = Math.Clamp(
                (((pointX - line.Start.X) * segmentX) +
                 ((pointY - line.Start.Y) * segmentY)) / lengthSquared,
                0d,
                1d);
            closestX = line.Start.X + (projection * segmentX);
            closestY = line.Start.Y + (projection * segmentY);
        }

        double distanceX = pointX - closestX;
        double distanceY = pointY - closestY;
        double distance = Math.Sqrt((distanceX * distanceX) + (distanceY * distanceY));
        return Math.Clamp(((line.StrokeWidth / 2d) + 0.5d) - distance, 0d, 1d);
    }

    private static double ReferenceOutlineCoverage(
        PresentationRectOutline outline,
        int pixelX,
        int pixelY)
    {
        double half = outline.StrokeWidth / 2d;
        var outer = new PreviewRect(
            outline.Bounds.X - half,
            outline.Bounds.Y - half,
            outline.Bounds.Width + outline.StrokeWidth,
            outline.Bounds.Height + outline.StrokeWidth);
        double outerCoverage = ReferenceRectangleCoverage(outer, pixelX, pixelY);
        if (outerCoverage <= 0d)
            return 0d;

        double innerWidth = outline.Bounds.Width - outline.StrokeWidth;
        double innerHeight = outline.Bounds.Height - outline.StrokeWidth;
        if (innerWidth <= 0d || innerHeight <= 0d)
            return outerCoverage;

        var inner = new PreviewRect(
            outline.Bounds.X + half,
            outline.Bounds.Y + half,
            innerWidth,
            innerHeight);
        return Math.Clamp(
            outerCoverage - ReferenceRectangleCoverage(inner, pixelX, pixelY),
            0d,
            1d);
    }

    private static double ReferenceRectangleCoverage(PreviewRect rectangle, int pixelX, int pixelY)
    {
        double horizontal = Math.Max(
            0d,
            Math.Min(rectangle.Right, pixelX + 1d) - Math.Max(rectangle.X, pixelX));
        double vertical = Math.Max(
            0d,
            Math.Min(rectangle.Bottom, pixelY + 1d) - Math.Max(rectangle.Y, pixelY));
        return horizontal * vertical;
    }

    private static PresentationScene ComposeVerticalLine(PresentationScene baseLayer, double x)
    {
        var line = new PresentationLine(
            new PreviewPoint(x, -5),
            new PreviewPoint(x, 10),
            strokeWidth: 1.5,
            color: new PremultipliedLinearRgba(1f, 1f, 1f, 1f));
        return CpuPresentationCompositor.Compose(
            baseLayer,
            baseLayer.Size,
            overlays: null,
            rasterPrimitives: new PresentationRasterPrimitive[] { line });
    }

    private static float RowAlpha(PresentationScene scene, int y)
    {
        float sum = 0f;
        for (int x = 0; x < scene.Size.Width; x++)
            sum += (float)scene.LinearExtendedSrgbRgba[((y * scene.Size.Width) + x) * 4 + 3];
        return sum;
    }

    private static Half AlphaAt(PresentationScene scene, int x, int y) =>
        scene.LinearExtendedSrgbRgba[((y * scene.Size.Width) + x) * 4 + 3];

    private static Half[] Pixel(float red, float green, float blue, float alpha) =>
        new[] { (Half)red, (Half)green, (Half)blue, (Half)alpha };

    private static void AssertClose(float expected, Half actual) =>
        Assert.InRange((float)actual, expected - 0.001f, expected + 0.001f);

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
}
