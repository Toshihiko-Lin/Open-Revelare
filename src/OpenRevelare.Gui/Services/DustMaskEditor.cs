using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

// Edit the full-resolution mask. A brush is a screen circle mapped to the local source
// ellipse, so rotation, crop and optical correction do not move the edited area.
internal sealed class DustMaskEditor
{
    internal readonly record struct Brush(double X, double Y, double Ax, double Ay, double Bx, double By);
    internal sealed record Stroke(string Kind, Brush[] Brushes);
    private readonly List<DustMask> _undo = [];
    private readonly List<DustMask> _redo = [];
    private readonly string _initialHash;
    private readonly List<Stroke> _history = [];
    private readonly List<Stroke> _historyRedo = [];
    public IReadOnlyList<Stroke> History => _history;
    public DustMask Mask { get; private set; }
    public bool IsModified => Mask.ContentHash != _initialHash;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public DustMaskEditor(DustMask mask)
    {
        Mask = mask;
        _initialHash = mask.ContentHash;
    }

    public bool Paint(IEnumerable<Brush> brushes, bool restore)
    {
        var samples = brushes.ToArray();
        if (_history.Count >= 1024 || samples.Length > 200_000 ||
            _history.Sum(s => (long)s.Brushes.Length) + samples.Length > 200_000)
            throw new ArgumentException("Dust brush history exceeds 1024 strokes or 200000 samples.");
        var rows = Rows();
        foreach (var brush in samples)
        {
            double xx = brush.Ax * brush.Ax + brush.Bx * brush.Bx;
            double xy = brush.Ax * brush.Ay + brush.Bx * brush.By;
            double yy = brush.Ay * brush.Ay + brush.By * brush.By;
            double determinant = xx * yy - xy * xy;
            if (!double.IsFinite(brush.X + brush.Y + xx + xy + yy) || determinant <= 1e-12) continue;
            double a = yy / determinant, b = -xy / determinant, c = xx / determinant;
            double verticalRadius = Math.Sqrt(yy);
            int firstY = Math.Max(0, (int)Math.Ceiling(brush.Y - verticalRadius));
            int lastY = Math.Min(Mask.Height - 1, (int)Math.Floor(brush.Y + verticalRadius));
            for (int y = firstY; y <= lastY; y++)
            {
                double dy = y - brush.Y;
                double root = a - (a * c - b * b) * dy * dy;
                if (root < 0) continue;
                double center = brush.X - b * dy / a, halfWidth = Math.Sqrt(root) / a;
                int start = Math.Max(0, (int)Math.Ceiling(center - halfWidth));
                int end = Math.Min(Mask.Width, (int)Math.Floor(center + halfWidth) + 1);
                ChangeRow(rows, y, start, end, restore);
            }
        }
        if (!Commit(rows)) return false;
        _history.Add(new(restore ? "erase" : "add", samples));
        _historyRedo.Clear();
        return true;
    }

    private SortedDictionary<int, List<DustMask.Run>> Rows() =>
        new(Mask.EnumerateRuns().GroupBy(run => run.Y).ToDictionary(group => group.Key, group => group.ToList()));

    private static void ChangeRow(SortedDictionary<int, List<DustMask.Run>> rows,
        int y, int start, int end, bool restore)
    {
        if (start >= end) return;
        rows.TryGetValue(y, out var current);
        var changed = new List<DustMask.Run>();
        if (restore)
        {
            foreach (var run in current ?? [])
            {
                if (run.End <= start || run.Start >= end) { changed.Add(run); continue; }
                if (run.Start < start) changed.Add(new(y, run.Start, start));
                if (run.End > end) changed.Add(new(y, end, run.End));
            }
        }
        else
        {
            bool inserted = false;
            foreach (var run in current ?? [])
            {
                if (run.End < start) { changed.Add(run); continue; }
                if (run.Start > end)
                {
                    if (!inserted) { changed.Add(new(y, start, end)); inserted = true; }
                    changed.Add(run);
                    continue;
                }
                start = Math.Min(start, run.Start);
                end = Math.Max(end, run.End);
            }
            if (!inserted) changed.Add(new(y, start, end));
        }
        if (changed.Count == 0) rows.Remove(y);
        else rows[y] = changed;
    }

    private bool Commit(SortedDictionary<int, List<DustMask.Run>> rows)
    {
        // Construct and validate before publishing. An oversized brush must leave the
        // previous mask and its history intact.
        var next = new DustMask(Mask.Width, Mask.Height, Mask.Bounds, rows.Values.SelectMany(row => row));
        if (next.ContentHash == Mask.ContentHash) return false;
        _undo.Add(Mask);
        while (_undo.Count > 20 || _undo.Count > 1 && _undo.Sum(mask => (long)mask.EnumerateRuns().Count()) > 250_000)
            _undo.RemoveAt(0);
        _redo.Clear();
        Mask = next;
        return true;
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _redo.Add(Mask);
        _historyRedo.Add(_history[^1]);
        _history.RemoveAt(_history.Count - 1);
        Mask = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _undo.Add(Mask);
        _history.Add(_historyRedo[^1]);
        _historyRedo.RemoveAt(_historyRedo.Count - 1);
        Mask = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }
}
