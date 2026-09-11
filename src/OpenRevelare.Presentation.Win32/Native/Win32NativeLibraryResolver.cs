using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRevelare.Presentation.Win32.Native;

internal static class Win32NativeLibraryResolver
{
    internal const string LibraryName = "OpenRevelare.Presentation.Win32.Native.dll";
    internal const string ManifestFileName = "OpenRevelare.Presentation.Win32.Native.manifest.json";
    internal const int ManifestSchemaVersion = 1;
    internal const int NativeAbiVersion = 1;

    private const string ComponentName = "OpenRevelare.Presentation.Win32.Native";
    private const string SourceIdentityAlgorithm =
        "sha256(path-lf-sha256-lf); paths sorted ordinal";
    private const int MaximumManifestBytes = 256 * 1024;
    private static readonly string[] RequiredExports =
    [
        "orwp_create",
        "orwp_resize",
        "orwp_present",
        "orwp_query_diagnostics",
        "orwp_destroy",
    ];
    private static readonly string[] RequiredSourceFiles =
    [
        "packaging/windows/build-win32-presenter.ps1",
        "packaging/windows/smoke-win32-presenter.ps1",
        "packaging/windows/verify-win32-presenter.ps1",
        "src/OpenRevelare.Presentation.Win32.Native/OpenRevelare.Presentation.Win32.Native.vcxproj",
        "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.cpp",
        "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.h",
    ];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static int _installed;

