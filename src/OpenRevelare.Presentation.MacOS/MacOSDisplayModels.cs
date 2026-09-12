using OpenRevelare.Presentation;

namespace OpenRevelare.Presentation.MacOS;

/// <summary>
/// What one probe of the screen behind the preview view found. Every field is what AppKit
/// reported, untouched; the policy that turns it into a <see cref="DisplayContract"/> lives in
/// <see cref="MacOSDisplayContractProvider"/> so the two can be tested apart.
///
/// <para>
/// WHAT THE NUMBERS MEAN ON THIS PLATFORM. Apple's extended-linear-sRGB carrier puts SDR white
/// at exactly <c>1.0</c> — a brightness-slider-relative reference, not a luminance — and reports
/// how far above it the panel can currently go as
/// <c>NSScreen.maximumExtendedDynamicRangeColorComponentValue</c>. That is the whole EDR model:
/// there is no separate "SDR white level" to read, because there is nothing to scale by (D-026).
/// </para>
/// </summary>
internal sealed record MacOSDisplayProbeSnapshot(
    string DisplayId,
    uint? DirectDisplayId,
    string? LocalizedName,
    string? ColorSpaceName,
    double BackingScaleFactor,
    double MaximumEdrValue,
    double MaximumPotentialEdrValue,
    double MaximumReferenceEdrValue,
    bool IsReliable,
    string? FailureReason)
{
    internal MacOSDisplaySemanticKey SemanticKey => new(
        DisplayId,
        IsReliable,
        ColorSpaceName,
        BackingScaleFactor,
        MaximumEdrValue,
        MaximumPotentialEdrValue,
        FailureReason);

    internal static MacOSDisplayProbeSnapshot Failed(nint view, string reason) => new(
        $"macos:unresolved:view-{unchecked((ulong)view):X}",
        null,
        null,
        null,
        1.0,
        1.0,
        1.0,
        1.0,
        IsReliable: false,
        reason);
}

/// <summary>The fields whose change means the contract must be republished under a new revision.</summary>
internal readonly record struct MacOSDisplaySemanticKey(
    string DisplayId,
    bool IsReliable,
    string? ColorSpaceName,
    double BackingScaleFactor,
    double MaximumEdrValue,
    double MaximumPotentialEdrValue,
    string? FailureReason);

internal interface IMacOSDisplayProbe
{
    MacOSDisplayProbeSnapshot Probe(nint viewHandle);
}

/// <summary>
/// The acceptance-facing view of the display state (§13: diagnostics are a feature, not a log).
/// </summary>
public sealed record MacOSDisplayDiagnostics(
    string DisplayId,
    long ContractRevision,
    uint? DirectDisplayId,
    string? LocalizedName,
    string? ColorSpaceName,
    double BackingScaleFactor,
    double MaximumEdrValue,
    double MaximumPotentialEdrValue,
    double MaximumReferenceEdrValue,
    bool EdrRequested,
    string? FallbackReason,
    bool WysiwygGuaranteed);
