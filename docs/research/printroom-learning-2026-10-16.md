# Printroom 源码调研与学习转化

日期：2026-10-16。Printroom 固定提交：`d02fa900bd5103a7db434dba70f6593e983d82fc`；OpenRevelare 对照提交：`339321bcd20a4c1d12e05c7442508757f8e5a9d7`，另含本文记录的本地改进。

本次通过克隆源码检查实现、测试和技术文档，未运行 macOS 上的 Printroom，也未做同源实片画质盲测或跨应用性能对测。“做得更好”限于能够从代码验证的工作流与工程机制，不推导整体画质、速度或稳定性排名。对方文档所述的性能与真实样片覆盖也不等于本次独立验证。

**2026-10-17 迁移更新：用户明确跳过第 2 项自动裁剪，继续第 3、4、5 项，现已完成代码迁移及生成 TIFF 的组件测量。**统一原片版本检查、分析提交快照、邻帧正式显示数据预热、中英文第一卷指南和结构化性能诊断均已实现。另修复 HDR JPEG 的 SDR 基图颜色与 HDR 主图不一致、SDR 齿孔填充被调色染色的问题。具体机制、复现命令和验收边界见 [预览性能诊断](../performance/preview-diagnostics.md)；后文“转化方案”保留最初调研依据，实际状态以下面的迁移说明为准。

## 结论与产品边界

最值得学习的是：整卷自动裁剪后的复核队列、批量编辑的提交边界、覆盖分析任务的原片身份验证、邻帧正式预览预热，以及以任务组织的上手指南。

我们已有整卷管理、虚拟副本、分组同步、整卷撤销、自动保存、中性点校色、LUT 输入输出契约、ICC 色彩管理和内存预算。这些不能列为“对方有而我们没有”。应借鉴实现中的边界处理，而非再做一套同名功能。

沿用此前确定的方向：[数字中间片输入标定](digital-intermediate-input-calibration.md)。优先提高输入设备标定与颜色准确性，保留既有 LUT 路径。Printroom 的印片外观是另一种产品选择，不能用固定矩阵或印片 LUT 替代我们需要解决的输入误差。

## 1. 批量编辑先准备、后提交——本次已落地

Printroom 的 `BatchEditTransaction` 先检查目标集合及原文件，再对值类型项目构造新状态；计算或文件验证失败时，调用者还未发布任何编辑。其交互规范要求成功操作一次撤销、失败整组不变。[事务源码][batch]、[交互契约][interaction]。

我们的 `MainViewModel.Sync.cs` 原先直接遍历并修改 `frame.Params`，没有统一检查原片是否还存在，也没有隔离可能失败的准备过程。虽然已有整卷快照，失败边界和成功提交边界仍有提升空间。

本次用现有 C# 参数模型独立实现 `BatchParameterEdit.Prepare`：

- 开始前检查全部目标；直接广播还检查当前来源。已复制的剪贴板快照可以在来源文件删除后继续使用。
- 在独立 `FrameParams.Clone()` 上准备整组结果。后面的目标计算失败，前面的真实参数也不会改变。
- 标定、场景、方向、裁切、齿孔和批量粘贴统一经过该机制。失败保留选择集合，并给出包含文件名的状态提示。
- 成功前结束上一次编辑，成功后立即结束本次撤销组，让一次同步独立于随后拖动滑块的操作。
- 无目标时不写参数、不增加编辑版本；修复只勾选当前帧时，方向同步错误回退到整卷的问题。

实现位于 `src/OpenRevelare.Gui/Services/BatchParameterEdit.cs` 与 `ViewModels/MainViewModel.Sync.cs`；回归测试在 `BatchParameterEditTests.cs`、`StripBatchTests.cs`，覆盖缺失最后目标、准备中途异常、虚拟副本参数隔离、剪贴板来源丢失，以及整组撤销/重做和选择范围。

边界：这是 UI 线程上的参数准备与统一发布，不是文件系统事务。不能保证原片在检查后的任意时刻不被外部进程移走，也不保证后续缩略图渲染一定成功。本次未实现“结果完全相同时不新增撤销”，也未把单帧粘贴改成批量操作。

