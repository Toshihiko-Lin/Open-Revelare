using BitMiracle.LibTiff.Classic;

namespace OpenRevelare.Core;

/// <summary>
/// TIFF load/export via BitMiracle.LibTiff.NET (BSD license — clean for a
/// proprietary product; guaranteed correct 16-bit output, which ImageSharp's TIFF
/// encoder silently downgrades to 8-bit). Phase-1 stand-in for tifffile.
///
/// Load: 8/16-bit, chunky (CONTIG) RGB or grey TIFF -&gt; linear-light f32 image.
/// <paramref name="inputIsSrgb"/> linearises a display-gamma scan; otherwise
/// samples are treated as already linear.
/// Export: f32 image -&gt; 16-bit RGB TIFF (Deflate-compressed). The pipeline hands
/// us data already in the target encoding (sRGB for BASIC, linear for NONE).
/// </summary>
public static class TiffIO
{
    // EXIF UserComment (37510) lives in an EXIF sub-IFD by the spec, so LibTiff refuses
    // SetField for it on the main IFD. tifffile writes it into the main IFD anyway via
    // extratags, and that is what export.py relies on — so mirror it by registering the
    // tag as a custom field. Without this the tag is silently dropped and non-ASCII
    // descriptions (the CJK notes the split exists for) are lost.
    private const TiffTag ExifUserCommentTag = (TiffTag)37510;

    /// <summary>Value of the Software tag (305) on everything we write.</summary>
    public const string SoftwareTag = "OpenRevelare";
    private static Tiff.TiffExtendProc? _parentExtender;
    private static bool _extenderRegistered;

    private static void RegisterUserCommentTag()
    {
        if (_extenderRegistered) return;
        _extenderRegistered = true;
        _parentExtender = Tiff.SetTagExtender(tif =>
        {
            var info = new[]
            {
                new TiffFieldInfo(ExifUserCommentTag, -1, -1, TiffType.UNDEFINED,
                                  FieldBit.Custom, true, true, "EXIFUserComment"),
            };
            tif.MergeFieldInfo(info, info.Length);
            _parentExtender?.Invoke(tif);
        });
    }

    /// <summary>Load a TIFF into a linear-light f32 image.</summary>
    public static ImageBuffer LoadTiff(string path, bool inputIsSrgb)
    {
        using Tiff tif = Tiff.Open(path, "r")
            ?? throw new IOException($"could not open TIFF: {path}");

        int w = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int h = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int bps = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
        int spp = tif.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();

        var planarField = tif.GetField(TiffTag.PLANARCONFIG);
        var planar = planarField != null ? (PlanarConfig)planarField[0].ToInt() : PlanarConfig.CONTIG;
        if (planar != PlanarConfig.CONTIG)
            throw new NotSupportedException("only chunky (CONTIG) TIFF is supported in phase 1");
        if (bps != 8 && bps != 16)
            throw new NotSupportedException($"unsupported BitsPerSample {bps} (need 8 or 16)");
        if (spp < 1)
            throw new NotSupportedException($"unexpected SamplesPerPixel {spp}");

        var data = new float[w * h * 3];
        int scanlineSize = tif.ScanlineSize();
        byte[] buf = new byte[scanlineSize];
        float inv = bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;

        for (int y = 0; y < h; y++)
        {
            if (!tif.ReadScanline(buf, y))
                throw new IOException($"failed reading TIFF scanline {y}");

            int o = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                float r, g, b;
                if (spp >= 3)
                {
                    r = Sample(buf, x * spp + 0, bps) * inv;
                    g = Sample(buf, x * spp + 1, bps) * inv;
                    b = Sample(buf, x * spp + 2, bps) * inv;
                }
                else
                {
                    float grey = Sample(buf, x * spp, bps) * inv; // grey -> replicate
                    r = g = b = grey;
                }

                if (inputIsSrgb)
                {
                    r = Srgb.SrgbToLinear(r);
                    g = Srgb.SrgbToLinear(g);
                    b = Srgb.SrgbToLinear(b);
                }

                int j = o + x * 3;
                data[j] = r; data[j + 1] = g; data[j + 2] = b;
            }
        }

