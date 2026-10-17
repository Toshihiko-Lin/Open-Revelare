using System.Runtime.CompilerServices;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>Keep the revision of the pixels, not a fresh revision captured after they were
/// fetched. Weak ownership prevents this provenance map from becoming another image cache.</summary>
internal static class DecodedSources
{
    private static readonly ConditionalWeakTable<WorkingFrame, SourceStamp> Sources = new();
    internal static WorkingFrame Track(WorkingFrame pixels, SourceStamp source)
    {
        Sources.AddOrUpdate(pixels, source);
        return pixels;
    }
    internal static SourceStamp? For(WorkingFrame pixels) => Sources.TryGetValue(pixels, out var source) ? source : null;
}
