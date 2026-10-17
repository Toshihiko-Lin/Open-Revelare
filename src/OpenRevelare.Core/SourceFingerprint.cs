using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenRevelare.Core;

/// <summary>Relocation evidence without reading multi-GB scans in full. Length plus the first
/// and last 64 KiB detects ordinary same-name mixups; it is not a full content checksum.</summary>
public static class SourceFingerprint
{
    private static readonly ConcurrentDictionary<string, (long Length, long Modified, long Created, string Hash)> Cache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string? TryCapture(string path)
    {
        try
        {
            var file = new FileInfo(path);
            long length = file.Length, modified = file.LastWriteTimeUtc.Ticks, created = file.CreationTimeUtc.Ticks;
            if (Cache.TryGetValue(file.FullName, out var hit) &&
                hit.Length == length && hit.Modified == modified && hit.Created == created) return hit.Hash;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = new byte[65536];
            int count = (int)Math.Min(length, bytes.Length);
            stream.ReadExactly(bytes.AsSpan(0, count));
            hash.AppendData(bytes, 0, count);
            if (length > count)
            {
                stream.Position = Math.Max(count, length - bytes.Length);
                count = (int)(length - stream.Position);
                stream.ReadExactly(bytes.AsSpan(0, count));
                hash.AppendData(bytes, 0, count);
            }
            file.Refresh();
            if (file.Length != length || file.LastWriteTimeUtc.Ticks != modified || file.CreationTimeUtc.Ticks != created)
                return null;
            string result = $"sample-v1:{length}:{Convert.ToHexString(hash.GetHashAndReset())}";
            // Bounded metadata cache; a library can contain many thousands of source paths.
            if (Cache.Count >= 4096) Cache.Clear();
            Cache[file.FullName] = (length, modified, created, result);
            return result;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
