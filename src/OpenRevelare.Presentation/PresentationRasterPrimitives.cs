namespace OpenRevelare.Presentation;

/// <summary>
/// A finite premultiplied RGBA color in the canonical linear extended-sRGB scene space.
/// RGB may be negative or greater than one. Alpha is constrained to [0,1], and zero alpha must
/// carry zero RGB so source-over remains well-defined.
/// </summary>
public readonly record struct PremultipliedLinearRgba
{
    public float Red { get; }
    public float Green { get; }
    public float Blue { get; }
    public float Alpha { get; }

    public PremultipliedLinearRgba(float red, float green, float blue, float alpha)
    {
        if (!float.IsFinite(red) || !float.IsFinite(green) ||
            !float.IsFinite(blue) || !float.IsFinite(alpha))
        {
            throw new ArgumentException("Premultiplied-linear color components must be finite.");
        }
        if (alpha is < 0f or > 1f)
            throw new ArgumentOutOfRangeException(nameof(alpha), "Alpha must be in [0,1].");
        if (alpha == 0f && (red != 0f || green != 0f || blue != 0f))
        {
            throw new ArgumentException(
                "A zero-alpha premultiplied-linear color must have zero RGB.");
        }

        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }
}

/// <summary>
/// Base type for deterministic CPU-rasterized primitives. Coordinates and stroke widths are
/// physical output pixels, never logical pixels or image-normalized units.
/// </summary>
public abstract class PresentationRasterPrimitive
{
    public PremultipliedLinearRgba Color { get; }

    private protected PresentationRasterPrimitive(PremultipliedLinearRgba color)
    {
        Color = color;
    }
}

/// <summary>
/// A solid axis-aligned rectangle. Fractional edges use exact pixel-area coverage before the
/// premultiplied-linear color is source-over composited.
/// </summary>
public sealed class PresentationSolidRect : PresentationRasterPrimitive
{
    public PreviewRect Bounds { get; }

    public PresentationSolidRect(PreviewRect bounds, PremultipliedLinearRgba color)
        : base(color)
    {
        RequireBounds(bounds);
        Bounds = bounds;
    }

    private static void RequireBounds(PreviewRect bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
            bounds.Width <= 0d || bounds.Height <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Rectangle bounds must be finite and non-empty.");
        }
    }
}

/// <summary>
/// An axis-aligned rectangle outline whose stroke is centered on <see cref="Bounds"/>. Raster
/// coverage is the exact pixel-area difference between its outer and inner rectangles, producing
/// deterministic square corners without over-blending the four sides.
/// </summary>
public sealed class PresentationRectOutline : PresentationRasterPrimitive
{
    public PreviewRect Bounds { get; }
    public double StrokeWidth { get; }

    public PresentationRectOutline(
        PreviewRect bounds,
        double strokeWidth,
        PremultipliedLinearRgba color)
        : base(color)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom) ||
            bounds.Width <= 0d || bounds.Height <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Rectangle bounds must be finite and non-empty.");
        }
        RequireStroke(strokeWidth);
        double half = strokeWidth / 2d;
        if (!double.IsFinite(bounds.X - half) || !double.IsFinite(bounds.Y - half) ||
            !double.IsFinite(bounds.Right + half) || !double.IsFinite(bounds.Bottom + half) ||
            !double.IsFinite(bounds.Width + strokeWidth) ||
            !double.IsFinite(bounds.Height + strokeWidth))
        {
            throw new ArgumentOutOfRangeException(
                nameof(strokeWidth),
                "Outlined rectangle and stroke must have finite outer edges.");
        }

        Bounds = bounds;
        StrokeWidth = strokeWidth;
    }

    private static void RequireStroke(double strokeWidth)
    {
        if (!double.IsFinite(strokeWidth) || strokeWidth <= 0d)
            throw new ArgumentOutOfRangeException(nameof(strokeWidth), "Stroke width must be finite and positive.");
    }
}

/// <summary>
/// A finite line segment with round caps. Coverage is a deterministic one-physical-pixel analytic
/// ramp around the requested stroke, so fractional placement cannot make a thin line disappear.
/// </summary>
public sealed class PresentationLine : PresentationRasterPrimitive
{
    public PreviewPoint Start { get; }
    public PreviewPoint End { get; }
    public double StrokeWidth { get; }

    public PresentationLine(
        PreviewPoint start,
        PreviewPoint end,
        double strokeWidth,
        PremultipliedLinearRgba color)
        : base(color)
    {
        if (!double.IsFinite(strokeWidth) || strokeWidth <= 0d)
            throw new ArgumentOutOfRangeException(nameof(strokeWidth), "Stroke width must be finite and positive.");

        double extent = (strokeWidth / 2d) + 0.5d;
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) ||
            !double.IsFinite((deltaX * deltaX) + (deltaY * deltaY)) ||
            !double.IsFinite(Math.Min(start.X, end.X) - extent) ||
            !double.IsFinite(Math.Min(start.Y, end.Y) - extent) ||
            !double.IsFinite(Math.Max(start.X, end.X) + extent) ||
            !double.IsFinite(Math.Max(start.Y, end.Y) + extent))
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "Line segment and stroke must have finite raster bounds.");
        }

        Start = start;
        End = end;
        StrokeWidth = strokeWidth;
    }
}
