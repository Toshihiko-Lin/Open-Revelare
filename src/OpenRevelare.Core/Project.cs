using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRevelare.Core;

/// <summary>
/// <c>.ncproj</c> project-file serialisation (format version 2) — byte-schema-compatible
/// with Python's <c>negative/project.py</c> so a roll edited in either build round-trips.
///
/// The file stores only calibration <em>source paths</em> (Path-A RGB cal files, LCC flat
/// field); the decouple matrix and flat field are recomputed on load, exactly as Python does.
/// Per-frame it stores the full <see cref="FrameParams"/>. Fields the C# build does not model
/// (lensfun_override, split_rects, per-frame text annotations) are echoed as Python-shaped
/// defaults so the file stays valid for the Python reader.
/// </summary>
public static class Project
{
    private const int FormatVersion = 2;

    // ── Public data model ───────────────────────────────────────────────────────
    public sealed class RollMeta
    {
        public string InputType = "raw";      // "raw" | "tiff"
        public string SourcePath = "B";       // "A" (RGB light) | "B" (white light)

        public bool? TiffIsLinear;
        public string? CalSourcePath;         // Path-A calibration directory
        public Dictionary<string, string>? CalRgbPaths;   // {"R":…,"G":…,"B":…}
        public string? LccPath;
        public string CameraBody = "", FilmStock = "", FilmIso = "", RollNumber = "";
        public string DevLab = "", DevProcess = "", DevDate = "", Location = "", RollNote = "";

        /// <summary>Frame format — 135, 120 (6x6), 4x5 … Free text on disk even though the UI
        /// offers a fixed list, because the list is a convenience and a roll shot on something
        /// the list does not name is still a roll.</summary>
        public string Format = "";
    }

    public sealed class Frame
    {
        public string SourcePath = "";
        public bool IsVirtual;
        public FrameParams Params = new();
    }

    public sealed class Data
    {
        public RollMeta Meta = new();
        public List<Frame> Frames = new();

        /// <summary>
        /// 这一卷存的是旧标定模型（没有 d_min_per_channel），载入后画面与保存时不同。
        /// 界面据此提示用户重跑标定；见 <see cref="NeedsRecalibration"/> 说明为何不静默折算。
        /// </summary>
        public bool NeedsRecalibration;
    }

    // ── Save ────────────────────────────────────────────────────────────────────
    public static void Save(string path, Data d)
    {
        var root = new JsonObject
        {
            ["version"] = FormatVersion,
            ["created"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["roll_meta"] = SerRollMeta(d.Meta),
            ["frames"] = new JsonArray(d.Frames.Select(SerFrame).ToArray()),
            ["export"] = new JsonObject
            {
                ["format"] = "tiff",
                ["color_space"] = "AdobeRGB",
                ["output_dir"] = "./export",
            },
        };
        string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        // Temp file + atomic replace, not a direct overwrite: the GUI autosaves this file on every
        // idle pause, and a process killed mid-write would otherwise leave a truncated project —
        // i.e. lose the whole roll's edit rather than the last few seconds of it.
        // Relaxed escaping matches Python's ensure_ascii=False: roll notes and paths are routinely
        // Chinese, and \uXXXX-escaping them makes the file unreadable and the two builds' output
        // pointlessly different.
        string json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    // ── Load ────────────────────────────────────────────────────────────────────
    public static Data Load(string path)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                        ?? throw new InvalidDataException(CoreText.T("空的工程文件"));
        int version = (int?)root["version"] ?? 1;
        if (version != FormatVersion)
            throw new InvalidDataException(CoreText.F($"不支持的 .ncproj 版本 {version}（本版本支持 {FormatVersion}）"));

        var d = new Data { Meta = DesRollMeta(root["roll_meta"]?.AsObject()) };
        if (root["frames"] is JsonArray frames)
            foreach (JsonNode? f in frames)
                if (f is JsonObject fo)
                {
                    d.Frames.Add(DesFrame(fo));
                    // 任何一帧是旧模型就标记整卷：标定是卷级的，逐帧提示没有意义。
                    if (NeedsRecalibration(fo)) d.NeedsRecalibration = true;
                }
        return d;
    }

    // ── Relink ──────────────────────────────────────────────────────────────────

    /// <summary>Distinct source files this project references that are no longer on disk. Virtual
    /// copies are skipped: they share a real frame's path and would double-count it.</summary>
    public static IReadOnlyList<string> MissingSources(Data data) =>
        data.Frames
            .Where(f => !f.IsVirtual && !File.Exists(f.SourcePath))
            .Select(f => f.SourcePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Re-point missing frames at same-named files in <paramref name="folder"/>; returns how many
    /// distinct source files were found there.
    ///
    /// Matching is by FILE NAME, which is what survives copying a roll to another disk. The remap
    /// is built per unique path and then applied to every frame, so virtual copies follow their
    /// parent instead of being left behind. Frames still not found keep their stored path: they
    /// simply fail to decode, which is recoverable, whereas dropping them would discard their
    /// adjustments.
    /// </summary>
    public static int Relink(Data data, string folder)
    {
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string old in MissingSources(data))
        {
            string candidate = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(old));
            if (File.Exists(candidate)) remap[old] = candidate;
        }
        if (remap.Count == 0) return 0;

        foreach (Frame f in data.Frames)
            if (remap.TryGetValue(f.SourcePath, out string? found)) f.SourcePath = found;
        return remap.Count;
    }

