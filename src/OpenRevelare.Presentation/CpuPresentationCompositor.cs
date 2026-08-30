namespace OpenRevelare.Presentation;

/// <summary>
/// Small platform-neutral reference compositor for the canonical presentation scene.
///
/// <para>
/// Scaling is deterministic bilinear sampling at pixel centers with clamp-to-edge addressing.
/// Every layer is premultiplied and blended in linear light using source-over. RGB is never clamped
/// to zero, one, sRGB gamut, or alpha; only a result outside finite Half storage is rejected.
/// </para>
/// </summary>
public static class CpuPresentationCompositor
{
    private const double MaximumFiniteHalf = 65504d;
    private const long ParallelPixelThreshold = 256L * 1024L;

    public static PresentationScene Compose(
        PresentationScene baseLayer,
        PixelSize outputSize,
        IReadOnlyList<PresentationOverlay>? overlays = null)
        => Compose(baseLayer, outputSize, overlays, rasterPrimitives: null);

    /// <summary>
    /// Compose image overlays followed by physical-pixel raster primitives. Both collections are
    /// applied in list order. Keeping raster primitives last matches the preview scene's required
    /// order: image, sharp patch and masks first; crop/selection geometry above them.
    /// </summary>
    public static PresentationScene Compose(
        PresentationScene baseLayer,
        PixelSize outputSize,
        IReadOnlyList<PresentationOverlay>? overlays,
        IReadOnlyList<PresentationRasterPrimitive>? rasterPrimitives)
    {
        ArgumentNullException.ThrowIfNull(baseLayer);
        if (outputSize.Width <= 0 || outputSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), "Output size must be non-empty.");

        overlays ??= Array.Empty<PresentationOverlay>();
        rasterPrimitives ??= Array.Empty<PresentationRasterPrimitive>();

        if (baseLayer.Size == outputSize && overlays.Count == 0 && rasterPrimitives.Count == 0)
        {
            // Scenes are immutable, so a no-op composition can safely preserve the existing
            // storage instead of allocating and validating another full-frame copy.
            return baseLayer;
        }

        Half[] output = new Half[checked(outputSize.PixelCount * 4)];
        InitializeBase(output, outputSize, baseLayer);

        foreach (PresentationOverlay overlay in overlays)
        {
            if (overlay is null) throw new ArgumentException("Overlay list contains null.", nameof(overlays));
            if (overlay.Scene.ReferenceWhiteScale != baseLayer.ReferenceWhiteScale)
            {
                throw new ArgumentException(
                    "Every overlay must use the base layer's exact reference-white scale.",
                    nameof(overlays));
            }

            CompositeOverlay(output, outputSize, overlay);
        }

        foreach (PresentationRasterPrimitive primitive in rasterPrimitives)
        {
            if (primitive is null)
            {
                throw new ArgumentException(
                    "Raster primitive list contains null.",
                    nameof(rasterPrimitives));
            }

            CompositePrimitive(output, outputSize, primitive);
        }

