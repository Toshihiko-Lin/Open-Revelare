using System.Text.Json.Serialization;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Models;

/// <summary>Container the export writes into. Only what an encoder actually exists for.</summary>
public enum ExportFormat
{
    /// <summary>16-bit RGB TIFF — the archival / continue-editing output.</summary>
    Tiff16,
    /// <summary>8-bit JPEG, 4:4:4.</summary>
    Jpeg,
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

    /// <summary>Embed the profile describing what was written. Ignored when
    /// <see cref="ExportLinear"/> is set, whose output is scene-linear and which no profile here
    /// describes.</summary>
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
        string format = Format == ExportFormat.Jpeg
            ? Loc.F($"JPEG 品质 {JpegQuality}")
            : Loc.F($"16-bit TIFF · {TiffCompression switch
            {
                TiffIO.CompressionMode.None => Loc.T("不压缩"),
                TiffIO.CompressionMode.Deflate => "Deflate",
                _ => "LZW",
            }}");
        string size = Downsample ? Loc.F($"长边 ≤ {MaxLongEdge}px") : Loc.T("原始尺寸");
        if (ExportLinear)
            return $"{format} · {size} · " + Loc.T("场景线性 ACEScg（无 ICC）");
        string space = ResolvedColorSpace.Name;
        return $"{format} · {size} · {space}" + (EmbedIcc ? Loc.F($" · 嵌 {space}") : "");
    }
}
