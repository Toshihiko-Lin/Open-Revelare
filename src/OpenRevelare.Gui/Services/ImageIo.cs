using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Image load + preview-downsample helpers for the GUI, all in the linear-light
/// domain Core works in. Load dispatch mirrors the CLI's <c>LoadLinear</c>: RAW
/// files go through LibRaw (UniWB), everything else is read as a linear TIFF.
/// </summary>
public static class ImageIo
{
    /// <summary>
    /// File dialog filter patterns for supported inputs — TIFF plus every RAW the decoder
    /// accepts, derived from <see cref="RawDecode.RawExtensions"/> so a format added there
    /// becomes selectable here without a second edit.
    ///
    /// Each extension is listed in BOTH cases, because the dialog's matching is not
    /// case-insensitive everywhere: Win32 and GTK fold case for us, but macOS hands these to
    /// NSOpenPanel, which does NOT. Cameras write the upper-case form constantly (Canon .CR2,
    /// Nikon .NEF, Hasselblad .3FR), so the lower-case-only list left mac users looking at
    /// their own negatives greyed out — while <see cref="RawDecode.IsRawExtension"/>, which
    /// compares OrdinalIgnoreCase, would have decoded them happily. A file the decoder accepts
    /// must never be a file the picker refuses to show.
    /// </summary>
    public static readonly string[] OpenPatterns =
        new[] { ".tif", ".tiff" }
            .Concat(RawDecode.RawExtensions.OrderBy(e => e, StringComparer.Ordinal))
            .SelectMany(e => new[] { "*" + e, "*" + e.ToUpperInvariant() })
            .ToArray();

    // ── Decode admission control ────────────────────────────────────────────────
    //
    // A single decode in flight costs several times the finished frame: the file bytes LibRaw
    // reads from, its unpacked Bayer data, its demosaiced working image, the 16-bit result, and
    // whatever the caller builds from it. Measured on a 60 MP Sony ARW at full quality that is
    // ~129 MB Bayer + ~518 MB demosaic workspace + ~388 MB result ≈ 1.0 GB resident at the
    // peak, hence <see cref="FullSlotBytes"/>. A PREVIEW decode is a different animal: it is
    // half-size (2×2 binning, no demosaic workspace), so an 80 MP ORF peaks around 500 MB and
    // a 24 MP frame well under 200 MB — <see cref="PreviewSlotBytes"/>. The gate weighs each
    // decode by which it is, because the import is all previews and sizing those as if they
    // were exports left two thirds of the machine idle.
    //
    // ONE gate shared by every entry point here — import warm-up, calibration, thumbnails and
    // export all draw on the same physical memory, so counting them separately is how you get
    // three "safe" limits multiplying into an unsafe total.
    //
    // The budget is RE-EVALUATED as work arrives rather than fixed at startup, and it is sized
    // from memory that is actually FREE, not from total RAM. Those are different questions: a
    // 48 GB workstation with a browser, a game and Lightroom open may have 4 GB left, and the
    // old rule — a fixed three slots chosen once from TotalAvailableMemoryBytes — would happily
    // start three 1 GB decodes into it. Re-checking also means a long import backs off when
    // something else on the machine grows, and opens back up when it exits.
    //
    // The two slot sizes are CAMERA numbers. A TIFF is weighed from its own header instead
    // (<see cref="TiffFullBytes"/>): scanner files range from a few MP to a whole 190 MP strip,
    // and no fixed slot describes both.
    private const long FullSlotBytes = 1_200L << 20;     // ~1.2 GB per in-flight full-quality RAW decode
    private const long PreviewSlotBytes = 512L << 20;    // ~0.5 GB per in-flight half-size preview
    private const long ReserveBytes = 2L << 30;          // leave the OS and the rest of the app room
    private const int HardCap = 8;                       // beyond this, decode is not the bottleneck
    private const int LimitRefreshMs = 1500;

    private static readonly object GateLock = new();
    private static int _inFlight;
    private static long _inFlightBytes;
    private static long _cachedBudget;   // bytes available to decodes; see the guard below
    private static bool _budgetKnown;
    private static long _budgetStamp;

