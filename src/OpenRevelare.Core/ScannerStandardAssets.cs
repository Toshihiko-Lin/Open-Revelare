using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

public sealed record ScannerStandardAsset(string Format, string Sha256, CalibrationReferenceRole Role,
    string[] Fields, string[][] Rows, IReadOnlyDictionary<string, string> Metadata,
    bool HasDeviceRgb, bool HasPcsReference, bool HasSpectralData);

/// <summary>Imports existing industry assets without converting PCS values into density.</summary>
public static class ScannerStandardAssets
{
    public static ScannerStandardAsset Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Scanner asset exceeds 16 MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        bool icc = bytes.Length >= 40 && Encoding.ASCII.GetString(bytes, 36, 4) == "acsp";
        if (icc || Path.GetExtension(path).Equals(".icc", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".icm", StringComparison.OrdinalIgnoreCase))
        {
            if (!icc || Encoding.ASCII.GetString(bytes, 12, 4) != "scnr"
                || Encoding.ASCII.GetString(bytes, 16, 4) != "RGB ")
                throw new InvalidDataException("A three-channel RGB input-device ICC profile is required, not a monitor/output profile.");
            var profile = ColorProfileRef.Create(bytes, Path.GetFileName(path), ProfileRole.Input,
                new ProfileSource.ExternalSnapshot(Path.GetFileName(path)));
            using var engine = new LittleCmsEngine();
            var validation = engine.Validate(profile);
            if (!validation.IsValid) throw new InvalidDataException(validation.Message);
            return new("ICC input RGB", hash, CalibrationReferenceRole.InputIcc, [], [],
                new Dictionary<string, string> { ["DEVICE_CLASS"] = "INPUT", ["PCS"] = Encoding.ASCII.GetString(bytes, 20, 4).Trim(),
                    ["CMM_VALIDATION"] = validation.Message }, true, true, false);
        }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        return ParseCgats(text, hash);
    }

    public static ScannerStandardAsset ParseCgats(string text, string sha256 = "")
    {
        if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("CGATS data exceeds 16 MiB.");
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<string>(); var rows = new List<string[]>();
        string? identifier = null; bool format = false, data = false, formatDone = false, dataDone = false;
        foreach (string line in text.TrimStart('\uFEFF').Split('\n'))
        {
            string[] tokens = Tokens(line.TrimEnd('\r'));
            if (tokens.Length == 0) continue;
            string key = tokens[0].ToUpperInvariant();
            if (identifier is null)
            {
                identifier = key;
                if (!(key.StartsWith("IT8.") || key.StartsWith("CGATS.") || key == "CTI3"))
                    throw new InvalidDataException("Expected an IT8/Q60 CGATS or Argyll CTI3 file.");
                continue;
            }
            if (key == "BEGIN_DATA_FORMAT")
            {
                if (format || data || formatDone || dataDone) throw new InvalidDataException("Only one CGATS table is supported.");
                format = true; continue;
            }
            if (key == "END_DATA_FORMAT")
            {
                if (!format) throw new InvalidDataException("Unexpected END_DATA_FORMAT.");
                format = false; formatDone = true; continue;
            }
            if (key == "BEGIN_DATA")
            {
                if (!formatDone || format || data || dataDone) throw new InvalidDataException("Invalid CGATS table order.");
                data = true; continue;
            }
            if (key == "END_DATA")
            {
                if (!data) throw new InvalidDataException("Unexpected END_DATA.");
                data = false; dataDone = true; continue;
            }
            if (format) { fields.AddRange(tokens.Select(t => t.ToUpperInvariant())); continue; }
            if (data)
            {
                if (tokens.Length != fields.Count) throw new InvalidDataException("CGATS row has the wrong field count.");
                for (int i = 0; i < fields.Count; i++)
                    if (IsMeasurement(fields[i]) && (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)))
                        throw new InvalidDataException("CGATS measurement must be a finite number.");
                rows.Add(tokens); continue;
            }
            if (tokens.Length >= 2) meta[key] = string.Join(" ", tokens.Skip(1));
        }
        if (identifier is null || !formatDone || !dataDone || format || data || fields.Count == 0 || rows.Count == 0
            || fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count)
            throw new InvalidDataException("Incomplete or ambiguous CGATS table.");
        int Declared(string key) => meta.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            ? count : throw new InvalidDataException("Missing CGATS " + key);
        if (Declared("NUMBER_OF_FIELDS") != fields.Count || Declared("NUMBER_OF_SETS") != rows.Count)
            throw new InvalidDataException("CGATS declared counts do not match the table.");
        if (identifier == "CTI3" && (!meta.TryGetValue("DEVICE_CLASS", out var cls) || cls is not ("INPUT" or "EMISINPUT")))
            throw new InvalidDataException("CTI3 must describe an input device, not a display/printer.");
        bool Has(params string[] keys) => keys.All(fields.Contains);
        return new(identifier == "CTI3" ? "Argyll CTI3" : "IT8/CGATS", sha256, CalibrationReferenceRole.TargetData,
            fields.ToArray(), rows.ToArray(), meta, Has("RGB_R", "RGB_G", "RGB_B"),
            Has("XYZ_X", "XYZ_Y", "XYZ_Z") || Has("LAB_L", "LAB_A", "LAB_B"), fields.Any(f => f.StartsWith("SPEC_")));
    }

    private static bool IsMeasurement(string field) => field.StartsWith("RGB_") || field.StartsWith("XYZ_")
        || field.StartsWith("LAB_") || field.StartsWith("SPEC_");

    private static string[] Tokens(string line)
    {
        var result = new List<string>(); var value = new StringBuilder(); bool quote = false, started = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { quote = !quote; started = true; continue; }
            if (!quote && c == '#') break;
            if (quote && c == '\\' && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; continue; }
            if (!quote && char.IsWhiteSpace(c))
            {
                if (started) { result.Add(value.ToString()); value.Clear(); started = false; }
            }
            else { value.Append(c); started = true; }
        }
        if (quote) throw new InvalidDataException("Unterminated CGATS quoted string.");
        if (started) result.Add(value.ToString());
        return result.ToArray();
    }
}
