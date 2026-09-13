using System.Globalization;
using BitMiracle.LibTiff.Classic;

namespace OpenRevelare.Core;

/// <summary>
/// What the detector actually stood on. This is not decoration: the choice it produces changes
/// every pixel downstream, so the UI shows it and the decode recipe records it. The order below is
/// the order of trust — everything above <see cref="ScannerSoftware"/> is read out of the file,
/// only the last two are inference.
/// </summary>
public enum TiffInputEvidence
{
    /// <summary>Nothing was inspected — the file could not be opened.</summary>
    None = 0,

    /// <summary>SampleFormat is IEEE float. Float samples are scene data, not display encoding.</summary>
    FloatSampleFormat,

    /// <summary>TIFF 6.0 WhitePoint + PrimaryChromaticities, optionally with TransferFunction.</summary>
    BaselineChromaticity,

    /// <summary>Exif ColorSpace names the encoding.</summary>
    ExifColorSpace,

    /// <summary>The Software tag identifies a writer whose untagged output has a known encoding.</summary>
    ScannerSoftware,

    /// <summary>
    /// The scanner's own settings block declares the encoding gamma it applied — today the
    /// Flextight plist in tag 50457 (<see cref="FlextightMeta"/>). Conclusive: the decoder undoes
    /// exactly the declared curve, which is neither of the two-way Linear/sRGB answers.
    /// </summary>
    VendorGammaDeclaration,

    /// <summary>Nothing in the file said anything. The conventional default was applied.</summary>
    ConventionalDefault,
}

/// <summary>
/// The detector's answer for one file.
///
/// <para>
/// <see cref="CharacterizedSpace"/> is the good case: the file carried enough to name its exact
/// primaries, white point and curve, which beats the two-way Linear/sRGB question entirely. When
/// it is null the answer degrades to <see cref="Assumption"/>, which is then always Linear or sRGB.
/// </para>
/// </summary>
/// <param name="Assumption">Resolved roll fallback. Never Unspecified.</param>
/// <param name="CharacterizedSpace">Exact space when the file characterized itself, else null.</param>
/// <param name="Evidence">What the answer rests on.</param>
/// <param name="Diagnostic">One line, shown to the user and recorded in the decode recipe.</param>
public sealed record TiffInputDetection(
    TiffInputAssumption Assumption,
    ColorSpaceDef? CharacterizedSpace,
    TiffInputEvidence Evidence,
    string Diagnostic)
{
    /// <summary>
    /// True when the file told us. False means the statistically likely option was applied and the
    /// user must be told so, in a way they can correct after seeing the picture.
    /// </summary>
    public bool IsConclusive =>
        Evidence is not (TiffInputEvidence.ConventionalDefault or TiffInputEvidence.None);
}

/// <summary>
/// Resolves what an untagged TIFF's samples mean, from the file itself.
///
/// <para>
/// WHY THIS EXISTS. A usable embedded ICC always wins and never reaches here. What reaches here is
/// a TIFF with no profile — and the reflex is to ask the user "linear or sRGB?". That question is
/// unanswerable for most people: it is about the scanner's output settings at the moment of
/// scanning, which nobody remembers per roll, and it is asked before they have seen a single
/// pixel. Meanwhile TIFF 6.0 defines baseline tags whose entire purpose is to declare colorimetry
/// without an ICC, and Exif defines another. Reading those first turns most of those questions
/// into facts.
/// </para>
///
/// <para>
/// DELIBERATELY NOT HERE: pixel-histogram heuristics. Linear and gamma-encoded data do separate
/// cleanly on ordinary photographs, but this application's input is negative film — an orange-mask
/// density distribution that breaks the usual thresholds. A wrong guess is a 2.2-gamma error on
/// the input, which is worse than admitting we do not know. The conventional default plus a
/// visible, correctable notice is the honest end of the chain.
/// </para>
/// </summary>
public static class TiffInputDetector
{
    /// <summary>
    /// Exif ColorSpace (0xA001). 1 is the only value the specification assigns (sRGB); 2 is not
    /// standard but is what several writers emit for Adobe RGB. 0xFFFF means "uncalibrated", which
    /// is an explicit refusal to say and must NOT be read as any particular space.
    /// </summary>
    private const int ExifColorSpaceSrgb = 1;

    private const int ExifColorSpaceAdobeRgb = 2;

