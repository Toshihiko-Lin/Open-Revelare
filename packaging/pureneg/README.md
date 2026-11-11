# PURENeg 跨平台构建与 GPU 方案

## 结论与交付边界

可以使用 GPU，首要目标是 FibreNet 概率图推理。现有 `filmdust.node` 是多线程 C++ **CPU** 加速器，不能将其安装成功当成 GPU 已启用。胶片扫描的解码、滤波、纤维追踪、区域合并和修复仍有 CPU 工作；FibreNet 的 GPU 加速比也不等于整个检测流程的加速比。

当前已提供各平台的原生构建入口、同权重 ONNX 转换器和独立模型进程，Windows DirectML 已完成本机推理测试。首次下载 PURENeg 时将同时安装当前平台的引擎、模型和运行库，并在实际推理成功后才标记为可用。Windows/Linux 模型进程在帧之间常驻，避免每张重新加载模型与构建 GPU 图。

`.github/workflows/pureneg-components.yml` 生成四个平台的组件候选包和 `components.json`，Mac 任务对转换模型与原 CoreML 做一致性检查。候选包通过跨平台验证并发布到 `openrevelare-models` 的 `pureneg-cbcc9fc825ba-v1` 后，正式客户端下载地址才可用。当前本机构建产物位于 artifacts 目录，不能把尚未发布的候选视为线上已经可下载。

## 构建矩阵

| 应用目标 | 原生引擎 | FibreNet 推理首选方案 | 回退与构建主机 |
| --- | --- | --- | --- |
| `osx-arm64` | arm64 C++ | 上游 CoreML，允许 CPU/GPU/ANE，由系统调度 | CoreML CPU；macOS arm64 原生构建 |
| `osx-x64` | x64 C++ | CoreML，在目标 Intel Mac 上验证 GPU 可用性 | CoreML CPU；macOS x64 原生构建 |
| `win-x64` | MSVC x64 C++ | 共享 ONNX 权重；Windows ML 用于满足其系统要求的新系统，DirectML 用于兼容目标 | ORT CPU；Windows 构建 |
| `linux-x64` | GCC x64 C++ | 共享 ONNX 权重；可选 CUDA，基础包使用 CPU | ORT CPU；Linux x64 构建 |
| `linux-arm64` | GCC arm64 C++ | ONNX CPU 先建立基线；GPU 后端按实际设备、驱动和运行时单独验证 | CPU；Linux arm64 原生构建 |

