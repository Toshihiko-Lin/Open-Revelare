using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenRevelare.Presentation.Win32.Interop;
using OpenRevelare.Presentation.Win32.Native;
using Xunit;

namespace OpenRevelare.Presentation.Win32.Tests;

public sealed class Win32NativeAbiTests
{
    [Fact]
    public void Managed_struct_sizes_match_x64_native_abi()
    {
        Assert.Equal(24, Marshal.SizeOf<NativePresentationContract>());
        Assert.Equal(384, Marshal.SizeOf<NativeDiagnostics>());
    }

    [Fact]
    public void DisplayConfig_struct_sizes_match_windows_sdk_layout()
    {
        Assert.Equal(8, Marshal.SizeOf<Win32Luid>());
        Assert.Equal(104, Marshal.SizeOf<MonitorInfoEx>());
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigDeviceInfoHeader>());
        Assert.Equal(20, Marshal.SizeOf<DisplayConfigPathSourceInfo>());
        Assert.Equal(48, Marshal.SizeOf<DisplayConfigPathTargetInfo>());
        Assert.Equal(72, Marshal.SizeOf<DisplayConfigPathInfo>());
        Assert.Equal(64, Marshal.SizeOf<DisplayConfigModeInfo>());
        Assert.Equal(84, Marshal.SizeOf<DisplayConfigSourceDeviceName>());
        Assert.Equal(420, Marshal.SizeOf<DisplayConfigTargetDeviceName>());
        Assert.Equal(36, Marshal.SizeOf<DisplayConfigAdvancedColorInfo2>());
        Assert.Equal(32, Marshal.SizeOf<DisplayConfigAdvancedColorInfo>());
        Assert.Equal(24, Marshal.SizeOf<DisplayConfigSdrWhiteLevel>());
        // dxgi1_6.h DXGI_OUTPUT_DESC1 on x64: 64 (name) + 16 (rect) + 4 + 4 + 8 (HMONITOR) + 4 + 4
        // + 32 (four chromaticity pairs) + 12 (three luminances) = 148, padded to 152.
        Assert.Equal(152, Marshal.SizeOf<DxgiOutputInterop.DxgiOutputDesc1>());
    }

    [Fact]
    public void Resolver_candidates_are_absolute_owned_paths_and_never_a_bare_search_name()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orwp-app"));
        string assembly = Path.Combine(root, "managed");

        IReadOnlyList<string> candidates = Win32NativeLibraryResolver.CandidatePaths(root, assembly);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, path =>
        {
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Equal(Win32NativeLibraryResolver.LibraryName, Path.GetFileName(path));
            Assert.NotEqual(Win32NativeLibraryResolver.LibraryName, path);
        });
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Resolver_accepts_exact_sibling_manifest_hash_and_abi()
    {
        using var temporary = new TemporaryDirectory();
        string library = WriteLibrary(temporary.Path, [1, 2, 3, 4, 5]);
        WriteManifest(temporary.Path, library);

        Win32NativeLibraryResolver.ValidateCandidate(library);
    }

    [Fact]
    public void Resolver_rejects_library_without_sibling_manifest()
    {
        using var temporary = new TemporaryDirectory();
        string library = WriteLibrary(temporary.Path, [1, 2, 3]);

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(
            () => Win32NativeLibraryResolver.ValidateCandidate(library));

        Assert.EndsWith(
            Win32NativeLibraryResolver.ManifestFileName,
            error.FileName!);
    }

    [Fact]
    public void Resolver_rejects_manifest_artifact_hash_mismatch()
    {
        using var temporary = new TemporaryDirectory();
        string library = WriteLibrary(temporary.Path, [1, 2, 3]);
        WriteManifest(temporary.Path, library, artifactSha256: new string('0', 64));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Win32NativeLibraryResolver.ValidateCandidate(library));

        Assert.Contains("SHA-256 mismatch", error.Message);
    }

    [Fact]
    public void Resolver_rejects_manifest_abi_mismatch()
    {
        using var temporary = new TemporaryDirectory();
        string library = WriteLibrary(temporary.Path, [1, 2, 3]);
        WriteManifest(temporary.Path, library, abiVersion: 2);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Win32NativeLibraryResolver.ValidateCandidate(library));

        Assert.Contains("ABI", error.Message);
    }

    [Fact]
    public void Resolver_rejects_source_tree_identity_not_derived_from_file_list()
    {
        using var temporary = new TemporaryDirectory();
        string library = WriteLibrary(temporary.Path, [1, 2, 3]);
        WriteManifest(temporary.Path, library, sourceTreeSha256: new string('F', 64));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Win32NativeLibraryResolver.ValidateCandidate(library));

        Assert.Contains("source-tree SHA-256", error.Message);
    }

    private static string WriteLibrary(string directory, byte[] bytes)
    {
        string path = Path.Combine(directory, Win32NativeLibraryResolver.LibraryName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteManifest(
        string directory,
        string library,
        int abiVersion = Win32NativeLibraryResolver.NativeAbiVersion,
        string? artifactSha256 = null,
        string? sourceTreeSha256 = null)
    {
        const string sourceHash =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var sourceFiles = new[]
        {
            new { path = "packaging/windows/build-win32-presenter.ps1", sha256 = sourceHash },
            new { path = "packaging/windows/smoke-win32-presenter.ps1", sha256 = sourceHash },
            new { path = "packaging/windows/verify-win32-presenter.ps1", sha256 = sourceHash },
            new { path = "src/OpenRevelare.Presentation.Win32.Native/OpenRevelare.Presentation.Win32.Native.vcxproj", sha256 = sourceHash },
            new { path = "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.cpp", sha256 = sourceHash },
            new { path = "src/OpenRevelare.Presentation.Win32.Native/OpenRevelarePresentationWin32.h", sha256 = sourceHash },
        };
        string identityText = string.Concat(
            sourceFiles.Select(file => $"{file.path}\n{file.sha256}\n"));
        string computedSourceIdentity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityText)));
        string libraryHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library)));
        long librarySize = new FileInfo(library).Length;
        var manifest = new
        {
            schemaVersion = Win32NativeLibraryResolver.ManifestSchemaVersion,
            component = "OpenRevelare.Presentation.Win32.Native",
            rid = "win-x64",
            abi = new
            {
                version = abiVersion,
                architecture = "x64",
                callingConvention = "cdecl",
                exports = new[]
                {
                    "orwp_create",
                    "orwp_resize",
                    "orwp_present",
                    "orwp_query_diagnostics",
                    "orwp_destroy",
                },
            },
            source = new
            {
                identityAlgorithm = "sha256(path-lf-sha256-lf); paths sorted ordinal",
                treeSha256 = sourceTreeSha256 ?? computedSourceIdentity,
                files = sourceFiles,
            },
            build = new
            {
                reproducible = true,
                verificationBuilds = 2,
                sourceSnapshotVerified = true,
            },
            artifacts = new[]
            {
                new
                {
                    file = Win32NativeLibraryResolver.LibraryName,
                    size = librarySize,
                    sha256 = artifactSha256 ?? libraryHash,
                },
            },
        };
        string path = Path.Combine(directory, Win32NativeLibraryResolver.ManifestFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest), new UTF8Encoding(false));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"orwp-resolver-{Guid.NewGuid():N}");

        internal TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