        return new ImageBuffer(w, h, data);
    }

    /// <summary>Compression choice for 16-bit TIFF export.</summary>
    public enum CompressionMode { None, Lzw, Deflate }

    /// <summary>Write a 16-bit RGB TIFF. Data is quantised as-is (already encoded upstream).
    /// Roll annotations are NOT written here — they are burned into the contact sheet's info bar
    /// instead (see the GUI's SheetInfoBar), so exports carry no note-derived EXIF.</summary>
    public static void ExportTiff16(ImageBuffer img, string path, CompressionMode mode = CompressionMode.Lzw,
                                    ColorSpace? iccSpace = null, string? description = null)
    {
        Compression compression = mode switch
        {
            CompressionMode.None => Compression.NONE,
            CompressionMode.Deflate => Compression.DEFLATE,
            _ => Compression.LZW,
        };

        if (description is not null) RegisterUserCommentTag();

        int w = img.Width, h = img.Height;
        using Tiff tif = Tiff.Open(path, "w")
            ?? throw new IOException($"could not create TIFF: {path}");

        tif.SetField(TiffTag.IMAGEWIDTH, w);
        tif.SetField(TiffTag.IMAGELENGTH, h);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, 16);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, compression);
        if (compression == Compression.LZW || compression == Compression.DEFLATE)
            tif.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL); // improves 16-bit compression
        tif.SetField(TiffTag.ROWSPERSTRIP, tif.DefaultStripSize(0));

        if (iccSpace is ColorSpace cs)
        {
            byte[] icc = IccProfiles.Build(cs);
            tif.SetField(TiffTag.ICCPROFILE, icc.Length, icc);
        }
        if (description is not null)
        {
            // TIFF ImageDescription (270) is spec'd 7-bit ASCII, so write an ASCII-safe
            // copy there for broad tool support and the FULL Unicode text in EXIF
            // UserComment (37510, UTF-16) so CJK notes survive losslessly. Same split
            // export.py makes, and for the same reason.
            tif.SetField(TiffTag.IMAGEDESCRIPTION, ToAsciiSafe(description));
            byte[] uc = System.Text.Encoding.ASCII.GetBytes("UNICODE\0")
                .Concat(System.Text.Encoding.Unicode.GetBytes(description)).ToArray();
            tif.SetField(ExifUserCommentTag, uc.Length, uc);
        }
        // The producing application — standard TIFF tag 305, always stamped.
        tif.SetField(TiffTag.SOFTWARE, SoftwareTag);

        float[] src = img.Data;
        byte[] row = new byte[w * 3 * 2];
        for (int y = 0; y < h; y++)
        {
            int o = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int j = o + x * 3;
                WriteU16(row, x * 3 + 0, To16(src[j]));
                WriteU16(row, x * 3 + 1, To16(src[j + 1]));
                WriteU16(row, x * 3 + 2, To16(src[j + 2]));
            }
            if (!tif.WriteScanline(row, y))
                throw new IOException($"failed writing TIFF scanline {y}");
        }

        tif.FlushData();
    }

    /// <summary>Read one sample (8- or 16-bit) at sample index <paramref name="s"/> as a float count.</summary>
    private static float Sample(byte[] buf, int s, int bps)
    {
        if (bps == 16)
        {
            int b = s * 2;
            return (ushort)(buf[b] | (buf[b + 1] << 8)); // libtiff returns native (LE) order
        }
        return buf[s];
    }

    // TIFF/EXIF ImageDescription is 7-bit ASCII per spec; mirror Python's
    // description.encode("ascii", "replace") rather than dropping the tag.
    // Shared with JpegIO so both containers fold identically.
    internal static string ToAsciiSafe(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < 128 ? c : '?');
        return sb.ToString();
    }

    private static void WriteU16(byte[] buf, int sampleIndex, ushort v)
    {
        int b = sampleIndex * 2;
        buf[b] = (byte)(v & 0xFF);
        buf[b + 1] = (byte)(v >> 8);
    }

    private static ushort To16(float v)
    {
        float c = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
        return (ushort)(c * 65535.0f + 0.5f);
    }
}
