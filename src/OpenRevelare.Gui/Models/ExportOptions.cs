using System.Text.Json.Serialization;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Models;

/// <summary>Container the export writes into. Only what an encoder actually exists for.</summary>
public enum ExportFormat
{
    /// <summary>
    /// TIFF selection persisted for compatibility: normalized output is RGB16, while
    /// <see cref="ExportLinear"/> upgrades the same container choice to RGB float32.
    /// </summary>
    Tiff16,
    /// <summary>8-bit JPEG, 4:4:4.</summary>
    Jpeg,
}

/// <summary>
/// Effective ICC state shown by the export dialog. <see cref="CanChange"/> is true only when
/// omitting the profile is a valid, portable choice; otherwise <see cref="EmbedIcc"/> is forced
/// on regardless of a stale persisted preference.
/// </summary>
public readonly record struct ExportIccUiState(bool EmbedIcc, bool CanChange)
{
    public bool IsForced => !CanChange;
}

/// <summary>
/// Pure UI policy mirroring the typed export boundary: only the exact built-in, display-referred
/// sRGB route may deliberately omit its ICC. All wider/different display encodings and every
/// scene-linear export require the exact profile describing their pixels.
/// </summary>
public static class ExportIccUiPolicy
{
    /// <param name="outputSpace">The space the FILE's display-referred pixels are in: the roll's
    /// output space, or for a gain-map JPEG its base space.</param>
    /// <param name="exportLinear">Scene-linear ACEScg export; always tagged.</param>
    /// <param name="requestedEmbedIcc">The persisted preference.</param>
    /// <param name="hdrMaster">The file is the extended linear carrier (a float32 TIFF of an HDR
    /// roll); it is only readable with the carrier's exact profile, so the choice is forced.</param>
    public static ExportIccUiState Resolve(
        ColorSpaceDef outputSpace,
        bool exportLinear,
        bool requestedEmbedIcc,
        bool hdrMaster = false)
    {
        // Full record equality is intentional. A space merely named "sRGB" is not proof that its
        // primaries, white point and transfer function are the exact built-in sRGB definition.
        bool canOmit = !exportLinear && !hdrMaster && outputSpace == ColorSpaces.Srgb;
        return new ExportIccUiState(
            EmbedIcc: canOmit ? requestedEmbedIcc : true,
            CanChange: canOmit);
    }
}

