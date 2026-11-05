using System.Buffers.Binary;
using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;

namespace OpenRevelare.Core;

/// <summary>
/// Estimated lens correction parameters extracted from RAW metadata or camera/lens heuristics.
/// </summary>
public sealed record LensCorrectionEstimate(
    string LensModel,
    double FocalLengthMm,
    double FNumber,
    double DistortionK1,
    double VignetteAmount,
    double VignetteFalloff,
    string SourceDescription)
{
    public string Diagnostic { get; init; } = "";
    public EmbeddedLensCalibration? Native { get; init; }
    public bool HasDistortion { get; init; } = true;
    public bool HasVignette { get; init; } = true;
    public bool IsHeuristic { get; init; }

    public string Summary => string.IsNullOrEmpty(LensModel)
        ? SourceDescription
        : $"{LensModel} ({SourceDescription})";
}

/// <summary>
/// Probes image metadata (Sony firmware correction tags in the maker note or the Exif SubIFD,
/// DNG OpcodeList3, standard EXIF lens model) to automatically determine radial distortion (k1)
/// and peripheral vignetting falloff. Nothing here keys on the camera body: a file is handled by
/// whichever of those three sources it actually carries.
/// </summary>
public static class LensMetadata
{
    /// <summary>EXIF LensModel. 0xA431 is BodySerialNumber and must never stand in for it.</summary>
    private const int TagLensModel = 0xa434;

    /// <summary>EXIF FocalLength.</summary>
    private const int TagFocalLength = 0x920a;

    /// <summary>EXIF FNumber.</summary>
    private const int TagFNumber = 0x829d;

    /// <summary>Sony maker note DistortionCorrParams: 16 radial knots after a count.</summary>
    private const int TagSonyDistortionCorrParams = 0x7037;

    /// <summary>Sony maker note VignettingCorrParams: 16 radial knots after a count.</summary>
    private const int TagSonyVignettingCorrParams = 0x7032;

    /// <summary>DNG OpcodeList3.</summary>
    private const int TagDngOpcodeList3 = 0xc74e;

    /// <summary>DNG opcode 1: WarpRectilinear (Adobe DNG SDK, dng_opcodes.h).</summary>
    private const uint DngOpcodeWarpRectilinear = 1;

    /// <summary>DNG opcode 3: FixVignetteRadial. Opcode 4 is FixBadPixelsConstant, not a warp.</summary>
    private const uint DngOpcodeFixVignetteRadial = 3;

    /// <summary>Radial coefficients per plane in a WarpRectilinear payload.</summary>
    private const int WarpCoefficientsPerPlane = 6;