    /// <summary>
    /// Writers whose untagged output has a stable, documented encoding. Matched case-insensitively
    /// as a prefix of the Software tag. Kept deliberately short: a wrong entry here is a silent
    /// colour error, so a writer belongs on this list only when its untagged default is not in
    /// doubt. Anything absent falls through to the conventional default, which reaches the same
    /// answer but labels it honestly as a guess.
    /// </summary>
    private static readonly (string Prefix, TiffInputAssumption Assumption, string Note)[] KnownWriters =
    {
        // This application's own pre-ICC exports. Its scene-linear ones are float and are already
        // resolved by SampleFormat before reaching here.
        ("OpenRevelare", TiffInputAssumption.Srgb, "OpenRevelare 旧版非线性导出使用 sRGB 编码"),
        ("VueScan", TiffInputAssumption.Srgb, "VueScan 未嵌入 profile 时输出 sRGB"),
        ("SilverFast", TiffInputAssumption.Srgb, "SilverFast 未嵌入 profile 时输出 sRGB"),
        ("EPSON Scan", TiffInputAssumption.Srgb, "Epson Scan 未嵌入 profile 时输出 sRGB"),
        ("Nikon Scan", TiffInputAssumption.Srgb, "Nikon Scan 未嵌入 profile 时输出 sRGB"),
    };

    /// <summary>
    /// Candidate curves a TransferFunction table is matched against. A table matching none of them
    /// is not forced into the nearest one; see <see cref="MatchTransferFunction"/>.
    /// </summary>
    private static readonly (string Name, TransferFunction Transfer, double Gamma)[] CandidateCurves =
    {
        ("linear", TransferFunction.Linear, 1.0),
        ("sRGB piecewise", TransferFunction.SrgbPiecewise, 2.4),
        ("gamma 1.8", TransferFunction.Power, 1.8),
        ("gamma 2.2", TransferFunction.Power, 2.2),
        ("gamma 2.4", TransferFunction.Power, 2.4),
    };

    /// <summary>Largest per-sample deviation still accepted as a curve match.</summary>
    private const double CurveTolerance = 0.02;

    /// <summary>
    /// TIFF 6.0 states that without a TransferFunction the data is assumed to carry the NTSC-style
    /// ~2.2 power curve. That makes 2.2 a specification default rather than a guess, but only for
    /// a file that did characterize its primaries.
    /// </summary>
    private const double BaselineDefaultGamma = 2.2;

    /// <summary>
    /// Untagged consumer scans are overwhelmingly sRGB-encoded, so that is the fallback. It
    /// carries <see cref="TiffInputEvidence.ConventionalDefault"/> precisely so the caller can tell
    /// the user it was not read out of the file.
    /// </summary>
    public static TiffInputDetection ConventionalDefault { get; } = new(
        TiffInputAssumption.Srgb,
        CharacterizedSpace: null,
        TiffInputEvidence.ConventionalDefault,
        "文件未声明色彩空间，按未标注扫描件的惯例假设 sRGB");

