using System.Collections.Immutable;
using System.Security.Cryptography;

namespace OpenRevelare.ColorManagement;

/// <summary>The SHA-256 identity of the exact ICC payload.</summary>
public sealed record ProfileIdentity
{
    public const int Sha256HexLength = 64;

    public string Sha256Hex { get; }

    private ProfileIdentity(string sha256Hex) => Sha256Hex = sha256Hex;

    public static ProfileIdentity FromIcc(ReadOnlySpan<byte> iccBytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(iccBytes, digest);
        return new ProfileIdentity(Convert.ToHexString(digest).ToLowerInvariant());
    }

    public static ProfileIdentity Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != Sha256HexLength)
            throw new FormatException($"Profile SHA-256 must contain {Sha256HexLength} hexadecimal characters.");

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        for (int i = 0; i < digest.Length; i++)
        {
            int high = HexNibble(value[i * 2]);
            int low = HexNibble(value[i * 2 + 1]);
            if (high < 0 || low < 0)
                throw new FormatException("Profile SHA-256 contains non-hexadecimal characters.");
            digest[i] = (byte)((high << 4) | low);
        }

        return new ProfileIdentity(Convert.ToHexString(digest).ToLowerInvariant());

        static int HexNibble(char value) => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };
    }

    public override string ToString() => Sha256Hex;
}

public enum ProfileRole
{
    Input,
    Working,
    Output,
    Proof,
    Monitor,
    CanonicalPresentation,
}

public enum BuiltInProfileId
{
    Srgb,
    DisplayP3,
    AdobeRgb1998,
    Rec709,
    LinearAcesCg,
    LinearExtendedSrgb,
    /// <summary>DCI-P3 primaries, DCI white, gamma 2.6 — a print LUT's native output, never a roll's container.</summary>
    DciP3,
}

/// <summary>
/// Where a profile snapshot came from. The exact bytes, rather than this descriptive source,
/// remain the colorimetric source of truth.
/// </summary>
public abstract record ProfileSource
{
    private ProfileSource() { }

    public sealed record BuiltIn(BuiltInProfileId Id) : ProfileSource;

    public sealed record Embedded : ProfileSource
    {
        public string StableSourceId { get; }
        public string Container { get; }

        public Embedded(string stableSourceId, string container)
        {
            StableSourceId = Required(stableSourceId, nameof(stableSourceId));
            Container = Required(container, nameof(container));
        }
    }

    public sealed record Generated : ProfileSource
    {
        public string GeneratorId { get; }
        public string GeneratorVersion { get; }

        public Generated(string generatorId, string generatorVersion)
        {
            GeneratorId = Required(generatorId, nameof(generatorId));
            GeneratorVersion = Required(generatorVersion, nameof(generatorVersion));
        }
    }

    /// <summary>An external profile copied into project-owned immutable storage.</summary>
    public sealed record ExternalSnapshot : ProfileSource
    {
        public string OriginalFileName { get; }

        public ExternalSnapshot(string originalFileName) =>
            OriginalFileName = Required(originalFileName, nameof(originalFileName));
    }

    /// <summary>Current device state. This source must never enter a render fingerprint.</summary>
    public sealed record Monitor : ProfileSource
    {
        public string DisplayId { get; }
        public long Revision { get; }

        public Monitor(string displayId, long revision)
        {
            DisplayId = Required(displayId, nameof(displayId));
            Revision = revision;
        }
    }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>An immutable ICC payload whose identity is always derived from its exact bytes.</summary>
public sealed class ColorProfileRef
{
    public ImmutableArray<byte> IccBytes { get; }
    public ProfileIdentity Identity { get; }
    public string Description { get; }
    public ProfileRole Role { get; }
    public ProfileSource Source { get; }

    private ColorProfileRef(
        ImmutableArray<byte> iccBytes,
        ProfileIdentity identity,
        string description,
        ProfileRole role,
        ProfileSource source)
    {
        IccBytes = iccBytes;
        Identity = identity;
        Description = description;
        Role = role;
        Source = source;
    }

    public static ColorProfileRef Create(
        ReadOnlySpan<byte> iccBytes,
        string description,
        ProfileRole role,
        ProfileSource source)
    {
        if (iccBytes.IsEmpty) throw new ArgumentException("ICC payload is empty.", nameof(iccBytes));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(source);

        // The identity is computed before publishing the copied immutable payload. There is no API
        // that accepts an independently supplied hash, so bytes and identity cannot drift apart.
        ProfileIdentity identity = ProfileIdentity.FromIcc(iccBytes);
        ImmutableArray<byte> frozen = ImmutableArray.CreateRange(iccBytes.ToArray());
        return new ColorProfileRef(frozen, identity, description, role, source);
    }

    public override string ToString() => $"{Description} [{Identity.Sha256Hex[..12]}]";
}
