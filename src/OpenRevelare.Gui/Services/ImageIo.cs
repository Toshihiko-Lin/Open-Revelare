using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Image load + preview-downsample helpers for the GUI, all in the linear-light
/// domain Core works in. Load dispatch mirrors the CLI's <c>LoadLinear</c>: RAW
/// files go through LibRaw (UniWB), everything else is read as a linear TIFF.
/// </summary>
public static class ImageIo
{
    /// <summary>File dialog filter patterns for supported inputs — TIFF plus every RAW the
    /// decoder accepts, derived from <see cref="RawDecode.RawExtensions"/> so a format added
    /// there becomes selectable here without a second edit.</summary>
    public static readonly string[] OpenPatterns =
        new[] { "*.tif", "*.tiff" }
            .Concat(RawDecode.RawExtensions.OrderBy(e => e, StringComparer.Ordinal).Select(e => "*" + e))
            .ToArray();

    // ── Decode admission control ────────────────────────────────────────────────
    //
    // A single decode in flight costs several times the finished frame: LibRaw's unpacked Bayer
    // data, its demosaiced working image, the 16-bit result, and whatever the caller builds from
    // it. Measured on a 60 MP Sony ARW that is ~129 MB Bayer + ~518 MB demosaic workspace +
    // ~388 MB result ≈ 1.0 GB resident at the peak, hence <see cref="PerSlotBytes"/>.
    //
    // ONE gate shared by every entry point here — import warm-up, calibration, thumbnails and
    // export all draw on the same physical memory, so counting them separately is how you get
    // three "safe" limits multiplying into an unsafe total.
    //
    // The limit is RE-EVALUATED as work arrives rather than fixed at startup, and it is sized
    // from memory that is actually FREE, not from total RAM. Those are different questions: a
    // 48 GB workstation with a browser, a game and Lightroom open may have 4 GB left, and the
    // old rule — a fixed three slots chosen once from TotalAvailableMemoryBytes — would happily
    // start three 1 GB decodes into it. Re-checking also means a long import backs off when
    // something else on the machine grows, and opens back up when it exits.
    private const long PerSlotBytes = 1_200L << 20;   // ~1.2 GB per in-flight decode
    private const long ReserveBytes = 2L << 30;       // leave the OS and the rest of the app room
    private const int HardCap = 8;                    // beyond this, decode is not the bottleneck
    private const int LimitRefreshMs = 1500;

    private static readonly object GateLock = new();
    private static int _inFlight;
    private static int _cachedLimit;   // 0 == never computed; see the guard below
    private static long _limitStamp;

    /// <summary>The concurrency currently allowed: the user's override, else derived from free
    /// physical memory. Cached briefly — this is consulted on every decode and, on Windows, each
    /// call is a syscall.</summary>
    /// <remarks>
    /// The <c>_cachedLimit > 0</c> test is load-bearing, not a nicety. Every computed limit is
    /// clamped to at least 1, so zero can only mean "not computed yet" — and it must not be
    /// served, because the caller uses it as <c>while (_inFlight >= limit)</c> and a limit of
    /// zero admits nobody, ever. Relying on the timestamp alone to catch the first call does
    /// not work: any sentinel far in the past makes <c>now - _limitStamp</c> overflow, which
    /// wraps NEGATIVE and reads as "cache is fresh".
    /// </remarks>
    private static int CurrentLimit()
    {
        long now = Environment.TickCount64;
        if (_cachedLimit > 0 && now - _limitStamp < LimitRefreshMs) return _cachedLimit;

        int manual = Settings.Current.DecodeConcurrency;
        _cachedLimit = manual > 0 ? Math.Clamp(manual, 1, HardCap) : AutoLimit();
        _limitStamp = now;
        return _cachedLimit;
    }