    /// <summary>
    /// Reads lens metadata and embedded corrections from the specified image file.
    /// Returns null if file does not exist, metadata cannot be read, or no lens information is available.
    /// </summary>
    public static LensCorrectionEstimate? EstimateCorrections(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var estimate = EstimateCorrections(directories);
            if (estimate is not { HasDistortion: true }) return estimate;
            // Prefer active EXIF dimensions, then the primary image IFD. Portrait and landscape agree.
            foreach (var d in directories.OfType<ExifSubIfdDirectory>().Cast<MetadataExtractor.Directory>()
                .Concat(directories.OfType<ExifIfd0Directory>()))
            {
                int wx = d is ExifSubIfdDirectory ? 0xa002 : 0x0100;
                int hy = d is ExifSubIfdDirectory ? 0xa003 : 0x0101;
                if (d.TryGetDouble(wx, out var w) && d.TryGetDouble(hy, out var h) && w > 1 && h > 1)
                {
                    double aspect = (Math.Max(w, h) - 1) / (Math.Min(w, h) - 1);
                    return estimate with { DistortionK1 = estimate.DistortionK1 * 3.25 / (1 + aspect * aspect) };
                }
            }
            return estimate;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads lens metadata and embedded corrections from already-extracted metadata directories.
    /// </summary>
    public static LensCorrectionEstimate? EstimateCorrections(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        if (directories is null || directories.Count == 0) return null;

        var subIfds = directories.OfType<ExifSubIfdDirectory>().ToList();

        // 0xA431 is BodySerialNumber, not a lens name: using it as a fallback fed serial numbers
        // into the preset lookup (never matching) and could overwrite a manual correction with the
        // "no built-in curve" estimate of k1 = 0.
        string lensModel = subIfds.Select(d => d.GetString(TagLensModel))
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
            ?? "";

        double focalLength = 0;
        foreach (var d in subIfds)
        {
            if (d.TryGetDouble(TagFocalLength, out var f) && f > 0)
            {
                focalLength = f;
                break;
            }
        }

        double fNumber = 0;
        foreach (var d in subIfds)
        {
            if (d.TryGetDouble(TagFNumber, out var fn) && fn > 0)
            {
                fNumber = fn;
                break;
            }
        }

        // 1. Sony firmware correction parameters:
        //    0x7037: DistortionCorrParams (16-bit signed shorts)
        //    0x7032: VignettingCorrParams (16-bit signed shorts)
        short[]? sonyDist = ReadSonyCorrectionKnots(directories, TagSonyDistortionCorrParams);
        short[]? sonyVign = ReadSonyCorrectionKnots(directories, TagSonyVignettingCorrParams);

        if (ValidKnots(sonyDist) || ValidKnots(sonyVign))
        {
            double k1 = 0;
            if (sonyDist is { Length: >= 17 })
            {
                int n = sonyDist[0];
                if (n >= 2 && n < sonyDist.Length)
                {
                    // Least-squares fit to radial quadratic: scale = 1 + k1 * 3.25 * r^2
                    // Sony knot: g(r) = 1 + knot / 16384.0
                    double num = 0, den = 0;
                    for (int i = 1; i <= n; i++)
                    {
                        double r = (double)(i - 1) / (n - 1);
                        double x = 3.25 * (r * r);
                        double y = (double)sonyDist[i] / 16384.0;
                        num += x * y;
                        den += x * x;
                    }
                    if (den > 0) k1 = Math.Round(num / den, 4);
                }
            }

            double vignAmount = 0;
            double vignFalloff = 2.5;
            if (sonyVign is { Length: >= 17 })
            {
                int n = sonyVign[0];
                if (n >= 2 && n < sonyVign.Length)
                {
                    short cornerVal = sonyVign[n];
                    vignAmount = Math.Round(Math.Pow(2.0, (double)cornerVal / 16384.0) - 1.0, 3);
                }
            }

            return new LensCorrectionEstimate(
                LensModel: lensModel,
                FocalLengthMm: focalLength,
                FNumber: fNumber,
                DistortionK1: k1,
                VignetteAmount: vignAmount,
                VignetteFalloff: vignFalloff,
                SourceDescription: "Sony RAW 固件参数")
            { HasDistortion = ValidKnots(sonyDist), HasVignette = ValidKnots(sonyVign),
                Native = new EmbeddedLensCalibration("Sony RAW 固件曲线",
                    DistortionKnots: ValidKnots(sonyDist) ? sonyDist!.Skip(1).Take(sonyDist![0]).Select(x => 1+x/16384.0).ToArray() : null,
                    VignetteKnots: ValidKnots(sonyVign) ? sonyVign!.Skip(1).Take(sonyVign![0]).Select(x => Math.Pow(2,x/16384.0)).ToArray() : null) };
        }

        // 2. DNG OpcodeList3 (Tag 51022 / 0xC74E)
        foreach (var dir in directories)
        {
            if (dir.ContainsTag(TagDngOpcodeList3))
            {
                var opcodeBytes = dir.GetByteArray(TagDngOpcodeList3);
                if (opcodeBytes != null && EmbeddedLensCalibration.TryParseDng(opcodeBytes, out var native))
                {
                    return new LensCorrectionEstimate(
                        LensModel: lensModel,
                        FocalLengthMm: focalLength,
                        FNumber: fNumber,
                        DistortionK1: native!.Warp is {} w ? w[0][1]/3.25 : 0,
                        VignetteAmount: native!.Gain(1)-1,
                        VignetteFalloff: 2.5,
                        SourceDescription: "DNG OpcodeList3（完整曲线）")
                    { HasDistortion = native!.HasDistortion, HasVignette = native.HasVignette, Native = native };
                }
            }
        }

        // 4. If lens model is detected but no specific profile exists
        if (!string.IsNullOrEmpty(lensModel))
        {
            return new LensCorrectionEstimate(
                LensModel: lensModel,
                FocalLengthMm: focalLength,
                FNumber: fNumber,
                DistortionK1: 0,
                VignetteAmount: 0,
                VignetteFalloff: 2.5,
                SourceDescription: "未内置校正曲线") { HasDistortion = false, HasVignette = false };
        }

        return null;
    }

    /// <summary>
    /// Parses DNG OpcodeList3 binary stream for WarpRectilinear and FixVignetteRadial.
    /// DNG opcodes are stored big-endian.
    /// </summary>
    /// <remarks>
    /// Opcode ids follow the Adobe DNG SDK (<c>dng_opcodes.h</c>): 1 = WarpRectilinear,
    /// 3 = FixVignetteRadial, 4 = FixBadPixelsConstant. Treating 4 as a radial warp (as an
    /// earlier revision did) meant no conforming DNG was ever read.
    /// </remarks>
    public static bool TryParseDngOpcodeList3(byte[] data, out double distortionK1, out double vignetteAmount, out double vignetteFalloff)
    {
        distortionK1 = 0; vignetteAmount = 0; vignetteFalloff = 2.5;
        if (!EmbeddedLensCalibration.TryParseDng(data, out var profile)) return false;
        if (profile!.Warp is {} warp) distortionK1 = Math.Round(warp[0][1]/3.25,6);
        vignetteAmount = Math.Round(profile.Gain(1)-1,3);
        return true;
    }

    private static bool ValidKnots(short[]? knots) => knots is { Length: >= 17 }
        && knots[0] >= 2 && knots[0] < knots.Length;

    private static bool Centered(ReadOnlySpan<byte> center) =>
        BinaryPrimitives.ReadDoubleBigEndian(center) == 0.5
        && BinaryPrimitives.ReadDoubleBigEndian(center.Slice(8)) == 0.5;

    /// <summary>
    /// Reads a Sony correction block from the Sony maker note directory, falling back to the
    /// Exif SubIFD (some DNG converters copy the block there).
    /// </summary>
    private static short[]? ReadSonyCorrectionKnots(
        IReadOnlyList<MetadataExtractor.Directory> directories,
        int tagId)
    {
        foreach (var makerNote in directories.OfType<SonyType1MakernoteDirectory>())
        {
            if (TryGetInt16Knots(makerNote, tagId, out var knots)) return knots;
        }

        foreach (var subIfd in directories.OfType<ExifSubIfdDirectory>())
        {
            if (TryGetInt16Knots(subIfd, tagId, out var knots)) return knots;
        }

        return null;
    }

    /// <summary>
    /// Reads a 16-bit Sony correction block as signed values. The CLR value depends on the type
    /// written in the file: TIFF SHORT (3) surfaces as <c>ushort[]</c>, SSHORT (8) as
    /// <c>short[]</c> (verified against MetadataExtractor 2.9.3), so both are accepted and the
    /// unsigned form keeps its two's-complement meaning.
    /// </summary>
    private static bool TryGetInt16Knots(MetadataExtractor.Directory directory, int tagId, out short[] knots)
    {
        switch (directory.GetObject(tagId))
        {
            case short[] signedValues:
                knots = signedValues;
                return knots.Length > 0;

            case ushort[] unsignedValues:
                knots = new short[unsignedValues.Length];
                for (int i = 0; i < unsignedValues.Length; i++)
                    knots[i] = unchecked((short)unsignedValues[i]);
                return knots.Length > 0;

            default:
                knots = [];
                return false;
        }
    }

}
