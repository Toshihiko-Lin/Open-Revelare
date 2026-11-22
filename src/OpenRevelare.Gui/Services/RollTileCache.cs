using System.Runtime.InteropServices;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>Exact linear thumbnail inputs with a bounded hot set and a temporary file per roll.</summary>
internal sealed class RollTileCache(long residentLimit, Func<long?> availableBytes) : IDisposable
{
    private sealed record StoredTile(long Offset, int Width, int Height, double SourceStep,
        WorkingSpaceId Space, WorkingAdmission Admission, SourceDescriptor Source, SourceStamp Stamp);

    private readonly object _gate = new();
    private readonly PreviewCache _resident = new(residentLimit, availableBytes, probeIntervalMs: 2_000);
    private readonly Dictionary<string, StoredTile> _index = new(StringComparer.Ordinal);
    private FileStream? _file;
    private long _fileLength;
    private long _generation;
    private bool _disposed;

    internal long Generation { get { lock (_gate) return _generation; } }
    internal long ResidentBytes => _resident.ResidentBytes;

    internal bool Contains(string key)
    {
        lock (_gate) return _index.ContainsKey(key) || _resident.Get(key, promote: false) is not null;
    }

    internal void Protect(IEnumerable<string> keys) => _resident.Protect(keys);
    internal void TrimResident() => _resident.TrimToProtected();

    internal void Put(string key, WorkingFrame tile, SourceStamp stamp, long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || !stamp.IsCurrent || Contains(key)) return;
            ImageBuffer pixels = tile.Pixels;
            try
            {
                _file ??= OpenTemporaryFile();
                var bytes = MemoryMarshal.AsBytes(pixels.Data.AsSpan());
                RandomAccess.Write(_file.SafeFileHandle, bytes, _fileLength);
                _index.Add(key, new StoredTile(_fileLength, pixels.Width, pixels.Height,
                    pixels.SourceQuantisationStep, tile.Space, tile.Admission, tile.Source, stamp));
                _fileLength += bytes.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp storage is optional. Keep only the normal in-memory working set.
            }
            _resident.PutIfCurrent(key, tile, pixels.Width, pixels.Height, _resident.Generation, stamp);
        }
    }

    internal WorkingFrame? Get(string key, long generation, bool promote = true)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return null;
            if (_resident.Get(key, promote) is { } hit) return hit.Working;
            if (_file is null || !_index.TryGetValue(key, out StoredTile? tile) || !tile.Stamp.IsCurrent)
                return null;

            var pixels = new ImageBuffer(tile.Width, tile.Height) { SourceQuantisationStep = tile.SourceStep };
            var bytes = MemoryMarshal.AsBytes(pixels.Data.AsSpan());
            try
            {
                for (int offset = 0; offset < bytes.Length;)
                {
                    int read = RandomAccess.Read(_file.SafeFileHandle, bytes[offset..], tile.Offset + offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
            }
            catch (IOException)
            {
                _index.Remove(key);
                return null;
            }
            if (!tile.Stamp.IsCurrent) return null;
            var restored = DecodedSources.Track(new WorkingFrame(pixels, tile.Space, tile.Admission, tile.Source), tile.Stamp);
            if (promote)
                _resident.PutIfCurrent(key, restored, tile.Width, tile.Height, _resident.Generation, tile.Stamp);
            return restored;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _resident.Clear();
            _index.Clear();
            _file?.Dispose();
            _file = null;
            _fileLength = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
        }
    }

    private static FileStream OpenTemporaryFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-tiles-{Guid.NewGuid():N}.tmp");
        // No managed buffering: the index addresses individual tiles by byte offset.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        if (OperatingSystem.IsWindows()) return stream;
        try
        {
            // Unix keeps the open file usable after unlink, including if the app is killed.
            File.Delete(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
