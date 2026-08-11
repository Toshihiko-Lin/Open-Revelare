using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Interop;

/// <summary>
/// Turns a Core <see cref="ImageBuffer"/> into an Avalonia bitmap for on-screen display.
///
/// The buffer handed in is already display-encoded in the ROLL'S OUTPUT SPACE — the render did the
/// step-4 conversion and Stage 2 ran inside that space — so there is no colour maths left to do
/// here. This is pure quantisation to 8-bit BGRA.
///
/// NO DISPLAY MANAGEMENT, deliberately. The bitmap goes to the compositor without a profile and
/// the panel does whatever it does with those numbers. Doing better means what Photoshop and
/// Lightroom do: convert through the OS's registered display profile, which is a calibrator's
/// MEASUREMENT of that individual panel. A dropdown of standard spaces is not that — it is a
/// guess, and a wrong guess is worse than none, because the user then grades against colours the
/// screen is not actually showing. Anyone who needs accuracy calibrates their display.
///
/// Soft proofing also used to live here as a working→target→back round trip, simulating an export
/// the render itself was not doing. It is gone because it stopped being a simulation of anything:
/// the output space is a render parameter now, so the preview shows the real thing.
/// </summary>
public static class BitmapConvert
{
    /// <summary>Display-encoded [0,1] interleaved RGB → 8-bit Bgra8888 WriteableBitmap.
    /// Safe to call off the UI thread (a WriteableBitmap is not a control).</summary>
    public static WriteableBitmap ToBitmap(ImageBuffer srgb)
    {
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
