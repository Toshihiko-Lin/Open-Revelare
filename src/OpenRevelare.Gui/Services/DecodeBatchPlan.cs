namespace OpenRevelare.Gui.Services;

internal static class DecodeBatchPlan
{
    /// <summary>Bounded source groups; RAW jobs remain independent so backend policies apply.</summary>
    public static T[][] Create<T>(IEnumerable<T> items, Func<T, string> path, Func<string, bool> isRaw, int limit = 4)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return items.GroupBy(path, comparer).SelectMany(group => isRaw(group.Key)
            ? group.Select(item => new[] { item }) : group.Chunk(limit)).ToArray();
    }
}
