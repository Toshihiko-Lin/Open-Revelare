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
/// Keyed by source path and preview region: virtual copies share decoded pixels while separate
/// negatives cut from one scan retain their own preview.
/// </summary>
public sealed class PreviewCache
{
    /// <summary>
    /// Maximum resident preview bytes, scaled to the machine. Actual retention also follows
    /// current free memory, so other applications can reclaim the working set under pressure.
    ///
    /// A preview is ~20 MB at a 1600 px long edge, so the old flat 1 GB held about 50 frames — a
    /// reasonable share of a 48 GB workstation and a quarter of an 8 GB laptop, which is not a
    /// cache, it is the reason the machine starts swapping. One twenty-fourth of physical memory
    /// keeps roughly a 16-frame working set on 8 GB and a full 36-frame roll from 16 GB up, and
    /// the ceiling stops a very large machine from hoarding more than the old limit. Frames beyond
    /// the budget are not lost, only re-decoded when revisited.
    /// </summary>
    private static readonly long BudgetBytes = PickBudget();
    private const long SeverePressureBytes = 512L << 20;
    private const long ModeratePressureBytes = 1L << 30;

    private static long PickBudget()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (total <= 0) total = 8L << 30;   // unknown → assume a small machine
        return Math.Clamp(total / 24, 192L << 20, 1L << 30);
    }

    /// <summary>A cached preview plus the dimensions of the full-resolution decode it came from —
    /// the source size is worth keeping because nothing else holds the full buffer any more.</summary>
    public sealed record Entry(WorkingFrame Working, int SourceWidth, int SourceHeight)
    {
        public ImageBuffer Preview => Working.Pixels;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();          // front = most recently used
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _protectedKeys = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    private long _generation;
    private readonly object _gate = new();
    private readonly Func<long?> _availableBytes;
    private readonly long _budgetBytes;
    private readonly int _probeIntervalMs;
    private bool _hasPressureSample;
    private long _lastPressureProbe;
    private long? _sampledFreeBytes;

    public PreviewCache() : this(BudgetBytes, () =>
        SystemMemory.TryGetAvailableBytes(out long bytes) ? bytes : null, probeIntervalMs: 2_000) { }

    internal PreviewCache(long budgetBytes, Func<long?> availableBytes, int probeIntervalMs = 0)
    {
        if (budgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        if (probeIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(probeIntervalMs));
        _availableBytes = availableBytes ?? throw new ArgumentNullException(nameof(availableBytes));
        _budgetBytes = budgetBytes;
        _probeIntervalMs = probeIntervalMs;
    }

    internal long Generation { get { lock (_gate) return _generation; } }
    internal long ResidentBytes { get { lock (_gate) return _bytes; } }

    internal void Protect(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_gate) _protectedKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
    }

    private static long SizeOf(ImageBuffer b) => (long)b.Data.Length * sizeof(float);

    /// <summary>The cached entry for <paramref name="path"/>, or null. Marks it most-recently-used.</summary>
    public Entry? Get(string path)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out Entry? e)) return null;
            Touch(path);
            Trim(path);
            return e;
        }
    }

    /// <summary>Store (or replace) the preview for <paramref name="path"/>, evicting LRU entries
    /// until the total fits the budget. The entry just added is never the one evicted.</summary>
    public void Put(string path, WorkingFrame working, int sourceWidth, int sourceHeight)
        => PutIfCurrent(path, working, sourceWidth, sourceHeight, Generation);

    internal bool PutIfCurrent(string path, WorkingFrame working, int sourceWidth,
        int sourceHeight, long generation)
    {
        ArgumentNullException.ThrowIfNull(working);
        lock (_gate)
        {
            if (generation != _generation) return false;
            if (_entries.TryGetValue(path, out Entry? old))
                _bytes -= SizeOf(old.Preview);
            _entries[path] = new Entry(working, sourceWidth, sourceHeight);
            _bytes += SizeOf(working.Pixels);
            Touch(path);
            Trim(path);
            return true;
        }
    }

    private void Trim(string protectedPath)
    {
        // Keep a stable working set at each pressure level. Deriving the limit from the current
        // cache size made every eviction lower the next limit before the OS reported freed pages,
        // causing repeated frame switches to evict the whole roll one frame at a time.
        long budget = _budgetBytes;
        long now = Environment.TickCount64;
        if (!_hasPressureSample || now - _lastPressureProbe >= _probeIntervalMs)
        {
            _sampledFreeBytes = _availableBytes();
            _lastPressureProbe = now;
            _hasPressureSample = true;
        }
        if (_sampledFreeBytes is >= 0 and < SeverePressureBytes)
            budget = Math.Min(budget / 3, 128L << 20);
        else if (_sampledFreeBytes is >= 0 and < ModeratePressureBytes)
            budget = Math.Min(budget / 2, 384L << 20);
        for (LinkedListNode<string>? victim = _lru.Last; _bytes > budget && victim is not null;)
        {
            LinkedListNode<string>? previous = victim.Previous;
            if (victim.Value != protectedPath && !_protectedKeys.Contains(victim.Value))
            {
                _bytes -= SizeOf(_entries[victim.Value].Preview);
                _entries.Remove(victim.Value);
                _nodes.Remove(victim.Value);
                _lru.Remove(victim);
            }
            victim = previous;
        }
    }

    /// <summary>Drop everything — call when the roll changes, so a new import never serves
    /// pixels decoded for the previous one.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _entries.Clear(); _lru.Clear(); _nodes.Clear(); _bytes = 0;
            _protectedKeys.Clear();
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