    // ── roll_meta ─────────────────────────────────────────────────────────────
    private static JsonObject SerRollMeta(RollMeta rm)
    {
        var d = new JsonObject
        {
            ["input_type"] = rm.InputType,
            ["source_path"] = rm.SourcePath,
            ["tiff_is_linear"] = rm.TiffIsLinear,
        };
        if (rm.CalSourcePath is not null) d["cal_source_path"] = rm.CalSourcePath;
        if (rm.CalRgbPaths is not null)
        {
            var o = new JsonObject();
            foreach (var (k, v) in rm.CalRgbPaths) o[k] = v;
            d["cal_rgb_paths"] = o;
        }
        if (rm.LccPath is not null) d["lcc_path"] = rm.LccPath;
        // Only emit non-empty annotation fields (tidy file; absent → "" on load).
        void Add(string k, string v) { if (!string.IsNullOrEmpty(v)) d[k] = v; }
        Add("camera_body", rm.CameraBody); Add("film_stock", rm.FilmStock); Add("film_iso", rm.FilmIso);
        Add("roll_number", rm.RollNumber); Add("dev_lab", rm.DevLab); Add("dev_process", rm.DevProcess);
        Add("dev_date", rm.DevDate); Add("location", rm.Location); Add("roll_note", rm.RollNote);
        Add("format", rm.Format);
        return d;
    }

    private static RollMeta DesRollMeta(JsonObject? d)
    {
        if (d is null) return new RollMeta();
        Dictionary<string, string>? rgb = null;
        if (d["cal_rgb_paths"] is JsonObject o)
        {
            rgb = new Dictionary<string, string>();
            foreach (var (k, v) in o) if (v is not null) rgb[k] = v.GetValue<string>();
        }
        return new RollMeta
        {
            InputType = Str(d, "input_type", "raw"),
            SourcePath = Str(d, "source_path", "B"),
            TiffIsLinear = d["tiff_is_linear"]?.GetValue<bool>(),
            CalSourcePath = d["cal_source_path"]?.GetValue<string>(),
            CalRgbPaths = rgb,
            LccPath = d["lcc_path"]?.GetValue<string>(),
            CameraBody = Str(d, "camera_body", ""), FilmStock = Str(d, "film_stock", ""),
            FilmIso = Str(d, "film_iso", ""), RollNumber = Str(d, "roll_number", ""),
            DevLab = Str(d, "dev_lab", ""), DevProcess = Str(d, "dev_process", ""),
            DevDate = Str(d, "dev_date", ""), Location = Str(d, "location", ""),
            RollNote = Str(d, "roll_note", ""), Format = Str(d, "format", ""),
        };
    }

