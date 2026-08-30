namespace OpenRevelare.Presentation;

/// <summary>A finite point used by the platform-neutral preview geometry.</summary>
public readonly record struct PreviewPoint
{
    public double X { get; }
    public double Y { get; }

    public PreviewPoint(double x, double y)
    {
        RequireFinite(x, nameof(x));
        RequireFinite(y, nameof(y));
        X = x;
        Y = y;
    }

    private static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Coordinate must be finite.");
    }
}

/// <summary>A finite, non-empty size expressed in device-independent logical pixels.</summary>
public readonly record struct PreviewSize
{
    public double Width { get; }
    public double Height { get; }

    public PreviewSize(double width, double height)
    {
        RequireFinitePositive(width, nameof(width));
        RequireFinitePositive(height, nameof(height));
        Width = width;
        Height = height;
    }

    private static void RequireFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
            throw new ArgumentOutOfRangeException(parameterName, "Dimension must be finite and positive.");
    }
}

/// <summary>
/// A finite, non-empty rectangle. Its units are stated by the API that returns or accepts it.
/// </summary>
public readonly record struct PreviewRect
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public double Right => X + Width;
    public double Bottom => Y + Height;

    public PreviewRect(double x, double y, double width, double height)
    {
        RequireFinite(x, nameof(x));
        RequireFinite(y, nameof(y));
        RequireFinitePositive(width, nameof(width));
        RequireFinitePositive(height, nameof(height));

        double right = x + width;
        double bottom = y + height;
        if (!double.IsFinite(right) || !double.IsFinite(bottom))
            throw new ArgumentOutOfRangeException(nameof(width), "Rectangle edges must be finite.");

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    private static void RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Coordinate must be finite.");
    }

    private static void RequireFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0d)
            throw new ArgumentOutOfRangeException(parameterName, "Dimension must be finite and positive.");
    }
}

/// <summary>
/// Immutable mapping between one image, a logical viewport, and its integer physical-pixel surface.
///
/// <para>
/// The viewport dimensions are multiplied by <see cref="RenderScaling"/> and rounded to the
/// nearest integer with midpoint values rounded away from zero. A positive logical extent that
/// rounds below one is promoted to one pixel. All subsequent fit, letterbox, zoom, pan, ROI, and
/// normalized-coordinate calculations use that rounded physical extent as their single authority.
/// </para>
///
/// <para>
/// <see cref="Zoom"/> multiplies fit: one means fit-to-viewport. Pan is supplied in logical pixels,
/// converted without quantizing, and then clamped so an overflowing image cannot expose a gutter;
/// an image axis which fits is centered. Normalized coordinates are relative to the displayed image
/// before clipping, so the forward and inverse transforms remain exact even outside [0,1].
/// </para>
/// </summary>
public sealed class PreviewViewportGeometry
{
    public PixelSize ImageSize { get; }
    public PreviewSize ViewportLogicalSize { get; }
    public PixelSize ViewportPhysicalSize { get; }
    public double RenderScaling { get; }

    /// <summary>Physical output pixels per image pixel at fit.</summary>
    public double FitScale { get; }

    /// <summary>
    /// Smallest useful fit multiplier. It permits physical 1:1 when fit would otherwise upscale
    /// a small image, while preventing empty gutters when fit already downsizes it.
    /// </summary>
    public double MinimumZoom { get; }

    /// <summary>The effective fit multiplier after applying <see cref="MinimumZoom"/>.</summary>
    public double Zoom { get; }

    /// <summary>Clamped translation in physical output pixels.</summary>
    public PreviewPoint PanPhysical { get; }

    /// <summary>Clamped translation converted back to logical pixels.</summary>
    public PreviewPoint PanLogical => new(PanPhysical.X / RenderScaling, PanPhysical.Y / RenderScaling);

    /// <summary>The centered fit rectangle before zoom and pan, in physical pixels.</summary>
    public PreviewRect LetterboxPhysical { get; }

    /// <summary>The final image rectangle after zoom and pan, in physical pixels.</summary>
    public PreviewRect DisplayedImagePhysical { get; }

