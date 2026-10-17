using OpenRevelare.Core;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Services;

/// <summary>Prepare against detached parameters: a late missing source or failed crop calculation
/// must not leave half a roll changed before undo/autosave learns about the operation.</summary>
internal static class BatchParameterEdit
{
    internal static IReadOnlyList<(RollFrame Frame, FrameParams Params)> Prepare(
        IEnumerable<RollFrame> targets, Action<FrameParams> edit, RollFrame? source = null)
    {
        RollFrame[] frames = targets.ToArray();
        if (source is not null) RequireSource(source);
        foreach (RollFrame frame in frames) RequireSource(frame);

        var prepared = new List<(RollFrame Frame, FrameParams Params)>(frames.Length);
        foreach (RollFrame frame in frames)
        {
            FrameParams next = frame.Params.Clone();
            edit(next);
            prepared.Add((frame, next));
        }
        return prepared;
    }

    private static void RequireSource(RollFrame frame)
    {
        if (!File.Exists(frame.Path))
            throw new IOException(Loc.F($"原片不可用：{frame.FileName}"));
    }
}
