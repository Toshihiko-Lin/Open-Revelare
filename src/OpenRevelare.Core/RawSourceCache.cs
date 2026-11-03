namespace OpenRevelare.Core;

/// <summary>Bounded, revision-keyed immutable RAW file bytes. Decoder contexts are never shared.</summary>
internal sealed class RawSourceCache(long capacity, Func<long?> availableBytes)
{
    internal sealed record Revision(string Path, long Length, long Modified, long Created)
    {
        public static Revision Capture(string path)
        {
            var file = new FileInfo(System.IO.Path.GetFullPath(path));
            file.Refresh();
            if (!file.Exists) throw new FileNotFoundException("RAW source does not exist.", file.FullName);
            return new(OperatingSystem.IsWindows() ? file.FullName.ToUpperInvariant() : file.FullName,
                file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Revision, (byte[] Bytes, LinkedListNode<Revision> Node)> _entries = new();
    private readonly Dictionary<Revision, (long Generation, Lazy<byte[]> Read)> _reading = new();
    private readonly LinkedList<Revision> _lru = new();
    private long _resident, _generation, _hits, _misses, _joins;

    public (long ResidentBytes, int Entries, long Hits, long Misses, long Joins) Snapshot()
    {
        lock (_gate) return (_resident, _entries.Count, _hits, _misses, _joins);
    }

    public void Clear()
    {
        lock (_gate) { _entries.Clear(); _lru.Clear(); _resident = 0; _generation++; }
    }

    public byte[] Read(string path, Func<string, byte[]> load, out string status)
    {
        Revision revision = Revision.Capture(path);
        Lazy<byte[]> reader;
        long generation;
        lock (_gate)
        {
            long budget = Budget();
            Trim(budget);
            // Replacement must release the previous revision even if the new file is too large.
            foreach (var stale in _entries.Keys.Where(k => k.Path == revision.Path && k != revision).ToArray()) Remove(stale);
            if (_entries.TryGetValue(revision, out var entry))
            {
                _lru.Remove(entry.Node); _lru.AddFirst(entry.Node);
                if (Revision.Capture(path) != revision) { Remove(revision); throw new IOException("RAW source changed during lookup."); }
                _hits++; status = "hit";
                return entry.Bytes;
            }
            generation = _generation;
            if (_reading.TryGetValue(revision, out var running) && running.Generation == generation)
            {
                reader = running.Read; _joins++; status = "join";
            }
            else
            {
                reader = new Lazy<byte[]>(() =>
                {
                    byte[] bytes = load(path);
                    if (bytes.LongLength != revision.Length || Revision.Capture(path) != revision)
                        throw new IOException("RAW source changed while reading; retry the operation.");
                    return bytes;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
                _reading[revision] = (generation, reader);
                _misses++; status = "miss";
            }
        }
        try
        {
            byte[] bytes = reader.Value;
            // Catch a replacement between a joined read finishing and this caller resuming.
            if (Revision.Capture(path) != revision) throw new IOException("RAW source changed while reading.");
            lock (_gate)
            {
                long budget = Budget();
                Trim(budget);
                if (generation == _generation && bytes.LongLength <= budget && !_entries.ContainsKey(revision))
                {
                    Trim(budget - bytes.LongLength);
                    while (_entries.Count >= 16 && _lru.Last is { } last) Remove(last.Value);
                    _entries.Add(revision, (bytes, _lru.AddFirst(revision)));
                    _resident += bytes.LongLength;
                }
            }
            return bytes;
        }
        finally
        {
            lock (_gate)
                if (_reading.TryGetValue(revision, out var current) && ReferenceEquals(current.Read, reader))
                    _reading.Remove(revision);
        }
    }

    private long Budget()
        => Math.Max(0, availableBytes() is { } free ? Math.Min(capacity, free - (1L << 30)) : capacity);

    private void Trim(long budget)
    {
        while (_resident > budget && _lru.Last is { } last) Remove(last.Value);
    }

    private void Remove(Revision key)
    {
        var entry = _entries[key];
        _entries.Remove(key); _lru.Remove(entry.Node); _resident -= entry.Bytes.LongLength;
    }
}
