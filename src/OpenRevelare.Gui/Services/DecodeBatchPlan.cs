namespace OpenRevelare.Gui.Services;

internal static class DecodeBatchPlan
{
    /// <summary>Bounded source groups sharing one decode, including RAW crop previews.</summary>
    public static T[][] Create<T>(IEnumerable<T> items, Func<T, string> path, int limit = 4)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return items.GroupBy(path, comparer).SelectMany(group => group.Chunk(limit)).ToArray();
    }
}
