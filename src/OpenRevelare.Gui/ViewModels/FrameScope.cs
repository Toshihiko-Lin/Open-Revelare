using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.ViewModels;

/// <summary>Both the visible count and the batch write use this exact target rule (audit D01).</summary>
internal static class FrameScope
{
    public static List<RollFrame> OtherFrames(IEnumerable<RollFrame> frames, RollFrame? source, bool selectedOnly)
        => source is null ? new() : frames.Where(f => !ReferenceEquals(f, source)
            && (!selectedOnly || f.IsSelected)).ToList();
}
