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
    public static ExportIccUiState Resolve(
        ColorSpaceDef outputSpace,
        bool exportLinear,
        bool requestedEmbedIcc)
    {
        // Full record equality is intentional. A space merely named "sRGB" is not proof that its
        // primaries, white point and transfer function are the exact built-in sRGB definition.
        bool canOmit = !exportLinear && outputSpace == ColorSpaces.Srgb;
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

    public bool Downsample { get; set; }

    /// <summary>Ceiling for the long edge when <see cref="Downsample"/> is on.</summary>
    public int MaxLongEdge { get; set; } = 2048;

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
        if (ExportLinear)
            return $"{format} · {size} · " + Loc.T("场景线性 ACEScg · 强制嵌入 exact ICC");
        string space = ResolvedColorSpace.Name;
        ExportIccUiState icc = ExportIccUiPolicy.Resolve(
            ResolvedColorSpace,
            exportLinear: false,
            EmbedIcc);
        string profile = icc.IsForced
            ? Loc.F($"强制嵌入 exact {space} ICC")
            : icc.EmbedIcc
                ? Loc.T("嵌入 exact sRGB ICC")
                : Loc.T("省略 ICC（仅限 exact sRGB）");
        return $"{format} · {size} · {space} · {profile}";
    }
}
