namespace OpenRevelare.Gui.Services;

/// <summary>Includes missing contributors too: a file appearing halfway through a pooled
/// analysis changes the requested population just as surely as one being replaced.</summary>
internal sealed class SourceSnapshot
{
    private readonly (string Path, SourceStamp? Stamp)[] _sources;
    internal SourceSnapshot(IEnumerable<string> paths) => _sources = paths
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .Select(path => (path, SourceStamp.TryCapture(path))).ToArray();

    internal bool IsCurrent => _sources.All(s => SourceStamp.TryCapture(s.Path) == s.Stamp);

    internal void RequireCurrent()
    {
        foreach (var (path, stamp) in _sources)
            if (SourceStamp.TryCapture(path) != stamp)
                throw new IOException(Loc.F($"处理期间原片已改变，请重试：{System.IO.Path.GetFileName(path)}"));
    }
}
