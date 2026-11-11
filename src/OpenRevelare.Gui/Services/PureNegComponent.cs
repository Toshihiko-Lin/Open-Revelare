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
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private static string Root => Path.Combine(Path.GetFullPath(Settings.DataDir), "components",
        $"pureneg-{Revision[..12]}-{NodeVersion}-{RuntimeInformation.RuntimeIdentifier}-2");
    private static string NodeName => OperatingSystem.IsWindows() ? "node.exe" : "node";
    public static bool IsInstalled => File.Exists(Path.Combine(Root, "installed"))
        && File.Exists(Path.Combine(Root, NodeName)) && File.Exists(Path.Combine(Root, "src", "cli.cjs"))
        && PureNegRuntimePackage.IsReady(Root);

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

    public static bool IsSupported => Runtime is { Platform: "win-x64" or "darwin-x64" or "darwin-arm64" or "linux-x64" };

    private static string? ResolveBundledNative()
    {
        if (Runtime is not { Platform: var platform }) return null;
        string directory = Path.Combine(AppContext.BaseDirectory, "optional", "pureneg");
        return ValidateNativeBundle(directory, platform);
    }

    internal static string? ValidateNativeBundle(string directory, string platform)
    {
        string addon = Path.Combine(directory, "filmdust.node");
        string manifest = Path.Combine(directory, "manifest.json");
        if (!File.Exists(addon) || !File.Exists(manifest)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<NativeManifest>(File.ReadAllText(manifest), Json);
            if (metadata is null || metadata.Platform != platform || metadata.Revision != Revision) return null;
            using var file = File.OpenRead(addon);
            return Convert.ToHexString(SHA256.HashData(file)).Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase)
                ? addon : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static async Task InstallAsync(IProgress<int> progress, CancellationToken ct, bool preferMirror = false)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try { await InstallCoreAsync(progress, ct, preferMirror).ConfigureAwait(false); }
        finally { InstallGate.Release(); }
    }

    private static async Task InstallCoreAsync(IProgress<int> progress, CancellationToken ct, bool preferMirror)
    {
        if (IsInstalled) return;
        if (!IsSupported) throw new PlatformNotSupportedException();
        var runtime = Runtime!.Value;
        string stage = Root + ".install-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            string prefix = $"node-{NodeVersion}-{runtime.Platform}";
            string name = prefix + (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");
            string archive = Path.Combine(stage, name);
            await PureNegDownload.DownloadAsync(Client, name, archive, runtime.Hash, 80 * 1024 * 1024,
                preferMirror, ct, new InstallProgress(progress)).ConfigureAwait(false);
            await ExtractRuntimeAsync(archive, prefix, stage, ct).ConfigureAwait(false);
            File.Delete(archive);
            progress.Report(40);

            using var manifest = typeof(PureNegComponent).Assembly.GetManifestResourceStream("PureNegFiles")
                ?? throw new InvalidOperationException("PURE Neg download manifest is missing.");
            var files = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)!;
            archive = Path.Combine(stage, "engine-sources.zip");
            await PureNegDownload.DownloadAsync(Client, "engine-sources.zip", archive,
                "d98b36439a111089b12d6ac311b9d730df1469ced800d996e3582485fa0d57da", 32 * 1024 * 1024,
                preferMirror, ct).ConfigureAwait(false);
            await ExtractSourcesAsync(archive, stage, files, ct).ConfigureAwait(false);
            File.Delete(archive);
            progress.Report(65);
            await PureNegRuntimePackage.InstallAsync(Client, stage, progress, ct, preferMirror).ConfigureAwait(false);
            // A component is ready only after the model actually performs inference.
            await RunAsync(stage, stage, "result", null, ct, "--eval", RuntimeProbe, "--", stage).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(stage, "runtime-ready"), PureNegRuntimePackage.Release, ct).ConfigureAwait(false);
            if (!PureNegRuntimePackage.IsReady(stage)) throw new InvalidDataException("PURENeg platform installation is incomplete.");
            await RunAsync(stage, stage, "result", null, ct, Path.Combine(stage, "src", "cli.cjs"), "help")
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(stage, "installed"), Revision, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            PureNegModelSession.Shutdown();
            string? previous = null;
            if (Directory.Exists(Root))
            {
                previous = Root + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(Root, previous);
            }
            try { Directory.Move(stage, Root); }
            catch
            {
                if (previous is not null && !Directory.Exists(Root)) Directory.Move(previous, Root);
                throw;
            }
            if (previous is not null) DeleteTemporaryDirectory(previous);
            progress.Report(100);
        }
        finally { DeleteTemporaryDirectory(stage); }
    }

    private sealed class InstallProgress(IProgress<int> target) : IProgress<int>
    {
        public void Report(int value) => target.Report(value * 40 / 100);
    }

    private const string RuntimeProbe = """
        const fs = require('node:fs'), path = require('node:path'), cp = require('node:child_process');
        const root = process.argv[1], runtime = path.join(root, 'runtime');
        (async () => {
        const native = require(path.join(root, 'vendor/film-dust/engine/native'));
        await native.ready();
        if (!native.available()) throw new Error('PURENeg native accelerator could not load on this system');
        if (process.platform === 'darwin') {
          const { encodeTiff } = require(path.join(root, 'src/tiff.cjs'));
          const input = path.join(root, 'model-probe.tiff'), output = path.join(root, 'model-probe.u8');
          try {
            fs.writeFileSync(input, encodeTiff({width: 1, height: 1, channels: 3, depth: 16, data: new Uint16Array([32768,32768,32768])}));
            cp.execFileSync(path.join(runtime, 'image-codec'), ['fibremap', input, output]);
            if (fs.statSync(output).size !== 1) throw new Error('CoreML inference probe failed');
          } finally { fs.rmSync(input, {force:true}); fs.rmSync(output, {force:true}); }
        } else {
          cp.execFileSync(path.join(runtime, process.platform === 'win32' ? 'pureneg-model-host.exe' : 'pureneg-model-host'), ['--probe']);
        }
        process.stdout.write(JSON.stringify({type:'result',modelReady:true})+'\n');
        })().catch(e => { process.stderr.write(e.message+'\n'); process.exitCode = 1; });
        """;

    internal static async Task ExtractSourcesAsync(string archive, string destination,
        IReadOnlyDictionary<string, string> manifest, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        var remaining = manifest.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!remaining.Remove(entry.FullName) || entry.Length > 8 * 1024 * 1024 ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Invalid PURENeg engine source archive.");
            string target = Path.Combine(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await using var file = File.OpenRead(target);
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(manifest[entry.FullName], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PURENeg engine source checksum mismatch.");
        }
        if (remaining.Count != 0) throw new InvalidDataException("PURENeg engine source archive is incomplete.");
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
        string fibreMap = "", runtimeInfo = "";
        if (!OperatingSystem.IsMacOS())
        {
            progress.Report(new(1, "正在生成纤维概率图", null));
            fibreMap = Path.Combine(workspace, "inference-map.u8");
            runtimeInfo = (await PureNegModelSession.InferAsync(Root, input, fibreMap, ct).ConfigureAwait(false)).GetRawText();
        }
        var result = await RunAsync(Root, workspace, "review", progress, ct, "--eval", adapter, "--",
            Root, input, runs, polarity, sensitivity, categories, fibreMap, runtimeInfo).ConfigureAwait(false);
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
        bool Native, int WorkingFactor = 1, JsonElement? TrainingMetadata = null,
        string? FibreMapPath = null, string? FibreMapSha256 = null);
    internal sealed record Region(int Id, int X, int Y, int Width, int Height, int Pixels, string[]? Kinds);
    internal readonly record struct CandidateRun(int RegionId, DustMask.Run Run);
}