macOS 上游已经配置 `MLModelConfiguration.computeUnits = .all`。这是允许 CoreML 选择 CPU、GPU 或 Neural Engine，并不保证每次实际都走 GPU。[Apple 文档](https://developer.apple.com/documentation/coreml/mlcomputeunits)

ONNX Runtime 官方对新 Windows 应用推荐 Windows ML；DirectML 已进入持续维护阶段，因此需要把最低 Windows 版本纳入选择，不能把 DirectML 当作唯一长期路线。[Windows 指南](https://onnxruntime.ai/docs/get-started/with-windows.html)。Linux CUDA 包必须固定并校验 ORT、CUDA、cuDNN 的兼容组合，不能仅根据 NVIDIA 品牌启用。[CUDA 后端要求](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html)

## 现在可执行的原生引擎构建

所有脚本从 PURENeg checkout 中提取固定提交 `cbcc9fc825ba9aaf12304c7e9524643995faf4dd` 的源码，不依赖该 checkout 当前分支或未提交文件。输出保留许可证并生成 `manifest.json`，写入 Node 平台名、提交和 SHA-256。

Windows x64，需要 Visual Studio C++ 工具：

```powershell
./packaging/pureneg/build-windows.ps1 -ForkSource C:/src/PURENeg -OutputDir C:/build/pureneg-win-x64
dotnet publish src/OpenRevelare.Gui -c Release -r win-x64 -p:PureNegNativeDirectory=C:/build/pureneg-win-x64
```

macOS，需要 Command Line Tools、Node 24.14.1，分别在 arm64/x64 主机运行：

```bash
bash packaging/pureneg/build-unix.sh /src/PURENeg /build/pureneg-darwin-arm64
dotnet publish src/OpenRevelare.Gui -c Release -r osx-arm64 -p:PureNegNativeDirectory=/build/pureneg-darwin-arm64
```

Intel Mac 将目录名和 RID 改为 `darwin-x64`、`osx-x64`。清单使用 Node 的 `darwin-*` 名称，应用使用 .NET 的 `osx-*` RID。其他应用原生依赖仍应按项目现有打包步骤准备。

Linux，需要 Node 24.14.1、GCC 13 或更新版本（上游 C++ 构建使用 `-fexcess-precision=standard`）、Git、tar：

```bash
bash packaging/pureneg/build-unix.sh /src/PURENeg /build/pureneg-linux-x64
dotnet publish src/OpenRevelare.Gui -c Release -r linux-x64 -p:PureNegNativeDirectory=/build/pureneg-linux-x64
```

arm64 主机将目录名和 RID 改成 `linux-arm64`。Unix 脚本使用全新输出目录，先编译并执行 Node 加载检查，再发布产物；不会覆盖已有构建目录。客户端只加载匹配平台、固定提交和文件哈希的模块，加载不可用时保留上游 JS 回退。新 Unix 脚本已做语法检查，尚需在对应 macOS/Linux 主机进行真实编译和图像回归。

## GPU 组件实现顺序

1. **macOS CoreML 包。** 从同一固定提交构建 `macos/ImageCodec.swift`，使用 `coremlcompiler` 编译 `macos/Models/FibreNet.mlpackage`，输出上游要求的 `runtime/image-codec` 与同目录 `FibreNet.mlmodelc`。构建流程生成带文件哈希的独立组件清单，安装器将二者安装到上游组件的 runtime 目录，保留真实概率图。
2. **同权重 ONNX 基线。** 当前转换器只接受固定提交的模型图与权重，将源 FP16 权重精确提升为 FP32 计算，拒绝未知算子和不同模型。逐项对齐 CoreML 的 RGB 输入、NCHW `[1,3,576,576]`、归一化、边缘填充、中央 512 像素拼接、输出裁剪与 uint8 量化。FP32 中间结果并非 CoreML FP16 的位级复制，需要经过 macOS 一致性检查后才能发布。
3. **建立独立的模型进程。** 用相同的输入 TIFF → 全分辨率 `width × height` uint8 概率图协议调用 CoreML/ORT。ORT CPU、Windows GPU 和 Linux CUDA 分包，不将不同版本的 `onnxruntime` 原生库直接塞进现有 Deep-WB 进程，避免库版本冲突。输出记录模型哈希、实际后端、耗时与回退原因。所有平台继续共用 PURENeg 的后续检测与训练包逻辑。
4. **接入 GPU，并保留明确回退。** Windows 选择受支持的 Windows ML/DirectML 路径；Linux 按兼容组合提供 CUDA。GPU 初始化、算子支持或显存不足失败时重试同模型 CPU 推理，记录实际使用的后端。模型缺失时保留当前传统检测路径，并明确它没有 FibreNet 概率图。推理分块顺序处理，初版不默认批量堆叠显存。
5. **通过验证后再发布下载。** 按操作系统、架构和运行时维护组件清单，包含固定版本、大小、哈希、模型格式与最低系统要求。ONNX 权重由 Windows/Linux 共用；大型 GPU 运行库按需获取。先在临时目录安装、验证加载和模型推理，再发布完整组件；失败保留现有组件，不自动把未验证的模型当作可用后端。

## 性能与一致性验收

测试集应含灰尘、纤维、划痕、干净背景和片边，以及 24/60/100MP、8/16 位和不同嵌入色彩配置的输入。对同一检测快照比较 CoreML、ORT CPU 和 GPU 的逐块及拼接概率图，记录均值/最大误差、边界误差及最终蒙版变化。误差门槛需由基线验证确定；不能用“模型能够加载”替代模型一致性验证。

分别记录首次加载、预热后推理、完整检测墙钟时间、CPU 内存、GPU 显存，以及 GPU→CPU 回退。通过实测决定是否自动启用 GPU，不承诺固定加速倍数。CPU 小图路径仍保留，避免传输和调度成本抵消收益。

CI 应在 Windows x64、macOS arm64/x64、Linux x64/arm64 上编译并执行加载测试；GPU 图像一致性和性能验证需要有对应硬件的 runner。仅 CPU 的 CI 通过不能标为 GPU 验证通过。

## 本机阶段性数据

同一张合成 576×576 RGB16 TIFF，按上游 512 中心区域规则分为 4 块，在模型进程常驻后，Windows DirectML 后两次用时 67/64ms，CPU 后两次为 352/372ms，包含输入读取、分块推理、量化与地图输出。该样本约为 5 倍推理速度差异；不是完整除尘加速比，也不能推广为所有 GPU 的性能承诺。该图的两个后端输出最大差异为一个 uint8 等级，约 0.0096% 像素存在差异。

Windows 本机完整组件解包后，已实际运行模型、C++ 原生引擎和检测适配器，得到完整尺寸概率图。macOS/Linux 的实际构建与 CoreML 对照结果须以对应 CI 任务为准。