## 2. 自动裁剪的价值在于可复核——用户指定跳过

Printroom 的 `AutoCropAnalyzer` 从未调色输入准备密度分析，在整卷种子上建立共享画幅模板，逐帧输出裁框、边缘证据和 `needsReview`。`AutoCropService` 限制分析内存，超预算时保留种子并重算，过程中验证原片身份。编辑器还提供“确认并下一张”，可以只遍历待复核帧。[算法][autocrop]、[调度][crop-service]、[编辑器][editor]。

我们已经有 `StripSplit` 的条带拆帧和导入确认，且有手动裁切、裁切同步与跨拆帧的 `CropRebase`。这与“逐帧整卷画幅自动定位 + 低可信复核队列”不同。现有拆帧算法的明暗/平坦度判断有自己的样片依据，不应直接替换。

转化方案：先为裁剪候选增加边缘证据和待复核状态，再提供只看待复核帧、确认并下一张、取消本次建议的操作。候选生成使用未调色输入，避免 LUT、曝光或白平衡改变检测结论；跨方向和拆帧的映射继续使用现有坐标约定。

验收条件：覆盖弱边界、齿孔、片夹条纹、全黑/全白、横竖混合和拆帧；低可信结果进入复核而非静默覆盖；取消不改工程；整卷应用一次撤销；原片在分析期间变化则丢弃结果。需要另建真实样片集，当前不能声称对方算法在我们的扫描仪与翻拍设备上更准确。

## 3. 缓存除了内存预算，还要认识原片——已迁移

Printroom 用 `SourceStamp` 表示路径、大小、修改时间和 RAW 处理身份，TIFF 另带 inode；正式预览键还包含帧 ID、标定、调整、方向、裁切及阶段。自动裁剪结束前会复核所有模板贡献者，避免把旧输入的分析应用到新文件。[原片身份][stamp]、[正式预览键][presentation-cache]、[裁剪调度][crop-service]。

我们 `PreviewKey` 已包含区域、管线版本和 TIFF 输入解释，`PreviewCache.Generation` 可以拒绝换卷后的旧解码写回，DNG 磁盘缓存也含源大小/修改时间。但内存 `PreviewAsync` 在命中后直接返回，没有在该入口验证同路径原文件是否被替换；常驻小图也有独立生命周期。仅修一个字典键会留下其他旧像素入口。

转化方案：统一跨平台的源版本对象，贯穿预览、常驻小图、在途解码合并和分析快照；解码前后检查版本，失败时不把旧结果发布到新版本缓存。LCC 等外部输入还需要自己的版本身份。

迁移实现：`SourceStamp` / `SourceSnapshot` 贯穿预览、常驻小图、在途解码、放大区域、全尺寸导出缓冲、负片 WB 元数据及分析提交。`CalibrationSources` 保存平场与矩阵参考版本；更改后要求显式重载，避免静默改变颜色依据。缓存键使用精确区域和输入契约，数据发布复核原片与代数。测试覆盖同路径同大小替换、删除重建、读取中改变、晚到文件、区域隔离、平场变化及缓存失效。

边界：长度、修改时间和创建时间都不变的内容替换仍可能漏检；不把 macOS inode 当作跨平台契约，也未增加常驻文件监控。已经发布的预览保留到下次读取或交互，不宣称实时监测磁盘。

## 4. 邻帧预热——已迁移，组件已测，真机体验待验

Printroom 有专门的交互渲染服务、显示结果缓存和后台预热调度。它区分原始代理、处理后显示图和直方图，缓存键带完整处理状态；交互或切卷取消预热，GPU 已提交工作完成后再丢弃过期结果。[渲染服务][renderer]、[显示缓存][presentation-cache]、[编辑器调度][editor]。

我们已有共享在途解码、整卷预热、内存压力预算、受保护工作集和缩略图小图缓存。差异主要是正式显示结果是否预先准备，而不是是否“有缓存”。`Pipeline.cs` 已记录此前 GPU 后端移除的实测原因，不能因为对方用 Metal 就认定应重写 GPU 管线。

