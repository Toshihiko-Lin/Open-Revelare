using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// The roll catalog — a persistent index of every roll this install has worked on, so a roll can
/// be reopened months later without hunting down its .ncproj.
///
/// The index holds only what a roll LIST needs (title, film stock, frame count, timestamps). The
/// edit itself stays in the roll's own .ncproj beside the source images and is loaded on demand.
/// That split is the point: losing or deleting the catalog costs no adjustment — it can be rebuilt
/// by rescanning for .ncproj files — and a roll copied to another disk carries its edit with it.
///
/// Stored at %APPDATA%/OpenRevelare/catalog.json (XDG on Linux), the directory settings/license
/// already use and the installer never touches, so upgrading keeps the catalog.
/// </summary>
public static class Catalog
{
    private const int FormatVersion = 1;

    /// <summary>One roll's index entry. Everything here is a DUPLICATE of something in the roll's
    /// .ncproj — held so the roll list can be drawn without opening dozens of project files.</summary>
    public sealed class Roll
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ProjectPath { get; set; } = "";

        /// <summary>Display name in the roll list. Renaming a roll changes THIS (and the .ncproj
        /// file name) — never the source images, and never <see cref="RollNumber"/>, which is
        /// printed on the contact sheet's info bar and means something to the user.</summary>
        public string Title { get; set; } = "";

        public string RollNumber { get; set; } = "";
        public string FilmStock { get; set; } = "";
        public string CameraBody { get; set; } = "";
        public string DevDate { get; set; } = "";
        public int FrameCount { get; set; }

        public DateTime ImportedAt { get; set; } = DateTime.Now;
        public DateTime ModifiedAt { get; set; } = DateTime.Now;
        public DateTime LastOpenedAt { get; set; } = DateTime.Now;

        /// <summary>The project file is gone (moved disk, deleted). Not persisted — recomputed on
        /// every read, because the answer changes without the catalog being written to.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Missing => !File.Exists(ProjectPath);

        /// <summary>Subtitle for the roll list: whatever of film/camera/date is known.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Subtitle => string.Join(" · ",
            new[] { FilmStock, CameraBody, DevDate }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private sealed class Model
    {
        public int Version { get; set; } = FormatVersion;
        public List<Roll> Rolls { get; set; } = new();
    }

    private static readonly string File_ = Path.Combine(Settings.ConfigDir, "catalog.json");
    // Relaxed escaping so a Chinese roll title stays readable in the file — this index is meant to
    // be openable in a text editor when something needs untangling by hand.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly object Gate = new();
    private static Model? _model;

    /// <summary>Fallback home for project files when the source folder cannot be written to
    /// (read-only card, network share). Rolls normally keep their .ncproj beside the images.</summary>
    public static string FallbackProjectDir => Path.Combine(Settings.ConfigDir, "rolls");

    private static Model Current
    {
        get
        {
            if (_model is not null) return _model;
            try
            {
                if (File.Exists(File_))
                    _model = JsonSerializer.Deserialize<Model>(File.ReadAllText(File_));
            }
            catch { /* corrupt index → start empty; the .ncproj files are the real data */ }
            return _model ??= new Model();
        }
    }

    public static IReadOnlyList<Roll> Rolls { get { lock (Gate) return Current.Rolls.ToList(); } }

    /// <summary>Most-recently-opened rolls first — what the 「最近的卷」 menu shows.</summary>
    public static IReadOnlyList<Roll> Recent(int max)
    {
        lock (Gate)
            return Current.Rolls.OrderByDescending(r => r.LastOpenedAt).Take(max).ToList();
    }

    public static Roll? ById(string id)
    {
        lock (Gate) return Current.Rolls.FirstOrDefault(r => r.Id == id);
    }