    internal static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(Win32NativeLibraryResolver).Assembly, Resolve);
    }

    internal static IReadOnlyList<string> CandidatePaths(string appBaseDirectory, string assemblyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyDirectory);

        var paths = new List<string>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Add(appBaseDirectory, LibraryName);
        Add(appBaseDirectory, "runtimes", "win-x64", "native", LibraryName);
        Add(assemblyDirectory, LibraryName);
        Add(assemblyDirectory, "runtimes", "win-x64", "native", LibraryName);
        return paths;

        void Add(params string[] components)
        {
            string candidate = Path.GetFullPath(Path.Combine(components));
            if (!Path.IsPathRooted(candidate) ||
                !string.Equals(Path.GetFileName(candidate), LibraryName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Native presenter candidate escaped the strict filename policy.");
            }
            if (seen.Add(candidate)) paths.Add(candidate);
        }
    }

    internal static void ValidateCandidate(string libraryPath)
    {
        using FileStream verifiedLibrary = OpenVerifiedCandidate(libraryPath);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        _ = searchPath;
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return nint.Zero;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The OpenRevelare native presenter is Windows-only.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The OpenRevelare native presenter requires an x64 process.");

        string? assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(assemblyDirectory)) assemblyDirectory = AppContext.BaseDirectory;
        IReadOnlyList<string> candidates = CandidatePaths(AppContext.BaseDirectory, assemblyDirectory);
        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;

            // Keep the exact verified file open without write/delete sharing until the loader has
            // acquired it. This closes the ordinary hash-then-replace window on Windows.
            using FileStream verifiedLibrary = OpenVerifiedCandidate(candidate);
            if (NativeLibrary.TryLoad(candidate, out nint handle)) return handle;
            throw new DllNotFoundException(
                $"The verified native presenter could not be loaded: {candidate}");
        }

        throw new DllNotFoundException(
            $"Could not load {LibraryName} from an application-owned x64 location. Checked: " +
            string.Join("; ", candidates));
    }

    private static FileStream OpenVerifiedCandidate(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        string fullLibraryPath = Path.GetFullPath(libraryPath);
        if (!Path.IsPathFullyQualified(fullLibraryPath) ||
            !string.Equals(Path.GetFileName(fullLibraryPath), LibraryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Native presenter verification requires the exact filename {LibraryName}.");
        }
        if (!File.Exists(fullLibraryPath))
            throw new FileNotFoundException("Native presenter DLL is missing.", fullLibraryPath);

        string directory = Path.GetDirectoryName(fullLibraryPath) ??
            throw new InvalidDataException("Native presenter DLL has no parent directory.");
        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Native presenter manifest must be a sibling named {ManifestFileName}.",
                manifestPath);
        }

        NativeLibraryManifest manifest = ReadManifest(manifestPath);
        NativeArtifactManifest artifact = ValidateManifest(manifest, manifestPath);

        FileStream? library = null;
        try
        {
            library = new FileStream(
                fullLibraryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                options: FileOptions.SequentialScan);
            if (library.Length != artifact.Size)
            {
                throw new InvalidDataException(
                    $"Native presenter size mismatch: manifest={artifact.Size}, actual={library.Length}.");
            }

            byte[] actualDigest = SHA256.HashData(library);
            byte[] expectedDigest = Convert.FromHexString(artifact.Sha256!);
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw new InvalidDataException(
                    $"Native presenter SHA-256 mismatch for {fullLibraryPath}: " +
                    $"expected {artifact.Sha256}, actual {Convert.ToHexString(actualDigest)}.");
            }
            return library;
        }
        catch
        {
            library?.Dispose();
            throw;
        }
    }

    private static NativeLibraryManifest ReadManifest(string manifestPath)
    {
        var info = new FileInfo(manifestPath);
        if (info.Length <= 0 || info.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"Native presenter manifest size {info.Length} is outside the accepted range.");
        }

        string json;
        using (var stream = new FileStream(
                   manifestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 16 * 1024,
                   options: FileOptions.SequentialScan))
        using (var reader = new StreamReader(
                   stream,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                   detectEncodingFromByteOrderMarks: true))
        {
            json = reader.ReadToEnd();
        }

        try
        {
            return JsonSerializer.Deserialize<NativeLibraryManifest>(json, ManifestJsonOptions) ??
                throw new InvalidDataException("Native presenter manifest deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Native presenter manifest is not valid schema JSON.", ex);
        }
    }

    private static NativeArtifactManifest ValidateManifest(
        NativeLibraryManifest manifest,
        string manifestPath)
    {
        if (manifest.SchemaVersion != ManifestSchemaVersion)
            throw InvalidManifest(manifestPath, "schemaVersion mismatch");
        if (!string.Equals(manifest.Component, ComponentName, StringComparison.Ordinal))
            throw InvalidManifest(manifestPath, "component mismatch");
        if (!string.Equals(manifest.Rid, "win-x64", StringComparison.Ordinal))
            throw InvalidManifest(manifestPath, "RID mismatch");

        NativeAbiManifest abi = manifest.Abi ??
            throw InvalidManifest(manifestPath, "ABI section is missing");
        if (abi.Version != NativeAbiVersion ||
            !string.Equals(abi.Architecture, "x64", StringComparison.Ordinal) ||
            !string.Equals(abi.CallingConvention, "cdecl", StringComparison.Ordinal) ||
            abi.Exports is null ||
            abi.Exports.Count != RequiredExports.Length ||
            abi.Exports.Where(
                (name, index) =>
                    !string.Equals(name, RequiredExports[index], StringComparison.Ordinal)).Any())
        {
            throw InvalidManifest(manifestPath, "ABI version/architecture/calling convention/exports mismatch");
        }

        NativeSourceManifest source = manifest.Source ??
            throw InvalidManifest(manifestPath, "source section is missing");
        if (!string.Equals(
                source.IdentityAlgorithm,
                SourceIdentityAlgorithm,
                StringComparison.Ordinal) ||
            !IsSha256(source.TreeSha256) ||
            source.Files is null || source.Files.Count != RequiredSourceFiles.Length)
        {
            throw InvalidManifest(manifestPath, "source-tree identity is incomplete");
        }
        var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
        var sourceIdentity = new StringBuilder();
        string? previousSourcePath = null;
        for (int sourceIndex = 0; sourceIndex < source.Files.Count; sourceIndex++)
        {
            NativeSourceFileManifest? file = source.Files[sourceIndex];
            if (file is null ||
                !IsCanonicalSourcePath(file.Path) ||
                !sourcePaths.Add(file.Path!) ||
                !IsSha256(file.Sha256) ||
                !string.Equals(
                    file.Path,
                    RequiredSourceFiles[sourceIndex],
                    StringComparison.Ordinal) ||
                (previousSourcePath is not null &&
                 StringComparer.Ordinal.Compare(previousSourcePath, file.Path) >= 0))
            {
                throw InvalidManifest(
                    manifestPath,
                    "source-tree file entry is invalid, duplicated, or not ordinal-sorted");
            }
            sourceIdentity.Append(file.Path).Append('\n')
                .Append(file.Sha256!.ToUpperInvariant()).Append('\n');
            previousSourcePath = file.Path;
        }
        string computedSourceIdentity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity.ToString())));
        if (!string.Equals(
                computedSourceIdentity,
                source.TreeSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidManifest(manifestPath, "source-tree SHA-256 does not match its file list");
        }

        if (manifest.Build.ValueKind != JsonValueKind.Object ||
            !manifest.Build.TryGetProperty("reproducible", out JsonElement reproducible) ||
            reproducible.ValueKind != JsonValueKind.True ||
            !manifest.Build.TryGetProperty("verificationBuilds", out JsonElement verificationBuilds) ||
            !verificationBuilds.TryGetInt32(out int builds) || builds < 2 ||
            !manifest.Build.TryGetProperty(
                "sourceSnapshotVerified",
                out JsonElement sourceSnapshotVerified) ||
            sourceSnapshotVerified.ValueKind != JsonValueKind.True)
        {
            throw InvalidManifest(manifestPath, "build does not attest stable-source reproducibility");
        }

        if (manifest.Artifacts is null || manifest.Artifacts.Count != 1)
            throw InvalidManifest(manifestPath, "exactly one artifact is required");
        NativeArtifactManifest? artifact = manifest.Artifacts[0];
        if (artifact is null ||
            !string.Equals(artifact.File, LibraryName, StringComparison.Ordinal) ||
            artifact.Size <= 0 ||
            !IsSha256(artifact.Sha256))
        {
            throw InvalidManifest(manifestPath, "artifact filename/size/SHA-256 is invalid");
        }
        return artifact;
    }

    private static bool IsCanonicalSourcePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith('/') || value.StartsWith('\\') ||
            value.Contains('\\') || value.Contains(':'))
        {
            return false;
        }
        return !value.Split('/').Any(part => part is "" or "." or "..");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static InvalidDataException InvalidManifest(string path, string detail) =>
        new($"Native presenter manifest validation failed ({detail}): {path}");

    private sealed class NativeLibraryManifest
    {
        public NativeLibraryManifest() { }

        public int SchemaVersion { get; init; }
        public string? Component { get; init; }
        public string? Rid { get; init; }
        public NativeAbiManifest? Abi { get; init; }
        public NativeSourceManifest? Source { get; init; }
        public JsonElement Build { get; init; }
        public List<NativeArtifactManifest>? Artifacts { get; init; }
    }

    private sealed class NativeAbiManifest
    {
        public NativeAbiManifest() { }

        public int Version { get; init; }
        public string? Architecture { get; init; }
        public string? CallingConvention { get; init; }
        public List<string?>? Exports { get; init; }
    }

    private sealed class NativeSourceManifest
    {
        public NativeSourceManifest() { }

        public string? IdentityAlgorithm { get; init; }
        public string? TreeSha256 { get; init; }
        public List<NativeSourceFileManifest?>? Files { get; init; }
    }

    private sealed class NativeSourceFileManifest
    {
        public NativeSourceFileManifest() { }

        public string? Path { get; init; }
        public string? Sha256 { get; init; }
    }

    private sealed class NativeArtifactManifest
    {
        public NativeArtifactManifest() { }

        public string? File { get; init; }
        public long Size { get; init; }
        public string? Sha256 { get; init; }
    }
}