        return PresentationScene.FromCompositorOwnedPixels(
            output,
            outputSize,
            baseLayer.ReferenceWhiteScale,
            knownOpaque: baseLayer.IsKnownOpaque);
    }

    private static void InitializeBase(
        Half[] output,
        PixelSize outputSize,
        PresentationScene baseLayer)
    {
        if (baseLayer.Size == outputSize)
        {
            baseLayer.Pixels.CopyTo(output);
            return;
        }

        if (baseLayer.Size.Width == 1 && baseLayer.Size.Height == 1)
        {
            Half red = baseLayer.Pixels[0];
            Half green = baseLayer.Pixels[1];
            Half blue = baseLayer.Pixels[2];
            Half alpha = baseLayer.Pixels[3];
            RunRows(0, outputSize.Height, outputSize.Width, y =>
            {
                int offset = y * outputSize.Width * 4;
                int end = offset + (outputSize.Width * 4);
                for (; offset < end; offset += 4)
                {
                    output[offset] = red;
                    output[offset + 1] = green;
                    output[offset + 2] = blue;
                    output[offset + 3] = alpha;
                }
            });
            return;
        }

        RunRows(0, outputSize.Height, outputSize.Width, y =>
        {
            for (int x = 0; x < outputSize.Width; x++)
            {
                Rgba sample = Sample(
                    baseLayer,
                    x,
                    y,
                    destinationX: 0,
                    destinationY: 0,
                    destinationWidth: outputSize.Width,
                    destinationHeight: outputSize.Height);
                Write(output, ((y * outputSize.Width) + x) * 4, sample);
            }
        });
    }

    private static void CompositeOverlay(Half[] output, PixelSize outputSize, PresentationOverlay overlay)
    {
        PixelRect destination = overlay.Destination;
        int left = (int)Math.Max(0L, destination.X);
        int top = (int)Math.Max(0L, destination.Y);
        int right = (int)Math.Min((long)outputSize.Width, (long)destination.X + destination.Width);
        int bottom = (int)Math.Min((long)outputSize.Height, (long)destination.Y + destination.Height);

        if (left >= right || top >= bottom) return;

        bool exactPixelMapping =
            overlay.Scene.Size.Width == destination.Width &&
            overlay.Scene.Size.Height == destination.Height;
        bool opaqueSource = overlay.Scene.IsKnownOpaque;
        SamplingCoordinate[]? horizontalCoordinates = null;
        if (!exactPixelMapping)
        {
            horizontalCoordinates = new SamplingCoordinate[right - left];
            for (int x = left; x < right; x++)
            {
                horizontalCoordinates[x - left] = GetSamplingCoordinate(
                    x,
                    destination.X,
                    destination.Width,
                    overlay.Scene.Size.Width);
            }
        }

        RunRows(top, bottom, right - left, y =>
        {
            if (exactPixelMapping && opaqueSource)
            {
                int sourceOffset =
                    ((((y - destination.Y) * overlay.Scene.Size.Width) +
                      (left - destination.X)) * 4);
                int destinationOffset = ((y * outputSize.Width) + left) * 4;
                overlay.Scene.Pixels
                    .Slice(sourceOffset, (right - left) * 4)
                    .CopyTo(output.AsSpan(destinationOffset));
                return;
            }

            SamplingCoordinate verticalCoordinate = exactPixelMapping
                ? default
                : GetSamplingCoordinate(
                    y,
                    destination.Y,
                    destination.Height,
                    overlay.Scene.Size.Height);
            for (int x = left; x < right; x++)
            {
                int componentOffset = ((y * outputSize.Width) + x) * 4;
                Rgba source = exactPixelMapping
                    ? Read(
                        overlay.Scene.Pixels,
                        ((((y - destination.Y) * overlay.Scene.Size.Width) +
                          (x - destination.X)) * 4))
                    : SampleMapped(
                        overlay.Scene,
                        horizontalCoordinates![x - left],
                        verticalCoordinate);
                if (opaqueSource)
                {
                    Write(output, componentOffset, source);
                    continue;
                }

                Rgba backdrop = Read(output, componentOffset);
                Write(output, componentOffset, SourceOver(source, backdrop));
            }
        });
    }

    private static void CompositePrimitive(
        Half[] output,
        PixelSize outputSize,
        PresentationRasterPrimitive primitive)
    {
        (double minimumX, double minimumY, double maximumX, double maximumY) =
            GetRasterBounds(primitive);
        int left = FloorAndClamp(minimumX, outputSize.Width);
        int top = FloorAndClamp(minimumY, outputSize.Height);
        int right = CeilingAndClamp(maximumX, outputSize.Width);
        int bottom = CeilingAndClamp(maximumY, outputSize.Height);
        if (left >= right || top >= bottom) return;

        PremultipliedLinearRgba color = primitive.Color;
        RunRows(top, bottom, right - left, y =>
        {
            int rowLeft = left;
            int rowRight = right;
            if (primitive is PresentationLine line &&
                !TryGetLineRowBounds(line, y, outputSize.Width, out rowLeft, out rowRight))
            {
                return;
            }

            if (primitive is PresentationRectOutline outline &&
                TryGetOutlineInteriorGap(
                    outline,
                    y,
                    outputSize.Width,
                    out int gapLeft,
                    out int gapRight))
            {
                CompositePrimitiveRange(
                    output,
                    outputSize.Width,
                    primitive,
                    color,
                    y,
                    rowLeft,
                    Math.Min(rowRight, gapLeft));
                CompositePrimitiveRange(
                    output,
                    outputSize.Width,
                    primitive,
                    color,
                    y,
                    Math.Max(rowLeft, gapRight),
                    rowRight);
                return;
            }

            CompositePrimitiveRange(
                output,
                outputSize.Width,
                primitive,
                color,
                y,
                rowLeft,
                rowRight);
        });
    }

    private static void CompositePrimitiveRange(
        Half[] output,
        int outputWidth,
        PresentationRasterPrimitive primitive,
        PremultipliedLinearRgba color,
        int y,
        int left,
        int right)
    {
        for (int x = left; x < right; x++)
        {
            double coverage = Coverage(primitive, x, y);
            if (coverage <= 0d) continue;

            Rgba source = new(
                color.Red * coverage,
                color.Green * coverage,
                color.Blue * coverage,
                color.Alpha * coverage);
            int componentOffset = ((y * outputWidth) + x) * 4;
            Rgba backdrop = Read(output, componentOffset);
            Write(output, componentOffset, SourceOver(source, backdrop));
        }
    }

    /// <summary>
    /// Finds the horizontal interior that has provably zero outline coverage on this row. Only
    /// rows where the inner and outer rectangles have identical vertical coverage qualify; top
    /// and bottom stroke rows retain the full span. The two edge spans are composited separately.
    /// </summary>
    private static bool TryGetOutlineInteriorGap(
        PresentationRectOutline outline,
        int pixelY,
        int outputWidth,
        out int gapLeft,
        out int gapRight)
    {
        double innerWidth = outline.Bounds.Width - outline.StrokeWidth;
        double innerHeight = outline.Bounds.Height - outline.StrokeWidth;
        if (innerWidth <= 0d || innerHeight <= 0d)
        {
            gapLeft = gapRight = 0;
            return false;
        }

        double half = outline.StrokeWidth / 2d;
        double outerVertical = IntervalCoverage(
            outline.Bounds.Y - half,
            outline.Bounds.Bottom + half,
            pixelY);
        double innerVertical = IntervalCoverage(
            outline.Bounds.Y + half,
            outline.Bounds.Bottom - half,
            pixelY);
        if (innerVertical <= 0d || innerVertical != outerVertical)
        {
            gapLeft = gapRight = 0;
            return false;
        }

        gapLeft = CeilingAndClamp(outline.Bounds.X + half, outputWidth);
        gapRight = FloorAndClamp(outline.Bounds.Right - half, outputWidth);
        return gapLeft < gapRight;
    }

    /// <summary>
    /// Computes a conservative horizontal span for one stroked-line row. The previous global
    /// axis-aligned bounds made a corner-to-corner line visit every viewport pixel. Any point
    /// close enough to the segment must be within the stroke ramp vertically and horizontally,
    /// so this smaller span cannot discard non-zero coverage; the analytic coverage function
    /// remains the final authority for every retained pixel.
    /// </summary>
    private static bool TryGetLineRowBounds(
        PresentationLine line,
        int pixelY,
        int outputWidth,
        out int left,
        out int right)
    {
        double extent = (line.StrokeWidth / 2d) + 0.5d;
        double centerY = pixelY + 0.5d;
        double deltaX = line.End.X - line.Start.X;
        double deltaY = line.End.Y - line.Start.Y;

        double minimumT;
        double maximumT;
        if (deltaY == 0d)
        {
            if (Math.Abs(centerY - line.Start.Y) > extent)
            {
                left = right = 0;
                return false;
            }

            minimumT = 0d;
            maximumT = 1d;
        }
        else
        {
            double first = (centerY - extent - line.Start.Y) / deltaY;
            double second = (centerY + extent - line.Start.Y) / deltaY;
            minimumT = Math.Max(0d, Math.Min(first, second));
            maximumT = Math.Min(1d, Math.Max(first, second));
            if (minimumT > maximumT)
            {
                left = right = 0;
                return false;
            }
        }

        double firstX = line.Start.X + (minimumT * deltaX);
        double secondX = line.Start.X + (maximumT * deltaX);
        double minimumCenterX = Math.Min(firstX, secondX) - extent;
        double maximumCenterX = Math.Max(firstX, secondX) + extent;

        // Pixel coverage is evaluated at x + 0.5. Expand one pixel beyond the exact center
        // interval to absorb floating-point endpoint rounding, then let LineCoverage reject it.
        left = FloorAndClamp(minimumCenterX - 0.5d, outputWidth);
        right = CeilingAndClamp(maximumCenterX + 0.5d, outputWidth);
        return left < right;
    }

    private static void RunRows(int top, int bottom, int rowWidth, Action<int> action)
    {
        long work = checked((long)(bottom - top) * rowWidth);
        if (Environment.ProcessorCount > 1 && work >= ParallelPixelThreshold)
        {
            try
            {
                Parallel.For(top, bottom, action);
            }
            catch (AggregateException exception)
                when (exception.Flatten().InnerExceptions.All(error => error is OverflowException))
            {
                // Keep the compositor's documented overflow exception surface even when several
                // independent rows detect an invalid result concurrently.
                throw (OverflowException)exception.Flatten().InnerExceptions[0];
            }
            return;
        }

        for (int y = top; y < bottom; y++)
            action(y);
    }

    private static (double MinimumX, double MinimumY, double MaximumX, double MaximumY)
        GetRasterBounds(PresentationRasterPrimitive primitive)
    {
        switch (primitive)
        {
            case PresentationSolidRect solid:
                return (solid.Bounds.X, solid.Bounds.Y, solid.Bounds.Right, solid.Bounds.Bottom);

            case PresentationRectOutline outline:
                double outlineHalf = outline.StrokeWidth / 2d;
                return (
                    outline.Bounds.X - outlineHalf,
                    outline.Bounds.Y - outlineHalf,
                    outline.Bounds.Right + outlineHalf,
                    outline.Bounds.Bottom + outlineHalf);

            case PresentationLine line:
                // The analytic line ramp reaches half a pixel beyond the geometric stroke.
                double lineExtent = (line.StrokeWidth / 2d) + 0.5d;
                return (
                    Math.Min(line.Start.X, line.End.X) - lineExtent,
                    Math.Min(line.Start.Y, line.End.Y) - lineExtent,
                    Math.Max(line.Start.X, line.End.X) + lineExtent,
                    Math.Max(line.Start.Y, line.End.Y) + lineExtent);

            default:
                throw new ArgumentException(
                    $"Unsupported raster primitive type {primitive.GetType().FullName}.",
                    nameof(primitive));
        }
    }

    private static double Coverage(PresentationRasterPrimitive primitive, int pixelX, int pixelY) =>
        primitive switch
        {
            PresentationSolidRect solid => RectangleCoverage(solid.Bounds, pixelX, pixelY),
            PresentationRectOutline outline => OutlineCoverage(outline, pixelX, pixelY),
            PresentationLine line => LineCoverage(line, pixelX, pixelY),
            _ => throw new ArgumentException(
                $"Unsupported raster primitive type {primitive.GetType().FullName}.",
                nameof(primitive)),
        };

    private static double RectangleCoverage(PreviewRect rectangle, int pixelX, int pixelY)
    {
        double horizontal = IntervalCoverage(rectangle.X, rectangle.Right, pixelX);
        double vertical = IntervalCoverage(rectangle.Y, rectangle.Bottom, pixelY);
        return horizontal * vertical;
    }

    private static double IntervalCoverage(double minimum, double maximum, int pixel) =>
        Math.Max(0d, Math.Min(maximum, pixel + 1d) - Math.Max(minimum, pixel));

    private static double OutlineCoverage(PresentationRectOutline outline, int pixelX, int pixelY)
    {
        double half = outline.StrokeWidth / 2d;
        PreviewRect outer = new(
            outline.Bounds.X - half,
            outline.Bounds.Y - half,
            outline.Bounds.Width + outline.StrokeWidth,
            outline.Bounds.Height + outline.StrokeWidth);
        double outerCoverage = RectangleCoverage(outer, pixelX, pixelY);
        if (outerCoverage <= 0d) return 0d;

        double innerWidth = outline.Bounds.Width - outline.StrokeWidth;
        double innerHeight = outline.Bounds.Height - outline.StrokeWidth;
        if (innerWidth <= 0d || innerHeight <= 0d) return outerCoverage;

        PreviewRect inner = new(
            outline.Bounds.X + half,
            outline.Bounds.Y + half,
            innerWidth,
            innerHeight);
        return Math.Clamp(outerCoverage - RectangleCoverage(inner, pixelX, pixelY), 0d, 1d);
    }

    private static double LineCoverage(PresentationLine line, int pixelX, int pixelY)
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

    private static int FloorAndClamp(double value, int maximum)
    {
        if (value <= 0d) return 0;
        if (value >= maximum) return maximum;
        return checked((int)Math.Floor(value));
    }

    private static int CeilingAndClamp(double value, int maximum)
    {
        if (value <= 0d) return 0;
        if (value >= maximum) return maximum;
        return checked((int)Math.Ceiling(value));
    }

    private static Rgba Sample(
        PresentationScene scene,
        int outputX,
        int outputY,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight)
    {
        SamplingCoordinate horizontal = GetSamplingCoordinate(
            outputX,
            destinationX,
            destinationWidth,
            scene.Size.Width);
        SamplingCoordinate vertical = GetSamplingCoordinate(
            outputY,
            destinationY,
            destinationHeight,
            scene.Size.Height);
        return SampleMapped(scene, horizontal, vertical);
    }

    private static SamplingCoordinate GetSamplingCoordinate(
        int outputCoordinate,
        int destinationCoordinate,
        int destinationLength,
        int sourceLength)
    {
        double sourceCoordinate =
            ((((outputCoordinate - (long)destinationCoordinate) + 0.5d) * sourceLength /
              destinationLength) - 0.5d);
        int rawLower = checked((int)Math.Floor(sourceCoordinate));
        return new SamplingCoordinate(
            Math.Clamp(rawLower, 0, sourceLength - 1),
            Math.Clamp(rawLower + 1, 0, sourceLength - 1),
            sourceCoordinate - rawLower);
    }

    private static Rgba SampleMapped(
        PresentationScene scene,
        SamplingCoordinate horizontal,
        SamplingCoordinate vertical)
    {
        int sourceWidth = scene.Size.Width;
        Rgba topLeft = Read(
            scene.Pixels,
            ((vertical.Lower * sourceWidth) + horizontal.Lower) * 4);
        Rgba topRight = Read(
            scene.Pixels,
            ((vertical.Lower * sourceWidth) + horizontal.Upper) * 4);
        Rgba bottomLeft = Read(
            scene.Pixels,
            ((vertical.Upper * sourceWidth) + horizontal.Lower) * 4);
        Rgba bottomRight = Read(
            scene.Pixels,
            ((vertical.Upper * sourceWidth) + horizontal.Upper) * 4);

        Rgba top = Lerp(topLeft, topRight, horizontal.Fraction);
        Rgba bottom = Lerp(bottomLeft, bottomRight, horizontal.Fraction);
        return Lerp(top, bottom, vertical.Fraction);
    }

    private static Rgba SourceOver(Rgba source, Rgba backdrop)
    {
        double remaining = 1d - source.A;
        return new Rgba(
            source.R + (backdrop.R * remaining),
            source.G + (backdrop.G * remaining),
            source.B + (backdrop.B * remaining),
            source.A + (backdrop.A * remaining));
    }

    private static Rgba Lerp(Rgba left, Rgba right, double amount) => new(
        left.R + ((right.R - left.R) * amount),
        left.G + ((right.G - left.G) * amount),
        left.B + ((right.B - left.B) * amount),
        left.A + ((right.A - left.A) * amount));

    private static Rgba Read(ReadOnlySpan<Half> values, int offset) => new(
        (double)values[offset],
        (double)values[offset + 1],
        (double)values[offset + 2],
        (double)values[offset + 3]);

    private static Rgba Read(Half[] values, int offset) => Read(values.AsSpan(), offset);

    private static void Write(Half[] destination, int offset, Rgba value)
    {
        destination[offset] = ToHalf(value.R);
        destination[offset + 1] = ToHalf(value.G);
        destination[offset + 2] = ToHalf(value.B);
        destination[offset + 3] = ToHalf(value.A);
    }

    private static Half ToHalf(double value)
    {
        if (!double.IsFinite(value) || value is > MaximumFiniteHalf or < -MaximumFiniteHalf)
        {
            throw new OverflowException(
                $"Composited value {value} cannot be represented by finite RGBA16F without clamping.");
        }

        return (Half)value;
    }

    private readonly record struct Rgba(double R, double G, double B, double A);
    private readonly record struct SamplingCoordinate(int Lower, int Upper, double Fraction);
}