转化方案：先测冷启动、热切帧、拖动参数、放大和导出，区分解码、处理、色彩转换、打包、呈现的耗时；只在热切帧仍明显受显示准备影响时，试验空闲期间预热前后少量帧，并服从当前内存压力与取消机制。

迁移实现：空闲 350 ms 后只对已有解码缓存的前后一帧准备管线、规范场景与直方图；上限两项／96 MiB，可用内存不足时清空，切帧／调参／长任务取消，后台结果经过完整参数及源版本检查。原片预热与显示准备分开，未新增 GPU 后端。

组件测量中，完整准备 P50 153.888 ms，含参数键与文件版本检查的命中 P50 0.286 ms，输出像素一致。完整 GUI 切帧、真实 RAW 和低内存长会话的收益仍需真机验证；该结果不支持“Printroom 更快”的结论。[原始数据及复现方法](../performance/preview-diagnostics.md)。

## 5. 面向第一卷的指南与可复现诊断

Printroom 提供独立使用指南，并将技术文档分成数值管线、交互契约、持久化架构。快速开始明确要求先裁剪再进行整卷分析，降低用户不知道下一步做什么的成本。[使用指南][guide]、[数值管线][pipeline]、[架构][architecture]。

我们 README 已有处理顺序、截图、安装和数据路径，应用内也有操作指引与技术原理；差异是任务指南的可直达性和契约的分层。适合增加“一卷从导入到导出”的逐步页面，逐项写明操作目的、成功状态、失败恢复方式，并在应用内帮助与 README 直接链接。将新行为的依据写进对应文档，而非只留在聊天记录。

性能诊断也有可学点：Printroom 的 `PerformanceTrace` 能汇总分段次数、总时长和最大值；我们已有 `RenderTrace` 的交互与呈现分段日志。后续应扩充结构化汇总和可复现冷/热缓存场景，复用现有入口。[对方性能记录][trace]。

验收条件：让新用户完成首次导入、片基取样、同步、逐帧调整、导出和重新打开，记录卡住的步骤；技术文档注明哪些是规范、哪些由自动测试覆盖、哪些仍待真机验证。

迁移实现：[中文第一卷指南](../../src/OpenRevelare.Gui/Assets/docs/FIRST-ROLL.md)与英文版已嵌入应用，成为首次启动／帮助文档的默认标签页，README 可直接打开。每一步注明目的、完成标志与失败恢复。现有 `RenderTrace` 增加有界样本、P50/P95、命中／取消／拒绝发布计数和退出时 JSON 汇总；另提供独立组件基准项目。新用户实际走查仍是人工验收项。

## 追加修复：HDR 的 SDR 基图与齿孔白色

用户于 2026-10-17 要求修复 HDR 被当 SDR 显示时的偏色，以及调色影响齿孔填白。源码定位到两个独立问题：SDR 齿孔填白发生在 Stage 2 之前；HDR JPEG 的 SDR primary 则独立重跑 SDR 调色，并会移除 HDR-only LUT，无法保证与最终主图同色。

现在所有最终正片输出在调色、LUT 和几何之后重新应用填白掩码，SDR／HDR、整帧／区域一致，填充恒为输出参考白。场景线性中间输出及普通无填充像素没有被重新调色。

`HdrGainMap.ColourMatchedSdrBase` 用现有 SDR 渲染的亮度作为模板，使用最终 HDR 主图在基图色域内的 RGB 比例，不再让另一套调色决定基图颜色。上界使用三通道统一缩放，避免逐通道裁切再次偏色；保留精确参考白。HDR JPEG、SDR 缩略图和印样共用这个规则，HDR 主图与增益图重建关系保留。

回归测试验证普通 JPEG 解码器仅读 primary 时的颜色、真实 PQ-only LUT 的色彩延续、增益图 HDR 重建，以及白平衡／曝光／曲线／LUT／方向／拉直／裁切／区域渲染后的填白。不修改已有像素金标准。

