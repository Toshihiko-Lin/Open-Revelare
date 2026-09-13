namespace OpenRevelare.Gui.Models;

/// <summary>
/// What the display currently under the window can show above SDR white. Reported by the
/// platform composition root; consumed only by hints. Nothing that renders reads it (I5).
/// </summary>
/// <param name="SdrWhiteNits">The reference white the user configured for SDR content.</param>
/// <param name="Headroom">Panel peak over SDR white; exactly one when unknown.</param>
/// <param name="PanelPeakNits">The panel's peak luminance, or null when it could not be read.</param>
/// <param name="FailureReason">Why the peak could not be read, when it could not.</param>
public sealed record DisplayHdrCapability(
    float SdrWhiteNits,
    float Headroom,
    float? PanelPeakNits,
    string? FailureReason);
