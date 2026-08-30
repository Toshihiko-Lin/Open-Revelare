using System.Security.Cryptography;
using System.Text;

namespace OpenRevelare.Core;

internal static class FrameSourceIds
{
    /// <summary>
    /// Diagnostic identity that is stable for a local source without exposing its absolute path.
    /// M2 replaces this path identity with the project/content-addressed source identity used by
    /// render fingerprints.
    /// </summary>
    public static string ForPath(string path)
    {
        string canonical = Path.GetFullPath(path).Replace('\\', '/');
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"path-sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
