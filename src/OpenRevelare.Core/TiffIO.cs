using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

/// <summary>
/// TIFF load/export via BitMiracle.LibTiff.NET (BSD license — clean for a
/// proprietary product; guaranteed correct 16-bit output, which ImageSharp's TIFF
/// encoder silently downgrades to 8-bit). Phase-1 stand-in for tifffile.
///
/// Load: 8/16-bit unsigned or 32-bit IEEE-float, chunky (CONTIG) RGB or grey TIFF
/// -&gt; linear-light f32 image.
/// <paramref name="inputIsSrgb"/> linearises a display-gamma scan; otherwise the
/// encoding comes from the embedded ICC profile, and failing that from the bit
/// depth (see <c>UntaggedTransform</c>): 8-bit untagged is sRGB by convention,
/// 16-bit untagged is treated as already linear.
/// Export: normalized frames -&gt; 16-bit RGB TIFF; extended frames -&gt; 32-bit float RGB TIFF.
/// The pipeline hands us data already in the target encoding.
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
    private static void ApplyIcc(
        in IccTransform t,
        ref float r,
        ref float g,
        ref float b,
        bool preserveExtendedRange)
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
            // Frozen integer scanner admission clips low-going matrix results. IEEE-float TIFF is
            // explicitly an extended-range interchange, so applying that compatibility clamp to
            // a linear ACEScg round-trip would silently destroy legitimate negative channels.
            r = preserveExtendedRange ? (float)nr : (float)Math.Max(nr, 0.0);
            g = preserveExtendedRange ? (float)ng : (float)Math.Max(ng, 0.0);
            b = preserveExtendedRange ? (float)nb : (float)Math.Max(nb, 0.0);
        }
    }

    private static int Lut16Index(float v)
        => (int)(Math.Clamp(v, 0.0f, 1.0f) * 65535.0f + 0.5f);

    /// <summary>
    /// One rectangle of a TIFF, box-averaged to one or more sizes — what a preview or a split
    /// cell asks for. Several sizes cost one pass over the file (see <see cref="ReadWindows"/>).
    /// </summary>
    /// <param name="Rect">Normalised (x, y, w, h) of the full image, origin top-left.</param>
    /// <param name="MaxEdges">Long edge of each result; 0 or less keeps full resolution.</param>
    public readonly record struct RegionRequest(
        (double X, double Y, double W, double H) Rect,
        int[] MaxEdges)
    {
        public RegionRequest((double X, double Y, double W, double H) rect, int maxEdge)
            : this(rect, new[] { maxEdge }) { }
    }

    /// <summary>
    /// The pixels a decode produces, in source coordinates: a crop box and an integer box
    /// factor. Resolved from a <see cref="RegionRequest"/> against the file's dimensions with the
    /// SAME arithmetic as <see cref="Geometry.ApplyCrop"/> and <see cref="Resample.BoxFactor"/>,
    /// so that decoding a window and decoding the whole frame then cropping and boxing it land on
    /// the same pixel grid — every Stage-1 measurement is taken on the preview, so a grid that
    /// moved with the decode path would silently move the numbers.
    /// </summary>
    private readonly record struct SampleWindow(int X0, int Y0, int X1, int Y1, int Factor)
    {
        public int OutWidth => (X1 - X0) / Factor;
        public int OutHeight => (Y1 - Y0) / Factor;

        public static SampleWindow Whole(int width, int height) => new(0, 0, width, height, 1);

        /// <summary>One window per requested edge, all on the same crop; the whole frame at
        /// full resolution when there is no request.</summary>
        public static SampleWindow[] Resolve(int width, int height, RegionRequest? region)
        {
            if (region is not { } r) return new[] { Whole(width, height) };
            if (r.MaxEdges is not { Length: > 0 })
                throw new ArgumentException("a region request needs at least one edge", nameof(region));
            var rect = r.Rect;
            // Geometry.ApplyCrop, verbatim — including its refusal of an empty crop.
            int x0 = Math.Max(0, (int)Math.Round(rect.X * width));
            int y0 = Math.Max(0, (int)Math.Round(rect.Y * height));
            int x1 = Math.Min(width, (int)Math.Round((rect.X + rect.W) * width));
            int y1 = Math.Min(height, (int)Math.Round((rect.Y + rect.H) * height));
            if (x1 <= x0 || y1 <= y0)
                throw new ArgumentException($"crop rect {rect} yields empty region for {width}×{height}");
            var windows = new SampleWindow[r.MaxEdges.Length];
            for (int k = 0; k < windows.Length; k++)
            {
                int factor = r.MaxEdges[k] > 0 ? Resample.BoxFactor(x1 - x0, y1 - y0, r.MaxEdges[k]) : 1;
                windows[k] = new SampleWindow(x0, y0, x1, y1, factor);
            }
            return windows;
        }
    }

    /// <summary>A per-row hook on the decoded, scaled samples of one window row — <c>count</c>
    /// pixels, interleaved RGB — before they are stored or averaged.</summary>
    private delegate void RowTransform(Span<float> rgb, int count);

    /// <summary>
    /// A row transform bound to the thread that asked for it, plus whatever must be released
    /// when that thread is done with it. A stateless transform (the v1 TRC/matrix, sRGB) has no
    /// owner; a LittleCMS lease is thread-affine and IS the owner.
    /// </summary>
    private readonly record struct RowStage(RowTransform? Transform, IDisposable? Owner);

    /// <summary>Makes the <see cref="RowStage"/> for the calling thread. Called once per worker.</summary>
    private delegate RowStage RowStageFactory();

    /// <summary>
    /// The one scanline loop every TIFF decode here runs through.
    ///
    /// Reads the rows the windows cover, scales each sample to its encoded range (1/255, 1/65535,
    /// or 1.0 for IEEE float), hands the row's crop segment to the row transform, then for each
    /// window either stores it (factor 1) or folds it into that window's box accumulator. The
    /// accumulator is <see cref="Resample.Box"/> unrolled: the same float sums in the same order
    /// (row by row, left to right within a row) and the same final multiply by <c>1/factor²</c>,
    /// so the result is bit-identical to boxing a full decode — that identity is what lets a
    /// split scan or a preview be decoded WITHOUT the full frame ever existing.
    ///
    /// Several windows share ONE pass so that a caller wanting two preview sizes of the same crop
    /// (the roll's opening frames want the editor size and the strip size) pays for the file and
    /// the colour transform once; all windows must share the crop and differ only in factor.
    ///
    /// IN PARALLEL, by bands of rows, when the file lets rows be reached without decoding
    /// everything above them (<see cref="ParallelBands"/>). Each band has its own LibTiff handle
    /// (a <see cref="Tiff"/> is not thread-safe) and its own <see cref="RowStage"/> (a LittleCMS
    /// lease is thread-affine). The bands are cut on multiples of every window's factor, so each
    /// output row's sum is formed by exactly one band from exactly its own input rows, in the
    /// same order as before — the identity above survives the parallelism untouched, and the
    /// tests that pin it are the regression suite for this loop. Measured on a 12-core machine:
    /// a 127 MP Flextight strip's preview from 2.5 s to under a second, and a 61 MP FlexColor
    /// export carrying the scanner's LUT profile — where LittleCMS was 13 s of a 14 s decode —
    /// to about 2 s.
    ///
    /// Memory is the outputs plus one scanline per band. That is the point: a whole Flextight
    /// strip is 127–190 MP, 1.5–2.3 GB as float, and the previous shape of every preview —
    /// decode the frame, crop it, box it — could not be run on an 8 GB machine, let alone eight
    /// cells of it side by side.
    /// </summary>
    private static float[][] ReadWindows(
        Tiff tif,
        string path,
        int spp,
        int bps,
        float sampleScale,
        SampleWindow[] windows,
        RowStageFactory? stage)
    {
        if (windows.Length == 0) throw new ArgumentException("at least one window", nameof(windows));
        SampleWindow crop = windows[0];
        foreach (SampleWindow w in windows)
            if (w.X0 != crop.X0 || w.X1 != crop.X1 || w.Y0 != crop.Y0 || w.Y1 != crop.Y1)
                throw new ArgumentException("windows sharing one pass must share the crop", nameof(windows));

        var outputs = new float[windows.Length][];
        int lastRow = crop.Y0;   // exclusive: the first row no window needs
        int unit = 1;            // band boundaries must be ≡ Y0 modulo every factor
        for (int k = 0; k < windows.Length; k++)
        {
            outputs[k] = new float[checked(windows[k].OutWidth * windows[k].OutHeight * 3)];
            lastRow = Math.Max(lastRow, crop.Y0 + windows[k].OutHeight * windows[k].Factor);
            unit = Lcm(unit, windows[k].Factor);
        }

        // A compressed strip can only be read from its first row (the codec has no random
        // access), so a band that starts inside one starts reading at the strip's top and drops
        // the rows above its own. Uncompressed rows are addressed directly.
        int rowsPerStrip = RowsPerStrip(tif);
        bool compressed = CompressionOf(tif) != BitMiracle.LibTiff.Classic.Compression.NONE;
        int SeekBase(int y) => compressed ? y / rowsPerStrip * rowsPerStrip : y;

        int rows = lastRow - crop.Y0;
        int bands = ParallelBands(rows, unit, compressed, rowsPerStrip);
        if (bands <= 1)
        {
            ReadBand(tif, path, spp, bps, sampleScale, windows, outputs, stage, SeekBase(crop.Y0), crop.Y0, lastRow);
        }
        else
        {
            int bandRows = (rows + bands - 1) / bands;
            bandRows = (bandRows + unit - 1) / unit * unit;
            int bandCount = (rows + bandRows - 1) / bandRows;
            Parallel.For(0, bandCount, band =>
            {
                int y0 = crop.Y0 + band * bandRows;
                int y1 = Math.Min(lastRow, y0 + bandRows);
                using Tiff own = Tiff.Open(path, "r")
                    ?? throw new IOException($"could not open TIFF: {path}");
                ReadBand(own, path, spp, bps, sampleScale, windows, outputs, stage, SeekBase(y0), y0, y1);
            });
        }

        for (int k = 0; k < windows.Length; k++)
        {
            int factor = windows[k].Factor;
            if (factor == 1) continue;
            float inv = 1.0f / (factor * factor);
            float[] data = outputs[k];
            for (int i = 0; i < data.Length; i++) data[i] *= inv;
        }
        return outputs;
    }

    /// <summary>Rows <paramref name="yFrom"/> (inclusive) to <paramref name="yTo"/> (exclusive) of
    /// every window, on the calling thread with the given handle, reading from
    /// <paramref name="readFrom"/> (≤ <paramref name="yFrom"/>; the rows between are decoded and
    /// dropped). See <see cref="ReadWindows"/>.</summary>
    private static void ReadBand(
        Tiff tif,
        string path,
        int spp,
        int bps,
        float sampleScale,
        SampleWindow[] windows,
        float[][] outputs,
        RowStageFactory? stage,
        int readFrom,
        int yFrom,
        int yTo)
    {
        SampleWindow crop = windows[0];
        int segW = crop.X1 - crop.X0;
        var row = new float[checked(segW * 3)];
        byte[] scanline = new byte[tif.ScanlineSize()];
        RowStage rowStage = stage?.Invoke() ?? default;
        try
        {
            for (int y = readFrom; y < yTo; y++)
            {
                if (!tif.ReadScanline(scanline, y))
                    throw new IOException($"failed reading TIFF scanline {y} from '{Path.GetFileName(path)}'");
                if (y < yFrom) continue;

                for (int x = 0; x < segW; x++)
                {
                    int sx = (crop.X0 + x) * spp, d = x * 3;
                    if (spp >= 3)
                    {
                        row[d] = Sample(scanline, sx, bps) * sampleScale;
                        row[d + 1] = Sample(scanline, sx + 1, bps) * sampleScale;
                        row[d + 2] = Sample(scanline, sx + 2, bps) * sampleScale;
                    }
                    else
                    {
                        float grey = Sample(scanline, sx, bps) * sampleScale;   // grey -> replicate
                        row[d] = grey; row[d + 1] = grey; row[d + 2] = grey;
                    }
                }
                rowStage.Transform?.Invoke(row, segW);

                for (int k = 0; k < windows.Length; k++)
                {
                    SampleWindow win = windows[k];
                    int factor = win.Factor, outW = win.OutWidth;
                    int oy = (y - crop.Y0) / factor;
                    if (oy >= win.OutHeight) continue;          // trailing rows outside this box
                    float[] data = outputs[k];
                    if (factor == 1)
                    {
                        Array.Copy(row, 0, data, oy * outW * 3, outW * 3);
                        continue;
                    }
                    int rowBase = oy * outW * 3;
                    for (int ox = 0; ox < outW; ox++)
                    {
                        int di = rowBase + ox * 3, si = ox * factor * 3;
                        float r = data[di], g = data[di + 1], b = data[di + 2];
                        for (int fx = 0; fx < factor; fx++, si += 3)
                        {
                            r += row[si]; g += row[si + 1]; b += row[si + 2];
                        }
                        data[di] = r; data[di + 1] = g; data[di + 2] = b;
                    }
                }
            }
        }
        finally
        {
            rowStage.Owner?.Dispose();
        }
    }

    /// <summary>
    /// How many bands <see cref="ReadWindows"/> may cut <paramref name="rows"/> into.
    ///
    /// Parallel reading needs a band's first row to be reachable without decoding the rows above
    /// it. An uncompressed file always is: LibTiff addresses any row of an uncompressed strip
    /// directly (and chops oversized strips on open). A compressed strip is decoded from its
    /// start, so a band beginning inside one pays for the strip's rows above it; that is cheap
    /// while strips are small against the band and ruinous when the whole image is one strip —
    /// every band would decode from row 0. So compressed files parallelise only when a strip is
    /// at most a quarter of a band, and a single-strip LZW/Deflate scan stays sequential, as it
    /// always was. Never more bands than cores or than <paramref name="unit"/>-sized pieces.
    /// </summary>
    private static int ParallelBands(int rows, int unit, bool compressed, int rowsPerStrip)
    {
        int cores = Environment.ProcessorCount;
        int maxByRows = Math.Max(1, rows / Math.Max(unit, MinBandRows));
        int bands = Math.Min(cores, maxByRows);
        if (bands <= 1 || !compressed) return bands;
        int bandRows = (rows + bands - 1) / bands;
        return rowsPerStrip * 4 <= bandRows ? bands : 1;
    }

    private static Compression CompressionOf(Tiff tif)
        => tif.GetField(TiffTag.COMPRESSION) is { Length: > 0 } c
            ? (Compression)c[0].ToInt()
            : BitMiracle.LibTiff.Classic.Compression.NONE;

    /// <summary>Rows per strip as LibTiff reports it after opening (an oversized uncompressed
    /// strip has been chopped by then); the whole image when the tag is absent.</summary>
    private static int RowsPerStrip(Tiff tif)
    {
        FieldValue[]? rps = tif.GetField(TiffTag.ROWSPERSTRIP);
        int height = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int rows = rps is { Length: > 0 } ? rps[0].ToInt() : height;
        return rows <= 0 ? height : Math.Min(rows, height);
    }

    /// <summary>A band shorter than this is not worth a thread and a file handle.</summary>
    private const int MinBandRows = 64;

    private static int Lcm(int a, int b)
    {
        int x = a, y = b;
        while (y != 0) (x, y) = (y, x % y);
        return checked(a / x * b);
    }

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

    /// <summary>
    /// M1 typed decode boundary. Pixel math intentionally remains the frozen v1 implementation;
    /// the wrapper preserves the exact embedded profile (or an explicit Uncharacterized reason)
    /// after admission to the working pipeline. M2 replaces the legacy split ICC parser with one
    /// atomic LittleCMS transform.
    /// </summary>
    public static WorkingFrame LoadWorkingFrame(string path, bool inputIsSrgb)
        => LoadWorkingFrame(path, inputIsSrgb, region: null)[0];

    /// <summary>One frame per window of <paramref name="region"/> (one, the whole frame, when
    /// there is no request); every frame carries the same descriptor and admission.</summary>
    private static WorkingFrame[] LoadWorkingFrame(string path, bool inputIsSrgb, RegionRequest? region)
    {
        SuppressLibTiffWarnings();
        string stableId = FrameSourceIds.ForPath(path);
        PixelEncoding original;
        WorkingAdmission admission;
        string decodeRecipe;

        using (Tiff tif = Tiff.Open(path, "r")
            ?? throw new IOException($"could not open TIFF: {path}"))
        {
            int bps = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
            byte[]? embedded = inputIsSrgb ? null : ReadIccBytes(tif);
            if (inputIsSrgb)
            {
                var profile = BuiltInColorProfiles.Srgb(ProfileRole.Input);
                original = new CharacterizedPixelEncoding(
                    profile,
                    ColorReference.DisplayReferred,
                    TransferState.ProfileEncoded,
                    bps == 32 ? NumericRange.Extended : NumericRange.Normalized);
                admission = WorkingAdmission.ConvertedFromCharacterized;
                decodeRecipe = "explicit sRGB override -> legacy linear working admission";
            }
            else if (embedded is not null)
            {
                var profile = ColorProfileRef.Create(
                    embedded,
                    $"Embedded TIFF profile ({Path.GetFileName(path)})",
                    ProfileRole.Input,
                    new ProfileSource.Embedded(stableId, "TIFF"));
                original = new CharacterizedPixelEncoding(
                    profile,
                    ColorReference.SceneReferred,
                    TransferState.ProfileEncoded,
                    bps == 32 ? NumericRange.Extended : NumericRange.Normalized);
                admission = WorkingAdmission.LegacyPartialIccTransform;
                decodeRecipe = "v1 split ICC TRC/matrix parser (M2 migration required)";
            }
            else
            {
                FlextightMeta.Settings flextight = FlextightMeta.Read(path);
                bool vendorGamma = flextight.HasEncodingGamma;
                CaptureKind kind = vendorGamma
                    ? CaptureKind.ScannerVendorDeclaredWithoutPrimaries
                    : bps == 8 ? CaptureKind.TiffUntagged8Bit
                    : bps == 16 ? CaptureKind.TiffUntagged16Bit
                    : CaptureKind.Other;
                CompatibilityPolicy policy = vendorGamma || bps == 8
                    ? CompatibilityPolicy.LegacyDecodeSrgbTransferThenTreatAsWorking
                    : CompatibilityPolicy.LegacyTreatNumbersAsWorking;
                original = new UncharacterizedPixelEncoding(
                    kind,
                    stableId,
                    policy,
                    TransferState.Unknown,
                    bps == 32 ? NumericRange.Extended : NumericRange.Normalized);
                admission = WorkingAdmission.LegacyUncharacterizedPassthrough;
                decodeRecipe = vendorGamma
                    ? "vendor gamma decoded; primaries uncharacterized; v1 working passthrough"
                    : bps == 8
                        ? "untagged TIFF8 legacy sRGB-TRC guess; v1 working passthrough"
                        : bps == 16
                            ? "untagged TIFF16 legacy linear guess; v1 working passthrough"
                            : "untagged TIFF32 float legacy linear guess; v1 working passthrough";
            }
        }

        ImageBuffer[] pixels = LoadTiff(path, inputIsSrgb, region);
        var source = new SourceDescriptor(
            stableId,
            Path.GetFileName(path),
            original,
            decodeRecipe);
        return Frames(pixels, WorkingSpaceId.LinearAcesCgV1, admission, source);
    }

    /// <summary>One <see cref="WorkingFrame"/> per window, sharing everything but the pixels.</summary>
    private static WorkingFrame[] Frames(
        ImageBuffer[] windows,
        WorkingSpaceId space,
        WorkingAdmission admission,
        SourceDescriptor source)
    {
        var frames = new WorkingFrame[windows.Length];
        for (int k = 0; k < windows.Length; k++)
            frames[k] = new WorkingFrame(windows[k], space, admission, source);
        return frames;
    }

    /// <summary>
    /// Compatibility overload for callers that predate the typed TIFF assumption. In ManagedV2,
    /// <c>false</c> means "no explicit override", which resolves through <see cref="TiffInputDetector"/>
    /// rather than guessing from bit depth; a usable embedded ICC still wins outright. LegacyV1
    /// retains the frozen boolean behaviour byte-for-byte.
    /// </summary>
    public static WorkingFrame LoadWorkingFrame(
        string path,
        bool inputIsSrgb,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        if (pipelineVersion == ColorPipelineVersion.LegacyV1)
            return LoadWorkingFrame(path, inputIsSrgb);

        return LoadWorkingFrame(
            path,
            inputIsSrgb ? TiffInputAssumption.Srgb : TiffInputAssumption.Unspecified,
            pipelineVersion,
            colorManagement);
    }

    /// <summary>
    /// Versioned TIFF decode boundary. A usable embedded ICC always has priority. ManagedV2 uses
    /// <paramref name="inputAssumption"/> only when that profile is absent or unusable, and records
    /// the exact admission in <see cref="SourceDescriptor.DecodeRecipe"/>. The bit-depth policy is
    /// reachable only through <see cref="TiffInputAssumption.LegacyByBitDepthCompatibility"/> for
    /// old/missing-field projects.
    /// </summary>
    public static WorkingFrame LoadWorkingFrame(
        string path,
        TiffInputAssumption inputAssumption,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        if (!Enum.IsDefined(inputAssumption))
            throw new ArgumentOutOfRangeException(nameof(inputAssumption), inputAssumption, null);
        if (pipelineVersion == ColorPipelineVersion.LegacyV1)
        {
            // Old projects used the bool as a transfer override. Unspecified/compatibility both
            // select the historical per-bit-depth route, preserving their established look.
            return LoadWorkingFrame(path, inputIsSrgb: inputAssumption == TiffInputAssumption.Srgb);
        }
        if (pipelineVersion != ColorPipelineVersion.ManagedV2)
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion), pipelineVersion, "Unknown colour pipeline version.");

        ArgumentNullException.ThrowIfNull(colorManagement);
        return LoadManagedWorkingFrame(path, inputAssumption, colorManagement, region: null)[0];
    }

    /// <summary>
    /// One window of a TIFF as a typed working frame: what <see cref="LoadWorkingFrame(string, TiffInputAssumption, ColorPipelineVersion, IColorManagementEngine)"/>
    /// followed by <see cref="Geometry.ApplyCrop"/> and <see cref="Resample.Box"/> would return —
    /// the same pixels bit for bit, the same admission, descriptor and decode recipe, the same
    /// source lattice — decoded without the whole frame.
    ///
    /// Every route the whole-frame decode can take (exact embedded ICC, detected tags, explicit
    /// linear/sRGB, vendor gamma, legacy compatibility) is resolved by the SAME code and only the
    /// sample reader differs, so a split scan never takes a partial ICC path here either; see
    /// <see cref="ReadWindow"/> for the identity and the memory it buys.
    /// </summary>
    public static WorkingFrame LoadWorkingRegion(
        string path,
        (double X, double Y, double W, double H) rect,
        int maxEdge,
        TiffInputAssumption inputAssumption,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
        => LoadWorkingRegions(path, rect, new[] { maxEdge }, inputAssumption, pipelineVersion, colorManagement)[0];

    /// <summary>
    /// <see cref="LoadWorkingRegion"/> at several sizes off ONE pass over the file: element
    /// <c>k</c> is the window boxed to <c>maxEdges[k]</c>. What a caller that wants both an
    /// editor preview and a strip thumbnail of the same frame uses, so the file and its colour
    /// transform are paid for once.
    /// </summary>
    public static WorkingFrame[] LoadWorkingRegions(
        string path,
        (double X, double Y, double W, double H) rect,
        int[] maxEdges,
        TiffInputAssumption inputAssumption,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        if (!Enum.IsDefined(inputAssumption))
            throw new ArgumentOutOfRangeException(nameof(inputAssumption), inputAssumption, null);
        ArgumentNullException.ThrowIfNull(maxEdges);
        if (maxEdges.Length == 0)
            throw new ArgumentException("At least one preview edge is required.", nameof(maxEdges));
        var region = new RegionRequest(rect, (int[])maxEdges.Clone());
        if (pipelineVersion == ColorPipelineVersion.LegacyV1)
            return LoadWorkingFrame(path, inputIsSrgb: inputAssumption == TiffInputAssumption.Srgb, region);
        if (pipelineVersion != ColorPipelineVersion.ManagedV2)
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion), pipelineVersion, "Unknown colour pipeline version.");

        ArgumentNullException.ThrowIfNull(colorManagement);
        return LoadManagedWorkingFrame(path, inputAssumption, colorManagement, region);
    }

    private static WorkingFrame[] LoadManagedWorkingFrame(
        string path,
        TiffInputAssumption inputAssumption,
        IColorManagementEngine colorManagement,
        RegionRequest? region)
    {
        SuppressLibTiffWarnings();
        string stableId = FrameSourceIds.ForPath(path);

        byte[]? embedded = null;
        string? fallbackDiagnostic = null;
        try
        {
            using (Tiff profileTif = Tiff.Open(path, "r")
                ?? throw new IOException($"could not open TIFF: {path}"))
            {
                _ = TryReadIccPayloadStrict(profileTif, path, out embedded);
            }
        }
        catch (ColorManagementException ex)
            when (TiffInputAssumptionPolicy.AdmitsFallback(inputAssumption))
        {
            fallbackDiagnostic = OneLine(ex.Message);
        }

        if (embedded is not null)
        {
            try
            {
                var embeddedProfile = ColorProfileRef.Create(
                    embedded,
                    $"Embedded TIFF profile ({Path.GetFileName(path)})",
                    ProfileRole.Input,
                    new ProfileSource.Embedded(stableId, "TIFF"));
                return DecodeManagedWithProfile(
                    path,
                    stableId,
                    embeddedProfile,
                    ColorReference.SceneReferred,
                    "managed v2 exact embedded ICC -> LittleCMS -> linear ACEScg",
                    colorManagement,
                    region);
            }
            catch (Exception ex)
                // A transform-creation failure is deliberately NOT caught here: the embedded
                // profile was valid, so falling back would hide an engine fault behind a silent
                // change of colour space.
                when (ex is not OperationCanceledException and not OutOfMemoryException
                    and not ColorTransformCreationException
                    && TiffInputAssumptionPolicy.AdmitsFallback(inputAssumption))
            {
                fallbackDiagnostic = OneLine(ex.Message);
            }
        }

        // Flextight metadata is a scanner declaration, not a bit-depth guess. Keep honouring it
        // before consulting the selected fallback; its primaries remain explicitly unknown.
        // The declaration does not, however, waive managed-v2 admission: an untagged scanner
        // file still needs either a deliberate fallback choice or the frozen legacy policy.
        bool fallbackAdmitted = TiffInputAssumptionPolicy.AdmitsFallback(inputAssumption)
            || inputAssumption == TiffInputAssumption.LegacyByBitDepthCompatibility;
        if (fallbackAdmitted
            && fallbackDiagnostic is null
            && FlextightMeta.Read(path).HasEncodingGamma)
            return LoadManagedCompatibility(
                path,
                "managed v2 scanner-vendor gamma declaration; primaries uncharacterized",
                region);

        return inputAssumption switch
        {
            TiffInputAssumption.Linear => DecodeManagedLinearAssumption(
                path, stableId, ExplicitPrefix("linear", fallbackDiagnostic), region),
            TiffInputAssumption.Srgb => DecodeManagedSrgbAssumption(
                path, stableId, ExplicitPrefix("sRGB", fallbackDiagnostic), colorManagement, region),
            TiffInputAssumption.LegacyByBitDepthCompatibility when fallbackDiagnostic is null =>
                LoadManagedCompatibility(
                    path,
                    "managed v2 versioned legacy-by-bit-depth compatibility",
                    region),
            TiffInputAssumption.Unspecified => DecodeManagedDetected(
                path, stableId, fallbackDiagnostic, colorManagement, region),
            TiffInputAssumption.LegacyByBitDepthCompatibility =>
                throw MissingTiffInputAssumption(path, fallbackDiagnostic),
            _ => throw new ArgumentOutOfRangeException(nameof(inputAssumption), inputAssumption, null),
        };
    }

    /// <summary>
    /// Resolves an untagged file from what it actually carries. A file that characterized itself
    /// through TIFF 6.0 or Exif tags gets an exact profile built from those tags — a better answer
    /// than the two-way Linear/sRGB question could ever produce. Everything else lands on one of
    /// the two assumption routes below, carrying the detector's reason into the decode recipe so
    /// the choice stays auditable.
    /// </summary>
    private static WorkingFrame[] DecodeManagedDetected(
        string path,
        string stableId,
        string? fallbackDiagnostic,
        IColorManagementEngine colorManagement,
        RegionRequest? region)
    {
        TiffInputDetection detection = TiffInputDetector.Detect(path);
        string prefix = fallbackDiagnostic is null
            ? "managed v2 detected input"
            : $"managed v2 embedded ICC unavailable ({fallbackDiagnostic}); detected input";
        string reason = $"{detection.Evidence}: {detection.Diagnostic}";

        if (detection.CharacterizedSpace is ColorSpaceDef characterized)
        {
            var profile = ColorProfileRef.Create(
                IccProfiles.Build(characterized),
                $"Detected TIFF input ({characterized.Name})",
                ProfileRole.Input,
                new ProfileSource.Generated(
                    "OpenRevelare.TiffInputDetector", detection.Evidence.ToString()));
            return DecodeManagedWithProfile(
                path,
                stableId,
                profile,
                characterized.Transfer == TransferFunction.Linear
                    ? ColorReference.SceneReferred
                    : ColorReference.DisplayReferred,
                $"{prefix} [{reason}] -> exact profile from file tags -> LittleCMS -> linear ACEScg",
                colorManagement,
                region);
        }

        // Normally unreachable — LoadManagedWorkingFrame honours the vendor declaration before
        // it gets here — but the detector now reports it, so route it the same way rather than
        // let the two-way fallback below apply an sRGB curve the file never declared.
        if (detection.Evidence == TiffInputEvidence.VendorGammaDeclaration)
            return LoadManagedCompatibility(
                path,
                $"{prefix} [{reason}] -> scanner-vendor gamma declaration; primaries uncharacterized",
                region);

        return detection.Assumption == TiffInputAssumption.Linear
            ? DecodeManagedLinearAssumption(path, stableId, $"{prefix} [{reason}]", region)
            : DecodeManagedSrgbAssumption(path, stableId, $"{prefix} [{reason}]", colorManagement, region);
    }

    /// <summary>Recipe wording for a user-selected roll override.</summary>
    private static string ExplicitPrefix(string kind, string? fallbackDiagnostic) =>
        fallbackDiagnostic is null
            ? $"managed v2 explicit roll fallback {kind}"
            : $"managed v2 embedded ICC unavailable ({fallbackDiagnostic}); " +
              $"explicit roll fallback {kind}";

    private static WorkingFrame[] DecodeManagedSrgbAssumption(
        string path,
        string stableId,
        string prefix,
        IColorManagementEngine colorManagement,
        RegionRequest? region)
    {
        return DecodeManagedWithProfile(
            path,
            stableId,
            BuiltInColorProfiles.Srgb(ProfileRole.Input),
            ColorReference.DisplayReferred,
            $"{prefix} -> exact built-in sRGB -> LittleCMS -> linear ACEScg",
            colorManagement,
            region);
    }

    private static WorkingFrame[] DecodeManagedLinearAssumption(
        string path,
        string stableId,
        string prefix,
        RegionRequest? region)
    {
        using Tiff tif = Tiff.Open(path, "r")
            ?? throw new IOException($"could not open TIFF for managed linear admission: {path}");
        (SampleWindow[] windows, int bitsPerSample, float[][] encoded, float codeStep, NumericRange range) =
            ReadTiffSamples(tif, path, region, stage: null);
        ImageBuffer[] pixels = Buffers(windows, encoded, codeStep);
        CaptureKind captureKind = bitsPerSample switch
        {
            8 => CaptureKind.TiffUntagged8Bit,
            16 => CaptureKind.TiffUntagged16Bit,
            _ => CaptureKind.Other,
        };
        var original = new UncharacterizedPixelEncoding(
            captureKind,
            stableId,
            CompatibilityPolicy.None,
            TransferState.LinearInProfilePrimaries,
            range);
        var source = new SourceDescriptor(
            stableId,
            Path.GetFileName(path),
            original,
            $"{prefix}; primaries uncharacterized; numeric working-space passthrough");
        return Frames(pixels, WorkingSpaceId.LinearAcesCgV1, WorkingAdmission.ExplicitUncharacterizedPassthrough, source);
    }

    /// <summary>The windows' samples as buffers, each stamped with the same source lattice.</summary>
    private static ImageBuffer[] Buffers(SampleWindow[] windows, float[][] samples, double quantisationStep)
    {
        var buffers = new ImageBuffer[windows.Length];
        for (int k = 0; k < windows.Length; k++)
        {
            buffers[k] = new ImageBuffer(windows[k].OutWidth, windows[k].OutHeight, samples[k])
            {
                SourceQuantisationStep = quantisationStep,
            };
        }
        return buffers;
    }

    private static WorkingFrame[] LoadManagedCompatibility(string path, string prefix, RegionRequest? region)
    {
        WorkingFrame[] legacy = LoadWorkingFrame(path, inputIsSrgb: false, region);
        var source = new SourceDescriptor(
            legacy[0].Source.StableSourceId,
            legacy[0].Source.DisplayName,
            legacy[0].Source.OriginalEncoding,
            $"{prefix} -> {legacy[0].Source.DecodeRecipe}");
        var frames = new WorkingFrame[legacy.Length];
        for (int k = 0; k < legacy.Length; k++)
        {
            frames[k] = new WorkingFrame(
                legacy[k].Pixels,
                legacy[k].Space,
                WorkingAdmission.LegacyUncharacterizedPassthrough,
                source);
        }
        return frames;
    }

    private static ColorManagementException MissingTiffInputAssumption(
        string path,
        string? fallbackDiagnostic)
    {
        string reason = fallbackDiagnostic is null
            ? "has no embedded ICC profile"
            : $"has no usable embedded ICC profile ({fallbackDiagnostic})";
        return new ColorManagementException(
            $"Managed TIFF input '{Path.GetFileName(path)}' {reason}. " +
            "Select an explicit roll-level Linear or sRGB input assumption; " +
            "bit depth is not a ManagedV2 colour policy.");
    }

    private static string OneLine(string text) =>
        string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    private static WorkingFrame[] DecodeManagedWithProfile(
        string path,
        string stableId,
        ColorProfileRef sourceProfile,
        ColorReference sourceReference,
        string decodeRecipe,
        IColorManagementEngine colorManagement,
        RegionRequest? region)
    {
        ColorProfileRef workingProfile = BuiltInColorProfiles.LinearAcesCg(ProfileRole.Working);

        EnsureValidInputProfile(colorManagement, sourceProfile, path, "source");
        EnsureValidInputProfile(colorManagement, workingProfile, path, "working destination");

        var request = new ColorTransformRequest(
            sourceProfile,
            workingProfile,
            TransformPurpose.InputToWorking,
            RenderingIntent.RelativeColorimetric,
            blackPointCompensation: false,
            adaptationState: 1.0);

        // Lease creation constructs the complete native transform. It happens before any
        // scanline is decoded, so failure cannot leave a half-transformed frame to fall back
        // into the legacy path.
        IColorTransformLease LeaseTransform()
        {
            try
            {
                return colorManagement.Lease(request);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new ColorTransformCreationException(
                    $"Managed TIFF input could not create the complete ICC transform for " +
                    $"'{Path.GetFileName(path)}' ({sourceProfile.Identity} -> {workingProfile.Identity}).",
                    ex);
            }
        }

        using Tiff tif = Tiff.Open(path, "r")
            ?? throw new IOException($"could not reopen TIFF for managed decode: {path}");
        float codeStep = ProbeCodeStep(tif);

        using IColorTransformLease transform = LeaseTransform();

        // Four private probe pixels estimate the source lattice after the full colour
        // conversion: black plus one code in each source primary. Same lease, same native
        // transform, their own Apply call — a per-pixel conversion gives the same answer for a
        // pixel whether it is batched with the image or on its own. Because a 3-D colour
        // conversion has no exact scalar lattice, SourceQuantisationStep records the
        // conservative largest first-code component in working-space units.
        double quantisationStep = ManagedQuantisationStep(ConvertProbes(transform, codeStep, path));

        // Converted row by row as the reader produces it, in place, before any box average —
        // the average has to be taken in the working space, the same order as converting the
        // whole frame and then cropping and boxing it, so the pixels agree bit for bit. The
        // reader may cut the rows into bands on several threads; a LittleCMS lease is
        // thread-affine, so a band on THIS thread uses the lease above and a band on any other
        // leases the same transform for itself and releases it with its rows. Nothing partial is
        // ever admitted: a failure anywhere throws and the frame is not produced.
        int ownerThread = Environment.CurrentManagedThreadId;
        SampleWindow[] windows;
        float[][] encoded;
        NumericRange sourceRange;
        try
        {
            (windows, _, encoded, _, sourceRange) = ReadTiffSamples(
                tif,
                path,
                region,
                () =>
                {
                    if (Environment.CurrentManagedThreadId == ownerThread)
                        return new RowStage((rgb, count) => transform.Apply(rgb, rgb, count), null);
                    IColorTransformLease lease = LeaseTransform();
                    return new RowStage((rgb, count) => lease.Apply(rgb, rgb, count), lease);
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException
                                   and not IOException and not NotSupportedException
                                   and not ArgumentException and not ColorTransformCreationException)
        {
            // The reader's own failures (file, layout, an empty window) pass through as they
            // always did; anything the transform raised is reported as the transform's.
            throw new ColorManagementException(
                $"Managed TIFF input failed while applying the atomic ICC transform for " +
                $"'{Path.GetFileName(path)}'; no legacy or partial result was admitted.",
                ex);
        }

        ImageBuffer[] pixels = Buffers(windows, encoded, quantisationStep);
        var original = new CharacterizedPixelEncoding(
            sourceProfile,
            sourceReference,
            TransferState.ProfileEncoded,
            sourceRange);
        var source = new SourceDescriptor(
            stableId,
            Path.GetFileName(path),
            original,
            decodeRecipe);
        return Frames(pixels, WorkingSpaceId.LinearAcesCgV1, WorkingAdmission.ConvertedFromCharacterized, source);
    }

    private static bool TryReadIccPayloadStrict(Tiff tif, string path, out byte[]? payload)
    {
        payload = null;
        FieldValue[]? field;
        try
        {
            field = tif.GetField(TiffTag.ICCPROFILE);
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new ColorManagementException(
                $"TIFF ICC tag in '{Path.GetFileName(path)}' is present but unreadable.", ex);
        }

        if (field is null) return false;
        if (field.Length < 2)
        {
            throw new ColorManagementException(
                $"TIFF ICC tag in '{Path.GetFileName(path)}' has no profile payload.");
        }

        try
        {
            int declaredLength = field[0].ToInt();
            byte[] bytes = field[1].ToByteArray();
            if (declaredLength <= 0 || bytes.Length == 0)
            {
                throw new ColorManagementException(
                    $"TIFF ICC tag in '{Path.GetFileName(path)}' contains an empty profile.");
            }
            if (declaredLength != bytes.Length)
            {
                throw new ColorManagementException(
                    $"TIFF ICC tag in '{Path.GetFileName(path)}' declares {declaredLength} bytes " +
                    $"but exposes {bytes.Length} bytes.");
            }
            payload = bytes;
            return true;
        }
        catch (ColorManagementException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new ColorManagementException(
                $"TIFF ICC tag in '{Path.GetFileName(path)}' is malformed.", ex);
        }
    }

    private static void EnsureValidInputProfile(
        IColorManagementEngine colorManagement,
        ColorProfileRef profile,
        string path,
        string role)
    {
        ProfileValidationResult validation;
        try
        {
            validation = colorManagement.Validate(profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new ColorManagementException(
                $"Managed TIFF input could not validate the {role} ICC profile for " +
                $"'{Path.GetFileName(path)}' ({profile.Identity}).",
                ex);
        }

        if (!validation.IsValid)
        {
            throw new ColorManagementException(
                $"Managed TIFF input rejected the {role} ICC profile for " +
                $"'{Path.GetFileName(path)}' ({profile.Identity}): {validation.Message}");
        }
    }

    /// <summary>
    /// The managed decode's sample reader: the whole frame, or the window <paramref name="region"/>
    /// resolves to, through <see cref="ReadWindow"/>. <paramref name="transform"/> runs on each
    /// window row before it is stored or averaged — the hook a per-row colour transform needs;
    /// <paramref name="stage"/> makes one per reading thread. One sample array per window, sized
    /// by that window.
    /// </summary>
    private static (SampleWindow[] Windows, int BitsPerSample, float[][] Encoded, float CodeStep, NumericRange Range)
        ReadTiffSamples(Tiff tif, string path, RegionRequest? region, RowStageFactory? stage)
    {
        int w = tif.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int h = tif.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int bps = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
        int spp = tif.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();

        var planarField = tif.GetField(TiffTag.PLANARCONFIG);
        var planar = planarField != null ? (PlanarConfig)planarField[0].ToInt() : PlanarConfig.CONTIG;
        if (planar != PlanarConfig.CONTIG)
            throw new NotSupportedException("only chunky (CONTIG) TIFF is supported in managed input");
        bool isFloat = ValidateSampleStorage(tif, bps);
        if (spp < 1)
            throw new NotSupportedException($"unexpected SamplesPerPixel {spp}");

        float codeStep = isFloat ? 0.0f : bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;
        float sampleScale = isFloat ? 1.0f : codeStep;
        SampleWindow[] windows = SampleWindow.Resolve(w, h, region);
        float[][] encoded = ReadWindows(tif, path, spp, bps, sampleScale, windows, stage);
        return (windows, bps, encoded, codeStep,
            isFloat ? NumericRange.Extended : NumericRange.Normalized);
    }

    /// <summary>
    /// Returns true for 32-bit IEEE-float storage and false for baseline unsigned integer
    /// storage. Other sample layouts are rejected before any scanline is interpreted.
    /// </summary>
    private static bool ValidateSampleStorage(Tiff tif, int bitsPerSample)
    {
        FieldValue[]? field = tif.GetField(TiffTag.SAMPLEFORMAT);
        SampleFormat? sampleFormat = field is { Length: > 0 }
            ? (SampleFormat)field[0].ToInt()
            : null;

        if (bitsPerSample == 32 && sampleFormat == SampleFormat.IEEEFP)
            return true;
        if (bitsPerSample is 8 or 16
            && (sampleFormat is null || sampleFormat == SampleFormat.UINT))
            return false;

        string describedFormat = sampleFormat?.ToString() ?? "default unsigned";
        throw new NotSupportedException(
            $"unsupported TIFF sample storage: BitsPerSample={bitsPerSample}, " +
            $"SampleFormat={describedFormat} (need uint8, uint16, or IEEE-float32)");
    }

    /// <summary>The encoded-domain code step the probes are built from, off the header alone.</summary>
    private static float ProbeCodeStep(Tiff tif)
    {
        int bps = tif.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
        bool isFloat = ValidateSampleStorage(tif, bps);
        return isFloat ? 0.0f : bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;
    }

    /// <summary>
    /// The four lattice probes — black, then one source code in each primary — through
    /// <paramref name="transform"/>. Twelve floats in, twelve out; see the caller for why they
    /// no longer ride along with the image.
    /// </summary>
    private static float[] ConvertProbes(IColorTransformLease transform, float codeStep, string path)
    {
        var probes = new float[12];
        probes[3] = codeStep;
        probes[7] = codeStep;
        probes[11] = codeStep;
        try
        {
            transform.Apply(probes, probes, 4);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new ColorManagementException(
                $"Managed TIFF input failed while applying the ICC transform to the lattice " +
                $"probes for '{Path.GetFileName(path)}'.",
                ex);
        }
        return probes;
    }

    private static double ManagedQuantisationStep(ReadOnlySpan<float> convertedProbes)
    {
        ReadOnlySpan<float> black = convertedProbes[..3];
        double largest = 0.0;
        for (int probe = 1; probe < 4; probe++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = Math.Abs(convertedProbes[probe * 3 + channel] - black[channel]);
                if (double.IsFinite(difference)) largest = Math.Max(largest, difference);
            }
        }
        return largest;
    }

    /// <summary>Load a TIFF into a linear-light f32 image.</summary>
    public static ImageBuffer LoadTiff(string path, bool inputIsSrgb) => LoadTiff(path, inputIsSrgb, null)[0];

    /// <summary>
    /// The frozen v1 decode of one PIXEL rectangle at full resolution — the same pixels the whole
    /// decode holds there, and only those.
    /// </summary>
    public static ImageBuffer LoadTiffRegionExact(string path, bool inputIsSrgb, (int X, int Y, int W, int H) px)
    {
        var (w, h) = ReadTiffSize(path);
        if (px.W <= 0 || px.H <= 0 || px.X < 0 || px.Y < 0 || px.X + px.W > w || px.Y + px.H > h)
            throw new ArgumentOutOfRangeException(nameof(px), px, $"outside the {w}×{h} image");
        // SampleWindow rounds rect × size back to the nearest integer; x/W × W lands within an ulp
        // or two of x, so the window is exactly the pixels asked for.
        var rect = ((double)px.X / w, (double)px.Y / h, (double)px.W / w, (double)px.H / h);
        return LoadTiff(path, inputIsSrgb, new RegionRequest(rect, 0))[0];
    }

    /// <summary>
    /// The frozen v1 decode boxed to each of <paramref name="maxEdges"/> off one pass —
    /// <see cref="Resample.Box"/> of <see cref="LoadTiff(string, bool)"/> pixel for pixel, with
    /// the full frame never allocated.
    /// </summary>
    public static ImageBuffer[] LoadTiffPreviews(string path, bool inputIsSrgb, int[] maxEdges)
    {
        ArgumentNullException.ThrowIfNull(maxEdges);
        if (maxEdges.Length == 0)
            throw new ArgumentException("At least one preview edge is required.", nameof(maxEdges));
        return LoadTiff(path, inputIsSrgb, new RegionRequest((0, 0, 1, 1), (int[])maxEdges.Clone()));
    }

    /// <summary>
    /// The frozen v1 decode, over the whole frame or one <see cref="RegionRequest"/>. The window
    /// form is what <see cref="Geometry.ApplyCrop"/> then <see cref="Resample.Box"/> of the whole
    /// decode would give, pixel for pixel — see <see cref="ReadWindow"/> — without the whole
    /// decode.
    /// </summary>
    private static ImageBuffer[] LoadTiff(string path, bool inputIsSrgb, RegionRequest? region)
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
        bool isFloat = ValidateSampleStorage(tif, bps);
        if (spp < 1)
            throw new NotSupportedException($"unexpected SamplesPerPixel {spp}");

        IccTransform icc = ResolveIcc(tif, inputIsSrgb, path, bps);

        float inv = isFloat ? 1.0f : bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;
        float codeStep = isFloat ? 0.0f : inv;

        RowTransform? transform = null;
        if (inputIsSrgb)
        {
            transform = (rgb, count) =>
            {
                for (int i = 0; i < count * 3; i++) rgb[i] = Srgb.SrgbToLinear(rgb[i]);
            };
        }
        else if (!icc.IsIdentity)
        {
            transform = (rgb, count) =>
            {
                for (int i = 0; i < count * 3; i += 3)
                    ApplyIcc(icc, ref rgb[i], ref rgb[i + 1], ref rgb[i + 2], preserveExtendedRange: isFloat);
            };
        }

        SampleWindow[] windows = SampleWindow.Resolve(w, h, region);
        // The v1 transforms are stateless — read-only LUTs and a matrix — so every band shares
        // the one delegate and there is nothing to release.
        RowStageFactory? stage = transform is null ? null : () => new RowStage(transform, null);
        float[][] data = ReadWindows(tif, path, spp, bps, inv, windows, stage);

        // Stamp the source lattice while the bit depth is still in scope; see
        // ImageBuffer.SourceQuantisationStep for why this cannot be recovered downstream. A
        // boxed window inherits it unchanged, exactly as Resample.Box would have.
        return Buffers(windows, data, ShadowStep(codeStep, icc, inputIsSrgb));
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
        bool isFloat = ValidateSampleStorage(tif, bps);

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
        float inv = isFloat ? 1.0f : bps == 16 ? 1.0f / 65535.0f : 1.0f / 255.0f;
        float codeStep = isFloat ? 0.0f : inv;

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
                    ApplyIcc(icc, ref r, ref g, ref b, preserveExtendedRange: isFloat);
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
        return new ImageBuffer(outW, outH, acc)
        {
            SourceQuantisationStep = ShadowStep(codeStep, icc, inputIsSrgb),
        };
    }

    /// <summary>Compression choice for integer and floating-point TIFF export.</summary>
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
               mode, ProfileBytes(iccSpace is ColorSpace c ? Legacy(c) : null), description));

    /// <summary>
    /// As above, embedding the profile of any registered space. The caller is responsible for
    /// having actually rendered the pixels into <paramref name="iccSpace"/> — see
    /// <see cref="OutputRender"/>; a profile that disagrees with the data is worse than none.
    /// </summary>
    public static void ExportTiff16(ImageBuffer img, string path, CompressionMode mode,
                                    ColorSpaceDef? iccSpace, string? description = null)
        => ExportFile.Write(path, target => WriteTiff16(
               img, target, mode, ProfileBytes(iccSpace), description));

    /// <summary>
    /// Typed TIFF exporter. Normalized frames use unsigned 16-bit samples; extended frames use
    /// IEEE-float32 so negative and greater-than-one values survive. Both variants embed the exact
    /// immutable profile bytes that travelled with the pixels.
    /// </summary>
    public static void ExportTiff(
        RenderedFrame frame,
        string path,
        CompressionMode mode = CompressionMode.Lzw,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Range == NumericRange.Extended)
        {
            ExportTiffFloat32(frame, path, mode, description, profilePolicy);
            return;
        }

        ExportTiff16(frame, path, mode, description, profilePolicy);
    }

    /// <summary>
    /// Typed normalized TIFF16 exporter. Extended frames are rejected rather than silently
    /// clipping them; use <see cref="ExportTiff"/> for automatic lossless storage selection.
    /// </summary>
    public static void ExportTiff16(
        RenderedFrame frame,
        string path,
        CompressionMode mode = CompressionMode.Lzw,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Range != NumericRange.Normalized)
        {
            throw new NotSupportedException(
                "TIFF16 requires normalized pixels; use typed ExportTiff for extended float32 output.");
        }
        byte[]? profileBytes = ExportColorPolicy.ResolveProfileBytes(frame, profilePolicy);
        ExportFile.Write(path, target => WriteTiff16(
            frame.Pixels, target, mode, profileBytes, description));
    }

    /// <summary>Typed IEEE-float32 TIFF exporter for extended-range pixels.</summary>
    public static void ExportTiffFloat32(
        RenderedFrame frame,
        string path,
        CompressionMode mode = CompressionMode.Lzw,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Range != NumericRange.Extended)
        {
            throw new NotSupportedException(
                "Float32 TIFF is reserved for frames whose typed numeric range is Extended.");
        }
        byte[]? profileBytes = ExportColorPolicy.ResolveProfileBytes(frame, profilePolicy);
        ExportFile.Write(path, target => WriteTiffFloat32(
            frame.Pixels, target, mode, profileBytes, description));
    }

    /// <summary>Bridges the two-value legacy enum onto the registry.</summary>
    internal static ColorSpaceDef Legacy(ColorSpace c) =>
        c == ColorSpace.AdobeRgb ? ColorSpaces.AdobeRgb : ColorSpaces.Srgb;

    private static byte[]? ProfileBytes(ColorSpaceDef? space) =>
        space is ColorSpaceDef value ? IccProfiles.Build(value) : null;

    private static void WriteTiff16(ImageBuffer img, string path, CompressionMode mode,
                                    byte[]? iccBytes, string? description)
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

        if (iccBytes is { Length: > 0 })
        {
            tif.SetField(TiffTag.ICCPROFILE, iccBytes.Length, iccBytes);
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

    private static void WriteTiffFloat32(ImageBuffer img, string path, CompressionMode mode,
                                         byte[]? iccBytes, string? description)
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
        tif.SetField(TiffTag.BITSPERSAMPLE, 32);
        tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, compression);
        // Do not use the integer horizontal predictor here. Floating-point predictor support
        // varies across TIFF readers, while raw LZW/Deflate float scanlines are portable.
        tif.SetField(TiffTag.ROWSPERSTRIP, tif.DefaultStripSize(0));

        if (iccBytes is { Length: > 0 })
            tif.SetField(TiffTag.ICCPROFILE, iccBytes.Length, iccBytes);
        if (description is not null)
        {
            tif.SetField(TiffTag.IMAGEDESCRIPTION, ToAsciiSafe(description));
            byte[] uc = System.Text.Encoding.ASCII.GetBytes("UNICODE\0")
                .Concat(System.Text.Encoding.Unicode.GetBytes(description)).ToArray();
            tif.SetField(ExifUserCommentTag, uc.Length, uc);
        }
        tif.SetField(TiffTag.SOFTWARE, SoftwareTag);

        float[] src = img.Data;
        byte[] row = new byte[checked(w * 3 * sizeof(float))];
        for (int y = 0; y < h; y++)
        {
            int sourceOffset = checked(y * w * 3 * sizeof(float));
            Buffer.BlockCopy(src, sourceOffset, row, 0, row.Length);
            if (!tif.WriteScanline(row, y))
                throw new IOException($"failed writing float TIFF scanline {y}");
        }

        tif.FlushData();
    }

    /// <summary>
    /// Read one sample at sample index <paramref name="s"/>. Integer storage is returned as its
    /// code value and scaled by the caller; IEEE-float32 is returned verbatim.
    /// </summary>
    private static float Sample(byte[] buf, int s, int bps)
    {
        if (bps == 32)
            return BitConverter.ToSingle(buf, s * sizeof(float));
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