    /// <summary>
    /// How many decode workers a roll-wide pass should run: the user's override, else most of
    /// the cores. LibRaw's unpack is single-threaded per file, so the roll only gets faster by
    /// running files side by side — measured on a 6-core/12-thread machine with 80 MP ORFs,
    /// 2 workers gave 1.28 s a frame and 8 gave 0.46 s. Two cores are left for the UI and the
    /// downsample's own parallel loop. This is the number of workers ASKING; the memory gate
    /// below still decides how many are admitted at once.
    /// </summary>
    public static int PreviewWorkers
    {
        get
        {
            int manual = Settings.Current.DecodeConcurrency;
            return manual > 0
                ? Math.Clamp(manual, 1, HardCap)
                : Math.Clamp(Environment.ProcessorCount * 2 / 3, 2, HardCap);
        }
    }

    /// <summary>The user's fixed concurrency, or 0 for automatic.</summary>
    private static int ManualLimit() => Math.Clamp(Settings.Current.DecodeConcurrency, 0, HardCap);

    /// <summary>Bytes the gate may hand out in total right now, refreshed briefly — this is
    /// consulted on every decode and, on Windows, each probe is a syscall.</summary>
    /// <remarks>
    /// <c>_budgetKnown</c> is load-bearing, not a nicety: relying on the timestamp alone to
    /// catch the first call does not work, because any sentinel far in the past makes
    /// <c>now - _budgetStamp</c> overflow, which wraps NEGATIVE and reads as "cache is fresh".
    /// </remarks>
    private static long CurrentBudget()
    {
        long now = Environment.TickCount64;
        if (_budgetKnown && now - _budgetStamp < LimitRefreshMs) return _cachedBudget;
        _cachedBudget = AutoBudget();
        _budgetKnown = true;
        _budgetStamp = now;
        return _cachedBudget;
    }

    /// <summary>Free memory less a reserve, with the decodes already in flight added back — they
    /// have themselves consumed free memory, so the probe is self-correcting and the figure
    /// describes total concurrency rather than "how much MORE fits".</summary>
    private static long AutoBudget()
    {
        if (SystemMemory.TryGetAvailableBytes(out long free))
            return free + Volatile.Read(ref _inFlightBytes) - ReserveBytes;

        // No free-memory API (macOS): fall back to the old total-based rule, expressed in
        // full-quality slots so it stays as conservative as it was.
        double gb = SystemMemory.TotalBytes() / (1024.0 * 1024 * 1024);
        int slots = Math.Clamp(Math.Min(gb < 12 ? 1 : gb < 24 ? 2 : 3, Environment.ProcessorCount), 1, HardCap);
        return slots * FullSlotBytes;
    }

    /// <summary>For 偏好设置: how many preview decodes and how many full-quality ones 自动 would
    /// admit right now, and the free memory it read to decide (null when the platform cannot
    /// report it and the total-based fallback is in use).</summary>
    public static (int AutoPreview, int AutoFull, long? FreeBytes) AutoConcurrencyInfo()
    {
        long budget = AutoBudget();
        int cap = Math.Min(Environment.ProcessorCount, HardCap);
        int preview = (int)Math.Clamp(budget / PreviewSlotBytes, 1, cap);
        int full = (int)Math.Clamp(budget / FullSlotBytes, 1, cap);
        return (preview, full, SystemMemory.TryGetAvailableBytes(out long f) ? f : null);
    }

    /// <summary>Run <paramref name="decode"/> once the gate admits a decode of
    /// <paramref name="slotBytes"/>. A manual limit counts decodes; the automatic one weighs
    /// them against free memory, always admitting at least one so a small machine still gets
    /// its picture, and never more than one per core.</summary>
    private static T Gated<T>(long slotBytes, Func<T> decode)
    {
        lock (GateLock)
        {
            // Timed wait, so a budget that GREW while we were blocked is noticed even if no
            // decode finished to pulse us. A budget that shrank simply stops admitting until
            // the excess drains — never cancels work already running.
            while (!Admits(slotBytes)) Monitor.Wait(GateLock, 250);
            _inFlight++;
            _inFlightBytes += slotBytes;
        }
        try { return decode(); }
        finally
        {
            lock (GateLock) { _inFlight--; _inFlightBytes -= slotBytes; Monitor.PulseAll(GateLock); }
        }
    }

