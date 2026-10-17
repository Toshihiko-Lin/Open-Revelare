using OpenRevelare.Core;
using OpenRevelare.Gui.Controls;
using OpenRevelare.Gui.Models;
using OpenRevelare.Presentation;

namespace OpenRevelare.Gui.Services;

internal sealed record PreparedPreviewKey(RollFrame Frame, SourceStamp Source, string Region,
    string Parameters, ColorPipelineVersion Pipeline, SourceStamp? Lut);

internal sealed record CachedPreviewData(RenderedFrame Rendered, PresentationScene Scene, HistogramData Histogram)
{
    internal long Bytes => (long)Rendered.Pixels.Data.Length * sizeof(float)
        + (long)Scene.LinearExtendedSrgbRgba.Length * sizeof(ushort)
        + (long)(Histogram.R.Length + Histogram.G.Length + Histogram.B.Length + Histogram.L.Length) * sizeof(float);
}

/// <summary>Only managed, display-independent data; native bitmaps remain owned by the published
/// preview. Two neighbours and a byte ceiling prevent prefetch from retaining a second roll.</summary>
internal sealed class PreparedPreviewCache(long byteLimit = 96L << 20, int countLimit = 2,
    Func<long?>? availableBytes = null)
{
    private readonly Func<long?> _availableBytes = availableBytes ?? (() =>
        SystemMemory.TryGetAvailableBytes(out long free) ? free : null);
    private readonly object _gate = new();
    private readonly List<(PreparedPreviewKey Key, CachedPreviewData Preview)> _entries = new();
    private long _generation;
    internal long Generation { get { lock (_gate) return _generation; } }
    internal long ResidentBytes { get { lock (_gate) return _entries.Sum(e => e.Preview.Bytes); } }

    internal CachedPreviewData? Get(PreparedPreviewKey key)
    {
        lock (_gate)
        {
            if (!HasMemory()) return null;
            int index = _entries.FindIndex(e => e.Key == key);
            if (index < 0) return null;
            var entry = _entries[index];
            _entries.RemoveAt(index);
            if (!key.Source.IsCurrent || key.Lut is { IsCurrent: false }) return null;
            _entries.Add(entry);
            return entry.Preview;
        }
    }

    internal bool Put(PreparedPreviewKey key, CachedPreviewData preview, long generation)
    {
        lock (_gate)
        {
            if (!HasMemory()) return false;
            if (_generation != generation || !key.Source.IsCurrent || key.Lut is { IsCurrent: false }
                || countLimit <= 0 || preview.Bytes > byteLimit) return false;
            _entries.RemoveAll(e => ReferenceEquals(e.Key.Frame, key.Frame));
            while (_entries.Count > 0 && (_entries.Count >= countLimit
                || _entries.Sum(e => e.Preview.Bytes) + preview.Bytes > byteLimit)) _entries.RemoveAt(0);
            _entries.Add((key, preview));
            return true;
        }
    }

    internal void Clear() { lock (_gate) { _generation++; _entries.Clear(); } }

    private bool HasMemory()
    {
        if (_availableBytes() is not (>= 0 and < (1L << 30))) return true;
        _entries.Clear();
        _generation++;
        return false;
    }
}