    public PreviewViewportGeometry(
        PixelSize imageSize,
        PreviewSize viewportLogicalSize,
        double renderScaling,
        double zoom = 1d,
        PreviewPoint requestedPanLogical = default)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageSize), "Image size must be non-empty.");
        if (viewportLogicalSize.Width <= 0d || viewportLogicalSize.Height <= 0d)
            throw new ArgumentOutOfRangeException(nameof(viewportLogicalSize), "Viewport must be non-empty.");
        if (!double.IsFinite(renderScaling) || renderScaling <= 0d)
            throw new ArgumentOutOfRangeException(nameof(renderScaling), "Render scaling must be finite and positive.");
        if (!double.IsFinite(zoom) || zoom <= 0d)
            throw new ArgumentOutOfRangeException(nameof(zoom), "Zoom must be finite and positive.");
        if (!double.IsFinite(requestedPanLogical.X) || !double.IsFinite(requestedPanLogical.Y))
            throw new ArgumentOutOfRangeException(nameof(requestedPanLogical), "Pan must be finite.");

        ImageSize = imageSize;
        ViewportLogicalSize = viewportLogicalSize;
        RenderScaling = renderScaling;
        ViewportPhysicalSize = new PixelSize(
            RoundPositiveLogicalDimension(viewportLogicalSize.Width, renderScaling, nameof(viewportLogicalSize)),
            RoundPositiveLogicalDimension(viewportLogicalSize.Height, renderScaling, nameof(viewportLogicalSize)));

        FitScale = Math.Min(
            ViewportPhysicalSize.Width / (double)imageSize.Width,
            ViewportPhysicalSize.Height / (double)imageSize.Height);
        MinimumZoom = FitScale > 1d ? 1d / FitScale : 1d;
        Zoom = Math.Max(zoom, MinimumZoom);

        double fittedWidth = imageSize.Width * FitScale;
        double fittedHeight = imageSize.Height * FitScale;
        LetterboxPhysical = new PreviewRect(
            (ViewportPhysicalSize.Width - fittedWidth) / 2d,
            (ViewportPhysicalSize.Height - fittedHeight) / 2d,
            fittedWidth,
            fittedHeight);

        PreviewPoint requestedPanPhysical = LogicalToPhysical(requestedPanLogical);
        PanPhysical = new PreviewPoint(
            ClampPanAxis(
                requestedPanPhysical.X,
                LetterboxPhysical.X,
                LetterboxPhysical.Width,
                ViewportPhysicalSize.Width,
                Zoom),
            ClampPanAxis(
                requestedPanPhysical.Y,
                LetterboxPhysical.Y,
                LetterboxPhysical.Height,
                ViewportPhysicalSize.Height,
                Zoom));

        DisplayedImagePhysical = new PreviewRect(
            (LetterboxPhysical.X * Zoom) + PanPhysical.X,
            (LetterboxPhysical.Y * Zoom) + PanPhysical.Y,
            LetterboxPhysical.Width * Zoom,
            LetterboxPhysical.Height * Zoom);
    }

    /// <summary>Convert a logical point to continuous physical-pixel coordinates.</summary>
    public PreviewPoint LogicalToPhysical(PreviewPoint logical) => new(
        logical.X * RenderScaling,
        logical.Y * RenderScaling);

    /// <summary>Convert a continuous physical-pixel point back to logical coordinates.</summary>
    public PreviewPoint PhysicalToLogical(PreviewPoint physical) => new(
        physical.X / RenderScaling,
        physical.Y / RenderScaling);

    /// <summary>Map an image-normalized point to continuous physical-pixel coordinates.</summary>
    public PreviewPoint NormalizedToPhysical(PreviewPoint normalized) => new(
        DisplayedImagePhysical.X + (normalized.X * DisplayedImagePhysical.Width),
        DisplayedImagePhysical.Y + (normalized.Y * DisplayedImagePhysical.Height));

    /// <summary>Map a continuous physical-pixel point to image-normalized coordinates.</summary>
    public PreviewPoint PhysicalToNormalized(PreviewPoint physical) => new(
        (physical.X - DisplayedImagePhysical.X) / DisplayedImagePhysical.Width,
        (physical.Y - DisplayedImagePhysical.Y) / DisplayedImagePhysical.Height);

    /// <summary>Map an image-normalized rectangle to continuous physical-pixel coordinates.</summary>
    public PreviewRect NormalizedToPhysical(PreviewRect normalized) => new(
        DisplayedImagePhysical.X + (normalized.X * DisplayedImagePhysical.Width),
        DisplayedImagePhysical.Y + (normalized.Y * DisplayedImagePhysical.Height),
        normalized.Width * DisplayedImagePhysical.Width,
        normalized.Height * DisplayedImagePhysical.Height);

    /// <summary>Map a continuous physical-pixel rectangle to image-normalized coordinates.</summary>
    public PreviewRect PhysicalToNormalized(PreviewRect physical) => new(
        (physical.X - DisplayedImagePhysical.X) / DisplayedImagePhysical.Width,
        (physical.Y - DisplayedImagePhysical.Y) / DisplayedImagePhysical.Height,
        physical.Width / DisplayedImagePhysical.Width,
        physical.Height / DisplayedImagePhysical.Height);

    /// <summary>
    /// Convert a normalized rectangle to integer physical pixels. Both edges are independently
    /// rounded away from zero, then clipped to the viewport. Adjacent normalized rectangles
    /// therefore share the same rounded edge and cannot introduce a one-pixel seam.
    /// </summary>
    public PixelRect? NormalizedToClippedPixelRect(PreviewRect normalized) =>
        PhysicalToClippedPixelRect(NormalizedToPhysical(normalized));

    /// <summary>
    /// Round both edges of a continuous physical rectangle independently and clip them to the
    /// viewport. Returns null when no pixel remains after rounding and clipping.
    /// </summary>
    public PixelRect? PhysicalToClippedPixelRect(PreviewRect physical)
    {
        int left = RoundAndClampEdge(physical.X, ViewportPhysicalSize.Width);
        int top = RoundAndClampEdge(physical.Y, ViewportPhysicalSize.Height);
        int right = RoundAndClampEdge(physical.Right, ViewportPhysicalSize.Width);
        int bottom = RoundAndClampEdge(physical.Bottom, ViewportPhysicalSize.Height);
        if (right <= left || bottom <= top) return null;
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Return the visible image ROI in normalized coordinates, optionally expanded on every side
    /// by a fraction of the visible width/height. Expansion occurs before clipping to [0,1].
    /// </summary>
    public PreviewRect? VisibleNormalizedRoi(double marginFraction = 0d)
    {
        if (!double.IsFinite(marginFraction) || marginFraction < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(marginFraction),
                "ROI margin must be finite and non-negative.");
        }

        double x0 = (0d - DisplayedImagePhysical.X) / DisplayedImagePhysical.Width;
        double y0 = (0d - DisplayedImagePhysical.Y) / DisplayedImagePhysical.Height;
        double x1 = (ViewportPhysicalSize.Width - DisplayedImagePhysical.X) / DisplayedImagePhysical.Width;
        double y1 = (ViewportPhysicalSize.Height - DisplayedImagePhysical.Y) / DisplayedImagePhysical.Height;

        double marginX = (x1 - x0) * marginFraction;
        double marginY = (y1 - y0) * marginFraction;
        x0 = Math.Clamp(x0 - marginX, 0d, 1d);
        y0 = Math.Clamp(y0 - marginY, 0d, 1d);
        x1 = Math.Clamp(x1 + marginX, 0d, 1d);
        y1 = Math.Clamp(y1 + marginY, 0d, 1d);
        if (x1 <= x0 || y1 <= y0) return null;
        return new PreviewRect(x0, y0, x1 - x0, y1 - y0);
    }

    private static int RoundPositiveLogicalDimension(double logical, double scale, string parameterName)
    {
        double physical = logical * scale;
        if (!double.IsFinite(physical) || physical > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Scaled viewport dimension must fit in a positive Int32 pixel extent.");
        }

        double rounded = Math.Round(physical, MidpointRounding.AwayFromZero);
        return Math.Max(1, checked((int)rounded));
    }

    private static int RoundAndClampEdge(double edge, int maximum)
    {
        double rounded = Math.Round(edge, MidpointRounding.AwayFromZero);
        if (rounded <= 0d) return 0;
        if (rounded >= maximum) return maximum;
        return checked((int)rounded);
    }

    private static double ClampPanAxis(double pan, double offset, double length, int viewport, double zoom)
    {
        double scaledLength = length * zoom;
        if (scaledLength <= viewport)
            return ((viewport - scaledLength) / 2d) - (offset * zoom);

        double minimum = viewport - ((offset + length) * zoom);
        double maximum = -offset * zoom;
        return Math.Clamp(pan, minimum, maximum);
    }
}
