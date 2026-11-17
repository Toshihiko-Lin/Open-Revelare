# Scanner DNG import

扫描 DNG 不限扫描仪品牌。VueScan、SilverFast 及其他途径输出的兼容文件可直接导入；按完整图像结构选取图像，跳过缩略预览。真实扫描样本目前验证过 Epson GT-X980；其他来源由生成的结构测试覆盖，仍需设备样本验证。

## 文件要求

- 支持未压缩、每通道 16 位无符号整数、通道交错存储的完整 RGB / LinearRaw 图像（3 或 4 通道）。
- 检查根 IFD、直接 SubIFD 和主 IFD 链；每次遍历最多 64 个目录。
- VueScan / SilverFast 不按设备品牌限制；其他来源需有完整 RGB 图像且不声明相机 ColorMatrix。相机 CFA DNG 和带相机 ColorMatrix 的通用线性 DNG 继续走 RAW 解码器。
- RGB 图像的嵌入 ICC 按 TIFF 色彩管理处理；LinearRaw 保留扫描仪原始线性 RGB，原色未表征，不套用相机矩阵。
- 暂不支持分块、平面通道、其他位深、非默认方向、DNG 线性化/黑白电平/裁剪/操作码校正标签。遇到这些情况会明确报错，不读取缩略图代替。
- 不兼容时请从扫描软件输出 48 位 RGB 原始 TIFF。VueScan 建议关闭 **Raw save film**，保留负片供 OpenRevelare 反转。整条底片可使用多格分割。

## 红外除尘

在除尘工具栏点击「红外除尘」，审核、补画或擦除蒙版，确认后应用。蒙版随工程保存并用于预览与导出。局部红外透射衰减检测适合小灰尘和划痕，大面积缺陷可能漏检；传统银盐黑白胶片不适用。

目前识别 VueScan 的 RGBI DNG 红外通道，不限扫描仪品牌。其他软件的第四通道可能是透明度，或将红外保存为独立图层，尚不用于红外除尘；没有可用红外数据时按钮置灰。红外数据不参与 RGB 颜色计算。

## English

Compatible scanner DNGs from VueScan, SilverFast and other film digitizers are admitted by image structure, without a scanner manufacturer whitelist. The current storage contract is uncompressed, unsigned 16-bit, interleaved RGB or LinearRaw, with three or four channels and default orientation. DNG correction tags and tiled storage are rejected explicitly. Full images in direct SubIFDs or the main IFD chain are selected rather than reduced previews.

Generic DNGs with camera ColorMatrix remain on the RAW path, as do camera CFA DNGs. Embedded ICC in RGB images follows TIFF colour admission. LinearRaw samples retain uncharacterized scanner RGB; camera colour matrices are not applied to scanner RGBI. Infrared dust removal uses only established VueScan RGBI layouts; other writers' fourth samples or separate infrared images are not yet interpreted as IR. Real-device validation beyond the Epson sample remains pending.

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
