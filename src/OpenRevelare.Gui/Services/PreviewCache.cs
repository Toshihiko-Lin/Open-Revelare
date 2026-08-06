using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Path-keyed LRU of downsampled linear preview buffers — the reason selecting a frame in the
/// film strip is instant instead of a multi-second RAW decode.
///
/// MEMORY MODEL (mirrors the Python GUI's <c>_raw_cache</c>): full-resolution negatives are NEVER
/// cached. A 24 MP float32 RGB frame is ~288 MB, so a 36-frame roll would be >10 GB. Preview
/// buffers are ~20 MB at a 1600 px long edge, so a whole roll sits around 0.7 GB — and the byte
/// budget below caps even that for pathologically long rolls.
///
/// Keyed by SOURCE PATH, not by frame: virtual copies share their parent's path and so reuse the
/// same decoded pixels at zero extra cost, which is exactly what makes toggling between an
/// original and its virtual copy free.
/// </summary>
public sealed class PreviewCache
{
    /// <summary>
    /// Total resident preview bytes to keep, scaled to the machine rather than fixed.
    ///
    /// A preview is ~20 MB at a 1600 px long edge, so the old flat 1 GB held about 50 frames — a
    /// reasonable share of a 48 GB workstation and a quarter of an 8 GB laptop, which is not a
    /// cache, it is the reason the machine starts swapping. One twenty-fourth of physical memory
    /// keeps roughly a 16-frame working set on 8 GB and a full 36-frame roll from 16 GB up, and
    /// the ceiling stops a very large machine from hoarding more than the old limit. Frames beyond
    /// the budget are not lost, only re-decoded when revisited.
    /// </summary>
    private static readonly long BudgetBytes = PickBudget();

    private static long PickBudget()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = 8L << 30;   // unknown → assume a small machine
        return Math.Clamp(total / 24, 192L << 20, 1L << 30);
    }

    /// <summary>A cached preview plus the dimensions of the full-resolution decode it came from —
    /// the source size is worth keeping because nothing else holds the full buffer any more.</summary>
    public sealed record Entry(ImageBuffer Preview, int SourceWidth, int SourceHeight);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();          // front = most recently used
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private readonly object _gate = new();

    private static long SizeOf(ImageBuffer b) => (long)b.Data.Length * sizeof(float);

    /// <summary>The cached entry for <paramref name="path"/>, or null. Marks it most-recently-used.</summary>
    public Entry? Get(string path)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out Entry? e)) return null;
            Touch(path);
            return e;
        }
    }

    /// <summary>Store (or replace) the preview for <paramref name="path"/>, evicting LRU entries
    /// until the total fits the budget. The entry just added is never the one evicted.</summary>
    public void Put(string path, ImageBuffer preview, int sourceWidth, int sourceHeight)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out Entry? old))
                _bytes -= SizeOf(old.Preview);
            _entries[path] = new Entry(preview, sourceWidth, sourceHeight);
            _bytes += SizeOf(preview);
            Touch(path);

            while (_bytes > BudgetBytes && _lru.Last is { } victim && victim.Value != path)
            {
                _bytes -= SizeOf(_entries[victim.Value].Preview);
                _entries.Remove(victim.Value);
                _nodes.Remove(victim.Value);
                _lru.RemoveLast();
            }
        }
    }

    /// <summary>Drop everything — call when the roll changes, so a new import never serves
    /// pixels decoded for the previous one.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear(); _lru.Clear(); _nodes.Clear(); _bytes = 0;
        }
    }

    private void Touch(string path)
    {
        if (_nodes.TryGetValue(path, out LinkedListNode<string>? node)) _lru.Remove(node);
        else node = new LinkedListNode<string>(path);
        _lru.AddFirst(node);
        _nodes[path] = node;
    }
}