    // ── frame ───────────────────────────────────────────────────────────────────
    private static JsonObject SerFrame(Frame e)
    {
        FrameParams p = e.Params;
        var o = new JsonObject
        {
            ["filename"] = System.IO.Path.GetFileName(e.SourcePath),
            // FilmBase calibration
            ["t_base"] = Arr(p.TBase),
            // ── 反相的六个自由度，就这两行 ──────────────────────────────────────
            ["d_min_per_channel"] = Arr(p.DMinPerChannel),
            ["d_max_per_channel"] = Arr(p.DMaxPerChannel),
            // ── 以下为兼容键，全部写成中性值 ────────────────────────────────────
            // 旧版本（及 Python 读端）仍会解析它们。写中性值而非真实值，是因为那些版本会把
            // 它们叠加到端点之上——写非中性就等于让旧版把同一个校正应用两遍。
            ["d_max"] = FrameParams.OutputRange,
            ["wb_high"] = new JsonArray(1.0, 1.0, 1.0),
            ["wb_offset"] = new JsonArray(0.0, 0.0, 0.0),
            ["chroma_channel_scale"] = Arr(p.ChromaChannelScale),
            ["scan_exposure_ev"] = 0.0,
            // Input colour space. Written only when it departs from the sRGB default, so a
            // project that never touched it stays byte-identical to one from an older build.
            ["input_primaries"] = p.InputPrimaries is { } ip
                ? new JsonArray(ip[0, 0], ip[0, 1], ip[1, 0], ip[1, 1], ip[2, 0], ip[2, 1])
                : null,
            ["input_white_point"] = p.InputWhitePoint is { Length: 2 } iw
                ? new JsonArray(iw[0], iw[1])
                : null,
            ["output_intent"] = p.OutputIntent == OutputIntent.Basic ? "basic" : "none",
            // Absent = false: a project written before the Stage-2 rework keeps the old chain.
            ["display_referred_stage2"] = p.DisplayReferredStage2,
            ["output_space"] = p.OutputSpace,
            ["sprocket_enabled"] = p.SprocketEnabled,
            ["sprocket_threshold"] = p.SprocketThreshold,
            ["lensfun_override"] = null,                     // C# build has no lensfun
            ["vignette_amount"] = p.VignetteAmount,
            ["vignette_falloff"] = p.VignetteFalloff,
            ["distortion_k1"] = p.DistortionK1,
            ["lcc_enabled"] = p.LccFlatField != null,
            // SceneBase adjustments
            ["wb_gains"] = Arr(p.WbGains),
            ["exposure_ev"] = p.ExposureEv,
            ["black_point"] = p.BlackPoint,
            ["white_point"] = p.WhitePoint,
            ["contrast"] = p.Contrast,
            ["highlights"] = p.Highlights,
            ["shadows"] = p.Shadows,
            ["saturation"] = p.Saturation,
            ["curve_points_m"] = Pts(p.CurvePointsM),
            ["curve_points_r"] = Pts(p.CurvePointsR),
            ["curve_points_g"] = Pts(p.CurvePointsG),
            ["curve_points_b"] = Pts(p.CurvePointsB),
            ["curve_preserve_hue"] = p.CurvePreserveHue,
            // Geometry
            ["crop_rect"] = p.CropRect is { } c ? new JsonArray(c.X, c.Y, c.W, c.H) : null,
            ["split_rects"] = null,                          // C# build has no split
            ["rotation"] = p.Rotation,
            ["quarter_turns"] = p.QuarterTurns,
            ["flip_h"] = p.FlipH,
            ["flip_v"] = p.FlipV,
            // Frame-level annotation (metadata only; not modelled in the C# UI)
            ["shot_date"] = "", ["shot_place"] = "", ["lens_model"] = "", ["frame_note"] = "",
            // Structure
            ["is_virtual"] = e.IsVirtual,
            ["source_path"] = e.SourcePath,
        };

        return o;
    }

