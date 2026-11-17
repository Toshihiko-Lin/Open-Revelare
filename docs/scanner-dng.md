# Epson VueScan scanner DNG

爱普生扫描仪使用 VueScan 输出的未压缩 16 位线性 RGB/RGBI DNG，可直接导入。程序自动选择完整扫描图，保留原始 RGB；第四个红外通道不参与颜色计算，RGBI 文件可在除尘工具栏使用「红外除尘」。VueScan 中建议启用 **Raw file** 和 **Raw DNG format**、选择 **48 bit RGB**，关闭 **Raw save film**，将负片反转留给 OpenRevelare。多格扫描图可使用现有分割功能。当前已用 GT-X980 的公开原始文件验证；SilverFast DNG 尚未验证。其他编码请先导出 48 位 RGB 原始 TIFF。

The initial scanner DNG path supports Epson scans written by VueScan with uncompressed,
unsigned 16-bit, chunky linear RGB or RGBI samples. It selects the full-resolution image
from IFD0 or its direct SubIFDs, never the reduced RGB preview. RGBI's fourth channel is
excluded from visible colour calculations; RGBI files support the Infrared dust removal toolbar action.
Camera DNGs continue to use LibRaw.

## 中文导入指引

1. 在 VueScan 中启用 **Output → Raw file** 和 **Raw DNG format**，使用每通道 16 位的
   **48 bit RGB**（或 RGBI），关闭 **Raw save film**，保留负片供 OpenRevelare 反转。
2. 将 DNG 拖入导入窗口；整条底片可勾选「扫描件多格分割」。程序自动选择完整扫描图。
3. 当前支持未压缩、交错存储的爱普生 VueScan 线性 DNG。RGBI 的第四个红外通道不参与
   颜色计算，RGBI 文件可在除尘工具栏使用「红外除尘」。输入按扫描仪原始线性 RGB 保留，不宣称完成设备色彩标定。
4. 遇到暂不支持的编码或校正标签时，按提示从 VueScan 输出 48 位 RGB 原始 TIFF 再导入。
   SilverFast DNG 尚未验证。

## Scanning and import

In VueScan, enable **Output → Raw file** and **Raw DNG format**, select **48 bit RGB**
(or RGBI), and disable **Raw save film** so film inversion is performed in OpenRevelare.
Import the DNG like a TIFF. Multi-frame scans can use the existing strip splitting option.
The decoder keeps the original scanner RGB and treats the samples as linear. Scanner
primaries remain explicitly uncharacterized, following the existing linear scanner TIFF
admission policy; this path does not apply the DNG camera matrix or a camera white balance.

Compression, tiles, separate planes, other bit depths, non-default orientation and DNG
linearization/level/crop/opcode corrections are outside this initial path. These scans
produce an explanatory error rather than decoding their preview. Export a 48-bit RGB raw
TIFF from VueScan as a fallback. SilverFast DNGs have not been validated and are not routed
through this scanner path.

## Evidence and reproduction

- [VueScan official output manual](https://www.hamrick.com/vuescan/html/vuesc33.htm):
  Raw DNG output and gamma 1.0 for 16-bit raw samples.
- [Public Epson GT-X980 sample and author report](https://community.adobe.com/questions-675/white-balance-eyedropper-maps-neutral-gray-to-magenta-on-4-channel-rgbi-dng-files-vuescan-film-scans-1650378):
  attachment `raw0010.dng`, VueScan 9.8.16. This is a user-contributed file, not a vendor test image.

The downloaded sample has SHA-256
`dc4ffb09b0fe7115e545109e3e99a492ac57933cc150f93fba9a01f4fd3151d5`.
IFD0 contains a 206×211 RGB8 preview; the SubIFD at offset 8718344 contains
1032×1056 RGBI16, PhotometricInterpretation=34892, Compression=1. Independent
Python/tifffile inspection reports pixel (516,528) as RGBI=(31121,11803,3387,56256).
The integration test checks the selected dimensions and normalized RGB at that pixel.

The public photograph is not redistributed in the repository. To run its optional integration
check, download the attachment and set `OPENREVELARE_SCANNER_DNG_SAMPLE` to its absolute path
before running `dotnet test src/OpenRevelare.Tests --filter FullyQualifiedName~ScannerDngTests`.
Generated fixtures cover preview selection, RGB and RGBI, managed linear admission, ROI reads,
unsupported layouts and camera-DNG routing without requiring a network download.

## 红外除尘 / Infrared dust removal

在除尘工具栏点击「红外除尘」，检测后审核蒙版，可补画、擦除并对比，点击应用后保存到工程。使用局部红外透射衰减检测并扩展一像素边缘，适合小灰尘和划痕；大面积缺陷可能漏检。传统银盐黑白胶片不适用。当前仅支持上述已验证编码的 Epson VueScan RGBI DNG，不把普通 RGBA TIFF 的透明通道作为红外。

Select Infrared dust removal, review and edit the mask, then apply. The mask is stored in the project and used for previews and exports. Local IR absorption detection targets small defects; broad defects may be missed. Traditional silver-based black-and-white film is unsuitable. Ordinary RGBA TIFF alpha is never interpreted as infrared.
