namespace OpenRevelare.Core;

/// <summary>
/// Roll-level meaning assigned to a TIFF whose embedded ICC profile is absent or unusable.
/// A usable embedded profile always wins; this value is only its explicit fallback.
/// </summary>
public enum TiffInputAssumption
{
    /// <summary>No fallback was selected. Managed input must reject an untagged/unusable file.</summary>
    Unspecified = 0,

    /// <summary>
    /// Samples are already linear. Their primaries remain uncharacterized, so the numeric values
    /// are admitted unchanged rather than being relabelled with an invented profile.
    /// </summary>
    Linear = 1,

    /// <summary>Samples are encoded by the exact built-in sRGB profile.</summary>
    Srgb = 2,

    /// <summary>
    /// Frozen pre-v2 behaviour for old projects only: untagged TIFF8 decodes the sRGB transfer
    /// while TIFF16/float passes through. New imports must never select this policy.
    /// </summary>
    LegacyByBitDepthCompatibility = 3,
}

/// <summary>Conversions shared by the import UI, project persistence and CLI admission.</summary>
public static class TiffInputAssumptionPolicy
{
    /// <summary>Resolve two mutually exclusive UI/CLI switches into one typed choice.</summary>
    public static TiffInputAssumption FromExplicitChoice(bool linear, bool srgb)
    {
        if (linear && srgb)
            throw new ArgumentException("TIFF input assumptions Linear and sRGB are mutually exclusive.");
        if (linear) return TiffInputAssumption.Linear;
        if (srgb) return TiffInputAssumption.Srgb;
        return TiffInputAssumption.Unspecified;
    }

    /// <summary>
    /// Recover the historical <c>roll_meta.tiff_is_linear</c> field. Missing/null means the
    /// versioned compatibility route, which preserves projects saved before the field was used.
    /// </summary>
    public static TiffInputAssumption FromPersistedLinearFlag(bool? tiffIsLinear) =>
        tiffIsLinear switch
        {
            true => TiffInputAssumption.Linear,
            false => TiffInputAssumption.Srgb,
            null => TiffInputAssumption.LegacyByBitDepthCompatibility,
        };

    /// <summary>Encode a live roll back into the existing project field.</summary>
    public static bool? ToPersistedLinearFlag(TiffInputAssumption assumption) => assumption switch
    {
        TiffInputAssumption.Linear => true,
        TiffInputAssumption.Srgb => false,
        TiffInputAssumption.LegacyByBitDepthCompatibility => null,
        TiffInputAssumption.Unspecified => throw new InvalidOperationException(
            "A new TIFF roll cannot be saved without an explicit input assumption."),
        _ => throw new ArgumentOutOfRangeException(nameof(assumption), assumption, null),
    };

    internal static bool IsExplicitFallback(TiffInputAssumption assumption) =>
        assumption is TiffInputAssumption.Linear or TiffInputAssumption.Srgb;
}