    private static bool Admits(long slotBytes)
    {
        int manual = ManualLimit();
        if (manual > 0) return _inFlight < manual;
        if (_inFlight == 0) return true;
        if (_inFlight >= Math.Min(Environment.ProcessorCount, HardCap)) return false;
        return _inFlightBytes + slotBytes <= CurrentBudget();
    }

    /// <summary>A full-quality RAW decode's weight.</summary>
    private static T Gated<T>(Func<T> decode) => Gated(FullSlotBytes, decode);

    /// <summary>
    /// A full-quality decode of <paramref name="path"/>: the RAW slot for RAW, the file's own
    /// size for a TIFF — see <see cref="TiffFullBytes"/> for why the fixed slot could not stand
    /// in for it.
    /// </summary>
    private static T GatedFull<T>(string path, Func<T> decode)
        => Gated(RawDecode.IsRawExtension(path) ? FullSlotBytes : TiffFullBytes(path), decode);

    /// <summary>A preview decode's weight — light only when it really is a half-size LibRaw
    /// decode. A TIFF preview still passes through the full frame, and the DNG backend's
    /// linear sources come back full size whatever was asked.</summary>
    private static T GatedPreview<T>(string path, Func<T> decode)
        => Gated(RawDecode.IsRawExtension(path)
                     ? Settings.Current.DecodeBackend != RawDecode.RawBackend.Dng ? PreviewSlotBytes : FullSlotBytes
                     : TiffFullBytes(path),
                 decode);

    /// <summary>
    /// What a TIFF decode will hold resident, from its header: the float frame (12 bytes a
    /// pixel) plus a quarter for the crop or box that every caller builds from it before
    /// letting it go.
    ///
    /// Read from the file rather than assumed, because a TIFF has no typical size. The RAW slot
    /// is measured on a camera frame and a 60 MP camera frame is the large end of that range;
    /// a scanner file is not on the same scale at all — a Flextight strip scanned whole is
    /// 127–190 MP, 1.5–2.3 GB of float, and charging it as 1.2 GB let an 8 GB machine admit
    /// two of them side by side and swap or fail on the allocation. A gate that does not know
    /// what it is admitting is not a gate. Falls back to the RAW slot only when the header
    /// cannot be read; the decode itself will then report the file properly.
    /// </summary>
    private static long TiffFullBytes(string path)
    {
        try
        {
            var (w, h) = TiffIO.ReadTiffSize(path);
            long frame = checked((long)w * h * 12);
            return Math.Max(frame + frame / 4, 64L << 20);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A gate probe must never be what stops a decode; the decode reports the file.
            return FullSlotBytes;
        }
    }

    /// <summary>Load any supported file into a linear ImageBuffer (RAW → LibRaw/UniWB,
    /// otherwise a linear TIFF). RAW honours the user's backend + FBDD preferences.</summary>
    public static ImageBuffer LoadLinear(string path) => GatedFull(path, () =>
    {
        if (!RawDecode.IsRawExtension(path)) return TiffIO.LoadTiff(path, inputIsSrgb: false);
        var s = Settings.Current;
        return RawDecode.DecodeRaw(path, s.DecodeBackend, s.FbddMode, out _);
    });

    /// <summary>
    /// Versioned, typed decode boundary used by the editor/export pipeline. TIFF input honours the
    /// exact embedded ICC through the app-owned CMM in ManagedV2; RAW remains explicitly
    /// uncharacterized because no camera characterization has been selected.
    /// </summary>
    public static WorkingFrame LoadWorking(
        string path,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement,
        TiffInputAssumption tiffInputAssumption) => GatedFull(path, () =>
    {
        RequirePipelineVersion(pipelineVersion);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (!RawDecode.IsRawExtension(path))
        {
            return TiffIO.LoadWorkingFrame(
                path,
                tiffInputAssumption,
                pipelineVersion,
                colorManagement);
        }

        var settings = Settings.Current;
        return RawDecode.DecodeRawWorking(
            path,
            settings.DecodeBackend,
            settings.FbddMode,
            out _);
    });

