using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRevelare.ColorManagement;

/// <summary>
/// Verifies the application-owned LittleCMS artifact before any native entry point is resolved.
/// The manifest and library must be siblings in the application's output directory: neither the
/// operating-system search path nor an installed system copy is a supported fallback.
/// </summary>
internal static class LittleCmsBundle
{
    internal const string ManifestFileName = "lcms2.manifest.json";
    internal const string RequiredSourceSha256 =
        "bfc54f7bab59fbc921012014a8032e4cba4abd46db47d46b76416a8c0b2815c8";

    internal static VerifiedLittleCmsBundle Verify(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        string directory = Path.GetFullPath(applicationDirectory);
        string manifestPath = Path.Combine(directory, ManifestFileName);
        (string rid, string libraryFileName) = CurrentArtifact();
        string nativePath = Path.Combine(directory, libraryFileName);

        if (!File.Exists(manifestPath))
        {
            throw new ColorManagementException(
                $"The app-owned LittleCMS manifest '{ManifestFileName}' is missing from " +
                $"'{directory}'. OpenRevelare will not fall back to a system '{LittleCmsNative.LibraryName}' library.");
        }
        if (!File.Exists(nativePath))
        {
            throw new ColorManagementException(
                $"The app-owned LittleCMS artifact '{libraryFileName}' is missing beside " +
                $"'{ManifestFileName}'. OpenRevelare will not fall back to a system library.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            JsonElement root = document.RootElement;

            int schemaVersion = RequiredInt32(root, "schemaVersion");
            if (schemaVersion != 1)
                throw Failure($"unsupported manifest schema {schemaVersion}; expected 1");

            string component = RequiredString(root, "component");
            if (!component.Equals("Little CMS", StringComparison.Ordinal))
                throw Failure($"component is '{component}', not 'Little CMS'");

            string version = RequiredString(root, "version");
            if (!version.Equals(LittleCmsEngine.RequiredRelease, StringComparison.Ordinal))
            {
                throw Failure(
                    $"manifest version is '{version}'; OpenRevelare requires " +
                    $"'{LittleCmsEngine.RequiredRelease}'");
            }

            string manifestRid = RequiredString(root, "rid");
            if (!manifestRid.Equals(rid, StringComparison.Ordinal))
                throw Failure($"manifest RID is '{manifestRid}'; current artifact RID is '{rid}'");

            JsonElement source = RequiredObject(root, "source");
            string sourceSha256 = NormalizeSha256(RequiredString(source, "sha256"), "source.sha256");
            if (!sourceSha256.Equals(RequiredSourceSha256, StringComparison.Ordinal))
            {
                throw Failure(
                    $"source SHA-256 is {sourceSha256}; the pinned LittleCMS " +
                    $"{LittleCmsEngine.RequiredRelease} source SHA-256 is {RequiredSourceSha256}");
            }

            JsonElement build = RequiredObject(root, "build");
            string buildOptions = build.GetRawText();

            JsonElement artifacts = RequiredArray(root, "artifacts");
            string? manifestNativeSha256 = null;
            foreach (JsonElement artifact in artifacts.EnumerateArray())
            {
                if (artifact.ValueKind != JsonValueKind.Object)
                    throw Failure("artifacts contains a non-object entry");

                string file = RequiredString(artifact, "file");
                if (!file.Equals(libraryFileName, StringComparison.Ordinal)) continue;
                if (manifestNativeSha256 is not null)
                    throw Failure($"artifacts contains duplicate entries for '{libraryFileName}'");

                manifestNativeSha256 = NormalizeSha256(
                    RequiredString(artifact, "sha256"), $"artifacts[{libraryFileName}].sha256");
            }

            if (manifestNativeSha256 is null)
                throw Failure($"artifacts has no entry for '{libraryFileName}'");

            string actualNativeSha256;
            using (FileStream library = new(
                       nativePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 128 * 1024, FileOptions.SequentialScan))
            {
                actualNativeSha256 = Convert.ToHexString(SHA256.HashData(library)).ToLowerInvariant();
            }

            if (!actualNativeSha256.Equals(manifestNativeSha256, StringComparison.Ordinal))
            {
                throw Failure(
                    $"SHA-256 mismatch for '{libraryFileName}': " +
                    $"manifest={manifestNativeSha256}, actual={actualNativeSha256}");
            }

            return new VerifiedLittleCmsBundle(
                nativePath,
                actualNativeSha256,
                sourceSha256,
                buildOptions);
        }
        catch (ColorManagementException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or InvalidOperationException or FormatException)
        {
            throw new ColorManagementException(
                $"The app-owned LittleCMS manifest '{ManifestFileName}' is invalid: {ex.Message}", ex);
        }

        ColorManagementException Failure(string reason) => new(
            $"The app-owned LittleCMS manifest '{ManifestFileName}' is invalid: {reason}.");
    }

    private static (string Rid, string LibraryFileName) CurrentArtifact()
    {
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && architecture == Architecture.X64)
            return ("win-x64", "lcms2.dll");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && architecture == Architecture.X64)
            return ("linux-x64", "liblcms2.so");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && architecture == Architecture.Arm64)
            return ("osx-arm64", "liblcms2.dylib");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && architecture == Architecture.X64)
            return ("osx-x64", "liblcms2.dylib");

        throw new ColorManagementException(
            $"No pinned LittleCMS artifact is defined for {RuntimeInformation.OSDescription} / {architecture}.");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException($"'{name}' must be an object");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Array)
            throw new FormatException($"'{name}' must be an array");
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"'{name}' must be a non-empty string");
        return value.GetString()!;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new FormatException($"'{name}' must be a 32-bit integer");
        return result;
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value))
            throw new FormatException($"required property '{name}' is missing");
        return value;
    }

    private static string NormalizeSha256(string value, string field)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new FormatException($"'{field}' must contain exactly 64 hexadecimal characters");
        return value.ToLowerInvariant();
    }
}

internal sealed record VerifiedLittleCmsBundle(
    string NativePath,
    string NativeSha256,
    string SourceSha256,
    string BuildOptions);
