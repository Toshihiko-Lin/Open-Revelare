using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Application-level persistent preferences — port of Python's <c>negative/settings.py</c>
/// (QSettings). Stored as JSON at %APPDATA%/OpenRevelare/settings.json (XDG on Linux), the same
/// config dir the license module uses. Only the preferences the C# build can actually drive
/// are persisted; the rest of settings.py (workers / session state) has no C# counterpart yet.
/// </summary>
public static class Settings
{
    public sealed class Model
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RawDecode.RawBackend DecodeBackend { get; set; } = RawDecode.RawBackend.Auto;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RawDecode.FbddMode FbddMode { get; set; } = RawDecode.FbddMode.Off;
        public string Theme { get; set; } = "dark";     // "dark" | "light"

        /// <summary>UI language: "auto" (follow the OS), "zh" or "en". See <see cref="Loc"/>.
        ///
        /// Defaults to "zh": the primary audience is Chinese-speaking users, and an English-first
        /// default would require them to find the setting in a language they may not read.
        /// Non-Chinese systems can switch to auto or en via Preferences.</summary>
        public string Language { get; set; } = "zh";

        // ── Disk cache for Adobe-converted linear DNGs ──────────────────────────
        // Session-scoped, so it never accumulates across runs. Location follows the SOURCE file
        // by default rather than %LOCALAPPDATA% — a 60 MP frame converts to ~349 MB, and a cache
        // that quietly grows on the system drive is how C: fills up.
        public bool CacheEnabled { get; set; } = true;

        /// <summary>Override directory; empty = beside each source file.</summary>
        public string CacheDirectory { get; set; } = "";

        /// <summary>
        /// Keep converted linear DNGs ACROSS runs instead of deleting them on exit.
        ///
        /// Session scope was the safe default when the cache had no ceiling: 349 MB a frame with
        /// nothing to reclaim it is how a disk fills up. It has had an LRU and a GB budget for a
        /// while now, and paying the Adobe round trip again on every launch is expensive in a way
        /// that is very visible — 6.1 s per frame uncached against 418 ms cached, so reopening a
        /// 36-frame roll after a restart spends minutes redoing work whose result was on disk
        /// until the moment the app closed. Off by default so nobody's disk usage changes without
        /// them asking; the DNG backend is the only thing that reads it.
        /// </summary>
        public bool CachePersistent { get; set; }

        /// <summary>Ceiling in GB; least-recently-used entries are dropped past it.</summary>
        public int CacheBudgetGb { get; set; } = 5;

        /// <summary>How many RAW decodes may run at once. 0 = 自动 (sized from the machine's
        /// FREE physical memory, re-checked as work arrives). A manual value overrides that
        /// entirely — useful both ways: cap it to keep a shared machine responsive, or raise it
        /// on a workstation with plenty of headroom to spare.</summary>
        public int DecodeConcurrency { get; set; }

        /// <summary>Backdrop the photo is judged against (预览区右键 → 背景色). Deliberately
        /// independent of <see cref="Theme"/>: it is a viewing condition, not UI chrome, and a
        /// neutral mid grey is what keeps simultaneous contrast from biasing a colour call.
        /// Default is Lightroom's medium-grey-ish neutral.</summary>
        public string ViewerBackground { get; set; } = "#5E5E5E";

        // ── Roll-cover contact sheets (the catalog's thumbnails) ────────────────
        // Regenerable cache, so it lives in %LOCALAPPDATA%, not beside the license. ~300 KB per
        // roll — 500 rolls is about 145 MB — but the location is still a user's call: a small
        // system drive is exactly where that quietly becomes a problem.

        /// <summary>Override directory; empty = %LOCALAPPDATA%/OpenRevelare/sheets.</summary>
        public string SheetCacheDirectory { get; set; } = "";

        /// <summary>Ceiling in GB; least-recently-written sheets are dropped past it.</summary>
        public int SheetCacheBudgetGb { get; set; } = 1;

        /// <summary>Which contact-sheet look to print (印样窗口 → 深色/浅色). Independent of
        /// <see cref="Theme"/>: the sheet is an artefact you hand to someone else, so the look you
        /// want on paper has nothing to do with the chrome you edit in.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SheetStyle SheetStyle { get; set; } = SheetStyle.Light;

        /// <summary>Last confirmed export settings. An export preset is picked once and wanted
        /// every time after, so the dialog opens on what was used last rather than on defaults.</summary>
        public Models.ExportOptions Export { get; set; } = new();
    }

    /// <summary>Per-user config directory — settings and the roll catalog both live here,
    /// and the installer touches none of it, so an upgrade keeps them.</summary>
    public static readonly string ConfigDir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRevelare")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "OpenRevelare");
    /// <summary>
    /// Per-user data directory — regenerable markers, i.e. things that are not settings but
    /// must survive an upgrade. Same folder as %LOCALAPPDATA%\OpenRevelare on Windows and
    /// ~/.local/share/OpenRevelare elsewhere.
    ///
    /// Written out longhand rather than via <c>SpecialFolder.LocalApplicationData</c> because
    /// .NET maps that to ~/Library/Application Support on macOS — which would scatter this app's
    /// state across a third location while the config sits under XDG paths.
    /// </summary>
    public static readonly string DataDir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevelare")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "OpenRevelare");

    private static readonly string File_ = Path.Combine(ConfigDir, "settings.json");

    private static Model? _current;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Model Current
    {
        get
        {
            if (_current is not null) return _current;
            try
            {
                if (File.Exists(File_))
                    _current = JsonSerializer.Deserialize<Model>(File.ReadAllText(File_));
            }
            catch { /* corrupt settings → defaults */ }
            return _current ??= new Model();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(File_, JsonSerializer.Serialize(Current, JsonOpts));
        }
        catch { /* best-effort; never crash on a settings write */ }
    }
}