/// <summary>
/// Everything the export dialog decides. Persisted in settings.json, because an export preset is
/// the kind of thing a person picks once and then wants every time.
///
/// Output colour space is NOT here any more, only carried through: it became a render parameter
/// when Stage 2 started running inside it, so it lives on the roll and is picked in the main
/// window. The export writes what the render already produced and labels it accordingly — which
/// is what makes the preview WYSIWYG rather than an approximation of the file.
///
/// Sharpening is still absent on purpose: there is no sharpening implementation to call.
///
/// Resizing is offered but honestly labelled: <see cref="Resample.Box"/> averages by an INTEGER
/// factor, so it lands at or under the requested long edge rather than exactly on it.
/// </summary>
public sealed class ExportOptions
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExportFormat Format { get; set; } = ExportFormat.Tiff16;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TiffIO.CompressionMode TiffCompression { get; set; } = TiffIO.CompressionMode.Lzw;

    public int JpegQuality { get; set; } = 95;

    /// <summary>
    /// Embed the profile describing what was written. This is a user preference only for exact
    /// display-referred sRGB; wider/different display spaces and scene-linear output normalize it
    /// to <see langword="true"/> before export.
    /// </summary>
    public bool EmbedIcc { get; set; } = true;

    /// <summary>
    /// Write the scene-linear working-space (ACEScg) render instead of the finished picture:
    /// skip step 4 and Stage 2 entirely.
    ///
    /// This is what the old roll-level "线性" output intent became. It is an EXPORT property, not
    /// a roll mode — the file is an intermediate for someone else's grading suite, so the person
    /// asking for it wants this one file linear, not their working preview stripped of every
    /// adjustment. Keeping it here is also what lets the preview stay honest: the main window
    /// always shows the full render, so the output-space picker means what it says.
    ///
    /// Not persisted: an export preset that silently defaulted to linear would hand somebody an
    /// unviewable file the next time they exported without looking.
    /// </summary>
    [JsonIgnore]
    public bool ExportLinear { get; set; }

    /// <summary>
    /// The space the file is written in, by <see cref="ColorSpaceDef.Name"/> — CARRIED from the
    /// roll, not chosen here.
    ///
    /// It moved to the main window because it stopped being an export decision: Stage 2 runs
    /// inside this space, so it changes the picture, and choosing it at export time would mean
    /// grading against one space and writing another. The dialog reports it; the roll owns it.
    ///
    /// Not persisted in settings for the same reason — an export preset that pinned a colour space
    /// would silently override the roll's own on the next export.
    /// </summary>
    [JsonIgnore]
    public string ColorSpace { get; set; } = "sRGB";

    /// <summary>The resolved destination space; the pipeline default when the name is unknown.</summary>
    [JsonIgnore]
    public ColorSpaceDef ResolvedColorSpace => ColorSpaces.ByName(ColorSpace, ColorPipeline.DefaultOutput);

    /// <summary>
    /// The roll's HDR limit in stops above SDR white, zero while HDR is off — CARRIED from the
    /// roll like <see cref="ColorSpace"/>, for the same reason: it is the render's master
    /// parameter (D-029), not an export decision. It decides what a format means: a TIFF of an
    /// HDR roll is the float32 linear master, a JPEG of one is a gain-map JPEG.
    /// </summary>
    [JsonIgnore]
    public double HdrLimitStops { get; set; }

    [JsonIgnore]
    public bool IsHdr => HdrLimitStops > 0d;

    /// <summary>A JPEG of an HDR roll: SDR base plus gain map (ISO 21496-1 / Adobe).</summary>
    [JsonIgnore]
    public bool WritesGainMap => IsHdr && Format == ExportFormat.Jpeg && !ExportLinear;

    /// <summary>
    /// The colour space of a gain-map JPEG's SDR base, by name — the file's container primaries.
    /// This IS an export decision, unlike <see cref="ColorSpace"/>: the HDR rendering has no
    /// output space (its carrier is unbounded, D-024), and which display space the base is
    /// written in changes the file's compatibility, not the picture. Persisted, because it is a
    /// delivery preference. sRGB by default: every reader assumes it; Display P3 is what phone
    /// cameras write and keeps more of a wide-gamut highlight.
    /// </summary>
    public string HdrBaseSpace { get; set; } = "sRGB";

    /// <summary>The base spaces a gain-map JPEG may be written in.</summary>
    [JsonIgnore]
    public static IReadOnlyList<ColorSpaceDef> GainMapBaseSpaces { get; } =
        [ColorSpaces.Srgb, ColorSpaces.DisplayP3];

    [JsonIgnore]
    public ColorSpaceDef ResolvedHdrBaseSpace
    {
        get
        {
            ColorSpaceDef s = ColorSpaces.ByName(HdrBaseSpace, ColorSpaces.Srgb);
            return GainMapBaseSpaces.Contains(s) ? s : ColorSpaces.Srgb;
        }
    }

    /// <summary>
    /// The space the file's display-referred pixels are in: the gain-map base for an HDR JPEG,
    /// otherwise the roll's output space. This is what the ICC policy is resolved against.
    /// </summary>
    [JsonIgnore]
    public ColorSpaceDef FileDisplaySpace => WritesGainMap ? ResolvedHdrBaseSpace : ResolvedColorSpace;

    public bool Downsample { get; set; }

    /// <summary>Ceiling for the long edge when <see cref="Downsample"/> is on.</summary>
    public int MaxLongEdge { get; set; } = 2048;

    /// <summary>
    /// Keep a JPEG under <see cref="MaxFileSizeMb"/>. JPEG only: it is the one container with a
    /// quality knob, so a ceiling can be met by re-encoding rather than by changing what the file
    /// is. A 16-bit TIFF's size is its pixel count and the option is greyed out for it.
    /// </summary>
    public bool LimitFileSize { get; set; }

    /// <summary>The ceiling, in MiB (1 MB = 1024 × 1024 bytes — what Windows Explorer shows).</summary>
    public double MaxFileSizeMb { get; set; } = 10d;

    /// <summary>The byte ceiling the JPEG encoder is held to, null when none applies: the option
    /// is off, or the file is not a JPEG.</summary>
    [JsonIgnore]
    public long? MaxFileBytes =>
        LimitFileSize && Format == ExportFormat.Jpeg && !ExportLinear && MaxFileSizeMb > 0d
            ? (long)Math.Round(MaxFileSizeMb * 1024d * 1024d)
            : null;

    /// <summary>What a roll export does when the name is taken. Single-frame export ignores this —
    /// its save dialog already asked.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExportFile.ConflictPolicy Conflict { get; set; } = ExportFile.ConflictPolicy.Unique;

    public string Extension => Format == ExportFormat.Jpeg ? "jpg" : "tiff";

    public ExportOptions Clone() => (ExportOptions)MemberwiseClone();

    /// <summary>One line naming the decisions that change the file, for the dialog footer and the
    /// status bar — the same summary in both places, so what you confirmed is what gets reported.</summary>
    public string Summary()
    {
        string compression = TiffCompression switch
        {
            TiffIO.CompressionMode.None => Loc.T("不压缩"),
            TiffIO.CompressionMode.Deflate => "Deflate",
            _ => "LZW",
        };
        string format = ExportLinear
            ? Loc.F($"32-bit float TIFF · {compression}")
            : Format == ExportFormat.Jpeg
            ? Loc.F($"JPEG 品质 {JpegQuality}")
            : Loc.F($"16-bit TIFF · {compression}");
        string size = Downsample ? Loc.F($"长边 ≤ {MaxLongEdge}px") : Loc.T("原始尺寸");
        if (MaxFileBytes is not null) size += " · " + Loc.F($"≤ {MaxFileSizeMb:0.#} MB");
        if (ExportLinear)
            return $"{format} · {size} · " + Loc.T("场景线性 ACEScg · 嵌入 ICC");
        if (IsHdr && Format != ExportFormat.Jpeg)
        {
            // The master: the carrier itself, which only its own profile describes.
            return Loc.F($"32-bit float TIFF · {compression}") + $" · {size} · "
                + Loc.F($"HDR 母版 +{HdrLimitStops:0.0} 档 · 嵌入 ICC");
        }
        if (WritesGainMap)
            format = Loc.F($"增益图 JPEG 品质 {JpegQuality}") + " · " + Loc.F($"HDR +{HdrLimitStops:0.0} 档");
        string space = WritesGainMap
            ? Loc.F($"基底 {ResolvedHdrBaseSpace.Name}")
            : ResolvedColorSpace.Name;
        ExportIccUiState icc = ExportIccUiPolicy.Resolve(
            FileDisplaySpace,
            exportLinear: false,
            EmbedIcc);
        string profile = icc.IsForced
            ? Loc.F($"嵌入 {FileDisplaySpace.Name} ICC")
            : icc.EmbedIcc
                ? Loc.T("嵌入 sRGB ICC")
                : Loc.T("不嵌入 ICC");
        return $"{format} · {size} · {space} · {profile}";
    }
}
