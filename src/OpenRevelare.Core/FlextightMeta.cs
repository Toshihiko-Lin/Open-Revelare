using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace OpenRevelare.Core;

/// <summary>
/// The scan settings a Hasselblad/Imacon Flextight writes into its <c>.fff</c> exports — read to
/// recover the transfer function, because the file does not carry one anywhere else.
///
/// WHY THIS EXISTS. A Flextight <c>.fff</c> is an ordinary uncompressed 16-bit RGB TIFF (see
/// <see cref="RawDecode.LooksLikeFullColorTiff"/>, which is what routes it here rather than to
/// LibRaw). Its pixels are NOT linear light: the scanner software applies an encoding gamma on
/// export. It declares <c>EmbedProfile = true</c> — and then embeds no ICC profile at all
/// (tag 34675 absent on the verified sample), so <see cref="IccRead"/> has nothing to read and
/// <c>TiffIO</c> would treat the file as already linear.
///
/// The cost of that is not subtle. Measured on the reference scan, reading the samples as linear
/// puts per-channel D_max at 0.55 / 0.69 / 0.77; undoing the declared gamma puts it at
/// 1.10 / 1.37 / 1.54. <see cref="FrameParams"/> documents real highlight density as 1.0–1.5, so
/// the uncorrected reading is low by about half — the whole density scale, not a tint.
///
/// WHERE THE NUMBER COMES FROM. It is READ, never fitted. The scanner writes an Apple plist into
/// private TIFF tag 50457, and <c>ImageSettings[CurrentIx].ImageCorrection.Gamma</c> is the
/// encoding gamma it used. Fitting was considered and rejected: the D_max criterion above cannot
/// separate 1.8 from 2.2 (both land inside the plausible band), so a fit would be choosing a look,
/// which is exactly what this project refuses to do. The declaration is authoritative or nothing
/// is applied.
///
/// NOT A FORMAT CONSTANT, AND NOT PER-MODEL. <c>Gamma</c> sits in <c>ImageCorrection</c> beside
/// <c>Contrast</c>, <c>Saturation</c> and <c>EV</c> — it is a per-scan operator setting. The
/// reference file says 2.0; another export from the same machine may say otherwise. Hence it is
/// parsed per file and never cached against a model name.
/// </summary>
public static class FlextightMeta
{
    /// <summary>Private TIFF tag holding the settings plist (verified on Flextight output).</summary>
    public const int SettingsPlistTag = 50457;

    /// <summary>
    /// How far <see cref="Gamma"/> may stray before the declaration is treated as absent.
    ///
    /// A transfer function outside this is not a scanner setting we understand, and guessing what
    /// it meant is worse than leaving the file linear and letting the user calibrate: the pipeline
    /// would silently apply a wrong power to every pixel. 1.0 (already linear) is admissible and
    /// simply produces no LUT.
    /// </summary>
    public const double MinGamma = 1.0, MaxGamma = 4.0;

    /// <summary>
    /// What was recovered from one file's plist. <see cref="Gamma"/> is null when the file carries
    /// no usable declaration — the caller must then leave the samples untouched rather than
    /// substitute a default.
    /// </summary>
    public readonly record struct Settings(double? Gamma, string? ColorSpaceName)
    {
        /// <summary>True when a gamma worth undoing was declared. 1.0 is a valid declaration that
        /// needs no work, so it deliberately reports false.</summary>
        public bool HasEncodingGamma => Gamma is { } g && g > 1.0 + 1e-9;
    }