    /// <summary>Slots that fit in free memory, with a reserve held back. Decodes already in
    /// flight have themselves consumed free memory, so the probe is self-correcting.</summary>
    private static int AutoLimit()
    {
        long usable;
        if (SystemMemory.TryGetAvailableBytes(out long free))
        {
            // Already-running decodes are counted in `free`; add them back so the limit
            // describes total concurrency rather than "how many MORE fit".
            usable = free + (long)Volatile.Read(ref _inFlight) * PerSlotBytes - ReserveBytes;
        }
        else
        {
            // No free-memory API (macOS): fall back to the old total-based rule.
            double gb = SystemMemory.TotalBytes() / (1024.0 * 1024 * 1024);
            return Math.Clamp(Math.Min(gb < 12 ? 1 : gb < 24 ? 2 : 3, Environment.ProcessorCount), 1, HardCap);
        }
        int bySlots = (int)(usable / PerSlotBytes);
        return Math.Clamp(Math.Min(bySlots, Environment.ProcessorCount), 1, HardCap);
    }

    /// <summary>For 偏好设置: what 自动 would pick right now, and the free memory it read to
    /// decide (null when the platform cannot report it and the total-based fallback is in use).</summary>
    public static (int Auto, long? FreeBytes) AutoConcurrencyInfo()
        => (AutoLimit(), SystemMemory.TryGetAvailableBytes(out long f) ? f : null);

    private static T Gated<T>(Func<T> decode)
    {
        lock (GateLock)
        {
            // Timed wait, so a limit that GREW while we were blocked is noticed even if no
            // decode finished to pulse us. A limit that shrank simply stops admitting until
            // the excess drains — never cancels work already running.
            while (_inFlight >= CurrentLimit()) Monitor.Wait(GateLock, 250);
            _inFlight++;
        }
        try { return decode(); }
        finally
        {
            lock (GateLock) { _inFlight--; Monitor.PulseAll(GateLock); }
        }
    }

    /// <summary>Load any supported file into a linear ImageBuffer (RAW → LibRaw/UniWB,
    /// otherwise a linear TIFF). RAW honours the user's backend + FBDD preferences.</summary>
    public static ImageBuffer LoadLinear(string path) => Gated(() =>
    {
        if (!RawDecode.IsRawExtension(path)) return TiffIO.LoadTiff(path, inputIsSrgb: false);
        var s = Settings.Current;
        return RawDecode.DecodeRaw(path, s.DecodeBackend, s.FbddMode, out _);
    });

    /// <summary>
    /// Centre-ROI channel mean of a full-quality decode — the Path A calibration frames' entire
    /// contribution. Streams off the decoder rather than decoding to a full float frame and then
    /// averaging a fifth of it; the mean is identical (see <see cref="RawDecode.RoiMeanFull"/>).
    /// </summary>
    public static double[] RoiMeanFull(string path) => Gated(() =>
    {
        if (!RawDecode.IsRawExtension(path))
            return DecoupleCalibration.RoiMean(TiffIO.LoadTiff(path, inputIsSrgb: false));
        var s = Settings.Current;
        return RawDecode.RoiMeanFull(path, s.DecodeBackend, s.FbddMode);
    });

    /// <summary>
    /// Per-channel high percentile (default 99.9%) via a 1024-bin histogram over [0,1].
    /// Used as a "brightest region" reference: the film base is the most transmissive
    /// part of a negative, so a t_base sample far below this almost certainly missed it.
    /// </summary>
    public static double[] BrightReference(ImageBuffer img, double pct = 0.999)
    {
        const int bins = 1024;
        var hist = new int[3, bins];
        float[] d = img.Data;
        int n = img.PixelCount;
        for (int p = 0; p < n; p++)
            for (int c = 0; c < 3; c++)
            {
                int b = (int)(d[p * 3 + c] * bins);
                if (b < 0) b = 0; else if (b >= bins) b = bins - 1;
                hist[c, b]++;
            }
        var refv = new double[3];
        long target = (long)(n * (1.0 - pct));
        for (int c = 0; c < 3; c++)
        {
            long acc = 0; int bin = bins - 1;
            for (; bin > 0; bin--) { acc += hist[c, bin]; if (acc >= target) break; }
            refv[c] = (bin + 0.5) / bins;
        }
        return refv;
    }