范围：已修复可验证的 **HDR JPEG / SDR companion** 路径。BT.2020/PQ HEIC 与浮点线性 TIFF 本身不含同样的 JPEG SDR primary；忽略其色彩标记的查看器不能靠上述修复获得自动 SDR 回退。本次保留它们正确的原有标记与主文件数据，尚无用户指定的故障文件和查看器用于复现这两种容器。Gain-map primary 的兼容语义参见 [Android Ultra HDR 格式说明](https://developer.android.com/media/platform/hdr-image-format) 与 [Adobe Gain Map 说明](https://helpx.adobe.com/in/camera-raw/desktop/hdr-and-advanced-output/gain-map.html)。

## 不直接移植的实现

Printroom 当前管线明确将输入样本指定为 P3-D65 Linear，记录中甚至说明真实参考 TIFF 嵌入 ProPhoto RGB Linear 仍按指定方式解释；这是它的数值约定，不是保留源 ICC 色度的证据。我们应继续使用现有输入域声明、ICC 与管线版本策略，不能退回固定原色解释。[输入契约][pipeline]。

内置密度矩阵和印片 LUT 的外观可以研究，但不能未经相机/光源/胶卷适用性验证就作为普适校色；不引入第二套曝光/对比度自由度去补偿输入误差。RAW 的 Adobe DNG Converter 路径也不是我们的缺失能力，我们已有随应用构建的 LibRaw。

本次采用的是机制与设计思想，以现有 C# 模型实现，没有复制 Swift 源码或 ICC/LUT 资产。双方代码虽均为 GPL-3.0-only，今后若引入第三方资产，仍须逐项检查其独立来源和声明。

## 执行顺序

1. 本次完成批量准备与提交、撤销边界及选择范围修复，保留像素管线和工程格式。
2. 已完成统一原片版本身份、缓存与在途分析的一致性检查。
3. 按用户要求跳过自动裁剪候选与复核队列。
4. 已完成第一卷任务指南、结构化性能诊断、组件基线与有限邻帧显示预热；完整设备体验继续依赖人工验收。

## 首轮验证（2026-10-16）

- `dotnet build -c Release -warnaserror --no-restore`：通过，0 警告、0 错误。
- `dotnet test -c Release --no-build --no-restore`：主测试 954 项、Windows 呈现测试 40 项、macOS 呈现契约测试 25 项，合计 1019 项通过，无跳过。
- 新增 11 个测试用例（4 个参数准备用例、6 个缺失目标工作流用例、1 个选择范围与撤销用例）；既有裁切与虚拟副本回归也通过。
- `git diff --check`：通过。未修改像素金标准、工程格式或已有 macOS 排查文件。macOS 测试在 Windows 主机执行，不代表 macOS 真机验证。

## 迁移与追加修复验证（2026-10-17）

- Release 主解决方案和独立基准项目均在 `-warnaserror` 下构建成功，0 警告、0 错误。
- 主测试 969 项、Windows 呈现 40 项、macOS 呈现契约 25 项，合计 1034 项通过，无跳过。
- 续作新增 15 个用例：9 个源版本／预热／统计用例，6 个填白／HDR-SDR 色彩回归用例。原有金标准未修改。
- 开启 `OPENREVELARE_RENDER_TRACE=1` 的定向测试确认 JSON 汇总真实生成，记录在途合并、缓存命中、邻帧预热及阶段统计。
- 生成 TIFF 组件基准通过，缓存与重新计算像素一致；原始数据与复现命令保存在 `docs/performance/`。未进行具体第三方查看器、macOS 真机或真实 RAW 的新增性能验收。

[batch]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomApp/BatchEditTransaction.swift
[interaction]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/docs/interaction.md
[autocrop]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomCore/AutoCrop.swift
[crop-service]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomApp/AutoCropService.swift
[editor]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomApp/EditorModel.swift
[stamp]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomApp/SourceStamp.swift
[presentation-cache]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomApp/PreviewPresentationCache.swift
[renderer]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomApp/PreviewRenderService.swift
[guide]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/docs/user-guide.md
[pipeline]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/docs/pipeline.md
[architecture]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/docs/architecture.md
[trace]: https://github.com/karasuyasabou/Printroom/blob/d02fa900bd5103a7db434dba70f6593e983d82fc/Sources/PrintroomCore/PerformanceTrace.swift
