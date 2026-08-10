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
/// Output colour space is a real choice now: <see cref="OutputRender"/> converts the working
/// space into the destination gamut, maps what falls outside it, and applies that space's own
/// encoding curve, so the embedded profile describes what was actually written. Before that
/// existed the option was deliberately withheld — offering it would have attached an Adobe RGB
/// profile to sRGB pixels, which is not a wider gamut but a mislabelled file.
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

    /// <summary>Embed the profile describing what was written. Ignored under
    /// <see cref="OutputIntent.None"/>, whose output is linear and which no profile here
    /// describes.</summary>
    public bool EmbedIcc { get; set; } = true;

    /// <summary>
    /// Destination colour space, by <see cref="ColorSpaceDef.Name"/>. Stored as a string rather
    /// than an enum so a project written by a newer build naming a space this one lacks falls
    /// back to sRGB instead of failing to parse.
    ///
    /// sRGB is the default because it is what an unmanaged viewer assumes; the wider and the
    /// print-emulating spaces are opt-in.
    /// </summary>
    public string ColorSpace { get; set; } = "sRGB";

    /// <summary>How colours outside the destination gamut are handled. Only matters when the
    /// destination is narrower than the source somewhere.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GamutMapping GamutMapping { get; set; } = GamutMapping.Desaturate;

    /// <summary>The resolved destination space; sRGB when the stored name is unknown.</summary>
    [JsonIgnore]
    public ColorSpaceDef ResolvedColorSpace => ColorSpaces.ByName(ColorSpace, ColorSpaces.Srgb);

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
        string space = ResolvedColorSpace.Name;
        return $"{format} · {size} · {space}" + (EmbedIcc ? Loc.F($" · 嵌 {space}") : "");
    }
}