    /// <summary>
    /// Load a file straight to preview size, at the requested long edges (one decode, one buffer
    /// per edge), plus the SOURCE dimensions.
    ///
    /// This exists instead of "decode, then downsample" because the intermediate full-resolution
    /// float frame is the single largest allocation the application makes — 288 MB at 24 MP, over
    /// 500 MB at 42 MP — and on the import path several of those are in flight at once. For RAW it
    /// is never materialised: the box average is taken directly off LibRaw's 16-bit output, which
    /// is bit-identical to averaging the float frame (same order, same arithmetic) while cutting
    /// the per-decode peak by that whole buffer.
    ///
    /// Non-RAW still goes through the full frame — TIFF decoding has no streaming entry point, and
    /// a linear TIFF roll is not the case that runs the machine out of memory.
    /// </summary>
    public static (ImageBuffer[] Previews, int SourceWidth, int SourceHeight) LoadPreviews(
        string path, params int[] maxEdges) => Gated(() =>
    {
        if (!RawDecode.IsRawExtension(path))
        {
            ImageBuffer full = TiffIO.LoadTiff(path, inputIsSrgb: false);
            var outs = new ImageBuffer[maxEdges.Length];
            for (int i = 0; i < maxEdges.Length; i++) outs[i] = Resample.Box(full, maxEdges[i]);
            return (outs, full.Width, full.Height);
        }
        var s = Settings.Current;
        // PPG, not AHD. Everything this produces is on its way through a 6× box downsample to a
        // 1600 px preview, and a box average is exactly the operation that destroys the detail an
        // edge-adaptive demosaic exists to protect. Costs ~1.2 s per 60 MP frame instead of ~2.4 s.
        // The export path (LoadLinear) and the Path A calibration ROI (RoiMeanFull) stay on AHD —
        // see RawDecode.Demosaic for the measured impact on the Stage-1 numbers.
        return RawDecode.DecodeRawDownsampled(path, s.DecodeBackend, s.FbddMode, maxEdges,
                                              RawDecode.Demosaic.Preview);
    });

    /// <summary>
    /// Decode ONE RECTANGLE of a source at full resolution — the sharp-patch path.
    ///
    /// Null when this source cannot be region-decoded (a TIFF). Both RAW backends work: the
    /// DNG one crops the cached linear DNG. Goes through the same admission gate as every other
    /// decode — far cheaper than a full frame, but still holds LibRaw buffers.
    /// </summary>
    public static (ImageBuffer Slice, int X0, int Y0)? LoadRegion(
        string path, int x, int y, int w, int h, int frameW, int frameH)
    {
        if (!RawDecode.IsRawExtension(path)) return null;
        var s = Settings.Current;
        return Gated(() => RawDecode.DecodeRawRegion(path, s.DecodeBackend, s.FbddMode,
                                                     x, y, w, h, frameW, frameH));
    }

    /// <summary>Single-edge <see cref="LoadPreviews"/>.</summary>
    public static (ImageBuffer Preview, int SourceWidth, int SourceHeight) LoadPreview(string path, int maxEdge)
    {
        var (outs, w, h) = LoadPreviews(path, maxEdge);
        return (outs[0], w, h);
    }

    /// <summary>
    /// Preview of ONE normalised sub-rectangle, cropped from the source BEFORE downsampling —
    /// what a frame that shares its file with other negatives needs to look sharp on screen.
    ///
    /// The reported source size is the RECTANGLE's size in source pixels, not the file's: it is
    /// what the caller means by "how big is this frame really", and the crop-overlay maths reads
    /// it that way.
    /// </summary>
    public static (ImageBuffer Preview, int SourceWidth, int SourceHeight) LoadPreviewRegion(
        string path, (double X, double Y, double W, double H) rect, int maxEdge) => Gated(() =>
    {
        if (RawDecode.IsRawExtension(path))
        {
            // RAW never reaches here today — only scanner TIFFs are split — but falling back to
            // the whole-frame preview is correct rather than merely safe: the pipeline still
            // applies the crop afterwards, so the frame is right, just softer.
            var (outs, fw, fh) = LoadPreviews(path, maxEdge);
            return (outs[0], fw, fh);
        }

        ImageBuffer region = TiffIO.LoadTiffRegion(path, rect, inputIsSrgb: false, maxEdge);
        // Taken from the file's own dimensions rather than back-computed from the returned
        // buffer: the box factor truncates, so that route loses up to a factor's worth of pixels.
        var (fullW, fullH) = TiffIO.ReadTiffSize(path);
        int rw = Math.Max(1, (int)Math.Round(rect.W * fullW));
        int rh = Math.Max(1, (int)Math.Round(rect.H * fullH));
        return (region, rw, rh);
    });
}