    public static Roll? ByProjectPath(string projectPath)
    {
        string norm = Norm(projectPath);
        lock (Gate)
            return Current.Rolls.FirstOrDefault(
                r => string.Equals(Norm(r.ProjectPath), norm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Insert or replace <paramref name="roll"/> (matched by Id) and persist.</summary>
    public static void Upsert(Roll roll)
    {
        lock (Gate)
        {
            List<Roll> list = Current.Rolls;
            int i = list.FindIndex(r => r.Id == roll.Id);
            if (i >= 0) list[i] = roll; else list.Add(roll);
        }
        Save();
    }

    public static void Remove(string id)
    {
        lock (Gate) { Current.Rolls.RemoveAll(r => r.Id == id); Forgotten.Add(id); }
        Save();
    }

    /// <summary>Ids removed in THIS session. Needed by the merge in <see cref="Save"/>: without
    /// it, a roll deleted here would be resurrected from the copy another instance still holds.
    /// </summary>
    private static readonly HashSet<string> Forgotten = new();

    /// <summary>
    /// Retitle a roll and move its project file to match, keeping it in the same folder as the
    /// images. Mutates the entry in place, so a roll that is currently open keeps autosaving —
    /// to its new path.
    /// </summary>
    public static void Rename(Roll roll, string newTitle)
    {
        string dir = Path.GetDirectoryName(roll.ProjectPath) ?? FallbackProjectDir;
        string name = Sanitize(newTitle);
        string target = Path.Combine(dir, name + ".ncproj");
        for (int n = 2; !PathEquals(target, roll.ProjectPath) && File.Exists(target); n++)
            target = Path.Combine(dir, $"{name}-{n}.ncproj");

        if (!PathEquals(target, roll.ProjectPath) && File.Exists(roll.ProjectPath))
            File.Move(roll.ProjectPath, target);

        roll.Title = newTitle;
        roll.ProjectPath = target;
        Save();
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Point an entry at a project file that moved. The Id is kept, so the roll holds on
    /// to its cover and its place in the wall.</summary>
    public static void Relocate(Roll roll, string newProjectPath)
    {
        roll.ProjectPath = Path.GetFullPath(newProjectPath);
        roll.LastOpenedAt = DateTime.Now;
        Save();
    }

    /// <summary>Rolls whose project file sits in <paramref name="dir"/> — used to notice that a
    /// folder being imported already has a roll, instead of silently making a second one.</summary>
    public static IReadOnlyList<Roll> InFolder(string dir)
    {
        string norm = Norm(dir);
        lock (Gate)
            return Current.Rolls
                .Where(r => string.Equals(Norm(Path.GetDirectoryName(r.ProjectPath) ?? ""), norm,
                                          StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>
    /// Adopt every .ncproj under <paramref name="root"/> that is not already indexed. This is how
    /// a lost or deleted catalog is rebuilt: the project files are the real data, so the index can
    /// always be reconstructed from the disk rather than restored from a backup.
    /// </summary>
    public static int Scan(string root)
    {
        var found = new List<Roll>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.ncproj", SearchOption.AllDirectories))
            {
                if (ByProjectPath(file) is not null) continue;
                try
                {
                    OpenRevelare.Core.Project.Data d = OpenRevelare.Core.Project.Load(file);
                    var info = new FileInfo(file);
                    found.Add(new Roll
                    {
                        ProjectPath = Path.GetFullPath(file),
                        Title = Path.GetFileNameWithoutExtension(file),
                        FrameCount = d.Frames.Count,
                        RollNumber = d.Meta.RollNumber,
                        FilmStock = d.Meta.FilmStock,
                        CameraBody = d.Meta.CameraBody,
                        DevDate = d.Meta.DevDate,
                        // The file's own timestamps are the best evidence available; a scanned
                        // roll should not jump to the front of the wall as if freshly imported.
                        ImportedAt = info.CreationTime,
                        ModifiedAt = info.LastWriteTime,
                        LastOpenedAt = info.LastWriteTime,
                    });
                }
                catch { /* not a project we can read → leave it alone */ }
            }
        }
        catch { /* unreadable tree → whatever was found still counts */ }

        if (found.Count == 0) return 0;
        lock (Gate) Current.Rolls.AddRange(found);   // one write for the whole scan, not one each
        Save();
        return found.Count;
    }

    /// <summary>
    /// Write the index. Goes through a temp file and an atomic replace: autosave rewrites this on
    /// every idle pause, and a half-written catalog.json read at next launch would look like an
    /// empty catalog.
    /// </summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.ConfigDir);
            string json;
            lock (Gate)
            {
                MergeFromDisk();
                json = JsonSerializer.Serialize(Current, JsonOpts);
            }
            AtomicWrite(File_, json);
        }
        catch { /* best-effort; never crash on an index write */ }
    }

    /// <summary>
    /// Adopt rolls a SECOND running instance added since we last read the file, so writing our
    /// copy does not silently drop theirs. Ours win for anything both sides know about, and rolls
    /// deleted here stay deleted (<see cref="Forgotten"/>).
    ///
    /// Not a lock — two instances saving in the same instant can still lose one entry — but it
    /// turns the common case (two windows, minutes apart) from data loss into a merge. A real
    /// lock is not worth it for a single-user desktop index that a rescan can rebuild.
    /// </summary>
    private static void MergeFromDisk()
    {
        if (!File.Exists(File_)) return;
        try
        {
            Model? disk = JsonSerializer.Deserialize<Model>(File.ReadAllText(File_));
            if (disk is null) return;
            var known = new HashSet<string>(Current.Rolls.Select(r => r.Id));
            foreach (Roll r in disk.Rolls)
                if (!known.Contains(r.Id) && !Forgotten.Contains(r.Id))
                    Current.Rolls.Add(r);
        }
        catch { /* unreadable file → our copy is the better one anyway */ }
    }

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/> without ever leaving a
    /// truncated file behind, even if the process dies mid-write.</summary>
    public static void AtomicWrite(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    // ── Project-file placement ──────────────────────────────────────────────────

    /// <summary>
    /// Where a newly imported roll's .ncproj goes: beside its images, named after the roll, with a
    /// numeric suffix if that name is taken. Falls back to <see cref="FallbackProjectDir"/> when
    /// the source folder is not writable — a roll on a read-only card still has to be editable.
    /// </summary>
    public static string NewProjectPath(string sourceDir, string title)
    {
        string name = Sanitize(title);
        if (name.Length == 0) name = "roll";

        foreach (string dir in Writable(sourceDir))
        {
            string candidate = Path.Combine(dir, name + ".ncproj");
            for (int n = 2; File.Exists(candidate) || ByProjectPath(candidate) is not null; n++)
                candidate = Path.Combine(dir, $"{name}-{n}.ncproj");
            return candidate;
        }
        // Nothing writable at all — hand back the fallback path anyway and let the save report it.
        return Path.Combine(FallbackProjectDir, name + ".ncproj");
    }

    /// <summary>The candidate directories, most-preferred first, that are actually writable.</summary>
    private static IEnumerable<string> Writable(string sourceDir)
    {
        foreach (string dir in new[] { sourceDir, FallbackProjectDir })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            bool ok;
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".revelare-write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                ok = true;
            }
            catch { ok = false; }
            if (ok) yield return dir;
        }
    }

    /// <summary>Strip what a file name cannot contain, and trim what Windows will not keep.</summary>
    public static string Sanitize(string title)
    {
        var sb = new System.Text.StringBuilder(title.Length);
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char c in title) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string Norm(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}
