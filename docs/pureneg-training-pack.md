# PURENeg 训练数据包

在「除尘」中运行 PURENeg 自动检测，检查修复预览，用画笔补画漏检、用橡皮擦恢复误修。点击工具栏的独立「导出训练数据包…」按钮，或照片导出菜单中的同名入口，打开缩略图选择窗口。

每次打开都默认不选择任何照片；勾选希望分享的照片后才能导出。每张照片单独设置「这张已修干净（可用于训练）」和「保留为测试集」，对应上游 `labels.clean` 和 `labels.test`，两项也默认不勾选。缩略图从**完整检测输入快照**生成，包含当前裁切外的区域，不使用可能隐藏这些区域的胶卷缩略图。输出将包含完整检测图像，请据此排除私人照片。

候选包括当前审核，以及本次程序会话中已经应用修复的检测快照；没有候选缺陷的检测也可以补画、导出。已应用快照跨照片保留，取消的审核不加入待选列表。窗口的「清除已保留样本」会释放已应用样本缓存；正在编辑的审核仍保留到应用或取消。正常关闭窗口时清除会话样本，不写入项目文件。临时图像在本机临时目录中，进程异常退出可能留下未清理文件。

选择目录后，程序只对勾选的样本创建 `training-dataset-<随机编号>` 文件夹，每张图位于独立的 `openrevelare-<随机编号>-training` 子文件夹中。未选照片的图像、文件名、标签和元数据均不会进入输出。导出期间使用临时目录，整批完成才发布；取消或失败会清理整批不完整输出。将整个数据集文件夹打包后自行分享给作者，程序不会自动上传。

