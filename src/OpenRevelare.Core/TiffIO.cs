using BitMiracle.LibTiff.Classic;

namespace OpenRevelare.Core;

/// <summary>
/// TIFF load/export via BitMiracle.LibTiff.NET (BSD license — clean for a
/// proprietary product; guaranteed correct 16-bit output, which ImageSharp's TIFF
/// encoder silently downgrades to 8-bit). Phase-1 stand-in for tifffile.
///
/// Load: 8/16-bit, chunky (CONTIG) RGB or grey TIFF -&gt; linear-light f32 image.
/// <paramref name="inputIsSrgb"/> linearises a display-gamma scan; otherwise the
/// encoding comes from the embedded ICC profile, and failing that from the bit
/// depth (see <c>UntaggedTransform</c>): 8-bit untagged is sRGB by convention,
/// 16-bit untagged is treated as already linear.
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

    /// <summary>
    /// Silences LibTiff's WARNING stream while leaving errors untouched.
    ///
    /// A Flextight .fff is a valid TIFF carrying Hasselblad's private tags, and LibTiff announces
    /// each one it does not recognise — six lines per open (tags 34152 / 46277 / 46279 / 50457 /
    /// 50458, plus "tags are not sorted in ascending order"). Nothing is wrong, and the file is
    /// read correctly, but the noise repeats on every preview and every export, and it reads to a
    /// user as though their scan were damaged.
    ///
    /// Only <see cref="TiffErrorHandler.WarningHandler"/> is overridden. The error handlers are
    /// deliberately left to the base implementation: a genuine failure must stay visible, and a
    /// handler that swallowed both would hide the diagnostics that make a broken file
    /// debuggable.
    /// </summary>
    private sealed class WarningSuppressor : TiffErrorHandler
    {
        public override void WarningHandler(Tiff tif, string method, string format, params object[] args) { }
        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args) { }
    }

    private static bool _warningsSuppressed;

    /// <summary>
    /// Install <see cref="WarningSuppressor"/> once per process.
    ///
    /// LibTiff's handler is global rather than per-file, so this is set up on first use and left
    /// in place — swapping it around individual opens would race as soon as two decodes overlap,
    /// which they routinely do (see ImageIo's decode gate).
    /// </summary>
    private static void SuppressLibTiffWarnings()
    {
        if (_warningsSuppressed) return;
        _warningsSuppressed = true;
        Tiff.SetErrorHandler(new WarningSuppressor());
    }

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

    /// <summary>Raw ICC profile bytes embedded in the TIFF (tag 34675), or null.</summary>
    public static byte[]? ReadIccBytes(Tiff tif)
    {
        try
        {
            var f = tif.GetField(TiffTag.ICCPROFILE);
            // LibTiff hands back [count, bytes] for this tag.
            if (f is { Length: >= 2 })
            {
                byte[] icc = f[1].ToByteArray();
                if (icc.Length >= 132) return icc;
            }
        }
        catch (Exception) { /* absent or unreadable profile → caller assumes linear */ }
        return null;
    }

    /// <summary>
    /// Colour handling for a scanner TIFF, resolved once per file from its ICC profile
    /// and then applied per pixel. See <see cref="IccRead"/> for why both steps matter.
    /// </summary>
    private readonly struct IccTransform
    {
        /// <summary>Per-channel encoded→linear LUTs; null when the file is already linear.</summary>
        public float[][]? Luts { get; init; }

        /// <summary>Device→working-space linear matrix; null on LUT-only profiles (step
        /// skipped). See <see cref="IccRead.ReadMatrix"/> for why the destination is the
        /// working space and not sRGB.</summary>
        public double[,]? Matrix { get; init; }

        public bool IsIdentity => Luts is null && Matrix is null;
    }

    /// <summary>
    /// Resolve the ICC transform for a file. <paramref name="inputIsSrgb"/> forces the
    /// plain sRGB inverse and bypasses the profile entirely, preserving the old
    /// caller contract; otherwise the profile's own curves and matrix are used.
    ///
    /// <paramref name="bps"/> decides the UNTAGGED case, where nothing in the file says what its
    /// numbers mean — see <see cref="UntaggedTransform"/>.
    /// </summary>
    private static IccTransform ResolveIcc(Tiff tif, bool inputIsSrgb, string path, int bps)
    {
        if (inputIsSrgb) return default;
        byte[]? icc = ReadIccBytes(tif);
        if (icc is null) return UntaggedTransform(path, bps);

        // Step 1 — per-channel TRC. Skipped when every channel is already identity,
        // so a genuinely linear scan keeps its exact sample values.
        float[][]? luts = IccRead.BuildTrcLuts(icc, out bool allLinear);
        if (allLinear) luts = null;

        // Step 2 — device→working-space primaries. Null (skipped) on LUT-only profiles.
        return new IccTransform { Luts = luts, Matrix = IccRead.ReadMatrix(icc) };
    }

    /// <summary>
    /// The UNTAGGED case: no embedded ICC, so nothing in the file states what its numbers mean and
    /// the encoding has to be inferred from the one thing that is always present — the bit depth.
    ///
    /// A Flextight declaration wins when there is one: it is an actual statement by the scanner,
    /// not an inference. Only when that comes back empty does the bit-depth rule apply.
    ///
    /// EIGHT BITS IS NEVER LINEAR IN PRACTICE. Linear light quantised to 256 steps puts roughly
    /// half of them above 18% grey and leaves the shadows in single digits, which is unusable for
    /// a negative — the density range this pipeline exists to invert lives precisely where 8-bit
    /// linear has no codes left. That is why the sRGB convention exists for untagged 8-bit files,
    /// and why every photo viewer applies it. Reading such a file as linear does not merely
    /// darken it: it COMPRESSES CHANNEL RATIOS toward neutral (a measured orange film base moved
    /// from a true R/B of 7.5 to 2.5), so the Stage-1 film-base and D_max estimators see a mask
    /// far weaker and flatter than the real one, invert against it, and throw the residual out as
    /// a colour cast. Manual sampling could absorb that error by eye; the automatic path cannot.
    ///
    /// SIXTEEN BITS IS LEFT ALONE. There the linear assumption is at least arguable — it is the
    /// depth real linear scans are written at — and silently regamma-ing existing 16-bit rolls
    /// would move calibrations that are currently correct. An untagged 16-bit sRGB file does
    /// exist, but it needs an explicit declaration to resolve, not a guess made here.
    /// </summary>
    private static IccTransform UntaggedTransform(string path, int bps)
    {
        IccTransform flextight = FlextightTransform(path);
        if (!flextight.IsIdentity) return flextight;
        return bps == 8 ? new IccTransform { Luts = Srgb.BuildDecodeLuts() } : default;
    }

    /// <summary>
    /// Fallback for a file with NO embedded ICC: recover the transfer function from a Flextight
    /// scanner's own settings block if that is what this is.
    ///
    /// Only reached when <see cref="ReadIccBytes"/> came back empty, so it cannot override a real
    /// profile. A Flextight .fff claims <c>EmbedProfile = true</c> and then embeds nothing, which
    /// is precisely the case that used to be read as "already linear" — see
    /// <see cref="FlextightMeta"/> for what that costs.
    ///
    /// Only the TRC is filled in. No matrix is applied: the file names its space
    /// "Negative RGB standard" but carries no primaries for it, and inventing a matrix would put
    /// an uncharacterised guess in the colour path. Gamma is declared and therefore honoured;
    /// primaries are not declared and therefore left alone.
    /// </summary>
    private static IccTransform FlextightTransform(string path)
    {
        var meta = FlextightMeta.Read(path);
        if (!meta.HasEncodingGamma) return default;
        return new IccTransform { Luts = FlextightMeta.BuildGammaLuts(meta.Gamma!.Value) };
    }

    /// <summary>Apply the resolved ICC transform to one pixel, in place.</summary>
    private static void ApplyIcc(in IccTransform t, ref float r, ref float g, ref float b)
    {
        if (t.Luts is { } luts)
        {
            r = luts[0][Lut16Index(r)];
            g = luts[1][Lut16Index(g)];
            b = luts[2][Lut16Index(b)];
        }
        if (t.Matrix is { } m)
        {
            double nr = m[0, 0] * r + m[0, 1] * g + m[0, 2] * b;
            double ng = m[1, 0] * r + m[1, 1] * g + m[1, 2] * b;
            double nb = m[2, 0] * r + m[2, 1] * g + m[2, 2] * b;
            // Out-of-gamut scanner primaries can send a channel negative; clip low only.
            r = (float)Math.Max(nr, 0.0);
            g = (float)Math.Max(ng, 0.0);
            b = (float)Math.Max(nb, 0.0);
        }
    }

    private static int Lut16Index(float v)
        => (int)(Math.Clamp(v, 0.0f, 1.0f) * 65535.0f + 0.5f);

    /// <summary>
    /// One code step of the source file expressed in the LINEAR units the buffer actually holds —
    /// measured at the shadow end, which is the only place it matters.
    ///
    /// <paramref name="codeStep"/> (1/255 or 1/65535) is the step in the ENCODED domain. The
    /// buffer stores linearised values, so stamping the encoded step would overstate the shadow
    /// lattice by the slope of the transfer curve — on sRGB that is ~13x at the bottom of the
    /// range, which turns a guard against quantisation noise into one that discards ordinary
    /// samples (measured: it pulled a perfectly healthy red endpoint from 1.96 down to 1.56).
    ///
    /// Evaluated as the gap between the two lowest codes because that is where the density
    /// uncertainty is decided; both sRGB and a pure-power TRC have their finest linear spacing
    /// there, so this is also the conservative end. Under sRGB's linear toe the spacing is
    /// genuinely constant across the low codes, which is exactly the region the endpoint guard
    /// judges.
    ///
    /// Falls back to the encoded step when no transform applies — a genuinely linear file already
    /// stores what it encoded.
    /// </summary>
    private static double ShadowStep(float codeStep, in IccTransform icc, bool inputIsSrgb)
    {
        if (inputIsSrgb) return Srgb.SrgbToLinear(codeStep) - Srgb.SrgbToLinear(0f);
        if (icc.Luts is { } luts)
        {
            // The TRC LUTs are indexed over the ENCODED range; sample the first two source codes.
            float[] lut = luts[1];   // green: the channel the luma-ish statistics lean on
            double lo = lut[Lut16Index(0f)];
            double hi = lut[Lut16Index(codeStep)];
            double gap = hi - lo;
            if (gap > 0) return gap;
        }
        return codeStep;
    }

    /// <summary>Load a TIFF into a linear-light f32 image.</summary>
    public static ImageBuffer LoadTiff(string path, bool inputIsSrgb)
    {
        SuppressLibTiffWarnings();
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

        IccTransform icc = ResolveIcc(tif, inputIsSrgb, path, bps);

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
                else if (!icc.IsIdentity)
                {
                    ApplyIcc(icc, ref r, ref g, ref b);
                }

                int j = o + x * 3;
                data[j] = r; data[j + 1] = g; data[j + 2] = b;
            }
        }

        // Stamp the source lattice while the bit depth is still in scope; see
        // ImageBuffer.SourceQuantisationStep for why this cannot be recovered downstream.
        return new ImageBuffer(w, h, data) { SourceQuantisationStep = ShadowStep(inv, icc, inputIsSrgb) };
    }

    /// <summary>Pixel dimensions from the header alone — no image data is decoded.</summary>
    public static (int Width, int Height) ReadTiffSize(string path)
    {
        SuppressLibTiffWarnings();
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
        SuppressLibTiffWarnings();
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

        IccTransform icc = ResolveIcc(tif, inputIsSrgb, path, bps);

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
                else if (!icc.IsIdentity)
                {
                    ApplyIcc(icc, ref r, ref g, ref b);
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
        // The SOURCE step, not the averaged one — the region loader box-averages internally, and
        // averaging changes the lattice without recovering information. See
        // ImageBuffer.SourceQuantisationStep.
        return new ImageBuffer(outW, outH, acc) { SourceQuantisationStep = ShadowStep(inv, icc, inputIsSrgb) };
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
        => ExportFile.Write(path, target => WriteTiff16(img, target,
               mode, iccSpace is ColorSpace c ? Legacy(c) : null, description));

    /// <summary>
    /// As above, embedding the profile of any registered space. The caller is responsible for
    /// having actually rendered the pixels into <paramref name="iccSpace"/> — see
    /// <see cref="OutputRender"/>; a profile that disagrees with the data is worse than none.
    /// </summary>
    public static void ExportTiff16(ImageBuffer img, string path, CompressionMode mode,
                                    ColorSpaceDef? iccSpace, string? description = null)
        => ExportFile.Write(path, target => WriteTiff16(img, target, mode, iccSpace, description));

    /// <summary>Bridges the two-value legacy enum onto the registry.</summary>
    internal static ColorSpaceDef Legacy(ColorSpace c) =>
        c == ColorSpace.AdobeRgb ? ColorSpaces.AdobeRgb : ColorSpaces.Srgb;

    private static void WriteTiff16(ImageBuffer img, string path, CompressionMode mode,
                                    ColorSpaceDef? iccSpace, string? description)
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

        if (iccSpace is ColorSpaceDef cs)
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
