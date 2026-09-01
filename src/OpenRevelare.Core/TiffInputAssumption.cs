namespace OpenRevelare.Core;

/// <summary>
/// Roll-level meaning assigned to a TIFF whose embedded ICC profile is absent or unusable.
/// A usable embedded profile always wins; this value is only its explicit fallback.
/// </summary>
public enum TiffInputAssumption
{
    /// <summary>
    /// No explicit user choice. Managed input resolves each file through
    /// <see cref="TiffInputDetector"/>, which reads the colorimetry the file already carries and
    /// falls back to a labelled conventional default when it carries none.
    ///
    /// <para>
    /// This deliberately does NOT reject the file. Demanding an upfront Linear/sRGB answer put the
    /// question at the moment the user knows least — before any pixel is on screen — to cover a
    /// minority of files, while the majority declare themselves through TIFF 6.0 or Exif tags that
    /// simply were not being read. The choice below remains available as an override, and the roll
    /// can be re-decided after the picture is visible.
    /// </para>
    /// </summary>
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
    /// Recover the historical <c>roll_meta.tiff_is_linear</c> field.
    ///
    /// <para>
    /// A missing/null field means different things either side of the pipeline version, and the
    /// version is the only thing that can tell them apart. Under <see cref="ColorPipelineVersion.LegacyV1"/>
    /// it is a project saved before the field carried meaning, which must keep the frozen
    /// by-bit-depth route. Under <see cref="ColorPipelineVersion.ManagedV2"/> it cannot be a legacy
    /// project at all — v2 never wrote null before automatic detection existed — so it is the
    /// detector's state.
    /// </para>
    /// </summary>
    public static TiffInputAssumption FromPersistedLinearFlag(
        bool? tiffIsLinear,
        ColorPipelineVersion pipelineVersion) =>
        tiffIsLinear switch
        {
            true => TiffInputAssumption.Linear,
            false => TiffInputAssumption.Srgb,
            null when pipelineVersion == ColorPipelineVersion.ManagedV2 =>
                TiffInputAssumption.Unspecified,
            null => TiffInputAssumption.LegacyByBitDepthCompatibility,
        };

    /// <summary>Encode a live roll back into the existing project field.</summary>
    public static bool? ToPersistedLinearFlag(TiffInputAssumption assumption) => assumption switch
    {
        TiffInputAssumption.Linear => true,
        TiffInputAssumption.Srgb => false,
        TiffInputAssumption.LegacyByBitDepthCompatibility => null,
        // Automatic detection is the absence of a stored override, which is what null already
        // meant. Reusing it keeps the project schema unchanged.
        TiffInputAssumption.Unspecified => null,
        _ => throw new ArgumentOutOfRangeException(nameof(assumption), assumption, null),
    };

    internal static bool IsExplicitFallback(TiffInputAssumption assumption) =>
        assumption is TiffInputAssumption.Linear or TiffInputAssumption.Srgb;

    /// <summary>
    /// Whether managed admission has somewhere to go when the embedded profile turns out to be
    /// absent or broken. Automatic detection counts: it always resolves to a usable answer.
    /// </summary>
    internal static bool AdmitsFallback(TiffInputAssumption assumption) =>
        IsExplicitFallback(assumption) || assumption == TiffInputAssumption.Unspecified;
}
