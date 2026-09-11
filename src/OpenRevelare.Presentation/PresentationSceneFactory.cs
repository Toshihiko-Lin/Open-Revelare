namespace OpenRevelare.Presentation;

/// <summary>Platform-neutral admission helpers for sRGB-authored UI pixels and colors.</summary>
public static class PresentationSceneFactory
{
    private static readonly float[] SrgbDecodeTable = BuildSrgbDecodeTable();

    /// <summary>
    /// Decodes unpremultiplied IEC sRGB RGBA8 into the canonical linear, premultiplied RGBA-half
    /// scene encoding. The reference-white scale is metadata here and is applied later by
    /// <see cref="PresentationBufferBuilder"/>.
    /// </summary>
    public static PresentationScene FromSrgbRgba8(
        ReadOnlySpan<byte> unpremultipliedRgba,
        PixelSize size,
        float referenceWhiteScale)
    {
        int requiredLength = size.RequiredByteCount(bytesPerPixel: 4);
        if (unpremultipliedRgba.Length != requiredLength)
        {
            throw new ArgumentException(
                $"Unpremultiplied RGBA8 requires exactly {requiredLength} bytes for " +
                $"{size.Width}x{size.Height}; received {unpremultipliedRgba.Length}.",
                nameof(unpremultipliedRgba));
        }

        var canonical = new Half[requiredLength];
        for (int offset = 0; offset < requiredLength; offset += 4)
        {
            float alpha = unpremultipliedRgba[offset + 3] / 255f;
            if (alpha != 0f)
            {
                canonical[offset] = (Half)(DecodeSrgb(unpremultipliedRgba[offset]) * alpha);
                canonical[offset + 1] = (Half)(DecodeSrgb(unpremultipliedRgba[offset + 1]) * alpha);
                canonical[offset + 2] = (Half)(DecodeSrgb(unpremultipliedRgba[offset + 2]) * alpha);
            }
            // The array starts at exact zero, so fully transparent source RGB is deliberately
            // discarded rather than leaking non-premultiplied color into the canonical scene.
            canonical[offset + 3] = (Half)alpha;
        }

        return PresentationScene.FromOwnedPixels(canonical, size, referenceWhiteScale);
    }

    /// <summary>Creates one canonical 1x1 scene from an unpremultiplied sRGB RGBA color.</summary>
    public static PresentationScene SolidSrgb(
        byte r,
        byte g,
        byte b,
        byte a,
        float referenceWhiteScale)
    {
        Span<byte> pixel = stackalloc byte[] { r, g, b, a };
        return FromSrgbRgba8(pixel, new PixelSize(1, 1), referenceWhiteScale);
    }

    private static float DecodeSrgb(byte value) => SrgbDecodeTable[value];

    private static float[] BuildSrgbDecodeTable()
    {
        var table = new float[256];
        for (int value = 0; value < table.Length; value++)
        {
            float encoded = value / 255f;
            table[value] = encoded <= 0.04045f
                ? encoded / 12.92f
                : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }
        return table;
    }
}