    /// <summary>
    /// Centre-ROI channel mean of a full-quality decode — the Path A calibration frames' entire
    /// contribution. Streams off the decoder rather than decoding to a full float frame and then
    /// averaging a fifth of it; the mean is identical (see <see cref="RawDecode.RoiMeanFull"/>).
    /// </summary>
    public static double[] RoiMeanFull(string path) => GatedFull(path, () =>
    {
        if (!RawDecode.IsRawExtension(path))
            return DecoupleCalibration.RoiMean(TiffIO.LoadTiff(path, inputIsSrgb: false));
        var s = Settings.Current;
        return RawDecode.RoiMeanFull(path, s.DecodeBackend, s.FbddMode);
    });

    /// <summary>Versioned counterpart used by calibration so profiled TIFF samples are measured
    /// in the same linear working domain as their content frames.</summary>
    public static double[] RoiMeanFull(
        string path,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement,
        TiffInputAssumption tiffInputAssumption) => GatedFull(path, () =>
    {
        RequirePipelineVersion(pipelineVersion);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (!RawDecode.IsRawExtension(path))
        {
            WorkingFrame working = TiffIO.LoadWorkingFrame(
                path,
                tiffInputAssumption,
                pipelineVersion,
                colorManagement);
            return DecoupleCalibration.RoiMean(working.Pixels);
        }

        var settings = Settings.Current;
        return RawDecode.RoiMeanFull(path, settings.DecodeBackend, settings.FbddMode);
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
        string path, params int[] maxEdges) => GatedPreview(path, () =>
    {
        if (!RawDecode.IsRawExtension(path))
        {
            ImageBuffer full = TiffIO.LoadTiff(path, inputIsSrgb: false);
            var outs = new ImageBuffer[maxEdges.Length];
            for (int i = 0; i < maxEdges.Length; i++) outs[i] = Resample.Box(full, maxEdges[i]);
            return (outs, full.Width, full.Height);
        }
        var s = Settings.Current;
        // Half-size, not a demosaic. Everything this produces is on its way through a box
        // downsample to a 1600 px preview, and a box average is exactly the operation that
        // destroys the detail a demosaic exists to protect. The export path (LoadLinear) and the
        // Path A calibration ROI (RoiMeanFull) stay on full AHD — see RawDecode.Demosaic for the
        // measured impact on the Stage-1 numbers.
        return RawDecode.DecodeRawDownsampled(path, s.DecodeBackend, s.FbddMode, maxEdges,
                                              RawDecode.Demosaic.Preview);
    });

    /// <summary>Typed/versioned preview decode. Every returned downsample keeps the exact source
    /// admission and profile identity of the full working frame.</summary>
    public static (WorkingFrame[] Previews, int SourceWidth, int SourceHeight) LoadWorkingPreviews(
        string path,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement,
        TiffInputAssumption tiffInputAssumption,
        params int[] maxEdges) => GatedPreview(path, () =>
    {
        RequirePipelineVersion(pipelineVersion);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (maxEdges.Length == 0)
            throw new ArgumentException("At least one preview edge is required.", nameof(maxEdges));

        if (!RawDecode.IsRawExtension(path))
        {
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path,
                tiffInputAssumption,
                pipelineVersion,
                colorManagement);
            var previews = new WorkingFrame[maxEdges.Length];
            for (int index = 0; index < maxEdges.Length; index++)
                previews[index] = full.WithPixels(Resample.Box(full.Pixels, maxEdges[index]));
            return (previews, full.Pixels.Width, full.Pixels.Height);
        }

        var settings = Settings.Current;
        var (pixels, width, height) = RawDecode.DecodeRawDownsampled(
            path,
            settings.DecodeBackend,
            settings.FbddMode,
            maxEdges,
            RawDecode.Demosaic.Preview);
        var typed = new WorkingFrame[pixels.Length];
        for (int index = 0; index < pixels.Length; index++)
        {
            typed[index] = RawDecode.AdmitRawWorking(
                pixels[index],
                path,
                settings.DecodeBackend.ToString(),
                settings.FbddMode,
                $"preview decode; max-edge={maxEdges[index]}");
        }
        return (typed, width, height);
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

    public static (WorkingFrame Slice, int X0, int Y0)? LoadWorkingRegion(
        string path,
        int x,
        int y,
        int width,
        int height,
        int frameWidth,
        int frameHeight,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement)
    {
        RequirePipelineVersion(pipelineVersion);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (!RawDecode.IsRawExtension(path)) return null;
        var settings = Settings.Current;
        return Gated<(WorkingFrame Slice, int X0, int Y0)?>(() =>
        {
            var decoded = RawDecode.DecodeRawRegion(
                path,
                settings.DecodeBackend,
                settings.FbddMode,
                x,
                y,
                width,
                height,
                frameWidth,
                frameHeight);
            if (decoded is not { } region) return null;
            return (
                RawDecode.AdmitRawWorking(
                    region.Slice,
                    path,
                    settings.DecodeBackend.ToString(),
                    settings.FbddMode,
                    "sharp-region decode"),
                region.X0,
                region.Y0);
        });
    }

    /// <summary>Single-edge <see cref="LoadPreviews"/>.</summary>
    public static (ImageBuffer Preview, int SourceWidth, int SourceHeight) LoadPreview(string path, int maxEdge)
    {
        var (outs, w, h) = LoadPreviews(path, maxEdge);
        return (outs[0], w, h);
    }

    public static (WorkingFrame Preview, int SourceWidth, int SourceHeight) LoadWorkingPreview(
        string path,
        int maxEdge,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement,
        TiffInputAssumption tiffInputAssumption)
    {
        var (previews, width, height) = LoadWorkingPreviews(
            path,
            pipelineVersion,
            colorManagement,
            tiffInputAssumption,
            maxEdge);
        return (previews[0], width, height);
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
        string path, (double X, double Y, double W, double H) rect, int maxEdge) => GatedPreview(path, () =>
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

    /// <summary>
    /// Versioned typed region preview. Managed profiled TIFFs currently decode/convert atomically
    /// before cropping; this trades peak memory for the invariant that a split scan never takes a
    /// partial ICC path. A future streaming implementation must preserve the same result/metadata.
    /// </summary>
    public static (WorkingFrame Preview, int SourceWidth, int SourceHeight) LoadWorkingPreviewRegion(
        string path,
        (double X, double Y, double W, double H) rect,
        int maxEdge,
        ColorPipelineVersion pipelineVersion,
        IColorManagementEngine colorManagement,
        TiffInputAssumption tiffInputAssumption) => GatedPreview(path, () =>
    {
        RequirePipelineVersion(pipelineVersion);
        ArgumentNullException.ThrowIfNull(colorManagement);
        if (RawDecode.IsRawExtension(path))
        {
            var settings = Settings.Current;
            var (pixels, fullWidth, fullHeight) = RawDecode.DecodeRawDownsampled(
                path,
                settings.DecodeBackend,
                settings.FbddMode,
                new[] { maxEdge },
                RawDecode.Demosaic.Preview);
            WorkingFrame working = RawDecode.AdmitRawWorking(
                pixels[0],
                path,
                settings.DecodeBackend.ToString(),
                settings.FbddMode,
                $"whole-frame preview fallback for requested region; max-edge={maxEdge}");
            return (working, fullWidth, fullHeight);
        }

        WorkingFrame full = TiffIO.LoadWorkingFrame(
            path,
            tiffInputAssumption,
            pipelineVersion,
            colorManagement);
        ImageBuffer region = Geometry.ApplyCrop(full.Pixels, rect);
        ImageBuffer preview = Resample.Box(region, maxEdge);
        int sourceWidth = Math.Max(1, (int)Math.Round(rect.W * full.Pixels.Width));
        int sourceHeight = Math.Max(1, (int)Math.Round(rect.H * full.Pixels.Height));
        return (full.WithPixels(preview), sourceWidth, sourceHeight);
    });

    private static void RequirePipelineVersion(ColorPipelineVersion version)
    {
        if (version is not ColorPipelineVersion.LegacyV1 and not ColorPipelineVersion.ManagedV2)
            throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown colour pipeline version.");
    }
}
