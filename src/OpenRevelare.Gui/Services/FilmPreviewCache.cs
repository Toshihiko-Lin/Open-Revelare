using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>Bounded physical-stage preparations for foreground preview edits. Decoded source
/// ownership stays with the decode cache; weak references here cannot pin evicted RAW pixels.</summary>
internal sealed class FilmPreviewCache(Func<long?> availableBytes, long byteLimit = 32L << 20, int countLimit = 2)
{
    private sealed record Entry(WeakReference<WorkingFrame> Source, SourceStamp? Stamp, string Parameters,
        ColorPipelineVersion Version, PreparedFilmFrame Film);
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private long _generation, _preparations, _hits;
    internal long ResidentBytes { get { lock (_gate) return _entries.Sum(e => e.Film.ResidentBytes); } }
    internal long Preparations => Interlocked.Read(ref _preparations);
    internal long Hits => Interlocked.Read(ref _hits);

    internal RenderedFrame Render(WorkingFrame source, FrameParams parameters, ColorPipelineVersion version,
        IColorManagementEngine cmm, CancellationToken ct, SourceStamp? stamp = null)
    {
        ct.ThrowIfCancellationRequested();
        if (version is not (ColorPipelineVersion.LegacyV1 or ColorPipelineVersion.ManagedV2))
            throw new ArgumentOutOfRangeException(nameof(version));
        stamp ??= DecodedSources.For(source);
        stamp?.RequireCurrent();
        CalibrationSources.RequireCurrent(parameters);
        string key = PreviewParametersKey.CreateFilm(parameters);
        PreparedFilmFrame? film = null;
        long generation;
        bool mayRetain;
        lock (_gate)
        {
            mayRetain = HasMemory() && countLimit > 0 && byteLimit > 0;
            if (mayRetain)
            {
                _entries.RemoveAll(e => !e.Source.TryGetTarget(out _));
                int index = _entries.FindIndex(e => e.Source.TryGetTarget(out var s) && ReferenceEquals(s, source)
                    && e.Stamp == stamp && e.Parameters == key && e.Version == version);
                if (index >= 0)
                {
                    Entry entry = _entries[index];
                    _entries.RemoveAt(index);
                    _entries.Add(entry);
                    film = entry.Film;
                }
            }
            generation = _generation;
        }
        ct.ThrowIfCancellationRequested();
        if (!mayRetain)
        {
            // Under pressure use the normal one-shot path: do not allocate a preparation plus
            // an extra finish copy that will never be reused.
            RenderTrace.Count("preview.film.bypass");
            Interlocked.Increment(ref _preparations);
            var fresh = Pipeline.Render(source, parameters, version, cmm);
            ct.ThrowIfCancellationRequested();
            stamp?.RequireCurrent();
            CalibrationSources.RequireCurrent(parameters);
            return fresh;
        }
        if (film is null)
        {
            RenderTrace.Count("preview.film.miss");
            film = Pipeline.PrepareFilm(source, parameters);
            Interlocked.Increment(ref _preparations);
            ct.ThrowIfCancellationRequested();
            stamp?.RequireCurrent();
            CalibrationSources.RequireCurrent(parameters);
            lock (_gate)
            {
                if (HasMemory() && generation == _generation && countLimit > 0 && film.ResidentBytes <= byteLimit)
                {
                    _entries.RemoveAll(e => !e.Source.TryGetTarget(out var s) || ReferenceEquals(s, source));
                    while (_entries.Count > 0 && (_entries.Count >= countLimit
                        || _entries.Sum(e => e.Film.ResidentBytes) + film.ResidentBytes > byteLimit)) _entries.RemoveAt(0);
                    _entries.Add(new(new(source), stamp, key, version, film));
                }
            }
        }
        else
        {
            RenderTrace.Count("preview.film.hit");
            Interlocked.Increment(ref _hits);
        }
        ct.ThrowIfCancellationRequested();
        var rendered = Pipeline.RenderPreparedFilm(film, parameters, version, cmm);
        ct.ThrowIfCancellationRequested();
        stamp?.RequireCurrent();
        CalibrationSources.RequireCurrent(parameters);
        return rendered;
    }

    internal void Clear() { lock (_gate) { _generation++; _entries.Clear(); } }

    private bool HasMemory()
    {
        if (availableBytes() is not (>= 0 and < (1L << 30))) return true;
        _generation++;
        _entries.Clear();
        return false;
    }
}
