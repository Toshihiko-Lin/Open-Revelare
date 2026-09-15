using System.Collections.Immutable;

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
    // Raster primitives fork much earlier. The crop tool's dimming panes are each a fraction of
    // the viewport — 130 K pixels on a small window — so under the layer threshold every one of
    // them ran serially, ~40 ns a pixel through the Half blend, and the four together were 12–20
    // ms of every crop-handle move. A fork costs tens of microseconds; 16 K pixels repays it.
    private const long ParallelPrimitiveThreshold = 16L * 1024L;

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

    /// <summary>
    /// Raster primitives over <paramref name="baseLayer"/>, written into <paramref name="destination"/>
    /// — which must be exactly the base's component count — instead of a fresh array.
    ///
    /// <para>
    /// The returned scene ALIASES <paramref name="destination"/>: it is valid only until the
    /// caller next writes that array. This exists for the one hot loop that wants it — the
    /// presentation worker re-rasterising crop/selection geometry over a cached base on every
    /// pointer move — where allocating an 18 MB Half[] per frame at full-screen size (plus the
    /// same again for the packed bytes) was 0.5–0.9 GB/s of large-object churn and the gen-2
    /// pauses that go with it: 100–250 ms stalls in a drag that otherwise ran at 20 ms a frame.
    /// Pixel results are identical to <see cref="Compose(PresentationScene, PixelSize, IReadOnlyList{PresentationOverlay}?, IReadOnlyList{PresentationRasterPrimitive}?)"/>.
    /// </para>
    /// </summary>
    internal static PresentationScene ComposePrimitivesInto(
        Half[] destination,
        PresentationScene baseLayer,
        IReadOnlyList<PresentationRasterPrimitive> rasterPrimitives)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(baseLayer);
        ArgumentNullException.ThrowIfNull(rasterPrimitives);
        if (destination.Length != baseLayer.LinearExtendedSrgbRgba.Length)
            throw new ArgumentException("Destination must match the base layer's component count.", nameof(destination));

        baseLayer.Pixels.CopyTo(destination);
        foreach (PresentationRasterPrimitive primitive in rasterPrimitives)
        {
            if (primitive is null)
                throw new ArgumentException("Raster primitive list contains null.", nameof(rasterPrimitives));
            CompositePrimitive(destination, baseLayer.Size, primitive);
        }

        return PresentationScene.FromCompositorOwnedPixels(
            destination,
            baseLayer.Size,
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

        float[] source = ToFloats(baseLayer);
        RunRows(0, outputSize.Height, outputSize.Width, y =>
        {
            for (int x = 0; x < outputSize.Width; x++)
            {
                Rgba sample = Sample(
                    source,
                    baseLayer.Size,
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

    /// <summary>
    /// The scene's pixels widened to float, once, before a scaled overlay is sampled.
    ///
    /// Bilinear sampling reads four source pixels per output pixel — sixteen Half components —
    /// and Half has no hardware conversion on the CPU: each read is a software bit-widening. On
    /// a 1900×1000 viewport that was 24 M conversions per compose, most of a 38 ms pass, and the
    /// pass runs on every slider step. Widening each source component exactly once (6.8 M on a
    /// 1600 px preview) and sampling the floats cuts the conversions by roughly four; the values
    /// are the same, because every Half is exactly representable as a float and the lerps stay
    /// in double, so the composed pixels are bitwise what the Half-sampled pass produced.
    /// </summary>
    private static float[] ToFloats(PresentationScene scene)
    {
        ImmutableArray<Half> pixels = scene.LinearExtendedSrgbRgba;
        var result = new float[pixels.Length];
        int width = scene.Size.Width;
        RunRows(0, scene.Size.Height, width, y =>
        {
            int start = y * width * 4;
            int end = start + (width * 4);
            for (int i = start; i < end; i++)
                result[i] = (float)pixels[i];
        });
        return result;
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
        float[]? sourceFloats = null;
        if (!exactPixelMapping)
        {
            sourceFloats = ToFloats(overlay.Scene);
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

        if (!exactPixelMapping && opaqueSource)
        {
            CompositeScaledOpaqueOverlay(
                output, outputSize, overlay, sourceFloats!, horizontalCoordinates!, left, top, right, bottom);
            return;
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
                        sourceFloats!,
                        overlay.Scene.Size.Width,
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

    /// <summary>
    /// The scaled opaque image — the preview fitted to the viewport, which is what almost every
    /// composition spends its time on — as one flat loop: the four taps, the three lerps and the
    /// write in scalar locals, with no per-pixel struct traffic. Same taps, same lerp order, same
    /// <see cref="ToHalf"/> at the end, so the output is bitwise what <see cref="SampleMapped"/>
    /// and <see cref="Write"/> produce through the general path below; that path remains for
    /// translucent overlays (the sprocket mask, the clipping tint), which need the backdrop.
    /// </summary>
    private static void CompositeScaledOpaqueOverlay(
        Half[] output,
        PixelSize outputSize,
        PresentationOverlay overlay,
        float[] source,
        SamplingCoordinate[] horizontalCoordinates,
        int left,
        int top,
        int right,
        int bottom)
    {
        PixelRect destination = overlay.Destination;
        int sourceWidth = overlay.Scene.Size.Width;
        int sourceHeight = overlay.Scene.Size.Height;
        int outputWidth = outputSize.Width;

        RunRows(top, bottom, right - left, y =>
        {
            SamplingCoordinate vertical = GetSamplingCoordinate(
                y, destination.Y, destination.Height, sourceHeight);
            int rowLower = vertical.Lower * sourceWidth;
            int rowUpper = vertical.Upper * sourceWidth;
            double vf = vertical.Fraction;
            int componentOffset = ((y * outputWidth) + left) * 4;
            for (int x = left; x < right; x++, componentOffset += 4)
            {
                SamplingCoordinate horizontal = horizontalCoordinates[x - left];
                int tl = (rowLower + horizontal.Lower) * 4;
                int tr = (rowLower + horizontal.Upper) * 4;
                int bl = (rowUpper + horizontal.Lower) * 4;
                int br = (rowUpper + horizontal.Upper) * 4;
                double hf = horizontal.Fraction;

                for (int c = 0; c < 4; c++)
                {
                    double topLeft = source[tl + c];
                    double bottomLeft = source[bl + c];
                    double topValue = topLeft + ((source[tr + c] - topLeft) * hf);
                    double bottomValue = bottomLeft + ((source[br + c] - bottomLeft) * hf);
                    output[componentOffset + c] = ToHalf(topValue + ((bottomValue - topValue) * vf));
                }
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

        if (primitive is PresentationSolidRect solidRect)
        {
            CompositeSolidRect(output, outputSize, solidRect, color, left, top, right, bottom);
            return;
        }

        RunRows(top, bottom, right - left, ParallelPrimitiveThreshold, y =>
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

    /// <summary>
    /// Solid rectangle: coverage is separable, so the horizontal factor is computed once per
    /// column and the vertical factor once per row, instead of both per pixel through the
    /// primitive-type dispatch. The product is the same two <see cref="IntervalCoverage"/> values
    /// multiplied in the same order as <see cref="RectangleCoverage"/>, so every pixel is
    /// bitwise what the general path writes.
    ///
    /// This is the primitive the crop tool leans on: four dimming panes that between them cover
    /// most of the image, re-rasterised on every pointer move of a handle. Through the general
    /// path they measured 50–90 ms of a 1900×1000 viewport's compose; that was most of why the
    /// crop frame lagged the pointer.
    /// </summary>
    private static void CompositeSolidRect(
        Half[] output,
        PixelSize outputSize,
        PresentationSolidRect solid,
        PremultipliedLinearRgba color,
        int left,
        int top,
        int right,
        int bottom)
    {
        PreviewRect bounds = solid.Bounds;
        var horizontal = new double[right - left];
        for (int x = left; x < right; x++)
            horizontal[x - left] = IntervalCoverage(bounds.X, bounds.Right, x);

        RunRows(top, bottom, right - left, ParallelPrimitiveThreshold, y =>
        {
            double vertical = IntervalCoverage(bounds.Y, bounds.Bottom, y);
            if (vertical <= 0d) return;
            int rowOffset = y * outputSize.Width;
            for (int x = left; x < right; x++)
            {
                double coverage = horizontal[x - left] * vertical;
                if (coverage <= 0d) continue;

                Rgba source = new(
                    color.Red * coverage,
                    color.Green * coverage,
                    color.Blue * coverage,
                    color.Alpha * coverage);
                int componentOffset = (rowOffset + x) * 4;
                Rgba backdrop = Read(output, componentOffset);
                Write(output, componentOffset, SourceOver(source, backdrop));
            }
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
        => RunRows(top, bottom, rowWidth, ParallelPixelThreshold, action);

    private static void RunRows(int top, int bottom, int rowWidth, long parallelThreshold, Action<int> action)
    {
        long work = checked((long)(bottom - top) * rowWidth);
        if (Environment.ProcessorCount > 1 && work >= parallelThreshold)
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
        float[] source,
        PixelSize sourceSize,
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
            sourceSize.Width);
        SamplingCoordinate vertical = GetSamplingCoordinate(
            outputY,
            destinationY,
            destinationHeight,
            sourceSize.Height);
        return SampleMapped(source, sourceSize.Width, horizontal, vertical);
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
        float[] source,
        int sourceWidth,
        SamplingCoordinate horizontal,
        SamplingCoordinate vertical)
    {
        Rgba topLeft = Read(
            source,
            ((vertical.Lower * sourceWidth) + horizontal.Lower) * 4);
        Rgba topRight = Read(
            source,
            ((vertical.Lower * sourceWidth) + horizontal.Upper) * 4);
        Rgba bottomLeft = Read(
            source,
            ((vertical.Upper * sourceWidth) + horizontal.Lower) * 4);
        Rgba bottomRight = Read(
            source,
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

    private static Rgba Read(float[] values, int offset) => new(
        values[offset],
        values[offset + 1],
        values[offset + 2],
        values[offset + 3]);

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
