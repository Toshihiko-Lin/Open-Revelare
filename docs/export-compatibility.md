# 导出兼容性检查（2026-10-18）

本次针对 JPEG、HDR 增益图 JPEG、TIFF16、TIFF32F 和线性 DNG 的实际编码检查。
Windows 本机和独立解码器结果见下文；没有 macOS、iOS 或 Android 真机访问权限，
平台支持说明与实际文件验收分开记录。HEIC 的另一次检查见 `packaging/heif/README.md`。

## 已修复

- **HDR JPEG 的 XMP 读取兼容性。** 原文件的逐通道增益范围以 RDF 数组写入 XMP。
  Android 规范允许数组，但实测 Google libultrahdr 的 XMP 解析路径仅接受标量属性，
  报 `could not find attribute hdrgm:GainMapMax`。导出现在让 RGB 通道共用最小/最大范围，
  增益像素仍分别保存三个颜色通道。XMP 和 ISO 描述同一张图，均可使用标量范围。
  较窄通道的量化精度可能略降，完整 HDR 重建测试仍通过；没有改为亮度单通道。
- **DNG 标签类型。** 两个 IFD 的 NewSubfileType 由 SHORT 改为 TIFF 要求的 LONG。
  数值、图像和颜色矩阵不变，避免依赖读取器对错误类型的容忍。
- 导出窗口说明 DNG 相册预览与完整图像的区别，以及 TIFF 的手机相册限制。

## 文件验证结果

生成 16 个 1031×769 样本，覆盖奇数尺寸：4 种色彩空间的 SDR JPEG 和 DNG、
sRGB / Display P3 HDR JPEG、16 位整数 / 32 位浮点 TIFF 的 None / LZW / Deflate。

- **SDR JPEG：** Pillow 完整解码，LittleCMS 接受并转换内嵌 ICC；Windows WIC 全尺寸解码。
  sRGB、Display P3、Adobe RGB、Rec.709 均通过。JPEG 为 8 位 baseline，4:4:4。
  profile 能被解析不代表不做色彩管理的查看器也能显示准确。
- **HDR JPEG：** Google libultrahdr 独立完整解码通过。对两种基图色彩空间分别测试
  双元数据、仅 ISO、仅 XMP，共 6 条路径。中性高光约为线性 SDR 白的 3.15 倍，
  全图无 NaN / Infinity。Windows WIC 也能完整解码 SDR 基图；此 WIC 路径未恢复增益图，
  不能据此宣称 Windows 所有照片应用都能显示 HDR。
- **TIFF：** tifffile / imagecodecs 独立解码六种组合，像素 SHA-256 与导出前数据完全一致。
  浮点 >1 数值完整保留。Pillow 可解码三个 TIFF16 样本。Windows WIC 六种组合全尺寸
  读取并转换成功；没有将转换为 8 位显示的结果当作 HDR 显示验证。
- **DNG：** tifffile 读出四个文件的完整 16 位 RGB LinearRaw SubIFD，尺寸正确；
  既有 LibRaw 重建测试继续通过。Windows WIC 对这些文件仅返回 256×191 缩略图。
  因此“相册能打开 DNG”不等于它读取了原始分辨率，也不代表图像损坏。

Windows 独立解码日志：`scratch/compat-windows-v2.json`；Python / HDR 解码结果：
`scratch/compat-independent-v2.json`。Google libultrahdr 检查版本为
`47d9f00726adba87e3f04059a50a8b34492e057a`；Pillow 12.2.0、tifffile 2026.5.15、
imagecodecs 2026.6.6。这些是本次本机证据，不是应用运行时依赖。

## 各端的使用边界

- **跨端普通分享：** sRGB JPEG 是首选。广色域 JPEG 依赖应用读取 ICC；
  缺少色彩管理可能偏色，不应靠去掉 ICC 来解决。
- **手机 HDR 分享：** 优先试增益图 JPEG。不识别增益图的应用仍可显示 SDR 基图。
  Android 14 起提供 Ultra HDR 支持，但屏幕、应用和系统设置共同决定是否呈现 HDR。
  小米 HyperOS 相册对本程序文件的实际 HDR 呈现仍需真机测试。
- **Apple：** iOS 18 / macOS 15 引入基于 ISO 的 Adaptive HDR；双写元数据是兼容基础。
  对本程序文件的 Photos、Preview、Quick Look 完整读取与 HDR 呈现尚未实机验证，
  也没有把旧版系统的私有 Apple Gain Map 与 ISO 格式混为同一种支持。
- **TIFF16：** 适合桌面修图、印刷和存档。Windows WIC 已通过；macOS 修图与预览仍待
  文件实测。Android 基础图片支持列表不保证 TIFF，各厂商相册可额外实现支持。
- **TIFF32F：** 线性后期母版，需要支持浮点、线性 ICC 和扩展范围的软件；
  能解码不代表相册会触发 HDR。手机分享使用对应 JPEG。
- **DNG：** 提供给支持线性 DNG 的 RAW 软件。不能用支持某款相机的 RAW 或 Apple ProRAW
  推断对任意线性 DNG 的完整支持。导出是已处理图像的线性容器，仍需保留原片和工程。

80 MP 时，16 位 RGB 仅像素数据约 480 MB，32 位约 960 MB；压缩文件尺寸不能代表解码
内存需求。本轮其他格式样本未做 80 MP 真机压力测试。传文件时应使用原文件传输，
再次压缩/编辑可能移除增益图。可用工具目录中的生成器复现样本，逐端检查导入、
放大后的清晰度、HDR 高光以及再次保存后的元数据保留。

## 规范与实现依据

- [Android Ultra HDR 规范](https://developer.android.com/media/platform/hdr-image-format)：
  scalar / array 元数据、JPEG 回退以及双元数据互通建议。
- [Google libultrahdr XMP 实现](https://github.com/google/libultrahdr/blob/47d9f00726adba87e3f04059a50a8b34492e057a/lib/src/jpegrutils.cpp)：
  实际 XMP 读取范围，不以规范允许就推断所有实现支持。
- [Android Ultra HDR 支持](https://developer.android.com/media/grow/ultra-hdr) 与
  [基础格式列表](https://developer.android.com/media/platform/supported-formats)。
- [Apple WWDC24 HDR](https://developer.apple.com/videos/play/wwdc2024/10177/)：
  ISO、Adaptive HDR、系统与应用的显示能力。
- [Adobe DNG / TIFF 规范入口](https://www.adobe.com/support/downloads/dng/dng_sdk.html)。
