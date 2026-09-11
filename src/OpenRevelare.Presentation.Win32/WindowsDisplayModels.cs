using OpenRevelare.ColorManagement;

namespace OpenRevelare.Presentation.Win32;

public enum WindowsAdvancedColorMode
{
    Unknown,
    Sdr,
    WideColorGamut,
    HighDynamicRange,
}

public enum WindowsMonitorProfileStatus
{
    Unavailable,
    ExactLegacyProfile,
    OwnedBySystemAdvancedColor,
}

public sealed record WindowsDisplayDiagnostics(
    string DisplayId,
    long ContractRevision,
    string ProbeApi,
    string? GdiDeviceName,
    string? MonitorDevicePath,
    string? MonitorFriendlyName,
    string? AdapterLuid,
    uint? SourceId,
    uint? TargetId,
    uint? WindowDpi,
    uint? AdvancedColorRawFlags,
    bool AdvancedColorSupported,
    bool AdvancedColorActive,
    bool HdrSupported,
    bool HdrUserEnabled,
    bool WideColorSupported,
    bool WideColorUserEnabled,
    WindowsAdvancedColorMode ActiveColorMode,
    string? ColorEncoding,
    uint? BitsPerColorChannel,
    uint? SdrWhiteRaw,
    float SdrWhiteNits,
    string? SdrWhiteFailureReason,
    WindowsMonitorProfileStatus ProfileStatus,
    string? ProfileScope,
    string? ProfileFileName,
    string? ProfileSha256,
    string? FallbackReason,
    bool WysiwygGuaranteed,
    bool RequiresExplicitRefresh);

internal sealed class MonitorProfileData
{
    internal byte[] IccBytes { get; }
    internal string Description { get; }
    internal string FileName { get; }
    internal string Scope { get; }
    internal ProfileIdentity Identity { get; }

    internal MonitorProfileData(
        ReadOnlySpan<byte> iccBytes,
        string description,
        string fileName,
        string scope)
    {
        if (iccBytes.IsEmpty) throw new ArgumentException("ICC bytes are empty.", nameof(iccBytes));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        IccBytes = iccBytes.ToArray();
        Description = description;
        FileName = fileName;
        Scope = scope;
        Identity = ProfileIdentity.FromIcc(IccBytes);
    }
}

internal sealed record AdvancedColorState(
    bool IsReliable,
    string ProbeApi,
    uint? RawFlags,
    bool Supported,
    bool Active,
    bool HdrSupported,
    bool HdrUserEnabled,
    bool WideColorSupported,
    bool WideColorUserEnabled,
    WindowsAdvancedColorMode Mode,
    string? ColorEncoding,
    uint? BitsPerColorChannel,
    string? FailureReason);

internal sealed record WindowsDisplayProbeSnapshot(
    string DisplayId,
    string? GdiDeviceName,
    string? MonitorDevicePath,
    string? MonitorFriendlyName,
    string? AdapterLuid,
    uint? SourceId,
    uint? TargetId,
    uint? WindowDpi,
    AdvancedColorState AdvancedColor,
    uint? SdrWhiteRaw,
    float SdrWhiteNits,
    string? SdrWhiteFailureReason,
    MonitorProfileData? MonitorProfile,
    string? MonitorProfileFailureReason,
    string? FailureReason)
{
    internal WindowsDisplaySemanticKey SemanticKey => new(
        DisplayId,
        AdvancedColor.IsReliable,
        AdvancedColor.RawFlags,
        AdvancedColor.Supported,
        AdvancedColor.Active,
        AdvancedColor.Mode,
        AdvancedColor.ColorEncoding,
        AdvancedColor.BitsPerColorChannel,
        WindowDpi,
        SdrWhiteRaw,
        SdrWhiteFailureReason,
        MonitorProfile?.Identity.Sha256Hex,
        FailureReason ?? AdvancedColor.FailureReason ?? MonitorProfileFailureReason);

    internal static WindowsDisplayProbeSnapshot Failed(nint hwnd, string reason)
    {
        string displayId = $"win32:unresolved:hwnd-{unchecked((ulong)hwnd):X}";
        return new WindowsDisplayProbeSnapshot(
            displayId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            new AdvancedColorState(
                false,
                "unavailable",
                null,
                false,
                false,
                false,
                false,
                false,
                false,
                WindowsAdvancedColorMode.Unknown,
                null,
                null,
                reason),
            null,
            80f,
            reason,
            null,
            null,
            reason);
    }
}

internal readonly record struct WindowsDisplaySemanticKey(
    string DisplayId,
    bool AdvancedStateReliable,
    uint? AdvancedRawFlags,
    bool AdvancedSupported,
    bool AdvancedActive,
    WindowsAdvancedColorMode AdvancedMode,
    string? ColorEncoding,
    uint? BitsPerColorChannel,
    uint? WindowDpi,
    uint? SdrWhiteRaw,
    string? SdrWhiteFailureReason,
    string? ProfileSha256,
    string? FailureReason);

internal interface IWindowsDisplayProbe
{
    WindowsDisplayProbeSnapshot Probe(nint windowHwnd);
}

/// <summary>
/// Serializes the final contract validation/upload interval with display refresh. Without this
/// lease, a refresh can publish a new revision after a presenter's last <c>Current</c> read but
/// before the native call.
/// </summary>
internal interface IWindowsDisplayContractLeaseProvider
{
    void WithStableContract(Action<OpenRevelare.Presentation.DisplayContract> action);
}
