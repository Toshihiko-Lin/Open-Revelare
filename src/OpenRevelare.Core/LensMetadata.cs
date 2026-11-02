using System.Buffers.Binary;
using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

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
    public string Summary => string.IsNullOrEmpty(LensModel)
        ? SourceDescription
        : $"{LensModel} ({SourceDescription})";
}

/// <summary>
/// Probes RAW image metadata (Sony SubIFD/MakerNotes, DNG OpcodeList3, Fuji, standard EXIF)
/// to automatically determine radial distortion (k1) and peripheral vignetting falloff.
/// </summary>
public static class LensMetadata
{
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
            return EstimateCorrections(directories);
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
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

        string lensModel = subIfds.Select(d => d.GetString(0xa434)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
            ?? subIfds.Select(d => d.GetString(0xa431)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim()
            ?? "";

        double focalLength = 0;
        foreach (var d in subIfds)
        {
            if (d.TryGetDouble(0x920a, out var f) && f > 0)
            {
                focalLength = f;
                break;
            }
        }

        double fNumber = 0;
        foreach (var d in subIfds)
        {
            if (d.TryGetDouble(0x829d, out var fn) && fn > 0)
            {
                fNumber = fn;
                break;
            }
        }

        // 1. Sony SubIFD MakerNote tags:
        //    0x7037: DistortionCorrParams (16-bit signed shorts)
        //    0x7032: VignettingCorrParams (16-bit signed shorts)
        short[]? sonyDist = null;
        short[]? sonyVign = null;
        foreach (var d in subIfds)
        {
            if (sonyDist is null && d.GetObject(0x7037) is short[] sDist)
                sonyDist = sDist;
            if (sonyVign is null && d.GetObject(0x7032) is short[] sVign)
                sonyVign = sVign;
        }

        if (sonyDist is { Length: >= 17 } || sonyVign is { Length: >= 17 })
        {
            double k1 = 0;
            if (sonyDist is { Length: >= 17 })
            {
                int n = sonyDist[0];
                if (n > 0 && n < sonyDist.Length)
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
                if (n > 0 && n < sonyVign.Length)
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
                SourceDescription: "Sony RAW 固件参数");
        }

        // 2. DNG OpcodeList3 (Tag 51022 / 0xC74E)
        foreach (var dir in directories)
        {
            if (dir.ContainsTag(0xc74e))
            {
                var opcodeBytes = dir.GetByteArray(0xc74e);
                if (opcodeBytes != null && TryParseDngOpcodeList3(opcodeBytes, out var dngK1, out var dngVign, out var dngFalloff))
                {
                    return new LensCorrectionEstimate(
                        LensModel: lensModel,
                        FocalLengthMm: focalLength,
                        FNumber: fNumber,
                        DistortionK1: dngK1,
                        VignetteAmount: dngVign,
                        VignetteFalloff: dngFalloff,
                        SourceDescription: "DNG OpcodeList3");
                }
            }
        }

        // 3. Known Lens Database / Heuristics
        if (TryLookupKnownLens(lensModel, focalLength, fNumber, out var k1Est, out var vignEst, out var falloffEst, out var profileName))
        {
            return new LensCorrectionEstimate(
                LensModel: lensModel,
                FocalLengthMm: focalLength,
                FNumber: fNumber,
                DistortionK1: k1Est,
                VignetteAmount: vignEst,
                VignetteFalloff: falloffEst,
                SourceDescription: profileName);
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
                SourceDescription: "未内置校正曲线");
        }

        return null;
    }

