using System.Runtime.InteropServices;
using OpenRevelare.Core;
using SkiaSharp;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The preview's colour-space bridge: the maths that hands a <see cref="ColorSpaceDef"/> to Skia
/// so the on-screen bitmap carries its space instead of being read as sRGB by default.
///
/// WHAT THESE GUARD. Avalonia cannot express a bitmap's colour space (AvaloniaUI/Avalonia#8450,
/// #14599), so the preview used to reach the compositor untagged and be interpreted as sRGB.
/// Correct for an sRGB roll, wrong for every other one — and worst on macOS, whose compositor acts
/// on that assumption and converts to the panel profile, so a Display P3 render was read as sRGB
/// and expanded a SECOND time. The fix describes the space to Skia; these tests check the
/// description is right, by comparing against the values Skia itself ships for the spaces it
/// happens to know.
///
/// Testing against Skia's own constants rather than a table copied into the test is the point: a
/// transcription error would agree with a transcribed expectation and disagree with reality.
/// </summary>
public class PreviewColorSpaceTests
{
    /// <summary>
    /// Skia's matrices are D50-adapted, ICC-PCS style. Ours must land on the same numbers, or the
    /// preview describes different primaries than the embedded profile does and the two disagree
    /// about what the pixels mean.
    ///
    /// The tolerance is 5e-4: Skia stores these as float32 constants rounded for publication, so
    /// exact equality is not available — but 5e-4 is far tighter than any visible difference and
    /// would still catch a wrong white point (which moves digits in the second decimal place).
    /// </summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    public void D50_matrix_matches_Skia(string name)
    {
        ColorSpaceDef space = ColorSpaces.All[name];
        double[,] ours = ColorSpaces.ToXyzD50(space);

        SKColorSpaceXyz theirs = name switch
        {
            "sRGB" => SKColorSpaceXyz.Srgb,
            "DisplayP3" => SKColorSpaceXyz.DisplayP3,
            _ => SKColorSpaceXyz.AdobeRgb,
        };

        float[] flat = theirs.Values;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.True(Math.Abs(ours[r, c] - flat[r * 3 + c]) < 5e-4,
                    $"{name}[{r},{c}]: ours {ours[r, c]:F6}, Skia {flat[r * 3 + c]:F6}");
    }

    /// <summary>
    /// sRGB's piecewise TRC, in the seven-parameter form, must be the standard's curve and not a
    /// power approximation of it. Skia ships the same curve as a constant, so this compares
    /// against that.
    ///
    /// This is the conflation the codebase has already paid for once: treating sRGB's curve as a
    /// 2.2 (or 2.4) power crushes the shadows, which is exactly where the linear toe lives. See
    /// the remarks on <see cref="TransferFunction"/>.
    /// </summary>
    [Fact]
    public void Srgb_transfer_is_the_piecewise_curve_Skia_ships()
    {
        double[] ours = ColorSpaces.TransferParameters(ColorSpaces.Srgb);
        float[] theirs = SKColorSpaceTransferFn.Srgb.Values;

        for (int i = 0; i < 7; i++)
            Assert.True(Math.Abs(ours[i] - theirs[i]) < 1e-6,
                $"param {i}: ours {ours[i]:F8}, Skia {theirs[i]:F8}");
    }

    /// <summary>
    /// Display P3 carries sRGB's curve — that is its definition, wide primaries over the sRGB TRC.
    /// It differs from sRGB in the matrix only, which the matrix test above covers.
    /// </summary>
    [Fact]
    public void DisplayP3_shares_sRGBs_curve()
        => Assert.Equal(ColorSpaces.TransferParameters(ColorSpaces.Srgb),
                        ColorSpaces.TransferParameters(ColorSpaces.DisplayP3));

    /// <summary>
    /// A pure-power space is described as one: d = 0 sends the whole domain through the power
    /// branch, so the linear-segment parameters are never consulted. Rec709 carries BT.1886's 2.4
    /// at FULL precision here, unlike the ICC 'curv' tag, which can only hold u8Fixed8 and rounds
    /// it to 2.3984375.
    /// </summary>
    [Theory]
    [InlineData("Rec709", 2.4)]
    [InlineData("AdobeRGB", 563.0 / 256.0)]
    public void Power_spaces_declare_their_exponent_exactly(string name, double gamma)
    {
        double[] p = ColorSpaces.TransferParameters(ColorSpaces.All[name]);
        Assert.Equal(gamma, p[0], 10);   // g
        Assert.Equal(1.0, p[1], 10);     // a
        Assert.Equal(0.0, p[4], 10);     // d — no linear segment
    }

    /// <summary>
    /// ACEScg is scene-linear, so its curve is the identity. Declaring a gamma here would apply a
    /// curve to data that has none.
    /// </summary>
    [Fact]
    public void AcesCg_is_linear()
    {
        double[] p = ColorSpaces.TransferParameters(ColorSpaces.AcesCg);
        Assert.Equal(1.0, p[0], 10);
        Assert.Equal(1.0, p[1], 10);
    }

