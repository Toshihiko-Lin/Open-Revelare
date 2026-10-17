using System.Runtime.CompilerServices;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>Loaded fields/matrices are immutable. If their external references change, require
/// an explicit reload/recalibration rather than silently changing a saved roll's colour basis.</summary>
internal static class CalibrationSources
{
    private static readonly ConditionalWeakTable<object, SourceSnapshot> Sources = new();
    internal static T Track<T>(T value, SourceSnapshot sources) where T : class
    {
        Sources.AddOrUpdate(value, sources);
        return value;
    }
    internal static bool IsCurrent(object? value) => value is null ||
        !Sources.TryGetValue(value, out var snapshot) || snapshot.IsCurrent;

    internal static void RequireCurrent(FrameParams p)
    {
        foreach (object? value in new object?[] { p.LccFlatField, p.DecoupleMatrix, p.DecoupleChromaMatrix })
            if (value is not null && Sources.TryGetValue(value, out var snapshot)) snapshot.RequireCurrent();
    }
}
