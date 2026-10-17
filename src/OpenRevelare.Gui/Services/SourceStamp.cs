using System.Globalization;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>A cheap revision, not a content hash. Size, write time and creation time detect
/// ordinary edits and replacements without reading a multi-GB scan on every cache lookup.
/// Replacing bytes while preserving all three attributes is an explicitly accepted blind spot.</summary>
internal sealed record SourceStamp(string Path, long Length, long ModifiedTicks,
    long CreatedTicks, string Processing)
{
    internal static SourceStamp Capture(string path)
    {
        string absolute = System.IO.Path.GetFullPath(path);
        var file = new FileInfo(absolute);
        file.Refresh();
        if (!file.Exists) throw new FileNotFoundException(Loc.F($"原片不可用：{file.Name}"), absolute);
        string processing = RawDecode.IsRawExtension(absolute)
            ? $"raw-v1:{(int)Settings.Current.DecodeBackend}:{(int)Settings.Current.FbddMode}"
            : "tiff-v1";
        return new(OperatingSystem.IsWindows() ? absolute.ToUpperInvariant() : absolute,
            file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks, processing);
    }

    internal static SourceStamp? TryCapture(string path)
    {
        try { return Capture(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal string Key => string.Create(CultureInfo.InvariantCulture,
        $"{Path}|{Length}:{ModifiedTicks}:{CreatedTicks}|{Processing}");

    internal bool IsCurrent => TryCapture(Path) == this;

    internal void RequireCurrent()
    {
        if (!IsCurrent) throw new IOException(Loc.F($"处理期间原片已改变，请重试：{System.IO.Path.GetFileName(Path)}"));
    }

    internal static T ReadStable<T>(string path, Func<T> read)
    {
        SourceStamp stamp = Capture(path);
        T value = read();
        stamp.RequireCurrent();
        return value;
    }
}
