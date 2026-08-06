using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Session-scoped disk cache for Adobe-converted linear DNGs.
///
/// The DNG-Converter backend costs two subprocess launches and ~3.5 s per frame, and it is paid
/// again on every decode — a zoom patch, an export, a re-export. Keeping the converted linear
/// DNG lets all of those read a file instead: measured 418 ms against 6.1 s, and it is what
/// makes region decoding possible on this backend at all (a cropbox needs a file to crop).
///
/// WHERE: next to the source file, in <c>.revelare-cache/</c>, unless the user has set a
/// directory in 偏好设置. Following the source is what keeps this off the system drive by
/// default — the photos are on E:, so the cache is too. A linear DNG of a 60 MP frame is
/// ~349 MB, so an unattended cache on C: would be a way to silently fill it.
///
/// LIFETIME: this session only. The session owns a subdirectory named after its process id, so
/// a previous run that crashed leaves an identifiable orphan rather than an anonymous pile —
/// <see cref="SweepOrphans"/> removes those on first use, and <see cref="Cleanup"/> removes
/// ours on exit. Both are best-effort: a cache that cannot be deleted must never be a cache
/// that stops the app.
/// </summary>
internal static class DngCache
{
    private const string RootName = ".revelare-cache";

    private static readonly string SessionTag =
        $"s-{Environment.ProcessId}-{Process.GetCurrentProcess().StartTime.Ticks:x}";

    private static readonly object Gate = new();
    private static bool _swept;

    /// <summary>Cache root for a source file: the user's override if set, else beside the file.</summary>
    private static string RootFor(string sourcePath)
    {
        string? overrideDir = Settings.Current.CacheDirectory;
        if (!string.IsNullOrWhiteSpace(overrideDir)) return Path.Combine(overrideDir, RootName);
        return Path.Combine(Path.GetDirectoryName(sourcePath) ?? Path.GetTempPath(), RootName);
    }

    private static string SessionDirFor(string sourcePath)
        => Path.Combine(RootFor(sourcePath), SessionTag);

    /// <summary>
    /// The cached linear DNG for <paramref name="sourcePath"/>, converting it if absent.
    /// Returns null when caching is disabled or anything at all goes wrong — every caller must
    /// be able to fall back to converting in a temp directory as before.
    /// </summary>
    /// <param name="convert">Given a destination path, produce the linear DNG there.</param>
    public static string? GetOrConvert(string sourcePath, Action<string> convert)
    {
        if (!Settings.Current.CacheEnabled) return null;
        try
        {
            lock (Gate)
            {
                if (!_swept) { _swept = true; SweepOrphans(RootFor(sourcePath)); }

                // Via DirectoryFor so the root is registered for Cleanup — a roll can span
                // drives, and a root we never recorded is a root we never delete.
                string dir = DirectoryFor(sourcePath);
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, KeyFor(sourcePath) + ".dng");

                if (File.Exists(target))
                {
                    File.SetLastAccessTimeUtc(target, DateTime.UtcNow);   // LRU stamp
                    return target;
                }

                // Convert to a temp name first so a crash mid-write cannot leave a truncated
                // DNG that later looks like a valid cache hit.
                string partial = target + ".part";
                convert(partial);
                if (!File.Exists(partial)) return null;
                File.Move(partial, target, overwrite: true);
                Evict(dir);
                return target;
            }
        }
        catch
        {
            return null;   // caching is an optimisation; never let it break a decode
        }
    }

    /// <summary>Identity of a source file: path + size + write time, so an edited or replaced
    /// original is a different key rather than a stale hit.</summary>
    private static string KeyFor(string sourcePath)
    {
        var fi = new FileInfo(sourcePath);
        string raw = $"{sourcePath.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        return Path.GetFileNameWithoutExtension(sourcePath) + "-" +
               Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                   System.Text.Encoding.UTF8.GetBytes(raw)))[..12];
    }

    /// <summary>Bytes this session is holding across every root it has touched — a roll can span
    /// drives, so one directory is not the whole answer. For the preferences readout.</summary>
    public static long CurrentBytes()
    {
        long total = 0;
        lock (Gate)
        {
            foreach (string root in _roots)
            {
                try
                {
                    var dir = new DirectoryInfo(Path.Combine(root, SessionTag));
                    if (dir.Exists) total += dir.EnumerateFiles().Sum(f => f.Length);
                }
                catch { }
            }
        }
        return total;
    }

    /// <summary>Where the cache is going right now, for the preferences readout: the user's
    /// override, or a description of the follow-the-source default.</summary>
    public static string LocationDescription()
    {
        string? o = Settings.Current.CacheDirectory;
        if (!string.IsNullOrWhiteSpace(o)) return Path.Combine(o, RootName);
        lock (Gate)
        {
            if (_roots.Count > 0) return string.Join("、", _roots);
        }
        return $"跟随源文件：<素材目录>\\{RootName}\\";
    }

    /// <summary>Drop least-recently-used entries until the session fits its budget.</summary>
    private static void Evict(string dir)
    {
        long budget = (long)Settings.Current.CacheBudgetGb * 1024 * 1024 * 1024;
        var files = new DirectoryInfo(dir).EnumerateFiles("*.dng")
                                          .OrderBy(f => f.LastAccessTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (FileInfo f in files)
        {
            if (total <= budget) break;
            try { total -= f.Length; f.Delete(); } catch { /* in use — try again next time */ }
        }
    }

    /// <summary>
    /// Remove session directories belonging to processes that are gone. Without this a crash
    /// leaves hundreds of megabytes behind with nothing that will ever clean it up — which is
    /// exactly how a "session" cache turns into a permanent one.
    /// </summary>
    private static void SweepOrphans(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string dir in Directory.EnumerateDirectories(root, "s-*"))
        {
            string name = Path.GetFileName(dir);
            if (name == SessionTag) continue;
            string[] parts = name.Split('-');
            if (parts.Length < 3 || !int.TryParse(parts[1], out int pid)) continue;
            bool alive;
            try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; }
            catch { alive = false; }
            if (alive) continue;   // another OpenRevelare is genuinely using it
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Delete this session's cache. Called on shutdown.</summary>
    public static void Cleanup()
    {
        try
        {
            lock (Gate)
            {
                foreach (string root in _roots)
                {
                    string dir = Path.Combine(root, SessionTag);
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    // Remove the container too when we were the only tenant.
                    try { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                              Directory.Delete(root); } catch { }
                }
            }
        }
        catch { }
    }

    // Every root touched this session, so Cleanup can find them all (a roll may span drives).
    private static readonly System.Collections.Generic.HashSet<string> _roots =
        new(StringComparer.OrdinalIgnoreCase);

    private static void Remember(string root) { lock (Gate) _roots.Add(root); }

    /// <summary>The directory the next cache write for this source would land in — for the
    /// preferences readout, and so callers can show the user where their disk is going.</summary>
    public static string DirectoryFor(string sourcePath)
    {
        string root = RootFor(sourcePath);
        Remember(root);
        return Path.Combine(root, SessionTag);
    }
}
