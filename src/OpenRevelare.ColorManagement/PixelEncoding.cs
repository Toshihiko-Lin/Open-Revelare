namespace OpenRevelare.ColorManagement;

public enum ColorReference
{
    SceneReferred,
    DisplayReferred,
    MonitorDevice,
}

public enum TransferState
{
    Unknown,
    ProfileEncoded,
    LinearInProfilePrimaries,
    /// <summary>Linear sensor/capture channels whose primaries are not an ICC colour space.</summary>
    LinearInCaptureChannels,
}

public enum NumericRange
{
    Normalized,
    Extended,
}

public enum CaptureKind
{
    RawCameraNative,
    TiffUntagged8Bit,
    TiffUntagged16Bit,
    EmbeddedProfileUnsupported,
    ScannerVendorDeclaredWithoutPrimaries,
    Synthetic,
    Other,
}

public enum CompatibilityPolicy
{
    None,
    Reject,
    RequireExplicitOverride,
    LegacyTreatNumbersAsWorking,
    LegacyDecodeSrgbTransferThenTreatAsWorking,
    LegacyPartialIccTransform,
}

public abstract record PixelEncoding(TransferState Transfer, NumericRange Range);

public sealed record CharacterizedPixelEncoding : PixelEncoding
{
    public ColorProfileRef Profile { get; }
    public ColorReference Reference { get; }

    public CharacterizedPixelEncoding(
        ColorProfileRef profile,
        ColorReference reference,
        TransferState transfer,
        NumericRange range)
        : base(transfer, range)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (transfer is not (TransferState.ProfileEncoded or TransferState.LinearInProfilePrimaries))
            throw new ArgumentException("A characterized encoding needs a transfer state in its profile primaries.", nameof(transfer));
        Profile = profile;
        Reference = reference;
    }
}

public sealed record UncharacterizedPixelEncoding : PixelEncoding
{
    public CaptureKind CaptureKind { get; }
    public string StableSourceId { get; }
    public CompatibilityPolicy Compatibility { get; }

    public UncharacterizedPixelEncoding(
        CaptureKind captureKind,
        string stableSourceId,
        CompatibilityPolicy compatibility,
        TransferState transfer,
        NumericRange range)
        : base(transfer, range)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableSourceId);
        CaptureKind = captureKind;
        StableSourceId = stableSourceId;
        Compatibility = compatibility;
    }
}
