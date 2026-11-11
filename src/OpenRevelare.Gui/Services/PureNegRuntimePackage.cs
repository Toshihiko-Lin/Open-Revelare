using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace OpenRevelare.Gui.Services;

// One platform runtime is installed in the same transaction as Node and the engine.
// The index is generated from the release artifacts, never guessed from GPU names.
internal static class PureNegRuntimePackage
{
    internal const string Release = "pureneg-cbcc9fc825ba-v1";
    internal const string ReleaseRoot = "https://github.com/Toshihiko-Lin/openrevelare-models/releases/download/" + Release + "/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal sealed record Index(string Revision, Dictionary<string, Package> Platforms);
    internal sealed record Package(string File, string Sha256, long Size, string ModelFormat);

    internal static string SelectPlatform(string os, Architecture architecture) => (os, architecture) switch
    {
        ("windows", Architecture.X64) => "win-x64",
        ("windows", Architecture.Arm64) => "win-arm64",
        ("macos", Architecture.X64) => "osx-x64",
        ("macos", Architecture.Arm64) => "osx-arm64",
        ("linux", Architecture.X64) => "linux-x64",
        ("linux", Architecture.Arm64) => "linux-arm64",
        _ => throw new PlatformNotSupportedException("No PURENeg inference runtime for this platform."),
    };

    public static string CurrentPlatform => SelectPlatform(OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" : "linux", RuntimeInformation.ProcessArchitecture);

    public static bool IsReady(string component) =>
        File.Exists(Path.Combine(component, "runtime-ready")) &&
        File.Exists(Path.Combine(component, "runtime", "component.json")) &&
        File.Exists(Path.Combine(component, "runtime", OperatingSystem.IsMacOS() ? "image-codec" :
            OperatingSystem.IsWindows() ? "pureneg-model-host.exe" : "pureneg-model-host")) &&
        (OperatingSystem.IsMacOS() ? Directory.Exists(Path.Combine(component, "runtime", "FibreNet.mlmodelc")) :
            File.Exists(Path.Combine(component, "runtime", "FibreNet.onnx"))) &&
        File.Exists(Path.Combine(component, "vendor", "film-dust", "native", "build", "filmdust.node"));

    public static async Task InstallAsync(HttpClient client, string component, IProgress<int> progress, CancellationToken ct)
    {
        var index = JsonSerializer.Deserialize<Index>(await client.GetStringAsync(ReleaseRoot + "components.json", ct), Json)
            ?? throw new InvalidDataException("Missing PURENeg platform component index.");
        if (index.Revision != PureNegComponent.Revision || !index.Platforms.TryGetValue(CurrentPlatform, out var package))
            throw new InvalidDataException("PURENeg model package is unavailable for the running platform.");
        if (Path.GetFileName(package.File) != package.File || !package.File.EndsWith(".zip", StringComparison.Ordinal) ||
            package.Size <= 0 || package.Size > 512L * 1024 * 1024 ||
            package.Sha256.Length != 64 || package.ModelFormat != (OperatingSystem.IsMacOS() ? "coreml" : "onnx"))
            throw new InvalidDataException("Invalid PURENeg platform component index.");
        string archive = Path.Combine(component, "runtime.zip");
        try
        {
            using (var response = await client.GetAsync(ReleaseRoot + Uri.EscapeDataString(package.File), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long size = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += count;
                    if (size > package.Size) throw new InvalidDataException("PURENeg runtime exceeds its declared size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress.Report(65 + (int)(30 * size / package.Size));
                }
                if (size != package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PURENeg model runtime checksum mismatch.");
            }
            await ExtractAsync(archive, component, ct);
            string host = Path.Combine(component, "runtime", OperatingSystem.IsMacOS() ? "image-codec" :
                OperatingSystem.IsWindows() ? "pureneg-model-host.exe" : "pureneg-model-host");
            if (!File.Exists(host) || (!OperatingSystem.IsMacOS() && !File.Exists(Path.Combine(component, "runtime", "FibreNet.onnx"))) ||
                (OperatingSystem.IsMacOS() && !Directory.Exists(Path.Combine(component, "runtime", "FibreNet.mlmodelc"))))
                throw new InvalidDataException("PURENeg runtime is missing its model or inference executable.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        finally { File.Delete(archive); }
    }

    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('\\', '/');
            // Runtime packages may only add runtime assets and the standard Node addon.
            if ((!relative.StartsWith("runtime/", StringComparison.Ordinal) &&
                relative != "vendor/film-dust/native/build/filmdust.node") ||
                relative.Split('/').Any(p => p is "." or "..") ||
                (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 || entry.Length > 512L * 1024 * 1024 ||
                (total += entry.Length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Invalid PURENeg runtime archive entry.");
            string target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("PURENeg runtime archive escaped its installation.");
            if (relative.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct);
        }
    }
}
