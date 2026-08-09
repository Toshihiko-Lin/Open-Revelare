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

    /// <summary>Pixel dimensions from the header alone — no image data is decoded.</summary>
    public static (int Width, int Height) ReadTiffSize(string path)
    {
        using Tiff tif = Tiff.Open(path, "r")
            ?? throw new IOException($"could not open TIFF: {path}");
        return (tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(),
                tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt());
    }

    /// <summary>
    /// Load ONE normalised sub-rectangle of a TIFF, box-averaged down to
    /// <paramref name="maxEdge"/> — the path a scan holding several negatives needs.
    ///
    /// A strip cut into six frames would otherwise be previewed by decoding the whole file to a
    /// 1600 px preview and cropping a sixth out of it, leaving each frame about 260 px and
    /// visibly soft on screen. Cropping FIRST and downsampling after gives each frame the full
    /// preview budget from the source pixels it actually covers.
    ///
    /// Only the rows the rectangle covers are read, and each is folded into the accumulator as it
    /// arrives, so peak memory is the OUTPUT plus one scanline rather than the whole image.
    /// Reading is still sequential from row 0 because a TIFF's strips are not randomly
    /// addressable in general; the skipped rows are decoded but never converted or kept.
    /// </summary>
    /// <param name="rect">(x, y, w, h) in [0,1] of the full image, origin top-left.</param>
    /// <param name="maxEdge">Long edge of the result. 0 or less means no downsampling.</param>
    public static ImageBuffer LoadTiffRegion(string path, (double X, double Y, double W, double H) rect,
                                             bool inputIsSrgb, int maxEdge)
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
            throw new NotSupportedException("only chunky (CONTIG) TIFF is supported");
        if (bps != 8 && bps != 16)
            throw new NotSupportedException($"unsupported BitsPerSample {bps} (need 8 or 16)");

        int x0 = Math.Clamp((int)Math.Round(rect.X * w), 0, Math.Max(0, w - 1));
        int y0 = Math.Clamp((int)Math.Round(rect.Y * h), 0, Math.Max(0, h - 1));
        int x1 = Math.Clamp((int)Math.Round((rect.X + rect.W) * w), x0 + 1, w);
        int y1 = Math.Clamp((int)Math.Round((rect.Y + rect.H) * h), y0 + 1, h);
        int cw = x1 - x0, ch = y1 - y0;

        // One integer box factor, matching Resample.Box, so a region preview and a whole-frame
        // preview of the same pixels land on the same grid.
        int factor = 1;
        if (maxEdge > 0)
            while (Math.Max(cw, ch) / (factor + 1) >= maxEdge) factor++;
        int outW = Math.Max(1, cw / factor), outH = Math.Max(1, ch / factor);

        var acc = new float[outW * outH * 3];
        var counts = new int[outW * outH];
        int scanlineSize = tif.ScanlineSize();
        byte[] buf = new byte[scanlineSize];
        float inv = bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;

        for (int y = y0; y < y1; y++)
        {
            if (!tif.ReadScanline(buf, y))
                throw new IOException($"failed reading TIFF scanline {y}");

            int oy = (y - y0) / factor;
            if (oy >= outH) continue;          // trailing rows outside the integer box
            for (int x = x0; x < x1; x++)
            {
                int ox = (x - x0) / factor;
                if (ox >= outW) continue;

                float r, g, b;
                if (spp >= 3)
                {
                    r = Sample(buf, x * spp + 0, bps) * inv;
                    g = Sample(buf, x * spp + 1, bps) * inv;
                    b = Sample(buf, x * spp + 2, bps) * inv;
                }
                else
                {
                    float grey = Sample(buf, x * spp, bps) * inv;
                    r = g = b = grey;
                }
                if (inputIsSrgb)
                {
                    r = Srgb.SrgbToLinear(r);
                    g = Srgb.SrgbToLinear(g);
                    b = Srgb.SrgbToLinear(b);
                }

                int p = oy * outW + ox, j = p * 3;
                acc[j] += r; acc[j + 1] += g; acc[j + 2] += b;
                counts[p]++;
            }
        }

        for (int p = 0; p < counts.Length; p++)
        {
            int n = counts[p];
            if (n <= 1) continue;
            int j = p * 3;
            acc[j] /= n; acc[j + 1] /= n; acc[j + 2] /= n;
        }
        return new ImageBuffer(outW, outH, acc);
    }

    /// <summary>Compression choice for 16-bit TIFF export.</summary>
    public enum CompressionMode { None, Lzw, Deflate }

    /// <summary>Write a 16-bit RGB TIFF. Data is quantised as-is (already encoded upstream).
    /// Roll annotations are NOT written here — they are burned into the contact sheet's info bar
    /// instead (see the GUI's SheetInfoBar), so exports carry no note-derived EXIF.
    ///
    /// Staged through <see cref="ExportFile.Write"/>: LibTiff truncates its destination on open,
    /// so a 60 MP write that fails partway would otherwise leave a stump where the previous good
    /// export was. Every caller gets this, which is the point of putting it here rather than at
    /// the call sites.</summary>
    public static void ExportTiff16(ImageBuffer img, string path, CompressionMode mode = CompressionMode.Lzw,
                                    ColorSpace? iccSpace = null, string? description = null)
        => ExportFile.Write(path, target => WriteTiff16(img, target, mode, iccSpace, description));

    private static void WriteTiff16(ImageBuffer img, string path, CompressionMode mode,
                                    ColorSpace? iccSpace, string? description)
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
