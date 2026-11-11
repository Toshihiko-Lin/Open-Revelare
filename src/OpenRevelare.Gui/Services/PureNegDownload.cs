using System.Security.Cryptography;

namespace OpenRevelare.Gui.Services;

// All installation assets use the same two release mirrors, including Node and engine sources.
internal static class PureNegDownload
{
    internal static IEnumerable<string> Sources(string file, bool preferMirror) =>
        (preferMirror ? new[] { PureNegRuntimePackage.MirrorRoot, PureNegRuntimePackage.ReleaseRoot }
            : new[] { PureNegRuntimePackage.ReleaseRoot, PureNegRuntimePackage.MirrorRoot })
        .Select(root => root + Uri.EscapeDataString(file));

    internal static async Task<T> FromSourcesAsync<T>(string file, bool preferMirror, CancellationToken ct,
        Func<string, CancellationToken, Task<T>> fetch)
    {
        var failures = new List<Exception>();
        foreach (string source in Sources(file, preferMirror))
        {
            ct.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromMinutes(3));
            try { return await fetch(source, attempt.Token).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
            { failures.Add(ex); }
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException("PURENeg download failed from GitHub and Gitee.", new AggregateException(failures));
    }

    internal static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(TimeSpan.FromSeconds(15));
        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
    }

    internal static async Task DownloadAsync(HttpClient client, string file, string destination, string? sha256,
        long limit, bool preferMirror, CancellationToken ct, IProgress<int>? progress = null, long? expectedSize = null)
    {
        await FromSourcesAsync(file, preferMirror, ct, async (url, token) =>
        {
            try
            {
                progress?.Report(0);
                using var response = await GetAsync(client, url, token).ConfigureAwait(false);
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0, length = expectedSize ?? response.Content.Headers.ContentLength ?? 0;
                await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        total += count;
                        if (total > limit) throw new InvalidDataException("PURENeg download exceeds its size limit.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        if (length > 0) progress?.Report((int)Math.Min(99, 100 * total / length));
                    }
                }
                if ((expectedSize.HasValue && total != expectedSize.Value) || (sha256 is not null &&
                    !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("PURENeg download integrity check failed.");
                token.ThrowIfCancellationRequested();
                progress?.Report(100);
                return true;
            }
            catch { File.Delete(destination); throw; }
        }).ConfigureAwait(false);
    }
}