格式依据固定依赖版本的 [PURENeg `writeTrainingPack`](https://github.com/Toshihiko-Lin/PURENeg/blob/cbcc9fc825ba9aaf12304c7e9524643995faf4dd/src/review.cjs)，沿用 `schema: pureneg-training-pack`、`version: 1`、文件名与差异层含义：

| 文件 | 内容 |
| --- | --- |
| `pack.json` | 图像尺寸、SHA-256、检测参数、缺陷标记、耗时、区域、像素计数与训练标签 |
| `source.tiff` | 检测时的未除尘图像快照，16 位 RGB SDR 正片 |
| `repair-preview.tiff` | 在检测输入网格应用最终蒙版的 Core 除尘修复快照，保留输入 ICC |
| `strokes.json` | 源坐标椭圆笔画历史，支持精确重放；导出前验证重放结果等于最终蒙版 |
| `fibre-map.u8.gz` | 可选；上游实际生成的 FibreNet 概率图，逐像素 uint8，gzip 压缩 |
| `mask-auto.tiff.gz` | 审核范围内初始自动修复蒙版 |
| `mask-erased.tiff.gz` | 初始自动蒙版中，被用户最终擦除的像素 |
| `mask-added.tiff.gz` | 初始自动蒙版外，被用户最终补画的像素 |
| `mask-undone.tiff.gz` | 整区域撤销层；当前编辑器只提供像素画笔，因此此层为空 |
| `mask-final.tiff.gz` | 当前审核的最终修复范围 |

蒙版都是 gzip 压缩的单通道 16 位 TIFF，0 表示未选中，65535 表示选中；所有文件共用检测输入的像素网格。差异根据当前蒙版计算，撤销和重做后的状态也会体现在导出中。输出先在临时目录中完成，成功后才发布最终目录；失败或取消会清理不完整目录。

## 与上游完整导出的差异

这是供模型反馈使用的审核蒙版导出。`source.tiff` 是 OpenRevelare 渲染的检测正片，不是原始 RAW 或用户源 TIFF；后续调色不会改变这张快照。源文件名和本地路径不会写入数据包，但包内包含完整检测画面。

迁移检测入口保留上游完整 `processScan`，使用平台模型运行时提供真实概率图，并记录完整检测设置与耗时；不再为审核写出用不到的全尺寸修复 TIFF 与蒙版 TIFF。导出时校验概率图尺寸和 SHA-256，写入 `fibre-map.u8.gz`，并按候选区域计算 `probMean`、`probMax`。`openRevelare.inference` 记录实际推理后端、模型哈希和组件信息。缺少真实概率图时 `fibreMap` 为 null，不生成替代数据。

上游模型运行时使用 macOS CoreML，需要其 `runtime/image-codec` 和同目录的 `FibreNet.mlmodelc`。新的安装事务会按运行平台获取原生引擎、模型和运行库：macOS 使用 CoreML，Windows 使用 ONNX/DirectML，Linux 使用 ONNX/CPU；只有实际推理检查成功才标记为安装完成。四个平台的组件已通过构建检查，两个 Mac 架构均通过 CoreML/ONNX 一致性检查；[组件预发布版](https://github.com/Toshihiko-Lin/openrevelare-models/releases/tag/pureneg-cbcc9fc825ba-v1)已提供自动下载索引，Windows 全新安装和实际检测已验证。

OpenRevelare 的画笔经过旋转、裁切和镜头校正映射后是源坐标浮点椭圆；上游 `strokes` 只接受整数坐标圆形笔画。为了保留真实编辑记录，顶层 `strokes` 仍为空，精确历史放在 `strokes.json`，由 `openRevelare.strokeHistory` 声明格式、版本及重放校验结果。每笔记录 `kind`（add/erase）及 `brushes`；每个椭圆记录中心 `x/y` 和两个轴向量 `ax/ay`、`bx/by`。撤销会移除有效历史，重做会恢复；分支编辑会丢弃旧的重做分支。上限为 1024 笔、200000 个采样点。

区域的 `kept` 表示该区域是否进入审核范围，不代表用户逐区域作出过判断。构图排除的区域不会计入用户擦除层；`openRevelare.protectedRegionIds` 和 `reviewGeometry` 记录其范围信息。未标记像素不应自动当作人工确认的干净背景，裁切外尤其如此。

导出时使用 Core 除尘算法修复检测输入，生成 `repair-preview.tiff`，然后重新读取 TIFF，逐像素比较原图和修复快照。`checks.sameSizeAsExport`、`checks.changedOutsideMask` 和 `checks.usable` 据此填写；若蒙版外出现改动则拒绝发布数据包。这里验证的是包内修复快照，所用 RGB 是检测输入的编码值，**不是**主色彩管理管线的线性工作值，也不是用户最终照片导出；`openRevelare.checkedImage` 和 `repairSpace` 标明验证对象与处理域。完整训练管线的接收验证仍需与原作者对接。

可用 Python 读取蒙版：

```python
import gzip
import io
import tifffile

with gzip.open('mask-final.tiff.gz', 'rb') as f:
    mask = tifffile.imread(io.BytesIO(f.read())) > 0
```

## 平台组件建议与当前状态

首次安装 PURENeg 时，Node、通用 JS、对应平台的原生引擎和模型运行库在同一个安装事务中完成，无须另行选择模型。运行库独立发布，基础应用仍不强制携带所有平台依赖。Windows/Linux 的模型进程在照片之间保留，避免反复构建推理图；关闭窗口或取消推理时释放。

| 层 | 推荐分发方式 |
| --- | --- |
| 通用 JS/检测规则 | 共用固定版本，统一输出与训练包格式 |
| 原生除尘加速 | 按操作系统、CPU 架构编译；不可用时使用 JS 实现 |
| macOS FibreNet | 保留上游 CoreML；发布匹配架构的 image-codec 与编译模型 |
| Windows FibreNet | 同权重 ONNX，DirectML 自动使用 GPU；初始化或推理失败时回退 CPU |
| Linux FibreNet | 共用转换后的 ONNX 权重，以 CPU 为基础后端；有适配运行时时可选 CUDA |
| 大型 GPU 运行时 | 按需下载，不在基础安装中强制携带全部后端 |

ONNX Runtime 提供不同的[硬件执行后端](https://onnxruntime.ai/docs/execution-providers/)，包括 [DirectML](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)。Windows/Linux 共用转换后的 ONNX 权重，运行库与加速包按平台拆分。Linux CUDA 构建入口已经预留，当前候选矩阵使用可移植 CPU 包，不把尚未验证的 CUDA/cuDNN 组合当作可用后端。

转换器固定源模型和权重 SHA-256，保留相同权重及 576 像素分块、中央 512 像素拼接方式和 FP16 网络。31×31 平均池化在 CoreML 和 ONNX 两边统一使用 FP32 累加，再转回 FP16，避免大窗口半精度求和漂移产生错误高通特征。组件精度版本为 `fp16-network-fp32-pool-accumulation`。两个 Mac 构建任务对修正后的 CoreML 与 ONNX 预测执行相同误差门槛检查；不承诺不同后端的概率图或候选逐位相同。

具体构建、发布边界和验收方法见[平台组件构建说明](../packaging/pureneg/README.md)。
