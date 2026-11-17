using BitMiracle.LibTiff.Classic;

namespace OpenRevelare.Core;

/// <summary>
/// VueScan's Epson linear scans are native scanner RGB (optionally RGBI), not camera mosaics.
/// IFD0 may be an 8-bit preview; always select the non-reduced linear image. Colour admission
/// deliberately follows the scanner TIFF policy: do not pretend native scanner RGB is sRGB,
/// or feed the infrared plane through the DNG's four-plane camera colour matrix.
/// </summary>
public static class ScannerDng
{
    public static bool IsScannerDng(string path)
    {
        if (!Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using Tiff? tif = Tiff.Open(path, "r");
            return tif is not null && IsScanner(tif);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static bool IsScanner(Tiff tif) =>
        tif.GetField((TiffTag)50706) is not null &&
        (tif.GetField(TiffTag.MAKE)?[0].ToString() ?? "").Trim().Equals("Epson", StringComparison.OrdinalIgnoreCase) &&
        (tif.GetField(TiffTag.SOFTWARE)?[0].ToString() ?? "").StartsWith("VueScan", StringComparison.OrdinalIgnoreCase);

    internal static void SelectImage(Tiff tif)
    {
        if (!IsScanner(tif)) return;
        RejectCorrections(tif);
        var offsets = tif.GetField(TiffTag.SUBIFD)?[1].TolongArray() ?? Array.Empty<long>();
        if (IsFullImage(tif)) { Validate(tif); return; }
        // Bound traversal and inspect only explicitly declared direct SubIFDs. A broken file
        // must fail rather than fall back to its attractive but low-resolution preview.
        foreach (long offset in offsets.Take(64))
            if (tif.SetSubDirectory(offset) && IsFullImage(tif)) { Validate(tif); return; }
        throw new NotSupportedException(CoreText.T("此扫描仪 DNG 未找到受支持的完整线性 RGB 图像。请用 VueScan 输出 48 位 RGB 原始 TIFF 后导入。"));
    }

    private static bool IsFullImage(Tiff tif) =>
        (tif.GetField(TiffTag.SUBFILETYPE)?[0].ToInt() ?? 0) == 0 &&
        (tif.GetField(TiffTag.PHOTOMETRIC)?[0].ToInt() ?? 0) == 34892 &&
        (tif.GetField(TiffTag.SAMPLESPERPIXEL)?[0].ToInt() ?? 0) is 3 or 4;

    private static bool HasCorrections(Tiff tif) =>
        new[] { 50710, 50711, 50712, 50714, 50715, 50716, 50717, 50719, 50720,
                50829, 51008, 51009, 51022 }
            .Any(tag => tif.GetField((TiffTag)tag) is not null);

    private static void RejectCorrections(Tiff tif)
    {
        if (HasCorrections(tif) || tif.GetField(TiffTag.ICCPROFILE) is not null ||
            (tif.GetField(TiffTag.ORIENTATION)?[0].ToInt() ?? 1) != 1)
            throw new NotSupportedException(CoreText.T("此扫描仪 DNG 的编码或校正标签暂不支持。请用 VueScan 输出未压缩的 48 位 RGB 原始 TIFF 后导入。"));
    }

    private static void Validate(Tiff tif)
    {
        // This initial path is intentionally bounded to the layout verified on GT-X980.
        // Other DNG encodings need their own decoder, never a silent TIFF approximation.
        if ((tif.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 0) != 16 ||
            (tif.GetField(TiffTag.PLANARCONFIG)?[0].ToInt() ?? 1) != 1 ||
            (tif.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? 1) != 1 ||
            tif.IsTiled() ||
            (tif.GetField(TiffTag.ORIENTATION)?[0].ToInt() ?? 1) != 1 ||
            (tif.GetField(TiffTag.SAMPLEFORMAT)?[0].ToInt() ?? 1) != 1 ||
            HasCorrections(tif))
            throw new NotSupportedException(CoreText.T("此扫描仪 DNG 的编码或校正标签暂不支持。请用 VueScan 输出未压缩的 48 位 RGB 原始 TIFF 后导入。"));
    }
}
