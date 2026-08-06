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
/// The list is deliberately shorter than it could be. Two options that a film exporter is
/// normally expected to have are missing ON PURPOSE, because the code behind them is not there:
///
/// • Output colour space — <see cref="ColorSpace.AdobeRgb"/> has a profile builder, but the
///   pipeline encodes pixels with the sRGB TRC under <see cref="OutputIntent.Basic"/> and there
///   is no conversion step. Offering the choice would attach an Adobe RGB profile to sRGB pixels,
///   which is not a wider gamut — it is a mislabelled file. So the choice is only whether to
///   embed the profile that matches what was actually written.
/// • Sharpening — there is no sharpening implementation to call.
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

    /// <summary>Embed the sRGB profile describing what was written. Ignored under
    /// <see cref="OutputIntent.None"/>, whose output is linear and which no profile here
    /// describes.</summary>
    public bool EmbedIcc { get; set; } = true;

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
            ? $"JPEG 品质 {JpegQuality}"
            : $"16-bit TIFF · {TiffCompression switch
            {
                TiffIO.CompressionMode.None => "不压缩",
                TiffIO.CompressionMode.Deflate => "Deflate",
                _ => "LZW",
            }}";
        string size = Downsample ? $"长边 ≤ {MaxLongEdge}px" : "原始尺寸";
        return $"{format} · {size}" + (EmbedIcc ? " · 嵌 sRGB" : "");
    }
}
