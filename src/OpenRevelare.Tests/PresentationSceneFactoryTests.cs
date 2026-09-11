using OpenRevelare.Presentation;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PresentationSceneFactoryTests
{
    [Fact]
    public void FromSrgbRgba8_requires_the_exact_interleaved_byte_length()
    {
        var size = new PixelSize(2, 1);

        ArgumentException shortInput = Assert.Throws<ArgumentException>(() =>
            PresentationSceneFactory.FromSrgbRgba8(new byte[7], size, 1f));
        ArgumentException longInput = Assert.Throws<ArgumentException>(() =>
            PresentationSceneFactory.FromSrgbRgba8(new byte[9], size, 1f));

        Assert.Equal("unpremultipliedRgba", shortInput.ParamName);
        Assert.Equal("unpremultipliedRgba", longInput.ParamName);
        Assert.Contains("exactly 8 bytes", shortInput.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Opaque_RGBA8_is_IEC_sRGB_decoded_into_linear_half_without_applying_scale()
    {
        PresentationScene scene = PresentationSceneFactory.FromSrgbRgba8(
            new byte[] { 0, 128, 255, 255 },
            new PixelSize(1, 1),
            referenceWhiteScale: 1.75f);

        Assert.Equal(new PixelSize(1, 1), scene.Size);
        Assert.Equal(1.75f, scene.ReferenceWhiteScale);
        Assert.Equal(PresentationEncoding.LinearExtendedSrgbRgba16F, scene.Encoding);
        Assert.Equal((Half)0f, scene.LinearExtendedSrgbRgba[0]);
        AssertClose(DecodeReference(128), scene.LinearExtendedSrgbRgba[1]);
        Assert.Equal((Half)1f, scene.LinearExtendedSrgbRgba[2]);
        Assert.Equal((Half)1f, scene.LinearExtendedSrgbRgba[3]);
    }

    [Fact]
    public void Nonopaque_RGBA8_is_decoded_then_premultiplied_by_alpha()
    {
        PresentationScene scene = PresentationSceneFactory.FromSrgbRgba8(
            new byte[] { 255, 128, 64, 128 },
            new PixelSize(1, 1),
            referenceWhiteScale: 1f);
        double alpha = 128.0 / 255.0;

        AssertClose(alpha, scene.LinearExtendedSrgbRgba[0]);
        AssertClose(DecodeReference(128) * alpha, scene.LinearExtendedSrgbRgba[1]);
        AssertClose(DecodeReference(64) * alpha, scene.LinearExtendedSrgbRgba[2]);
        AssertClose(alpha, scene.LinearExtendedSrgbRgba[3]);
    }

    [Fact]
    public void Fully_transparent_source_discards_hidden_sRGB_color_exactly()
    {
        PresentationScene scene = PresentationSceneFactory.FromSrgbRgba8(
            new byte[] { 255, 128, 64, 0 },
            new PixelSize(1, 1),
            referenceWhiteScale: 1f);

        Assert.Equal(
            new Half[] { (Half)0f, (Half)0f, (Half)0f, (Half)0f },
            scene.LinearExtendedSrgbRgba.ToArray());
    }

    [Fact]
    public void Multiple_pixels_remain_RGBA_interleaved_after_decode_and_premultiply()
    {
        PresentationScene scene = PresentationSceneFactory.FromSrgbRgba8(
            new byte[]
            {
                255, 0, 0, 255,
                0, 0, 255, 128,
            },
            new PixelSize(2, 1),
            referenceWhiteScale: 1f);
        double halfAlpha = 128.0 / 255.0;

        Assert.Equal((Half)1f, scene.LinearExtendedSrgbRgba[0]);
        Assert.Equal((Half)0f, scene.LinearExtendedSrgbRgba[1]);
        Assert.Equal((Half)0f, scene.LinearExtendedSrgbRgba[2]);
        Assert.Equal((Half)1f, scene.LinearExtendedSrgbRgba[3]);
        Assert.Equal((Half)0f, scene.LinearExtendedSrgbRgba[4]);
        Assert.Equal((Half)0f, scene.LinearExtendedSrgbRgba[5]);
        AssertClose(halfAlpha, scene.LinearExtendedSrgbRgba[6]);
        AssertClose(halfAlpha, scene.LinearExtendedSrgbRgba[7]);
    }

    [Fact]
    public void SolidSrgb_is_the_same_one_pixel_admission_as_FromSrgbRgba8()
    {
        PresentationScene solid = PresentationSceneFactory.SolidSrgb(
            23,
            117,
            241,
            173,
            referenceWhiteScale: 1.25f);
        PresentationScene explicitPixel = PresentationSceneFactory.FromSrgbRgba8(
            new byte[] { 23, 117, 241, 173 },
            new PixelSize(1, 1),
            referenceWhiteScale: 1.25f);

        Assert.Equal(new PixelSize(1, 1), solid.Size);
        Assert.Equal(1.25f, solid.ReferenceWhiteScale);
        Assert.Equal(
            explicitPixel.LinearExtendedSrgbRgba.ToArray(),
            solid.LinearExtendedSrgbRgba.ToArray());
    }

    private static double DecodeReference(byte value)
    {
        double encoded = value / 255.0;
        return encoded <= 0.04045
            ? encoded / 12.92
            : Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    private static void AssertClose(double expected, Half actual)
    {
        double difference = Math.Abs(expected - (float)actual);
        Assert.True(
            difference <= 0.0006,
            $"expected={expected:R}, actual={(float)actual:R}, abs-error={difference:R}");
    }
}