    /// <summary>
    /// Read <see cref="SettingsPlistTag"/> straight out of the file's IFD0 and parse it.
    ///
    /// Read here rather than through LibTiff because LibTiff does not surface unregistered
    /// private tags — it logs "unknown field with tag 50457" and drops the value — and
    /// registering a tag extender would mutate global state for every TIFF the process opens.
    /// The payload is large (400 KB on the reference file) and read only when the tag is present.
    ///
    /// Never throws; an unreadable or non-Flextight file yields an empty <see cref="Settings"/>.
    /// </summary>
    public static Settings Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) != 8) return default;

            bool big = head[0] == 'M' && head[1] == 'M';
            if (!big && !(head[0] == 'I' && head[1] == 'I')) return default;
            if (U16(head[2..4], big) != 42) return default;

            long ifd = U32(head[4..8], big);
            if (ifd < 8 || ifd >= fs.Length) return default;
            fs.Seek(ifd, SeekOrigin.Begin);

            Span<byte> buf = stackalloc byte[12];
            if (fs.Read(buf[..2]) != 2) return default;
            int count = U16(buf[..2], big);
            if (count is <= 0 or > 512) return default;

            for (int i = 0; i < count; i++)
            {
                if (fs.Read(buf) != 12) return default;
                if (U16(buf[..2], big) != SettingsPlistTag) continue;

                // BYTE/UNDEFINED payload; anything this large is always out-of-line.
                long len = U32(buf[4..8], big);
                if (len is < 32 or > (8 << 20)) return default;
                long at = U32(buf[8..12], big);
                if (at < 8 || at + len > fs.Length) return default;

                var payload = new byte[len];
                fs.Seek(at, SeekOrigin.Begin);
                return fs.ReadAtLeast(payload, payload.Length, throwOnEndOfStream: false) == payload.Length
                    ? Parse(payload)
                    : default;
            }

            return default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static int U16(ReadOnlySpan<byte> b, bool big) =>
        big ? (b[0] << 8) | b[1] : (b[1] << 8) | b[0];

    private static uint U32(ReadOnlySpan<byte> b, bool big) => big
        ? ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]
        : ((uint)b[3] << 24) | ((uint)b[2] << 16) | ((uint)b[1] << 8) | b[0];

    /// <summary>
    /// Parse the Flextight settings out of raw tag-50457 bytes.
    ///
    /// Never throws: an absent, truncated, or unrecognised payload returns an empty
    /// <see cref="Settings"/>, which the caller reads as "no declaration, change nothing".
    /// </summary>
    public static Settings Parse(byte[]? plistBytes)
    {
        if (plistBytes is null || plistBytes.Length < 32) return default;

        try
        {
            // The tag is a fixed-size buffer zero-padded well past the document, and on the
            // verified sample the XML starts a few bytes in rather than at offset 0 — so locate
            // the declaration and the closing tag instead of assuming the payload is exactly it.
            string text = Encoding.UTF8.GetString(plistBytes);
            int start = text.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0) start = text.IndexOf("<plist", StringComparison.Ordinal);
            if (start < 0) return default;

            const string close = "</plist>";
            int end = text.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) return default;

            var doc = XDocument.Parse(text[start..(end + close.Length)]);
            XElement? root = doc.Root?.Element("dict");
            if (root is null) return default;

            // ImageSettings is an array of per-image dicts; CurrentIx selects the live one.
            XElement? settings = DictValue(root, "ImageSettings");
            if (settings is null) return default;
            var images = settings.Elements("dict").ToList();
            if (images.Count == 0) return default;

            int ix = 0;
            if (DictValue(root, "CurrentIx") is { } cur &&
                int.TryParse(cur.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed >= 0 && parsed < images.Count)
                ix = parsed;

            XElement? correction = DictValue(images[ix], "ImageCorrection");
            double? gamma = null;
            if (correction is not null && DictValue(correction, "Gamma") is { } g &&
                double.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gv) &&
                gv >= MinGamma && gv <= MaxGamma)
                gamma = gv;

            return new Settings(gamma, DictValue(images[ix], "Name")?.Value);
        }
        catch (Exception)
        {
            return default;   // malformed metadata must never stop a file from opening
        }
    }

    /// <summary>
    /// The value element following <paramref name="key"/> in an Apple plist <c>&lt;dict&gt;</c>,
    /// or null. plist dicts are a flat alternating run of <c>&lt;key&gt;</c> and value elements
    /// rather than nested pairs, so the value is simply the next sibling.
    /// </summary>
    private static XElement? DictValue(XElement dict, string key)
    {
        foreach (XElement k in dict.Elements("key"))
            if (k.Value == key)
                return k.ElementsAfterSelf().FirstOrDefault();
        return null;
    }

    /// <summary>
    /// A 65536-entry encoded→linear LUT undoing <paramref name="gamma"/>, or null when there is
    /// nothing to undo. Indexed by <c>round(v * 65535)</c>, matching the TRC LUTs from
    /// <see cref="IccRead.BuildTrcLuts"/> so both reach the pipeline the same way.
    /// </summary>
    public static float[][]? BuildGammaLuts(double gamma)
    {
        if (!(gamma > 1.0 + 1e-9)) return null;

        const int n = 65536;
        var lut = new float[n];
        for (int i = 0; i < n; i++)
            lut[i] = (float)Math.Pow((double)i / (n - 1), gamma);

        // One curve, shared by all three channels: the declaration is a single scalar, so giving
        // each channel its own copy would only invite someone to edit them apart later.
        return new[] { lut, lut, lut };
    }
}