    /// <summary>
    /// Parses DNG OpcodeList3 binary stream for WarpRectilinear and FixVignetteRadial.
    /// DNG opcodes are stored big-endian.
    /// </summary>
    public static bool TryParseDngOpcodeList3(byte[] data, out double distortionK1, out double vignetteAmount, out double vignetteFalloff)
    {
        distortionK1 = 0;
        vignetteAmount = 0;
        vignetteFalloff = 2.5;

        if (data.Length < 4) return false;

        try
        {
            var span = data.AsSpan();
            uint count = BinaryPrimitives.ReadUInt32BigEndian(span);
            int offset = 4;

            for (uint i = 0; i < count && offset + 16 <= data.Length; i++)
            {
                uint opcodeId = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
                // uint version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 4, 4));
                // uint flags = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 8, 4));
                uint paramBytes = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 12, 4));
                offset += 16;

                if (offset + paramBytes > data.Length) break;

                var paramSpan = span.Slice(offset, (int)paramBytes);
                if (opcodeId == 4 && paramBytes >= 32) // WarpRectilinear
                {
                    // k0, k1, k2, k3 (8-byte doubles, big-endian)
                    // double k0 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(0, 8));
                    double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(8, 8));
                    // OpenRevelare's k1 is normalized by the short frame radius squared (aspect 3:2 => corner r^2 = 3.25)
                    distortionK1 = Math.Round(k1 / 3.25, 4);
                }
                else if (opcodeId == 3 && paramBytes >= 40) // FixVignetteRadial
                {
                    // k0, k1, k2, k3, k4 (8-byte doubles, big-endian)
                    double k0 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(0, 8));
                    double k1 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(8, 8));
                    double k2 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(16, 8));
                    double k3 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(24, 8));
                    double k4 = BinaryPrimitives.ReadDoubleBigEndian(paramSpan.Slice(32, 8));
                    double totalGain = k0 + k1 + k2 + k3 + k4;
                    vignetteAmount = Math.Round(totalGain, 3);
                    vignetteFalloff = 2.5;
                }

                offset += (int)paramBytes;
            }

            return distortionK1 != 0 || vignetteAmount != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Known reproduction and macro lens profile estimates.
    /// </summary>
    public static bool TryLookupKnownLens(
        string lensModel,
        double focalLength,
        double fNumber,
        out double distortionK1,
        out double vignetteAmount,
        out double vignetteFalloff,
        out string profileName)
    {
        distortionK1 = 0;
        vignetteAmount = 0;
        vignetteFalloff = 2.5;
        profileName = "";

        if (string.IsNullOrWhiteSpace(lensModel)) return false;

        string norm = lensModel.ToUpperInvariant();

        // Sony FE 70-200mm F4 Macro G OSS II
        if (norm.Contains("70-200") && norm.Contains("MACRO"))
        {
            double t = focalLength > 0 ? Math.Clamp((focalLength - 70.0) / 130.0, 0.0, 1.0) : 0.5;
            distortionK1 = Math.Round(0.002 + 0.016 * t, 4);
            vignetteAmount = fNumber >= 8 ? 0.168 : 0.22;
            profileName = "镜头预设 (Sony 70-200 F4 G II Macro)";
            return true;
        }

        // Sony FE 90mm F2.8 Macro G OSS
        if (norm.Contains("90MM") && (norm.Contains("MACRO") || norm.Contains("SEL90M28G")))
        {
            distortionK1 = 0.000;
            vignetteAmount = fNumber >= 8 ? 0.12 : 0.18;
            profileName = "镜头预设 (Sony 90mm F2.8 Macro)";
            return true;
        }

        // Sony FE 50mm F2.8 Macro
        if (norm.Contains("50MM") && norm.Contains("MACRO") && norm.Contains("SONY"))
        {
            distortionK1 = 0.001;
            vignetteAmount = fNumber >= 8 ? 0.14 : 0.20;
            profileName = "镜头预设 (Sony 50mm F2.8 Macro)";
            return true;
        }

        // Sigma 105mm F2.8 DG DN Macro | Art
        if (norm.Contains("105MM") && norm.Contains("MACRO") && (norm.Contains("SIGMA") || norm.Contains("DG DN")))
        {
            distortionK1 = 0.002;
            vignetteAmount = fNumber >= 8 ? 0.15 : 0.20;
            profileName = "镜头预设 (Sigma 105mm F2.8 DG DN Macro)";
            return true;
        }

        // Sigma 70mm F2.8 DG Macro | Art
        if (norm.Contains("70MM") && norm.Contains("MACRO") && norm.Contains("SIGMA"))
        {
            distortionK1 = 0.000;
            vignetteAmount = fNumber >= 8 ? 0.14 : 0.19;
            profileName = "镜头预设 (Sigma 70mm F2.8 Macro)";
            return true;
        }

        // Canon RF 100mm F2.8 L MACRO IS USM
        if (norm.Contains("100MM") && norm.Contains("MACRO") && norm.Contains("RF"))
        {
            distortionK1 = -0.003;
            vignetteAmount = fNumber >= 8 ? 0.14 : 0.18;
            profileName = "镜头预设 (Canon RF 100mm F2.8 L Macro)";
            return true;
        }

        // Canon EF 100mm f/2.8L Macro IS USM
        if (norm.Contains("100MM") && norm.Contains("MACRO") && norm.Contains("EF"))
        {
            distortionK1 = -0.002;
            vignetteAmount = fNumber >= 8 ? 0.15 : 0.20;
            profileName = "镜头预设 (Canon EF 100mm F2.8 L Macro)";
            return true;
        }

        // Nikon NIKKOR Z MC 105mm f/2.8 VR S
        if (norm.Contains("105MM") && norm.Contains("MC") && norm.Contains("NIKKOR"))
        {
            distortionK1 = 0.001;
            vignetteAmount = fNumber >= 8 ? 0.12 : 0.17;
            profileName = "镜头预设 (Nikkor Z MC 105mm F2.8)";
            return true;
        }

        // Nikon AF-S Micro Nikkor 60mm f/2.8G ED
        if (norm.Contains("60MM") && (norm.Contains("MICRO") || norm.Contains("NIKKOR")))
        {
            distortionK1 = 0.001;
            vignetteAmount = fNumber >= 8 ? 0.15 : 0.22;
            profileName = "镜头预设 (Micro-Nikkor 60mm F2.8G)";
            return true;
        }

        return false;
    }
}
