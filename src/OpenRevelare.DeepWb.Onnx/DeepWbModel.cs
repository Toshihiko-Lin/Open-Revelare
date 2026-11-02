using System.Net.Http;
using System.Security.Cryptography;

namespace OpenRevelare.DeepWb.Onnx;

/// <summary>Optional, per-user model installation. No network access occurs during inference.</summary>
public static class DeepWbModel
{
    public const string DownloadUrl = "https://github.com/Toshihiko-Lin/openrevelare-models/releases/download/deep-wb-v1/net_awb.onnx";
    public const string MirrorDownloadUrl = "https://gitee.com/Toshihiko-Lin/openrevelare-models/releases/download/deep-wb-v1/net_awb.onnx";
    public const long Size = 17480341;
    public const string Sha256 = "50503B7F6CFB0663EBA0D0D61CBC68E8E93E56581DB3143B203BAB163F7C9F54";

    public static string InstalledPath
    {
        get
        {
            string? dataDir = Environment.GetEnvironmentVariable("OPENREVELARE_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dataDir))
                dataDir = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRevelare")
                    : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "OpenRevelare");
            return Path.Combine(dataDir, "models", "net_awb.onnx");
        }
    }

    // Retain support for portable/offline installations beside the executable.
    public static string DefaultPath => File.Exists(InstalledPath) ? InstalledPath
        : File.Exists(BundledPath) ? BundledPath : InstalledPath;
    private static string BundledPath => Path.Combine(AppContext.BaseDirectory, "models", "net_awb.onnx");
    public static bool IsInstalled => File.Exists(DefaultPath);

    public static async Task InstallAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default, bool preferMirror = false)
    {
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await InstallFromSourcesAsync(client, InstalledPath, progress, cancellationToken, preferMirror).ConfigureAwait(false);
    }

    internal static async Task InstallFromSourcesAsync(HttpClient client, string destination,
        IProgress<int>? progress, CancellationToken cancellationToken, bool preferMirror = false)
    {
        var failures = new List<Exception>();
        foreach (string source in preferMirror ? new[] { MirrorDownloadUrl, DownloadUrl } : new[] { DownloadUrl, MirrorDownloadUrl })
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                progress?.Report(0);
                await InstallAsync(client, destination, progress, attempt.Token, source).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                failures.Add(ex);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new IOException("Deep-WB download failed from GitHub and Gitee. Please retry or install the model offline.",
            new AggregateException(failures));
    }

    internal static async Task InstallAsync(HttpClient client, string destination,
        IProgress<int>? progress, CancellationToken cancellationToken, string downloadUrl = DownloadUrl)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headers.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    total += count;
                    if (total > Size) throw new InvalidDataException("Deep-WB model exceeds the expected size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    progress?.Report((int)(total * 100 / Size));
                }
            }
            if (total != Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Deep-WB model integrity check failed. Please retry the download.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
