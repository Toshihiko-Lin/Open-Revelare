using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Interop;

/// <summary>
/// Turns a Core <see cref="ImageBuffer"/> into an Avalonia bitmap for on-screen display.
///
/// The buffer handed in must already be sRGB-encoded [0,1] — i.e. the output of
/// <c>Pipeline.ProcessFrame</c> with BASIC intent, which applies the sRGB exit TRC.
/// We only quantise to 8-bit BGRA here; no colour maths happens in the view layer.
/// </summary>
public static class BitmapConvert
{
    /// <summary>
    /// Soft-proof target: when set, every preview is re-rendered through this space and back to
    /// sRGB, so the screen shows what exporting to it would look like. Null = off, the direct
    /// path, which is what the app has always done.
    ///
    /// A static rather than a parameter because it must apply to every preview surface at once —
    /// main view, thumbnails, sharp patches — and threading it through eight call sites would
    /// invite the one that got missed. It is read on render threads and written from the UI
    /// thread; a torn read is impossible for a reference-sized field and the worst case is one
    /// frame rendered against the previous setting, which the follow-up render corrects.
    /// </summary>
    public static ColorSpaceDef? SoftProof;

    /// <summary>sRGB-encoded [0,1] interleaved RGB → 8-bit Bgra8888 WriteableBitmap.
    /// Safe to call off the UI thread (a WriteableBitmap is not a control).</summary>
    public static WriteableBitmap ToBitmap(ImageBuffer srgb)
    {
        if (SoftProof is ColorSpaceDef proof)
        {
            // Round-trip in LINEAR light: decode once, sRGB → target (gamut-mapped) → back to
            // sRGB, encode once. The outbound leg discards what the target cannot hold; the
            // return leg puts the survivors back on a screen that assumes sRGB. Going through
            // the encoded helpers instead would apply the TRC twice.
            var copy = (float[])srgb.Data.Clone();
            Srgb.ApplyInverseInPlace(copy);
            OutputRender.Convert(copy, ColorSpaces.Srgb, proof, GamutMapping.Desaturate);
            OutputRender.Convert(copy, proof, ColorSpaces.Srgb, GamutMapping.Desaturate);
            Srgb.ApplyForwardInPlace(copy);
            srgb = new ImageBuffer(srgb.Width, srgb.Height, copy);
        }

        int w = srgb.Width, h = srgb.Height;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using ILockedFramebuffer fb = bmp.Lock();
        float[] d = srgb.Data;
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                int di = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    row[x * 4 + 0] = To8(d[di + 2]);   // B
                    row[x * 4 + 1] = To8(d[di + 1]);   // G
                    row[x * 4 + 2] = To8(d[di + 0]);   // R
                    row[x * 4 + 3] = 255;              // A
                    di += 3;
                }
            }
        }
        return bmp;
    }

    /// <summary>Boolean mask → translucent-red Bgra8888 overlay (masked = red, else transparent).</summary>
    public static WriteableBitmap ToMaskOverlay(bool[] mask, int w, int h)
        => ToMaskOverlay(mask, w, h, r: 230, g: 0, b: 0, a: 140);

    /// <summary>Boolean mask → translucent colour Bgra8888 overlay (masked = colour, else transparent).</summary>
    public static WriteableBitmap ToMaskOverlay(bool[] mask, int w, int h, byte r, byte g, byte b, byte a)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer fb = bmp.Lock();
        unsafe
        {
            byte* basePtr = (byte*)fb.Address;
            int stride = fb.RowBytes;
            for (int y = 0; y < h; y++)
            {
                byte* row = basePtr + y * stride;
                int mi = y * w;
                for (int x = 0; x < w; x++)
                {
                    bool on = mask[mi + x];
                    row[x * 4 + 0] = on ? b : (byte)0;
                    row[x * 4 + 1] = on ? g : (byte)0;
                    row[x * 4 + 2] = on ? r : (byte)0;
                    row[x * 4 + 3] = on ? a : (byte)0;
                }
            }
        }
        return bmp;
    }

    private static byte To8(float v)
    {
        float s = v * 255.0f + 0.5f;
        return s <= 0f ? (byte)0 : s >= 255f ? (byte)255 : (byte)s;
    }
}
