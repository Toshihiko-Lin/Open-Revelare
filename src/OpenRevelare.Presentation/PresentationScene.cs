using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace OpenRevelare.Presentation;

/// <summary>
/// Immutable D65 linear extended-sRGB pixels in interleaved premultiplied RGBA-half order.
/// Negative RGB and RGB greater than one are valid and retained. Alpha remains finite in [0,1].
/// </summary>
public sealed class PresentationScene
{
    public ImmutableArray<Half> LinearExtendedSrgbRgba { get; }
    public PixelSize Size { get; }
    public float ReferenceWhiteScale { get; }

    public PresentationEncoding Encoding => PresentationEncoding.LinearExtendedSrgbRgba16F;

    /// <summary>
    /// True only when construction or a closed internal operation proved that every alpha is
    /// exactly one. False means "not proven" rather than necessarily translucent.
    /// </summary>
    internal bool IsKnownOpaque { get; }

    public PresentationScene(
        ReadOnlySpan<Half> linearExtendedSrgbRgba,
        PixelSize size,
        float referenceWhiteScale)
        : this(
            linearExtendedSrgbRgba.ToArray(),
            size,
            referenceWhiteScale,
            validatePixels: true,
            knownOpaque: false)
    {
    }

    private PresentationScene(
        Half[] linearExtendedSrgbRgba,
        PixelSize size,
        float referenceWhiteScale,
        bool validatePixels,
        bool knownOpaque)
    {
        ArgumentNullException.ThrowIfNull(linearExtendedSrgbRgba);

        ValidateSize(size);
        if (!float.IsFinite(referenceWhiteScale) || referenceWhiteScale <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(referenceWhiteScale),
                "Reference-white scale must be finite and positive.");
        }

        int requiredLength = checked(size.PixelCount * 4);
        if (linearExtendedSrgbRgba.Length != requiredLength)
        {
            throw new ArgumentException(
                $"RGBA-half scene requires exactly {requiredLength} components for {size.Width}x{size.Height}; received {linearExtendedSrgbRgba.Length}.",
                nameof(linearExtendedSrgbRgba));
        }

        IsKnownOpaque = validatePixels
            ? ValidatePremultipliedPixels(linearExtendedSrgbRgba)
            : knownOpaque;
        LinearExtendedSrgbRgba = ImmutableCollectionsMarshal.AsImmutableArray(linearExtendedSrgbRgba);
        Size = size;
        ReferenceWhiteScale = referenceWhiteScale;
    }

    /// <summary>Shares one scene's validated pixel storage under a different reference-white policy.</summary>
    private PresentationScene(PresentationScene source, float referenceWhiteScale)
    {
        if (!float.IsFinite(referenceWhiteScale) || referenceWhiteScale <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(referenceWhiteScale),
                "Reference-white scale must be finite and positive.");
        }

        LinearExtendedSrgbRgba = source.LinearExtendedSrgbRgba;
        Size = source.Size;
        IsKnownOpaque = source.IsKnownOpaque;
        ReferenceWhiteScale = referenceWhiteScale;
    }

    /// <summary>
    /// Returns these exact pixels tagged with a different reference-white policy.
    ///
    /// <para>
    /// THE PIXELS ARE NOT TOUCHED, AND THAT IS THE POINT. A scene always stores canonical D65
    /// linear extended-sRGB at the carrier's nominal white; <see cref="ReferenceWhiteScale"/>
    /// records which display policy those pixels are destined for, and
    /// <c>PresentationBufferBuilder</c> applies that scale exactly once while packing. Re-tagging
    /// is therefore metadata only, and the immutable storage is shared rather than copied.
    /// </para>
    ///
    /// <para>
    /// This exists because the scale is a property of the DISPLAY, not of the render. The shared
    /// view model builds scenes without knowing which monitor they will land on — §11.1 forbids
    /// platform state from leaking there — while the composition root does know. Dragging a
    /// window from a WCG display to an HDR one changes the contract scale (D-020) without
    /// changing a single rendered pixel, and every scene in one composition must agree on the
    /// policy or the compositor and builder reject the frame.
    /// </para>
    /// </summary>
    public PresentationScene WithReferenceWhiteScale(float referenceWhiteScale) =>
        referenceWhiteScale == ReferenceWhiteScale ? this : new PresentationScene(this, referenceWhiteScale);

    /// <summary>
    /// Adopts a freshly-created array that has never escaped the presentation assembly. Public
    /// callers continue to receive a defensive copy through the public constructor.
    /// </summary>
    internal static PresentationScene FromOwnedPixels(
        Half[] linearExtendedSrgbRgba,
        PixelSize size,
        float referenceWhiteScale) =>
        new(
            linearExtendedSrgbRgba,
            size,
            referenceWhiteScale,
            validatePixels: true,
            knownOpaque: false);

    /// <summary>
    /// Adopts output produced by the closed compositor. Every write there checks finite-half
    /// storage, while valid source scenes and source-over preserve the alpha/premultiplication
    /// invariants. An opaque base also proves the output is exactly opaque.
    /// </summary>
    internal static PresentationScene FromCompositorOwnedPixels(
        Half[] linearExtendedSrgbRgba,
        PixelSize size,
        float referenceWhiteScale,
        bool knownOpaque) =>
        new(
            linearExtendedSrgbRgba,
            size,
            referenceWhiteScale,
            validatePixels: false,
            knownOpaque);

    internal ReadOnlySpan<Half> Pixels => LinearExtendedSrgbRgba.AsSpan();

    private static bool ValidatePremultipliedPixels(ReadOnlySpan<Half> pixels)
    {
        bool opaque = true;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            float red = (float)pixels[i];
            float green = (float)pixels[i + 1];
            float blue = (float)pixels[i + 2];
            float alpha = (float)pixels[i + 3];

            if (!float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue) || !float.IsFinite(alpha))
                throw new ArgumentException($"Pixel {i / 4} contains a non-finite component.", nameof(pixels));
            if (alpha is < 0f or > 1f)
                throw new ArgumentException($"Pixel {i / 4} alpha must be in [0,1].", nameof(pixels));
            if (alpha != 1f)
                opaque = false;
            if (alpha == 0f && (red != 0f || green != 0f || blue != 0f))
            {
                throw new ArgumentException(
                    $"Pixel {i / 4} has non-zero RGB at zero alpha and is not premultiplied.",
                    nameof(pixels));
            }
        }

        return opaque;
    }

    private static void ValidateSize(PixelSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Pixel size must be non-empty.");
    }
}

/// <summary>An integer destination rectangle. It may extend outside the output and is clipped.</summary>
public readonly record struct PixelRect
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public PixelRect(int x, int y, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

/// <summary>
/// One canonical premultiplied-linear scene scaled into a rectangular output region. Layers are
/// composited in list order using source-over.
/// </summary>
public sealed record PresentationOverlay
{
    public PresentationScene Scene { get; }
    public PixelRect Destination { get; }

    public PresentationOverlay(PresentationScene scene, PixelRect destination)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene = scene;
        Destination = destination;
    }
}