    /// <summary>
    /// The end-to-end claim, and the one the user actually sees: a colour rendered in ANY output
    /// space, described to Skia and converted to sRGB, lands where converting it through the
    /// pipeline's own maths would land.
    ///
    /// This is what "the preview matches the export" reduces to. Before the fix the preview did no
    /// conversion at all — P3 numbers were shown as though they were sRGB — so a mid saturated red
    /// sat visibly off. The comparison is against <see cref="OutputRender"/>, i.e. against the same
    /// code that produced the exported file.
    /// </summary>
    [Theory]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    [InlineData("Rec709")]
    public void Skia_conversion_to_sRGB_agrees_with_the_pipeline(string name)
    {
        ColorSpaceDef space = ColorSpaces.All[name];

        // A saturated mid red: in gamut for all three spaces, and far enough from neutral that a
        // missing matrix shows up immediately.
        var encoded = new[] { 0.75f, 0.25f, 0.20f };

        // What the pipeline says this colour is, once carried into sRGB.
        float[] expected = (float[])encoded.Clone();
        OutputRender.Decode(expected, space);
        OutputRender.Convert(expected, space, ColorSpaces.Srgb, GamutMapping.Clip);
        OutputRender.Encode(expected, ColorSpaces.Srgb);

        // What Skia does with the same colour, given our description of the space.
        float[] actual = ThroughSkia(encoded, space, ColorSpaces.Srgb);

        for (int i = 0; i < 3; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < 3.0f / 255.0f,
                $"{name} channel {i}: pipeline {expected[i]:F4}, Skia {actual[i]:F4}");
    }

    /// <summary>
    /// Rendering one 8-bit pixel from <paramref name="from"/> into <paramref name="to"/> the way
    /// the preview does: build an image that DECLARES its space, draw it into a surface that
    /// declares the destination, read back what landed.
    ///
    /// RAW BYTES, never SKBitmap.SetPixel or Canvas.Clear. Those take an <c>SKColor</c>, which
    /// Skia treats as sRGB and converts INTO the bitmap's space on the way in — so writing a
    /// colour that way and reading it back cancels out, and the pixel appears to survive a
    /// conversion untouched no matter how the spaces are described. That is a property of the
    /// convenience setters, not of the conversion, and it silently makes this test vacuous. The
    /// preview path itself copies raw bytes (Bitmap.CopyPixels), which is what is modelled here.
    /// </summary>
    private static float[] ThroughSkia(float[] encoded, ColorSpaceDef from, ColorSpaceDef to)
    {
        using SKColorSpace src = Make(from);
        using SKColorSpace dst = Make(to);

        var srcInfo = new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul, src);
        byte[] inBytes = { To8(encoded[0]), To8(encoded[1]), To8(encoded[2]), 255 };

        GCHandle pin = GCHandle.Alloc(inBytes, GCHandleType.Pinned);
        SKImage srcImage;
        try { srcImage = SKImage.FromPixelCopy(srcInfo, pin.AddrOfPinnedObject(), 4); }
        finally { pin.Free(); }

        var dstInfo = new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul, dst);
        using (srcImage)
        using (SKSurface surface = SKSurface.Create(dstInfo))
        {
            surface.Canvas.DrawImage(srcImage, 0, 0);
            surface.Canvas.Flush();

            byte[] outBytes = new byte[4];
            GCHandle outPin = GCHandle.Alloc(outBytes, GCHandleType.Pinned);
            try
            {
                Assert.True(surface.ReadPixels(dstInfo, outPin.AddrOfPinnedObject(), 4, 0, 0),
                            "could not read the converted pixel back");
            }
            finally { outPin.Free(); }

            return new[] { outBytes[0] / 255.0f, outBytes[1] / 255.0f, outBytes[2] / 255.0f };
        }
    }

    /// <summary>Mirrors Gui/Interop/SkiaColorSpace.cs. Duplicated rather than referenced because
    /// the test project deliberately does not depend on the GUI (and so on Avalonia); the maths it
    /// exercises lives in Core, which is the part that can actually be wrong.</summary>
    private static SKColorSpace Make(ColorSpaceDef space)
    {
        double[] t = ColorSpaces.TransferParameters(space);
        double[,] m = ColorSpaces.ToXyzD50(space);
        return SKColorSpace.CreateRgb(
            new SKColorSpaceTransferFn((float)t[0], (float)t[1], (float)t[2], (float)t[3],
                                       (float)t[4], (float)t[5], (float)t[6]),
            new SKColorSpaceXyz((float)m[0, 0], (float)m[0, 1], (float)m[0, 2],
                                (float)m[1, 0], (float)m[1, 1], (float)m[1, 2],
                                (float)m[2, 0], (float)m[2, 1], (float)m[2, 2]));
    }

    private static byte To8(float v) => (byte)Math.Clamp(v * 255.0f + 0.5f, 0.0f, 255.0f);
}
