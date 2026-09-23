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

        // ── 图库 wall ───────────────────────────────────────────────────────────
        // Both persisted, because how a wall is arranged is a standing preference: a user who
        // works by roll number re-picks it on every launch otherwise.

        /// <summary>Which <see cref="RollSortKey"/> the roll wall is ordered by. Stored by NAME
        /// rather than as a number so a settings file survives the enum gaining a member.</summary>
        public string LibrarySortKey { get; set; } = nameof(RollSortKey.ImportedAt);

        /// <summary>Newest / Z→A first. Defaults to true with the default key, which is the order
        /// the wall has always had: the roll you just imported sits next to the 新建 tile.</summary>
        public bool LibrarySortDescending { get; set; } = true;

        /// <summary>Which contact-sheet look to print (印样窗口 → 深色/浅色). Independent of
        /// <see cref="Theme"/>: the sheet is an artefact you hand to someone else, so the look you
        /// want on paper has nothing to do with the chrome you edit in.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SheetStyle SheetStyle { get; set; } = SheetStyle.Light;

        /// <summary>Which proportion to lay the sheet out for (印样窗口 → 版面比例). Like
        /// <see cref="SheetStyle"/> this is a standing preference: you print sheets the shape you
        /// file or post them in, not a shape you re-decide per roll.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SheetAspect SheetAspect { get; set; } = SheetAspect.Auto;

        /// <summary>Which way round <see cref="SheetAspect"/> is read (印样窗口 → 横/竖).</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SheetOrientation SheetOrientation { get; set; } = SheetOrientation.Landscape;

        /// <summary>
        /// Run the full auto-inversion chain (片基 → 亮部 WB → D-max → 色阶) when a roll is
        /// imported, instead of only estimating the film base.
        ///
        /// On by default: the chain's four steps are what a user does by hand immediately after
        /// every import anyway, and each one is re-runnable from its own button afterwards, so
        /// the cost of a wrong guess is a click. Off restores the film-base-only import — worth
        /// having for rolls whose calibration is being dialled in deliberately, where an
        /// automatic D-max and levels are noise on top of the measurement.
        /// </summary>
        public bool AutoInvertOnImport { get; set; } = true;

        /// <summary>
        /// Whether the import dialog's strip-split box opens ticked.
        ///
        /// Off by default, unlike the auto-inversion above, because the two have opposite costs
        /// when guessed wrong. A needless inversion pass is a click to redo; a needless split
        /// pre-pass decodes every file in the roll and puts a dialog in the way before anything
        /// can be seen. Whoever scans whole strips turns it on once and it stays on.
        /// </summary>
        public bool SplitStripsOnImport { get; set; }

        // RecentPrintLuts was here: a history of every cube ever chosen through the file dialog.
        // Removed once the app began shipping built-in stocks and a drop-in LUT folder — the
        // history's rows were labelled by filename, which for a bundled stock is the same text as
        // its built-in row, so the picker showed apparent duplicates. Anything worth keeping goes
        // in the LUT folder. Old settings files still carrying the key are harmless: unknown
        // members are ignored on read, and the key disappears on the next save.

        /// <summary>
        /// Where the over/under-exposure overlay (J) puts its two ends, as display luma in [0,1].
        ///
        /// Adjustable because "clipped" is a judgement about the DELIVERY, not a property of the
        /// picture: 98% is right for a file going to a lab that will hold the last two percent, and
        /// far too lax for a screen-only JPEG where the top three percent will read as paper white.
        /// The defaults are the fixed values the overlay used before, so nobody's reading of it
        /// changes until they move a slider.
        /// </summary>
        public double ClipShadowThreshold { get; set; } = 0.02d;

        /// <inheritdoc cref="ClipShadowThreshold"/>
        public double ClipHighlightThreshold { get; set; } = 0.98d;

        /// <summary>Last confirmed export settings. An export preset is picked once and wanted
        /// every time after, so the dialog opens on what was used last rather than on defaults.</summary>
        public Models.ExportOptions Export { get; set; } = new();
    }

    /// <summary>
    /// Environment overrides for the two per-user directories, honoured before the platform
    /// defaults below.
    ///
    /// <para>
    /// THE REASON THEY EXIST is that a test run is otherwise indistinguishable from the real
    /// application. Anything that constructs a <c>MainViewModel</c> and opens a roll reaches
    /// <c>RegisterRoll</c> → <c>Catalog.Upsert</c>, which writes
    /// <c>%APPDATA%\OpenRevelare\catalog.json</c> — the developer's OWN photo library. The rolls
    /// land there titled after the temp folder their fixtures were written in, the fixture files
    /// are deleted when the test finishes, and what is left is a permanent "文件缺失" card in a
    /// real person's library, one per test per run. Measured on this machine: 123 of 124 catalog
    /// entries were that residue.
    /// </para>
    ///
    /// <para>
    /// Read once into <c>static readonly</c> fields, so a process must set them BEFORE anything
    /// touches <see cref="Settings"/>. The test assembly does that from a
    /// <c>[ModuleInitializer]</c>, which runs before any test body.
    /// </para>
    /// </summary>
    private const string ConfigDirVariable = "OPENREVELARE_CONFIG_DIR";
    private const string DataDirVariable = "OPENREVELARE_DATA_DIR";

    private static string? Override(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Per-user config directory — settings and the roll catalog both live here,
    /// and the installer touches none of it, so an upgrade keeps them.</summary>
    public static readonly string ConfigDir = Override(ConfigDirVariable) ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRevelare")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "OpenRevelare"));
    /// <summary>
    /// Per-user data directory — regenerable markers, i.e. things that are not settings but
    /// must survive an upgrade. Same folder as %LOCALAPPDATA%\OpenRevelare on Windows and
    /// ~/.local/share/OpenRevelare elsewhere.
    ///
    /// Written out longhand rather than via <c>SpecialFolder.LocalApplicationData</c> because
    /// .NET maps that to ~/Library/Application Support on macOS — which would scatter this app's
    /// state across a third location while the config sits under XDG paths.
    /// </summary>
    public static readonly string DataDir = Override(DataDirVariable) ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevelare")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME")
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "OpenRevelare"));

    /// <summary>
    /// Drop-in folder for print LUTs: any .cube copied here is offered in 【胶片风格】 on the next
    /// start, so a collection of stocks is added once rather than picked file by file.
    ///
    /// Under <see cref="DataDir"/> and not beside the executable, because the install location is
    /// not writable on any of the three platforms — Program Files needs admin, a signed .app must
    /// not be modified, and an AppImage is a read-only mount whose path changes every run. Here it
    /// also survives an upgrade and an uninstall, which is what a user's own files should do.
    /// </summary>
    public static readonly string LutDir = Path.Combine(DataDir, "luts");

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
