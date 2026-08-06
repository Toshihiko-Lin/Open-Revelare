using System;
using System.IO;
using System.Linq;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// One contact sheet per roll, on disk — the catalog's cover art.
///
/// A roll's thumbnail is its contact sheet rather than a grid of per-frame JPEGs. It costs about
/// the same (≈298 KB at a 2048 px long edge for 36 frames, vs ≈367 KB for 36 separate 384 px
/// thumbnails), but it arrives with the roll's own info bar burned into it — camera, film, roll
/// number, lab, date — so a wall of covers identifies itself without consulting the index.
///
/// Lives in %LOCALAPPDATA% (or a user-chosen directory), NOT in the config dir: it is regenerable
/// cache, and cache does not belong in the folder that holds the license and the settings. Losing
/// it costs one re-render, never an adjustment.
/// </summary>
public static class SheetStore
{
    /// <summary>Long edge of the stored sheet. 2048 is what the export path already uses, and a
    /// roll cover is never displayed larger than a card — storing 3072 would nearly double the
    /// footprint to sharpen an image nothing zooms into.</summary>
    public const int MaxLong = 2048;

    /// <summary>Quality 82: at this size the difference from 88 is invisible on a cover and worth
    /// ~20% of the file. The exported sheet the user actually keeps is still written at 92.</summary>
    private const int Quality = 82;

    public static string Dir
    {
        get
        {
            string custom = Settings.Current.SheetCacheDirectory;
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            string local = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(local, "OpenRevelare", "sheets");
        }
    }

    public static string PathFor(string rollId) => Path.Combine(Dir, rollId + ".jpg");

    public static bool Exists(string rollId) => File.Exists(PathFor(rollId));

    /// <summary>Write a roll's sheet, then bring the store back under budget.</summary>
    public static void Save(string rollId, ImageBuffer sheet)
    {
        string path = PathFor(rollId);
        Directory.CreateDirectory(Dir);
        // Temp + replace: the roll list reads these files while they are being rewritten.
        string tmp = path + ".tmp";
        JpegIO.ExportJpeg(sheet, tmp, Quality);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        Trim();
    }

    public static void Delete(string rollId)
    {
        try { File.Delete(PathFor(rollId)); } catch { /* already gone */ }
    }

    /// <summary>Total bytes on disk — shown in preferences so the cost is never a mystery.</summary>
    public static long TotalBytes()
    {
        try
        {
            return new DirectoryInfo(Dir).Exists
                ? new DirectoryInfo(Dir).EnumerateFiles("*.jpg").Sum(f => f.Length)
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Drop least-recently-written sheets past the budget. At ~300 KB a roll the default 1 GB is
    /// some 3000 rolls, so this effectively never fires — it exists so a pathological catalog
    /// cannot quietly eat a disk. An evicted sheet costs one re-render when its roll is opened.
    /// </summary>
    public static void Trim()
    {
        long budget = Math.Max(1, Settings.Current.SheetCacheBudgetGb) * (1L << 30);
        try
        {
            var dir = new DirectoryInfo(Dir);
            if (!dir.Exists) return;
            FileInfo[] files = dir.GetFiles("*.jpg");
            long total = files.Sum(f => f.Length);
            if (total <= budget) return;
            foreach (FileInfo f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= budget) break;
                total -= f.Length;
                try { f.Delete(); } catch { /* in use → skip it */ }
            }
        }
        catch { /* cache maintenance is never worth an exception */ }
    }
}
