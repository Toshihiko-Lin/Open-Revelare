using Sdcb.LibRaw;

namespace OpenRevelare.Core;

/// <summary>
/// Camera RAW decoder via LibRaw (Sdcb.LibRaw binding). Port of the core
/// <c>negative/raw_decode.py::_decode_rawpy</c> path.
///
/// Returns a linear-light float32 (H,W,3) array in CAMERA-NATIVE colour — the
/// UniWB baseline: no white balance, no tone curve, no colour matrix, no
/// histogram stretch. White balance is applied later in the positive domain.
/// This is the shared entry line for both the white-light and RGB-decouple paths.
///
/// The Adobe DNG Converter quality path (Windows-only, raw_decode.py) is a later
/// optional add-on; this is the cross-platform core every backend falls back to.
/// </summary>
public static class RawDecode
{
    // Common camera RAW extensions (superset of the formats LibRaw handles).
    private static readonly HashSet<string> RawExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".nef", ".cr2", ".cr3", ".dng", ".raf", ".rw2", ".orf", ".pef",
        ".srw", ".dcr", ".kdc", ".mrw", ".3fr", ".mos", ".iiq", ".nrw", ".srf",
        ".x3f", ".erf", ".mef", ".rwl",
    };

    /// <summary>True when the path's extension is a known camera RAW format.</summary>
    public static bool IsRawExtension(string path) => RawExts.Contains(Path.GetExtension(path));

    /// <summary>FBDD Bayer-domain chroma noise reduction (pre-demosaic). Port of raw_decode.py's
    /// FBDD_OFF/LIGHT/FULL. Values match LibRaw's fbdd_noiserd (0/1/2).</summary>
    public enum FbddMode { Off = 0, Light = 1, Full = 2 }

    /// <summary>RAW decode backend. Port of raw_decode.py BACKEND_AUTO/RAWPY/DNG.</summary>
    public enum RawBackend { Auto, LibRaw, Dng }

    /// <summary>
    /// Which demosaic to spend time on.
    ///
    /// <see cref="Demosaic.Full"/> is AHD — the reference, and what the rawpy parity baseline
    /// was measured against. Everything whose VALUES matter uses it: the export, and the Path A
    /// calibration ROI means the whole colour basis rests on.
    ///
    /// <see cref="Demosaic.Preview"/> is PPG, ~2.5× faster on the demosaic step (measured on a
    /// 60 MP ARW: 1931 ms → 782 ms, taking the whole decode 3117 → 1916 ms). It is for buffers
    /// that get box-downsampled by 6× on their way to a 1600 px preview, where an edge-adaptive
    /// demosaic's advantage is averaged away long before anyone sees it.
    ///
    /// The choice is NOT free of consequences and the split is deliberate: measured on a real
    /// frame, PPG vs AHD previews differ in 83% of samples (mean 7.8 / max 1034 of 65535), but
    /// the aggregate statistics Stage 1 actually reads move by only ~0.016% (per-channel p99.9).
    /// So: fine for anything reduced to a mean or a percentile, not for the export itself.
    /// </summary>
    public enum Demosaic { Full, Preview }

    private static DemosaicAlgorithm Algorithm(Demosaic d) => d == Demosaic.Preview
        ? DemosaicAlgorithm.PatternedPixelGrouping
        : DemosaicAlgorithm.AdaptiveHomogeneityDirected;


    /// <summary>
    /// The camera-declared image area (libraw sizes.raw_inset_crops[0]), to hand to
    /// LibRaw's own params.cropbox. Null when it cannot be read or does not look sane —
    /// in which case we decode the full frame, as before.
    ///
    /// WHY THIS EXISTS: Sdcb bundles LibRaw 0.21, which does not crop the visible frame
    /// for (at least) Sony A7R IV — it reports sizes.width == iwidth == raw_width with all
    /// margins 0 and fills only part of a raw_width x raw_height buffer, leaving pure-zero
    /// padding. rawpy's LibRaw 0.22 does crop. Zero padding is not cosmetic: on a negative
    /// T=0 hits the -log10 1e-10 clamp -> density 10 (real highlights are ~1.0-1.5), which
    /// blows out to white in the positive and poisons DetectDMax / EstimateDarkValley.
    /// LibRaw 0.22.x would be the clean fix but no such NuGet package exists (Sdcb.LibRaw
    /// tops out at 0.21.1.7 and is the only maintained .NET binding), so we drive LibRaw's
    /// own crop with the file's own metadata instead of cutting pixels ourselves.
    ///
    /// WHY THE REFLECTION: Sdcb 0.21 exposes no accessor for libraw_image_sizes_t, so we
    /// marshal it off the native libraw_data_t handle, where it sits at offset 8 (right
    /// after the leading `ushort (*image)[4]` pointer on x64). That offset is an assumption
    /// about a struct layout we do not own, so it is VALIDATED against values we can read
    /// through the supported API before any of it is trusted; a mismatch returns null
    /// rather than cropping to garbage.
    /// </summary>
    private static System.Drawing.Rectangle? CameraInsetCrop(RawContext ctx)
    {
        try
        {
            var handleField = typeof(RawContext).GetField("_r",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (handleField?.GetValue(ctx) is not IntPtr h || h == IntPtr.Zero) return null;

            var sizes = System.Runtime.InteropServices.Marshal
                .PtrToStructure<Sdcb.LibRaw.Natives.LibRawImageSizes>(h + 8);

            // Layout check: if these disagree, offset 8 is not sizes and nothing below means
            // anything.
            if (sizes.RawWidth != ctx.RawWidth || sizes.RawHeight != ctx.RawHeight) return null;
            if (sizes.RawInsetCrops is null || sizes.RawInsetCrops.Length == 0) return null;

            var c = sizes.RawInsetCrops[0];
            // 65535 / 0 are LibRaw's "unset" sentinels; also require the box to sit inside
            // the raw frame and to actually be a crop.
            if (c.CWidth == 0 || c.CHeight == 0 || c.CLeft == 65535 || c.CTop == 65535) return null;
            if (c.CLeft + c.CWidth > sizes.RawWidth || c.CTop + c.CHeight > sizes.RawHeight) return null;
            if (c.CLeft + c.CWidth == sizes.RawWidth && c.CTop + c.CHeight == sizes.RawHeight
                && c.CLeft == 0 && c.CTop == 0) return null;   // full frame — nothing to do

            return new System.Drawing.Rectangle(c.CLeft, c.CTop, c.CWidth, c.CHeight);
        }
        catch
        {
            return null;   // never let a metadata probe break decoding
        }
    }

    /// <summary>Decode a RAW file to a linear-light, camera-native, UniWB float32 image.
    /// <paramref name="fbdd"/> = pre-demosaic chroma noise reduction (default off, as before).</summary>
    public static ImageBuffer DecodeRaw(string path, FbddMode fbdd = FbddMode.Off)
        => DecodeLibRaw(path, fbdd, halfSize: false);

    /// <summary>
    /// The camera's own colour matrix: linear camera-native RGB → linear sRGB, row-major 3×3.
    /// Null when the file is not a RAW we can open, or the camera is not in LibRaw's database.
    ///
    /// This is the piece that makes the pipeline's colour input KNOWN rather than assumed.
    /// <see cref="DecodeRaw(string, FbddMode)"/> decodes camera-native on purpose — the density
    /// maths wants the sensor's own numbers, undisturbed — but that leaves the result in a space
    /// whose primaries nothing had recorded. Calling the result "sRGB" was a label of
    /// convenience, and it is precisely the gap chroma_grade grew into: with no characterised
    /// input there was no conversion to perform, so a scalar was the only lever left.
    /// See docs/CALIBRATION.md.
    ///
    /// LibRaw derives this from the camera's published colorimetry (its cam_xyz, composed with
    /// XYZ→sRGB). Rows sum to 1, so a neutral in camera space stays neutral in sRGB — the
    /// conversion changes chromaticity, never the grey axis, which is what makes it safe to
    /// apply after t_base normalisation has already set the white.
    /// </summary>
    public static double[,]? CameraToSrgbMatrix(string path) => CameraToSrgbMatrix(path, out _);

    /// <summary>
    /// As above, reporting WHY the matrix is unavailable. The reason matters to the user: "this
    /// camera is not in LibRaw's database" and "the file could not be opened at all" call for
    /// completely different responses, and a single "no colour data" message conflates them.
    /// </summary>
    public static double[,]? CameraToSrgbMatrix(string path, out string diagnosis)
    {
        diagnosis = "";
        try
        {
            // OpenFile alone is enough: the colour matrix comes from the camera's metadata, which
            // the header parse already has. Calling Unpack() first would decode the entire raw
            // frame to read nine numbers — measured at 3.5 s per file against 14 ms without it.
            using RawContext ctx = RawContext.OpenFile(path);

            var m = ctx.RgbCamera;
            var result = new double[3, 3];
            double sum = 0;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    result[r, c] = m[r, c];
                    sum += Math.Abs(result[r, c]);
                }

            // An unknown camera leaves the matrix all-zero; an identity means LibRaw had nothing
            // to say either. Both are "no characterisation available", not a usable transform.
            if (sum < 1e-6)
            {
                diagnosis = CoreText.F($"矩阵全零（{ctx.ImageParams.Make} {ctx.ImageParams.Model}）");
                return null;
            }

            bool identity = true;
            for (int r = 0; r < 3 && identity; r++)
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(result[r, c] - (r == c ? 1.0 : 0.0)) > 1e-6) { identity = false; break; }

            if (identity)
            {
                // LibRaw parsed the file but has no colorimetry for this body — its camera table
                // stops before the newer ones. Try the hand-maintained fallback before giving up.
                string make = ctx.ImageParams.Make, model = ctx.ImageParams.Model;
                double[,]? fb = CameraMatrixFallback.CameraToSrgb(make, model);
                if (fb is not null)
                {
                    diagnosis = CoreText.F($"内置后备矩阵（{make} {model}）");
                    return fb;
                }
                diagnosis = CoreText.F($"LibRaw 与内置后备表都没有这台相机（{make} {model}）");
                return null;
            }

            diagnosis = CoreText.F($"LibRaw 的相机矩阵（{ctx.ImageParams.Make} {ctx.ImageParams.Model}）");
            return result;
        }
        catch (Exception ex)
        {
            // A colour probe must never break opening a file — but it must not hide why either.
            diagnosis = CoreText.F($"读取失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // NOTE: there is deliberately no public half-size DECODE entry point any more
    // (raw_decode.py::decode_raw_fast has no live C# counterpart). It existed and became
    // unreachable once the roll warm-up started sharing ONE full-quality decode between the
    // preview cache and the thumbnail pass — which is also the behaviour we want, because
    // whether the warm-up or a frame switch got there first must not change which pixels every
    // Stage-1 measurement is taken on. Half-size survives only inside RoiMeanProbe, whose
    // result feeds nothing but an argmax.

    /// <summary>Open + unpack + dcraw_process on the shared UniWB baseline. The caller owns
    /// the returned context and pulls the processed image out of it.</summary>
    private static RawContext OpenAndProcess(string path, FbddMode fbdd, bool halfSize,
                                             Demosaic demosaic = Demosaic.Full,
                                             System.Drawing.Rectangle? regionInFrame = null)
    {
        RawContext ctx = RawContext.OpenFile(path);
        try
        {
            ctx.Unpack();

            // Half-size skips demosaic, so the inset-crop pixel geometry no longer applies.
            System.Drawing.Rectangle? cropbox = halfSize ? null : CameraInsetCrop(ctx);

            // A region request is expressed in the INSET-CROPPED frame's coordinates (the frame
            // every other part of the app sees), so it composes with the camera's own inset
            // rather than replacing it.
            if (!halfSize && regionInFrame is System.Drawing.Rectangle reg)
            {
                int baseX = cropbox?.X ?? 0, baseY = cropbox?.Y ?? 0;
                cropbox = new System.Drawing.Rectangle(baseX + reg.X, baseY + reg.Y, reg.Width, reg.Height);
            }

            // Mirror rawpy.postprocess(..) from _decode_rawpy: UniWB, raw colour,
            // linear gamma, 16-bit, no auto-bright, AHD demosaic, FBDD per preference.
            ctx.DcrawProcess(p =>
            {
                // True UniWB baseline: unit gain on every channel (user_mul = 1,1,1,1).
                // This is what _decode_rawpy's DOCSTRING intends — no white balance baked
                // in at decode time, so both the white-light and RGB-decouple paths enter
                // Stage 1 on the same line and the decouple matrix maps cleanly. (The
                // shipping Python omits user_wb and so silently falls back to LibRaw's
                // pre_mul daylight coefficients; the rewrite fixes that to match intent.
                // t_base normalisation absorbs the per-channel gain either way, so final
                // frames are equivalent.)
                p.UseCameraWb = false;
                p.UseAutoWb = false;
                p.UserMultipliers = new[] { 1f, 1f, 1f, 1f };       // UniWB (R,G,B,G2)
                p.OutputColor = (LibRawColorSpace)0;                // 0 = raw / camera-native, no matrix
                p.OutputBps = 16;
                p.NoAutoBright = true;                              // no histogram stretch
                p.UserQual = Algorithm(demosaic);                   // AHD, or PPG for previews
                p.Gamma = new[] { 1.0, 1.0, 0.0, 0.0, 0.0, 0.0 };   // linear (gamm[0]=gamm[1]=1)
                p.FbddNoiserd = (int)fbdd;                          // FBDD off / light / full
                p.HalfSize = halfSize;                              // fast preview path
                if (cropbox is System.Drawing.Rectangle cb) p.Cropbox = cb;
            });
            return ctx;
        }
        catch { ctx.Dispose(); throw; }
    }

    /// <summary>
    /// Centre-ROI (40–60%) channel means of a RAW file, on the same UniWB baseline as
    /// <see cref="DecodeRaw(string, FbddMode)"/> but decoded half-size and accumulated
    /// straight off LibRaw's buffer — no full-frame float image is ever materialised.
    ///
    /// This is a *probe*, not a decode: it exists for cheap content classification (which
    /// calibration frame is red / green / blue), where only the relative ordering of the
    /// three means matters. Half-size changes the per-channel values slightly versus a full
    /// AHD decode, so never feed this into the decouple matrix — that still wants
    /// <see cref="DecoupleCalibration.RoiMean"/> over a full <see cref="DecodeRaw(string, FbddMode)"/>.
    /// </summary>
    public static double[] RoiMeanProbe(string path)
    {
        using RawContext ctx = OpenAndProcess(path, FbddMode.Off, halfSize: true);
        using ProcessedImage img = ctx.MakeDcrawMemoryImage();
        if (img.Bits != 16)
            throw new NotSupportedException($"expected 16-bit RAW output, got {img.Bits}-bit");

        int w = img.Width, h = img.Height, ch = img.Channels;
        int r0 = (int)(h * 0.4), r1 = (int)(h * 0.6);
        int c0 = (int)(w * 0.4), c1 = (int)(w * 0.6);
        if (r0 >= r1 || c0 >= c1) { r0 = 0; r1 = h; c0 = 0; c1 = w; }

        ReadOnlySpan<ushort> span = img.AsSpan<ushort>();
        double s0 = 0, s1 = 0, s2 = 0;
        for (int y = r0; y < r1; y++)
        {
            int row = y * w * ch;
            for (int x = c0; x < c1; x++)
            {
                int i = row + x * ch;
                if (ch == 1) { double v = span[i]; s0 += v; s1 += v; s2 += v; }
                else { s0 += span[i]; s1 += span[i + 1]; s2 += span[i + 2]; }
            }
        }

        double n = (double)(r1 - r0) * (c1 - c0) * 65535.0;
        return new[] { s0 / n, s1 / n, s2 / n };
    }

    /// <summary>
    /// Run the decode and hand back ONLY the finished 16-bit image, with the LibRaw context already
    /// torn down.
    ///
    /// The context holds its own copies of the frame — the unpacked Bayer data plus the demosaiced
    /// 4-channel working image, together roughly twice the size of the result — and
    /// <c>MakeDcrawMemoryImage</c> returns an independently allocated buffer that outlives it. So
    /// disposing here, BEFORE the caller allocates its output, keeps those two from being resident
    /// at the same time. With several decodes in flight during an import that ordering is worth
    /// hundreds of megabytes of peak.
    /// </summary>
    private static ProcessedImage Process(string path, FbddMode fbdd, bool halfSize,
                                          Demosaic demosaic = Demosaic.Full,
                                          System.Drawing.Rectangle? regionInFrame = null)
    {
        ProcessedImage img;
        using (RawContext ctx = OpenAndProcess(path, fbdd, halfSize, demosaic, regionInFrame))
            img = ctx.MakeDcrawMemoryImage();
        if (img.Bits != 16)
        {
            img.Dispose();
            throw new NotSupportedException("expected 16-bit RAW output");
        }
        return img;
    }

    private static ImageBuffer DecodeLibRaw(string path, FbddMode fbdd, bool halfSize)
    {
        using ProcessedImage img = Process(path, fbdd, halfSize);
        int w = img.Width, h = img.Height, ch = img.Channels;

        ReadOnlySpan<ushort> span = img.AsSpan<ushort>();
        var data = new float[w * h * 3];
        const float inv = 1.0f / 65535.0f;

        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w * ch;
            int dstRow = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int s = srcRow + x * ch;
                int dst = dstRow + x * 3;
                switch (ch)
                {
                    case 1: // monochrome — replicate
                        data[dst] = data[dst + 1] = data[dst + 2] = span[s] * inv;
                        break;
                    default: // 3 (RGB) or 4 (RGBG/RGBA) — take first three
                        data[dst] = span[s] * inv;
                        data[dst + 1] = span[s + 1] * inv;
                        data[dst + 2] = span[s + 2] * inv;
                        break;
                }
            }
        }

        return new ImageBuffer(w, h, data);
    }

    /// <summary>
    /// Demosaic needs neighbours, and a cropped decode has none at its edges. Measured on a
    /// 60 MP ARW against the same region cut out of a full decode: with this border discarded
    /// the interior is BIT-IDENTICAL (0 differing samples out of 2.5 M), while the border itself
    /// reaches 1119 levels of difference. LibRaw handles the CFA phase for odd offsets by
    /// itself, so no alignment is required — only the margin.
    /// </summary>
    private const int RegionDemosaicMargin = 48;

    /// <summary>
    /// Decode ONE RECTANGLE of a RAW at full resolution, without ever materialising the whole
    /// frame — the sharp-patch path.
    ///
    /// The rectangle is in the coordinates of the inset-cropped frame (what
    /// <see cref="DecodeRaw(string, RawBackend, FbddMode, out bool)"/> would return), and is
    /// grown by <see cref="RegionDemosaicMargin"/> before being handed to LibRaw so the pixels
    /// the caller asked for are all interior. Measured on a 60 MP ARW: 137 MB and 1.14 s for a
    /// 1200x800 patch, against 989 MB and 3.03 s for the whole frame.
    ///
    /// The unpack stage is whole-file and irreducible (~1.07 s of that 1.14 s) — only the
    /// demosaic and output shrink. So this is worth caching, not repeating per pan.
    /// </summary>
    /// <param name="frameWidth">Inset-cropped frame width, for clamping.</param>
    /// <param name="frameHeight">Inset-cropped frame height, for clamping.</param>
    /// <returns>The slice plus the frame-space origin it actually starts at — the request is
    /// clamped at the frame edges, so the caller must use what came back, not what it asked
    /// for. Null when the backend cannot do a region decode (see remarks).</returns>
    /// <remarks>
    /// Works on the DNG-Converter backend too, by cropping the CONVERTED linear DNG. That file's
    /// geometry is Adobe's rather than the sensor's — but so is the geometry of the preview and
    /// the export on that backend, because they all go through the same conversion and the same
    /// <see cref="CameraInsetCrop"/> applied to the same file. The frame coordinates therefore
    /// line up as long as the caller's frame size came from the same backend, which it does (it
    /// comes from the decoded preview). Only worth doing because
    /// <see cref="LinearDngCache"/> keeps that conversion off the per-decode path.
    /// </remarks>
    public static (ImageBuffer Slice, int X0, int Y0)? DecodeRawRegion(
        string path, RawBackend backend, FbddMode fbdd,
        int x, int y, int width, int height, int frameWidth, int frameHeight,
        Demosaic demosaic = Demosaic.Full)
    {
        if (UseDngBackend(backend))
        {
            try
            {
                return WithDngLinear(path, p => DecodeRegionLibRaw(
                    p, fbdd, demosaic, x, y, width, height, frameWidth, frameHeight));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException)
            {
                // Same fallback rule as every other DNG entry point: a converter failure drops
                // to LibRaw rather than failing the operation.
            }
        }
        return DecodeRegionLibRaw(path, fbdd, demosaic, x, y, width, height, frameWidth, frameHeight);
    }

    private static (ImageBuffer Slice, int X0, int Y0)? DecodeRegionLibRaw(
        string path, FbddMode fbdd, Demosaic demosaic,
        int x, int y, int width, int height, int frameWidth, int frameHeight)
    {
        int x0 = Math.Clamp(x - RegionDemosaicMargin, 0, Math.Max(0, frameWidth - 1));
        int y0 = Math.Clamp(y - RegionDemosaicMargin, 0, Math.Max(0, frameHeight - 1));
        int x1 = Math.Clamp(x + width + RegionDemosaicMargin, x0 + 1, frameWidth);
        int y1 = Math.Clamp(y + height + RegionDemosaicMargin, y0 + 1, frameHeight);

        using ProcessedImage img = Process(path, fbdd, halfSize: false, demosaic,
                                           new System.Drawing.Rectangle(x0, y0, x1 - x0, y1 - y0));
        int w = img.Width, h = img.Height, ch = img.Channels;
        ReadOnlySpan<ushort> span = img.AsSpan<ushort>();
        var buf = new ImageBuffer(w, h);
        float[] d = buf.Data;
        const float Inv = 1.0f / 65535.0f;
        for (int yy = 0; yy < h; yy++)
        {
            int s = yy * w * ch, o = yy * w * 3;
            for (int xx = 0; xx < w; xx++)
            {
                int si = s + xx * ch, oi = o + xx * 3;
                if (ch == 1) { float v = span[si] * Inv; d[oi] = d[oi + 1] = d[oi + 2] = v; }
                else { d[oi] = span[si] * Inv; d[oi + 1] = span[si + 1] * Inv; d[oi + 2] = span[si + 2] * Inv; }
            }
        }
        return (buf, x0, y0);
    }

    /// <summary>
    /// Decode a RAW straight to one or more BOX-DOWNSAMPLED buffers, never materialising the
    /// full-resolution float frame.
    ///
    /// Same decode as <see cref="DecodeRaw(string, FbddMode)"/> — full AHD demosaic, same UniWB
    /// baseline, same camera crop — but the box average is accumulated off LibRaw's 16-bit buffer.
    /// The per-sample arithmetic and summation order match <see cref="Resample.Box"/> exactly
    /// (each sample scaled to float by 1/65535 first, then summed, then divided by factor²), so a
    /// frame decoded this way is bit-identical to one decoded whole and downsampled afterwards.
    /// That equality is the point: the previews carry every Stage-1 measurement.
    ///
    /// Several edges in one call because the import needs two sizes of the same frame (the cached
    /// preview and a smaller chroma-measurement buffer) and decoding twice would cost more than
    /// the buffer we are avoiding.
    /// </summary>
    /// <returns>One buffer per entry of <paramref name="maxEdges"/>, plus the source dimensions.</returns>
    public static (ImageBuffer[] Previews, int SourceWidth, int SourceHeight) DecodeRawDownsampled(
        string path, FbddMode fbdd, IReadOnlyList<int> maxEdges, Demosaic demosaic = Demosaic.Full)
    {
        using ProcessedImage img = Process(path, fbdd, halfSize: false, demosaic);
        int w = img.Width, h = img.Height, ch = img.Channels;

        var outs = new ImageBuffer[maxEdges.Count];
        for (int e = 0; e < maxEdges.Count; e++)
            outs[e] = BoxFromRaw(img, w, h, ch, Resample.BoxFactor(w, h, maxEdges[e]));
        return (outs, w, h);
    }

    /// <summary>Box-average LibRaw's 16-bit output into a linear float buffer.</summary>
    private static ImageBuffer BoxFromRaw(ProcessedImage img, int w, int h, int ch, int factor)
    {
        const float Inv = 1.0f / 65535.0f;
        int ow = w / factor, oh = h / factor;
        var dst = new ImageBuffer(ow, oh);
        float[] d = dst.Data;
        float invN = 1.0f / (factor * factor);

        Parallel.For(0, oh, oy =>
        {
            // Re-acquired per row: a span cannot be hoisted into a lambda, and the view is a
            // pointer + length over LibRaw's unmanaged buffer, so this is free.
            ReadOnlySpan<ushort> s = img.AsSpan<ushort>();
            for (int ox = 0; ox < ow; ox++)
            {
                float r = 0f, g = 0f, b = 0f;
                for (int fy = 0; fy < factor; fy++)
                {
                    int sy = oy * factor + fy;
                    int rowBase = (sy * w + ox * factor) * ch;
                    for (int fx = 0; fx < factor; fx++)
                    {
                        int i = rowBase + fx * ch;
                        if (ch == 1) { float v = s[i] * Inv; r += v; g += v; b += v; }
                        else { r += s[i] * Inv; g += s[i + 1] * Inv; b += s[i + 2] * Inv; }
                    }
                }
                int di = (oy * ow + ox) * 3;
                d[di] = r * invN; d[di + 1] = g * invN; d[di + 2] = b * invN;
            }
        });
        return dst;
    }

    /// <summary>
    /// The centre-ROI (40–60%) channel mean of a full-quality decode, accumulated straight off
    /// LibRaw's buffer — the Path A calibration frames' only contribution to the roll.
    ///
    /// Distinct from <see cref="RoiMeanProbe"/>, which decodes HALF-SIZE and is explicitly unfit
    /// for the decouple matrix. This is the full AHD decode <see cref="DecoupleCalibration.RoiMean"/>
    /// wants, summed in float32 in the same row-major order, so the mean is identical — it just
    /// never allocates the ~300–500 MB float frame that mean was being extracted from.
    /// </summary>
    public static double[] RoiMeanFull(string path, RawBackend backend, FbddMode fbdd)
    {
        if (UseDngBackend(backend))
        {
            try { return WithDngLinear(path, p => RoiMeanFullLibRaw(p, fbdd)); }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException) { }
        }
        return RoiMeanFullLibRaw(path, fbdd);
    }

    private static double[] RoiMeanFullLibRaw(string path, FbddMode fbdd)
    {
        using ProcessedImage img = Process(path, fbdd, halfSize: false);
        int w = img.Width, h = img.Height, ch = img.Channels;
        const float Inv = 1.0f / 65535.0f;

        int r0 = (int)(h * 0.4), r1 = (int)(h * 0.6);
        int c0 = (int)(w * 0.4), c1 = (int)(w * 0.6);
        if (r0 >= r1 || c0 >= c1) { r0 = 0; r1 = h; c0 = 0; c1 = w; }

        ReadOnlySpan<ushort> s = img.AsSpan<ushort>();
        float s0 = 0, s1 = 0, s2 = 0;
        int n = 0;
        for (int y = r0; y < r1; y++)          // row-major, matching DecoupleCalibration.RoiMean
        {
            int row = y * w * ch;
            for (int x = c0; x < c1; x++)
            {
                int i = row + x * ch;
                if (ch == 1) { float v = s[i] * Inv; s0 += v; s1 += v; s2 += v; }
                else { s0 += s[i] * Inv; s1 += s[i + 1] * Inv; s2 += s[i + 2] * Inv; }
                n++;
            }
        }
        return new double[] { s0 / (float)n, s1 / (float)n, s2 / (float)n };
    }

    // ── Adobe DNG Converter path (Windows; higher-quality Adobe demosaic) ─────────
    private static readonly string[] DngConverterCandidates =
    {
        @"C:\Program Files\Adobe\Adobe DNG Converter\Adobe DNG Converter.exe",
        @"C:\Program Files (x86)\Adobe\Adobe DNG Converter\Adobe DNG Converter.exe",
    };

    /// <summary>Locate Adobe DNG Converter.exe (Windows), or null.</summary>
    public static string? FindDngConverter()
    {
        foreach (string c in DngConverterCandidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>True when Adobe DNG Converter is installed and reachable.</summary>
    public static bool IsDngConverterAvailable() => FindDngConverter() is not null;

    /// <summary>
    /// Optional supplier of an already-converted linear DNG, so the ~3.5 s Adobe round trip is
    /// paid once per frame instead of once per decode.
    ///
    /// A hook rather than a direct dependency because the cache is a GUI concern (it needs the
    /// user's directory preference and a session lifetime) and Core must stay free of that.
    /// Given the source path and a delegate that writes a linear DNG to a destination, it
    /// returns the path to use — or null to fall back to a throwaway temp conversion.
    /// </summary>
    public static Func<string, Action<string>, string?>? LinearDngCache { get; set; }

    /// <summary>Two-pass Adobe conversion (RAW → mosaic DNG → linear DNG), landing the result at
    /// <paramref name="destPath"/>. The intermediate lives in a temp directory that is always
    /// removed; only the linear DNG survives, for the caller to keep or discard.</summary>
    private static void ConvertToLinearDng(string sourcePath, string destPath)
    {
        string? converter = FindDngConverter()
            ?? throw new FileNotFoundException(CoreText.T("未找到 Adobe DNG Converter，请安装或改用 LibRaw 后端。"));

        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string tmpDir = Path.Combine(Path.GetTempPath(), "revelare_dng_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string preDng = Path.Combine(tmpDir, stem + "_pre.dng");
            string linearDng = Path.Combine(tmpDir, stem + ".dng");
            RunConverter(converter, new[] { "-u", "-p0", "-cr5.4", "-d", tmpDir, "-o", Path.GetFileName(preDng), sourcePath });
            if (!File.Exists(preDng)) throw new InvalidOperationException(CoreText.T("DNG Converter 第一步失败"));
            RunConverter(converter, new[] { "-u", "-l", "-p0", "-dng1.1", "-d", tmpDir, "-o", Path.GetFileName(linearDng), preDng });
            if (!File.Exists(linearDng)) throw new InvalidOperationException(CoreText.T("DNG Converter 第二步失败"));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Move(linearDng, destPath, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>Run <paramref name="read"/> on this source's linear DNG — from
    /// <see cref="LinearDngCache"/> when one is available, else a temp conversion that is
    /// deleted afterwards. Port of raw_decode.py::_decode_dng_converter, with the READ left to
    /// the caller so the full-frame and downsampling decodes can share one conversion.</summary>
    private static T WithDngLinear<T>(string path, Func<string, T> read)
    {
        if (LinearDngCache?.Invoke(path, dest => ConvertToLinearDng(path, dest)) is { } cached)
            return read(cached);

        string? converter = FindDngConverter()
            ?? throw new FileNotFoundException(CoreText.T("未找到 Adobe DNG Converter，请安装或改用 LibRaw 后端。"));

        string stem = Path.GetFileNameWithoutExtension(path);
        string tmpDir = Path.Combine(Path.GetTempPath(), "revelare_dng_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            string preDng = Path.Combine(tmpDir, stem + "_pre.dng");
            string linearDng = Path.Combine(tmpDir, stem + ".dng");

            // Pass 1: RAW → uncompressed DNG (mosaic preserved).
            RunConverter(converter, new[] { "-u", "-p0", "-cr5.4", "-d", tmpDir, "-o", Path.GetFileName(preDng), path });
            if (!File.Exists(preDng)) throw new InvalidOperationException(CoreText.T("DNG Converter 第一步失败"));

            // Pass 2: mosaic DNG → linear DNG (Adobe demosaic).
            RunConverter(converter, new[] { "-u", "-l", "-p0", "-dng1.1", "-d", tmpDir, "-o", Path.GetFileName(linearDng), preDng });
            if (!File.Exists(linearDng)) throw new InvalidOperationException(CoreText.T("DNG Converter 第二步失败"));

            return read(linearDng);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    private static bool UseDngBackend(RawBackend backend)
        => backend == RawBackend.Dng
           || (backend == RawBackend.Auto && OperatingSystem.IsWindows() && IsDngConverterAvailable());

    private static void RunConverter(string exe, string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException(CoreText.T("无法启动 DNG Converter"));
        if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } throw new TimeoutException(CoreText.T("DNG Converter 超时")); }
        if (proc.ExitCode != 0) throw new InvalidOperationException(CoreText.F($"DNG Converter 退出码 {proc.ExitCode}"));
    }

    /// <summary>Decode honouring a backend preference. AUTO uses DNG Converter on Windows when
    /// available, else LibRaw; DNG failures fall back to LibRaw. <paramref name="dngFellBack"/>
    /// reports when a requested DNG decode silently fell back. Port of raw_decode.py::decode_raw.</summary>
    public static ImageBuffer DecodeRaw(string path, RawBackend backend, FbddMode fbdd, out bool dngFellBack)
    {
        dngFellBack = false;
        if (!UseDngBackend(backend)) return DecodeLibRaw(path, fbdd, halfSize: false);
        try { return WithDngLinear(path, p => DecodeLibRaw(p, fbdd, halfSize: false)); }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException)
        {
            dngFellBack = true;
            return DecodeLibRaw(path, fbdd, halfSize: false);
        }
    }

    /// <summary>Backend-honouring <see cref="DecodeRawDownsampled(string, FbddMode, IReadOnlyList{int})"/>.
    /// A DNG-Converter failure falls back to LibRaw, as the full decode does.</summary>
    public static (ImageBuffer[] Previews, int SourceWidth, int SourceHeight) DecodeRawDownsampled(
        string path, RawBackend backend, FbddMode fbdd, IReadOnlyList<int> maxEdges,
        Demosaic demosaic = Demosaic.Full)
    {
        if (UseDngBackend(backend))
        {
            // The DNG path hands LibRaw an ALREADY-DEMOSAICED linear DNG, so UserQual is moot
            // there — Adobe did the demosaic. The quality choice only bites on the LibRaw path.
            try { return WithDngLinear(path, p => DecodeRawDownsampled(p, fbdd, maxEdges, demosaic)); }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException) { }
        }
        return DecodeRawDownsampled(path, fbdd, maxEdges, demosaic);
    }
}
