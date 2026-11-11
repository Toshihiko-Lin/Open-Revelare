using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

internal static class PureNegComponent
{
    internal const string Revision = "cbcc9fc825ba9aaf12304c7e9524643995faf4dd";
    private const string NodeVersion = "v24.14.1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly Lazy<string?> BundledNative = new(ResolveBundledNative);

    private static string Root => Path.Combine(Path.GetFullPath(Settings.DataDir), "components",
        $"pureneg-{Revision[..12]}-{NodeVersion}-{RuntimeInformation.RuntimeIdentifier}-1");
    private static string NodeName => OperatingSystem.IsWindows() ? "node.exe" : "node";
    public static bool IsInstalled => File.Exists(Path.Combine(Root, "installed"))
        && File.Exists(Path.Combine(Root, NodeName)) && File.Exists(Path.Combine(Root, "src", "cli.cjs"));

    private static (string Platform, string Hash)? Runtime => (OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, _, _, Architecture.X64) => ("win-x64", "6e50ce5498c0cebc20fd39ab3ff5df836ed2f8a31aa093cecad8497cff126d70"),
        (true, _, _, Architecture.Arm64) => ("win-arm64", "a7b7c68490e4a8cde1921fe5a0cfb3001d53f9c839e416903e4f28e727b62f60"),
        (_, true, _, Architecture.X64) => ("darwin-x64", "2526230ad7d922be82d4fdb1e7ee1e84303e133e3b4b0ec4c2897ab31de0253d"),
        (_, true, _, Architecture.Arm64) => ("darwin-arm64", "25495ff85bd89e2d8a24d88566d7e2f827c6b0d3d872b2cebf75371f93fcb1fe"),
        (_, _, true, Architecture.X64) => ("linux-x64", "ace9fa104992ed0829642629c46ca7bd7fd6e76278cb96c958c4b387d29658ea"),
        (_, _, true, Architecture.Arm64) => ("linux-arm64", "734ff04fa7f8ed2e8a78d40cacf5ac3fc4515dac2858757cbab313eb483ba8a2"),
        _ => null,
    };

    public static bool IsSupported => Runtime.HasValue;

    private static string? ResolveBundledNative()
    {
        if (Runtime is not { Platform: "win-x64" }) return null;
        string directory = Path.Combine(AppContext.BaseDirectory, "optional", "pureneg");
        string addon = Path.Combine(directory, "filmdust.node");
        string manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(addon) || !File.Exists(manifest)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<NativeManifest>(File.ReadAllText(manifest), Json);
            if (metadata is not { Platform: "win-x64" } || metadata.Revision != Revision) return null;
            using var file = File.OpenRead(addon);
            return Convert.ToHexString(SHA256.HashData(file)).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase)
                ? addon : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task InstallAsync(IProgress<int> progress, CancellationToken ct)
    {
        if (IsInstalled) return;
        var runtime = Runtime ?? throw new PlatformNotSupportedException();
        string stage = Root + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            string prefix = $"node-{NodeVersion}-{runtime.Platform}";
            string name = prefix + (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");
            string archive = Path.Combine(stage, name);
            await DownloadAsync($"https://nodejs.org/dist/{NodeVersion}/{name}", archive,
                runtime.Hash, 80 * 1024 * 1024, ct, progress).ConfigureAwait(false);
            await ExtractRuntimeAsync(archive, prefix, stage, ct).ConfigureAwait(false);
            File.Delete(archive);
            progress.Report(50);

            using var manifest = typeof(PureNegComponent).Assembly.GetManifestResourceStream("PureNegFiles")
                ?? throw new InvalidOperationException("PURE Neg download manifest is missing.");
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
            int completed = 0;
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (entry, token) =>
                {
                    string destination = Path.Combine(stage, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await DownloadAsync($"https://raw.githubusercontent.com/Toshihiko-Lin/PURENeg/{Revision}/{entry.Key}",
                        destination, entry.Value, 8 * 1024 * 1024, token).ConfigureAwait(false);
                    progress.Report(50 + 50 * Interlocked.Increment(ref completed) / files.Count);
                }).ConfigureAwait(false);
            await RunAsync(stage, stage, "result", null, ct, Path.Combine(stage, "src", "cli.cjs"), "help")
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(stage, "installed"), Revision, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(stage, Root); }
            catch (IOException) when (IsInstalled) { }
        }
        finally { DeleteTemporaryDirectory(stage); }
    }

    private static async Task DownloadAsync(string url, string path, string sha256, int limit, CancellationToken ct,
        IProgress<int>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = response.Content.Headers.ContentLength ?? 0;
        int lastPercent = 0;
        int size = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            size += count;
            if (size > limit) throw new IOException("PURE Neg download exceeds its size limit.");
            hash.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            if (progress is not null && total > 0)
            {
                int percent = (int)Math.Min(49, 50L * size / total);
                if (percent > lastPercent) progress.Report(lastPercent = percent);
            }
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("PURE Neg download SHA-256 mismatch.");
    }

    private static async Task ExtractRuntimeAsync(string archive, string prefix, string stage, CancellationToken ct)
    {
        string node = Path.Combine(stage, NodeName);
        string license = Path.Combine(stage, "Node-LICENSE");
        if (OperatingSystem.IsWindows())
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var (name, target) in new[] { ($"{prefix}/node.exe", node), ($"{prefix}/LICENSE", license) })
            {
                var entry = zip.GetEntry(name) ?? throw new IOException("Node.js archive entry is missing.");
                await using var input = entry.Open();
                await using var output = File.Create(target);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
            {
                string? target = entry.Name == $"{prefix}/bin/node" ? node
                    : entry.Name == $"{prefix}/LICENSE" ? license : null;
                if (target is null || entry.DataStream is not { } data) continue;
                await using var output = File.Create(target);
                await data.CopyToAsync(output, ct).ConfigureAwait(false);
                if (File.Exists(node) && File.Exists(license)) break;
            }
            File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!File.Exists(license)) throw new IOException("Node.js license is missing.");
    }

    public static async Task<ReviewImage> PrepareAsync(string workspace, string input,
        string polarity, string sensitivity, string categories, IProgress<DetectionProgress> progress, CancellationToken ct)
    {
        using var script = new StreamReader(typeof(PureNegComponent).Assembly.GetManifestResourceStream("PureNegAnalyze")
            ?? throw new InvalidOperationException("PURE Neg analysis adapter is missing."));
        string adapter = await script.ReadToEndAsync(ct).ConfigureAwait(false);
        string runs = Path.Combine(workspace, "regions-runs.bin");
        var result = await RunAsync(Root, workspace, "review", progress, ct, "--eval", adapter, "--",
            Root, input, runs, polarity, sensitivity, categories).ConfigureAwait(false);
        var review = result.GetProperty("review").Deserialize<ReviewImage>(Json)!;
        return review with
        {
            SourcePath = input,
            RunsPath = runs,
        };
    }

    public static CandidateRun[] ReadRuns(ReviewImage image, CancellationToken ct)
    {
        using var reader = new BinaryReader(File.OpenRead(image.RunsPath));
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("FDRUNS01"u8))
            throw new InvalidDataException("Invalid PURE Neg mask index.");
        int count = reader.ReadInt32();
        int width = reader.ReadInt32(), height = reader.ReadInt32(), regions = reader.ReadInt32();
        if (count is < 0 or > 2_000_000 || reader.BaseStream.Length != 24 + 16L * count ||
            width != image.Width || height != image.Height || regions != image.Regions.Length)
            throw new InvalidDataException("Unexpected PURE Neg mask index dimensions.");
        var ids = image.Regions.Select(r => r.Id).ToHashSet();
        var runs = new CandidateRun[count];
        int previousY = -1, previousEnd = 0;
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            int id = reader.ReadInt32(), y = reader.ReadInt32(), start = reader.ReadInt32(), end = reader.ReadInt32();
            if (!ids.Contains(id) || y < 0 || y >= height || start < 0 || end > width || start >= end ||
                y < previousY || (y == previousY && start < previousEnd))
                throw new InvalidDataException("Invalid PURE Neg mask span.");
            runs[i] = new(id, new DustMask.Run(y, start, end));
            previousY = y;
            previousEnd = end;
        }
        return runs;
    }

    private static async Task<JsonElement> RunAsync(string component, string workspace, string expected,
        IProgress<DetectionProgress>? progress, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(component, NodeName))
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("PURENEG_", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("FILMDUST_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        if (BundledNative.Value is { } addon)
            start.Environment.TryAdd("FILMDUST_NATIVE_PATH", addon);
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("PURE Neg could not start.");
        using var cancel = ct.Register(() => Stop(process));
        var stderr = process.StandardError.ReadToEndAsync();
        JsonElement result = default;
        string? error = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var data = json.RootElement;
                string? type = data.GetProperty("type").GetString();
                if (type == expected) result = data.Clone();
                if (type == "progress" && data.TryGetProperty("fraction", out var fraction))
                    progress?.Report(new((int)Math.Clamp(fraction.GetDouble() * 100, 0, 100),
                        data.TryGetProperty("message", out var stage) ? stage.GetString() : null,
                        data.TryGetProperty("native", out var engine) ? engine.GetBoolean() : null));
                if (type == "error" && data.TryGetProperty("message", out var message)) error = message.GetString();
            }
        }
        catch { Stop(process); throw; }
        finally { await process.WaitForExitAsync().ConfigureAwait(false); }
        string diagnostic = await stderr.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || result.ValueKind == JsonValueKind.Undefined)
            throw new IOException(error ?? (diagnostic.Length > 0 ? diagnostic.Trim() : "PURE Neg returned no result."));
        return result;
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static void DeleteTemporaryDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record NativeManifest(string Revision, string Platform, string Sha256);
    internal sealed record DetectionProgress(int Percent, string? Message, bool? Native);
    internal sealed record ReviewImage(string SourcePath, string RunsPath, int Width, int Height, Region[] Regions,
        bool Native, int WorkingFactor = 1);
    internal sealed record Region(int Id, int X, int Y, int Width, int Height, int Pixels, string[]? Kinds);
    internal readonly record struct CandidateRun(int RegionId, DustMask.Run Run);
}