    /// <summary>Inspects <paramref name="path"/> and resolves what its samples mean.</summary>
    public static TiffInputDetection Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using Tiff tif = Tiff.Open(path, "r");
            return tif is null ? ConventionalDefault : Detect(tif);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConventionalDefault;
        }
    }

    /// <summary>
    /// Inspects an already-open handle. This reads the Exif sub-directory, which moves the handle's
    /// current directory; it is restored to directory 0 before returning.
    /// </summary>
    internal static TiffInputDetection Detect(Tiff tif)
    {
        ArgumentNullException.ThrowIfNull(tif);

        if (IsFloatSampleFormat(tif))
        {
            return new TiffInputDetection(
                TiffInputAssumption.Linear,
                CharacterizedSpace: null,
                TiffInputEvidence.FloatSampleFormat,
                "SampleFormat 为 IEEE float：浮点样本是场景数据，按线性处理");
        }

        if (TryReadBaselineChromaticity(tif, out ColorSpaceDef baseline, out string baselineNote))
        {
            return new TiffInputDetection(
                baseline.Transfer == TransferFunction.Linear
                    ? TiffInputAssumption.Linear
                    : TiffInputAssumption.Srgb,
                baseline,
                TiffInputEvidence.BaselineChromaticity,
                baselineNote);
        }

        if (TryReadExifColorSpace(tif, out ColorSpaceDef exifSpace, out string exifNote))
        {
            return new TiffInputDetection(
                TiffInputAssumption.Srgb,
                exifSpace,
                TiffInputEvidence.ExifColorSpace,
                exifNote);
        }

        // Before the Software-tag table: a per-file declaration of the curve actually applied
        // outranks a per-writer assumption about what that writer usually does. Same precedence
        // TiffIO gives it at decode time, so what the notice says and what the pixels got agree.
        if (TryReadVendorGamma(tif, out string vendorNote))
        {
            return new TiffInputDetection(
                // Display-encoded, not linear — the nearest of the two classes. Never consulted for
                // the decode itself: TiffIO routes this evidence to the declared curve.
                TiffInputAssumption.Srgb,
                CharacterizedSpace: null,
                TiffInputEvidence.VendorGammaDeclaration,
                vendorNote);
        }

        if (TryMatchKnownWriter(tif, out TiffInputAssumption writerAssumption, out string writerNote))
        {
            return new TiffInputDetection(
                writerAssumption,
                CharacterizedSpace: null,
                TiffInputEvidence.ScannerSoftware,
                writerNote);
        }

        return ConventionalDefault;
    }

    /// <summary>
    /// The Flextight settings plist, read from the file behind <paramref name="tif"/>.
    ///
    /// This was the one declaration the detector did not know about: TiffIO honoured it, so a
    /// Flextight scan decoded correctly, while the detector fell through to the conventional
    /// default and the GUI then told the user the file had "no colour declaration" and offered
    /// Linear/sRGB — a choice the decode would go on to ignore. The notice and the decode have
    /// to read the same evidence.
    /// </summary>
    private static bool TryReadVendorGamma(Tiff tif, out string note)
    {
        note = string.Empty;
        string? path = tif.FileName();
        if (string.IsNullOrEmpty(path)) return false;
        FlextightMeta.Settings meta = FlextightMeta.Read(path);
        if (!meta.HasEncodingGamma) return false;
        note = $"Flextight 设置（tag {FlextightMeta.SettingsPlistTag}）声明编码 gamma "
             + $"{meta.Gamma!.Value.ToString("0.0#", CultureInfo.InvariantCulture)}"
             + (meta.ColorSpaceName is { Length: > 0 } name ? $"，设置名 \"{name}\"" : string.Empty)
             + "，按声明还原为线性";
        return true;
    }

    private static bool IsFloatSampleFormat(Tiff tif)
    {
        FieldValue[] field = tif.GetField(TiffTag.SAMPLEFORMAT);
        return field is { Length: > 0 } && (SampleFormat)field[0].ToInt() == SampleFormat.IEEEFP;
    }

    /// <summary>
    /// Reads TIFF 6.0 WhitePoint (318) + PrimaryChromaticities (319), plus TransferFunction (301)
    /// when present. Those three tags exist for exactly this situation and describe the file more
    /// precisely than any Linear/sRGB choice could.
    /// </summary>
    private static bool TryReadBaselineChromaticity(
        Tiff tif,
        out ColorSpaceDef space,
        out string note)
    {
        space = default;
        note = string.Empty;

        float[]? white = ReadFloats(tif, TiffTag.WHITEPOINT, 2);
        float[]? primaries = ReadFloats(tif, TiffTag.PRIMARYCHROMATICITIES, 6);
        if (white is null || primaries is null) return false;
        if (!IsUsableChromaticity(white[0], white[1])) return false;
        for (int i = 0; i < 6; i += 2)
            if (!IsUsableChromaticity(primaries[i], primaries[i + 1])) return false;

        (TransferFunction transfer, double gamma, string curveNote) = MatchTransferFunction(tif);

        var candidate = new ColorSpaceDef(
            "TIFF-baseline",
            (primaries[0], primaries[1]),
            (primaries[2], primaries[3]),
            (primaries[4], primaries[5]),
            (white[0], white[1]),
            transfer,
            gamma);

        // Collinear primaries would throw inside the matrix construction much later. Reject them
        // here so an odd file falls through to the next tier instead of failing the whole load.
        try
        {
            _ = candidate.ToXyz();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        space = candidate;
        note = $"文件自带 TIFF 6.0 色度标签（白点 {white[0]:0.####},{white[1]:0.####}），{curveNote}";
        return true;
    }

    /// <summary>
    /// Matches the TransferFunction table against the curves the pipeline can express. An
    /// unrecognized table is not forced into the nearest candidate: the primaries are still exact,
    /// so the honest result is the specification's default curve plus a note that says the table
    /// was not recognized.
    /// </summary>
    private static (TransferFunction Transfer, double Gamma, string Note) MatchTransferFunction(Tiff tif)
    {
        short[]? table = ReadTransferFunctionTable(tif);
        if (table is null || table.Length < 2)
        {
            return (TransferFunction.Power, BaselineDefaultGamma,
                "无 TransferFunction，按 TIFF 6.0 默认 gamma 2.2");
        }

        int count = table.Length;
        const int probeCount = 17;
        foreach ((string name, TransferFunction transfer, double gamma) in CandidateCurves)
        {
            var probe = new ColorSpaceDef(
                "probe", (0.64, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290), transfer, gamma);
            bool matches = true;
            for (int p = 0; p < probeCount && matches; p++)
            {
                double encoded = p / (double)(probeCount - 1);
                int index = (int)Math.Round(encoded * (count - 1));
                // TIFF stores the table as fractions of 65535, mapping stored code -> linear light.
                double actualLinear = (ushort)table[index] / 65535.0;
                double expectedLinear = DecodeSample(encoded, probe);
                matches = Math.Abs(actualLinear - expectedLinear) <= CurveTolerance;
            }
            if (matches) return (transfer, gamma, $"TransferFunction 匹配 {name}");
        }

        return (TransferFunction.Power, BaselineDefaultGamma,
            "TransferFunction 无法匹配已知曲线，按 TIFF 6.0 默认 gamma 2.2");
    }

    /// <summary>Encoded -> linear for one sample, using the space's own declared curve.</summary>
    private static double DecodeSample(double encoded, ColorSpaceDef space)
    {
        float[] one = { (float)encoded, (float)encoded, (float)encoded };
        OutputRender.Decode(one, space);
        return one[0];
    }

    private static short[]? ReadTransferFunctionTable(Tiff tif)
    {
        FieldValue[] field = tif.GetField(TiffTag.TRANSFERFUNCTION);
        if (field is not { Length: > 0 }) return null;
        try
        {
            return field[0].ToShortArray();
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryReadExifColorSpace(Tiff tif, out ColorSpaceDef space, out string note)
    {
        space = default;
        note = string.Empty;

        FieldValue[] offsetField = tif.GetField(TiffTag.EXIFIFD);
        if (offsetField is not { Length: > 0 }) return false;

        int colorSpace;
        try
        {
            if (!tif.ReadEXIFDirectory(offsetField[0].ToLong())) return false;
            FieldValue[] field = tif.GetField(TiffTag.EXIF_COLORSPACE);
            if (field is not { Length: > 0 }) return false;
            colorSpace = field[0].ToInt();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            // ReadEXIFDirectory leaves the handle pointing at the Exif sub-directory.
            _ = tif.SetDirectory(0);
        }

        switch (colorSpace)
        {
            case ExifColorSpaceSrgb:
                space = ColorSpaces.Srgb;
                note = "Exif ColorSpace = 1（sRGB）";
                return true;
            case ExifColorSpaceAdobeRgb:
                space = ColorSpaces.AdobeRgb;
                note = "Exif ColorSpace = 2（Adobe RGB）";
                return true;
            default:
                // 0xFFFF is "uncalibrated" — an explicit refusal to name a space, so it must not be
                // turned into one. Fall through to the next tier.
                return false;
        }
    }

    private static bool TryMatchKnownWriter(
        Tiff tif,
        out TiffInputAssumption assumption,
        out string note)
    {
        assumption = TiffInputAssumption.Unspecified;
        note = string.Empty;

        FieldValue[] field = tif.GetField(TiffTag.SOFTWARE);
        if (field is not { Length: > 0 }) return false;
        string software = field[0].ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(software)) return false;

        foreach ((string prefix, TiffInputAssumption known, string writerNote) in KnownWriters)
        {
            if (!software.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            assumption = known;
            note = $"Software = \"{software.Trim()}\"：{writerNote}";
            return true;
        }
        return false;
    }

    private static float[]? ReadFloats(Tiff tif, TiffTag tag, int expected)
    {
        FieldValue[] field = tif.GetField(tag);
        if (field is not { Length: > 0 }) return null;
        try
        {
            float[] values = field[0].ToFloatArray();
            return values is not null && values.Length >= expected ? values : null;
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A chromaticity must be finite with a strictly positive y to build a matrix from.</summary>
    private static bool IsUsableChromaticity(float x, float y) =>
        float.IsFinite(x) && float.IsFinite(y) && y > 1e-6f && x > -1e-6f && x < 1.5f && y < 1.5f;
}