    private static Frame DesFrame(JsonObject d)
    {
        var p = new FrameParams
        {
            // TBase 恒为中性：片基改由黑端以绝对密度承载。旧工程存在 t_base 里的片基由
            // RebaseToNeutral 折进两端——把片基从除数移到减数是同一个仿射变换，所以这一步
            // 渲染逐位不变（与 NeedsRecalibration 提示的那种真正会变的迁移不同）。
            TBase = new[] { 1.0, 1.0, 1.0 },
            // 两端，按文件写入时的 schema 迁移——见 MigrateHighlightEndpoint / MigrateShadowEndpoint。
            DMaxPerChannel = MigrateHighlightEndpoint(d),
            DMinPerChannel = MigrateShadowEndpoint(d),
            ChromaChannelScale = Vec3(d, "chroma_channel_scale", 1, 1, 1),
            // Deliberately not read back. A stored 3.05 described a chroma boost compensating for
            // a colour-space conversion the pipeline was missing; that conversion now exists
            // (InputTransform / OutputRender), so honouring the old number would double up.
            InputPrimaries = Xy3(d, "input_primaries"),
            InputWhitePoint = d["input_white_point"] is JsonArray wa && wa.Count == 2
                ? new[] { wa[0]!.GetValue<double>(), wa[1]!.GetValue<double>() }
                : null,
            OutputIntent = Str(d, "output_intent", "basic") == "none" ? OutputIntent.None : OutputIntent.Basic,
            DisplayReferredStage2 = Bool(d, "display_referred_stage2", false),
            // Projects saved before the colour-managed rework carry no output space. They were
            // rendered in sRGB throughout, so sRGB is the name that describes their stored
            // adjustment values — but they are MIGRATED to the new pipeline rather than pinned to
            // the old one, which means the working space is now ACEScg for them too and step 4 is
            // a real conversion. Their pixels will differ from what the old build produced.
            OutputSpace = Str(d, "output_space", "sRGB"),
            SprocketEnabled = Bool(d, "sprocket_enabled", false),
            SprocketThreshold = d["sprocket_threshold"] is { } st ? st.GetValue<double>() : 0.9,
            VignetteAmount = Dbl(d, "vignette_amount", 0.0),
            VignetteFalloff = Dbl(d, "vignette_falloff", 2.5),
            DistortionK1 = Dbl(d, "distortion_k1", 0.0),
            WbGains = Vec3(d, "wb_gains", 1, 1, 1),
            ExposureEv = Dbl(d, "exposure_ev", 0.0),
            BlackPoint = Dbl(d, "black_point", 0.0),
            WhitePoint = Dbl(d, "white_point", 1.0),
            Contrast = Dbl(d, "contrast", 0.0),
            Highlights = Dbl(d, "highlights", 0.0),
            Shadows = Dbl(d, "shadows", 0.0),
            Saturation = Dbl(d, "saturation", 0.0),
            CurvePointsM = DesPts(d["curve_points_m"]),
            CurvePointsR = DesPts(d["curve_points_r"]),
            CurvePointsG = DesPts(d["curve_points_g"]),
            CurvePointsB = DesPts(d["curve_points_b"]),
            CurvePreserveHue = Bool(d, "curve_preserve_hue", true),
            CropRect = DesRect(d["crop_rect"]),
            Rotation = Dbl(d, "rotation", 0.0),
            QuarterTurns = (int)Dbl(d, "quarter_turns", 0),
            FlipH = Bool(d, "flip_h", false),
            FlipV = Bool(d, "flip_v", false),
        };
        RebaseToNeutral(d, p);
        // lcc_enabled is stored, but the flat-field object is roll-level and re-baked by the loader.
        string src = d["source_path"]?.GetValue<string>() ?? Str(d, "filename", "");
        return new Frame { SourcePath = src, IsVirtual = Bool(d, "is_virtual", false), Params = p };
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────
    private static JsonArray Arr(double[] v) => new(v[0], v[1], v[2]);
    private static JsonArray Pts(List<(double X, double Y)> pts)
        => new(pts.Select(p => (JsonNode)new JsonArray(p.X, p.Y)).ToArray());

    private static List<(double X, double Y)> DesPts(JsonNode? n)
    {
        var list = new List<(double, double)>();
        if (n is JsonArray a)
            foreach (JsonNode? pt in a)
                if (pt is JsonArray xy && xy.Count >= 2)
                    list.Add((xy[0]!.GetValue<double>(), xy[1]!.GetValue<double>()));
        return list;
    }

    private static (double, double, double, double)? DesRect(JsonNode? n)
        => n is JsonArray a && a.Count >= 4
            ? (a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>(), a[3]!.GetValue<double>())
            : null;

    /// <summary>Six flat numbers → the 3×2 CIE xy primaries array; null when absent or malformed.</summary>
    private static double[,]? Xy3(JsonObject d, string key)
    {
        if (d[key] is not JsonArray a || a.Count != 6) return null;
        var m = new double[3, 2];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
                m[i, j] = a[i * 2 + j]!.GetValue<double>();
        return m;
    }

    /// <summary>A scalar as a neutral three-channel endpoint set.</summary>
    private static double[] Repeat3(double v) => new[] { v, v, v };

    /// <summary>
    /// 把存在 <c>t_base</c> 里的片基折进两端，使 TBase 归于中性 1,1,1。
    ///
    /// 密度定义是 <c>D = -log10(T / t_base)</c>；把除数换成 1 相当于给每个通道的密度加上
    /// <c>-log10(t_base[c])</c>。两端同时加同一个量，端点之差（即反差与色偏）完全不变，
    /// 渲染逐位相同——这只是同一个仿射变换的另一种写法，不是重新标定。
    ///
    /// 这样做是为了让黑端显示**可验证的绝对密度**（橙色片基必然 R&lt;G&lt;B，典型
    /// 0.09/0.29/0.54），而不是恒为 0,0,0 的哨兵值——后者把真实信息藏在一个界面上看不到的
    /// 字段里，用户无从判断自动标定是否正确。
    /// </summary>
    private static void RebaseToNeutral(JsonObject d, FrameParams p)
    {
        double[] stored = Vec3(d, "t_base", 1, 1, 1);
        for (int c = 0; c < 3; c++)
        {
            double shift = -Math.Log10(Math.Max(stored[c], 1e-10));
            if (Math.Abs(shift) < 1e-12) continue;
            p.DMinPerChannel[c] += shift;
            p.DMaxPerChannel[c] += shift;
        }
    }

    /// <summary>
    /// 这一帧的标定是否来自旧模型、需要重跑。
    ///
    /// 判据是 <c>d_min_per_channel</c> 缺失——那是新模型独有的键。旧工程的 d_max/scan_ev 与
    /// 现在固定的输出范围不是同一个量纲，**渲染结果一定会变**，所以不做静默折算（试过：把
    /// 增益折进端点在薄部偏 -18%、浓部 +53%，比不折算更糟），而是如实告诉用户重跑标定。
    /// </summary>
    public static bool NeedsRecalibration(JsonObject frame) => frame["d_min_per_channel"] is not JsonArray;

    /// <summary>
    /// 亮端三个密度。<c>d_max_per_channel</c> 在任何写过端点的版本里都存在，直接读取；
    /// 更早的工程只有标量 d_max，复制成三个通道作为一组中性起点。
    ///
    /// 旧版的 <c>wb_high</c> 乘数**不再折算**：它与固定输出范围下的端点不是同一量纲，折算
    /// 反而引入跨色调的大幅偏差。带旧参数的工程由 <see cref="NeedsRecalibration"/> 标记，
    /// 界面提示重跑标定。
    /// </summary>
    private static double[] MigrateHighlightEndpoint(JsonObject d)
        => d["d_max_per_channel"] is JsonArray dpc && dpc.Count == 3
            ? new[] { dpc[0]!.GetValue<double>(), dpc[1]!.GetValue<double>(), dpc[2]!.GetValue<double>() }
            : Repeat3(Dbl(d, "d_max", FrameParams.OutputRange));

    /// <summary>
    /// 暗端三个密度。新键 <c>d_min_per_channel</c> 优先；其次是上一版的
    /// <c>wb_offset_density</c>（同为绝对密度，语义一致）；再早的加性 <c>wb_offset</c> 翻号。
    /// </summary>
    private static double[] MigrateShadowEndpoint(JsonObject d)
    {
        if (d["d_min_per_channel"] is JsonArray n && n.Count >= 3)
            return new[] { n[0]!.GetValue<double>(), n[1]!.GetValue<double>(), n[2]!.GetValue<double>() };
        if (d["wb_offset_density"] is JsonArray a && a.Count >= 3)
            return new[] { a[0]!.GetValue<double>(), a[1]!.GetValue<double>(), a[2]!.GetValue<double>() };

        double[] legacy = Vec3(d, "wb_offset", 0, 0, 0);
        return new[] { -legacy[0], -legacy[1], -legacy[2] };
    }

    private static double[] Vec3(JsonObject d, string key, double a, double b, double c)
        => d[key] is JsonArray arr && arr.Count >= 3
            ? new[] { arr[0]!.GetValue<double>(), arr[1]!.GetValue<double>(), arr[2]!.GetValue<double>() }
            : new[] { a, b, c };

    private static double Dbl(JsonObject d, string key, double def)
        => d[key] is { } n ? n.GetValue<double>() : def;
    private static bool Bool(JsonObject d, string key, bool def)
        => d[key] is { } n ? n.GetValue<bool>() : def;
    private static string Str(JsonObject d, string key, string def)
        => d[key]?.GetValue<string>() ?? def;
}
