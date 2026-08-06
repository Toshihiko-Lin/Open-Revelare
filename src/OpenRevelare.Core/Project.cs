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
                if (f is JsonObject fo) d.Frames.Add(DesFrame(fo));
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
        return new JsonObject
        {
            ["filename"] = System.IO.Path.GetFileName(e.SourcePath),
            // FilmBase calibration
            ["t_base"] = Arr(p.TBase),
            ["d_max"] = p.DMax,
            ["wb_high"] = Arr(p.WbHigh),
            ["wb_offset"] = Arr(p.WbOffset),
            ["chroma_channel_scale"] = Arr(p.ChromaChannelScale),
            ["scan_exposure_ev"] = p.ScanExposureEv,
            ["chroma_grade"] = p.ChromaGrade,
            ["grade"] = p.Grade,
            ["pivot"] = p.Pivot,
            ["output_intent"] = p.OutputIntent == OutputIntent.Basic ? "basic" : "none",
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
    }

    private static Frame DesFrame(JsonObject d)
    {
        var p = new FrameParams
        {
            TBase = Vec3(d, "t_base", 0.82, 0.51, 0.29),
            DMax = Dbl(d, "d_max", 2.0),
            WbHigh = Vec3(d, "wb_high", 1, 1, 1),
            WbOffset = Vec3(d, "wb_offset", 0, 0, 0),
            ChromaChannelScale = Vec3(d, "chroma_channel_scale", 1, 1, 1),
            ScanExposureEv = Dbl(d, "scan_exposure_ev", 0.0),
            ChromaGrade = Dbl(d, "chroma_grade", 3.05),
            Grade = d["grade"] is { } g ? g.GetValue<double>() : Dbl(d, "gamma", 1.65),
            Pivot = Dbl(d, "pivot", 0.9),
            OutputIntent = Str(d, "output_intent", "basic") == "none" ? OutputIntent.None : OutputIntent.Basic,
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
