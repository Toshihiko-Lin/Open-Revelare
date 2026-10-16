using System.Globalization;
using System.Text.Json.Serialization;

namespace OpenRevelare.Core;

/// <summary>A reusable physical device, independent of a particular exposure or digitization.</summary>
public sealed record CaptureDevice
{
    public string Make { get; init; } = "";
    public string Model { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    [JsonIgnore]
    public string Label => string.Join(" · ", new[] { Make, Model, SerialNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Make) && string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// The original film exposure. Empty fields mean unknown, never "borrow the copying camera's
/// setting". This belongs to one export and is deliberately not a reusable device preset.
/// </summary>
public sealed record FilmCaptureMetadata
{
    public CaptureDevice Camera { get; init; } = new();
    public CaptureDevice Lens { get; init; } = new();
    public string Iso { get; init; } = "";
    public string FNumber { get; init; } = "";
    public string ExposureTime { get; init; } = "";
    public string FocalLength { get; init; } = "";
    public string DateTimeOriginal { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Copyright { get; init; } = "";

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(Iso) && (!ushort.TryParse(Iso, out ushort iso) || iso == 0))
            throw new FormatException("ISO must be an integer from 1 to 65535.");
        Number(FNumber, "Aperture");
        Number(FocalLength, "Focal length");
        Number(ExposureTime, "Shutter speed", allowFraction: true);
        if (!string.IsNullOrWhiteSpace(DateTimeOriginal)) ParseDate(DateTimeOriginal);
    }

    internal static double? Number(string text, string field, bool allowFraction = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Trim().Split('/');
        if (parts.Length > (allowFraction ? 2 : 1)
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"{field} must be a positive number.");
        if (parts.Length == 2)
        {
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
                || denominator <= 0) throw new FormatException($"{field} has an invalid denominator.");
            value /= denominator;
        }
        if (!double.IsFinite(value) || value <= 0 || value > 1_000_000)
            throw new FormatException($"{field} must be a positive number below 1000000.");
        return value;
    }

    internal static DateTime ParseDate(string text)
    {
        string[] formats = ["yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];
        if (!DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new FormatException("Capture time must use YYYY-MM-DD HH:mm:ss.");
        return date;
    }
}
