using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace OpenRevelare.Core;

/// <summary>
/// JPEG export via ImageSharp (8-bit sRGB). Port of negative/export.py::export_jpeg.
/// ImageSharp handles 8-bit JPEG cleanly (the 16-bit TIFF limitation that ruled it
/// out for TIFF does not apply here) — TIFF stays on LibTiff, JPEG on ImageSharp,
/// each where it is strong.
///
/// The pipeline hands us data already in the target encoding (sRGB for BASIC), so
/// this only quantises to 8-bit and encodes. 4:4:4 subsampling (no chroma loss).
/// </summary>
public static class JpegIO
{
    /// <summary>Encode to JPEG. Roll annotations are NOT written to EXIF — they are burned into
    /// the contact sheet's info bar instead (see the GUI's SheetInfoBar).</summary>
    public static void ExportJpeg(ImageBuffer img, string path, int quality = 95,
                                  string? description = null)
    {
        int w = img.Width, h = img.Height;
        float[] src = img.Data;
        using var image = new Image<Rgb24>(w, h);

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Software, TiffIO.SoftwareTag);
        if (!string.IsNullOrEmpty(description))
        {
            // ImageDescription (270) is ASCII per spec, so it gets the folded copy and the full
            // Unicode text goes to UserComment (37510) — same split as TiffIO, for the same reason.
            exif.SetValue(ExifTag.ImageDescription, TiffIO.ToAsciiSafe(description));
            exif.SetValue(ExifTag.UserComment,
                new EncodedString(EncodedString.CharacterCode.Unicode, description));
        }
        image.Metadata.ExifProfile = exif;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    int j = o + x * 3;
                    row[x] = new Rgb24(To8(src[j]), To8(src[j + 1]), To8(src[j + 2]));
                }
            }
        });

        var encoder = new JpegEncoder
        {
            Quality = quality,
            ColorType = JpegEncodingColor.YCbCrRatio444, // 4:4:4 — no chroma subsampling
        };
        image.Save(path, encoder);
    }

    private static byte To8(float v)
    {
        float c = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
        return (byte)(c * 255.0f + 0.5f);
    }
}
