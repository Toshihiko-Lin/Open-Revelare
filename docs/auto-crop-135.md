# 135 自动裁切

在编辑器的「裁切」面板使用统一的「比例预设」下拉框，再点击「自动裁切本帧」或「自动裁切选中 / 整卷」。有勾选时仅处理勾选帧；未勾选时处理整卷。自动裁切覆盖目标帧已有裁切，可撤销，也可重新打开拖框工具微调。

| 模式 | 目标 | 导出比例 |
|---|---|---|
| 135 全幅 — 内容 | 实际画面，向内留少量余量避开边界 | 横幅 3:2，竖幅 2:3 |
| 135 全幅 — 含边框 | 画面、上下齿孔和片边 | 横幅 37:35，竖幅 35:37 |

自动与手动裁切共用同一个下拉框。选择「自由」时，自动按 135 内容 (3:2) 检测；选其他非 135 比例时保留手动拖框操作，不会静默改成 3:2。正片、负片均从反转和齿孔遮罩之前的线性预览识别，不需要为了裁切改变反转参数。

## 识别和整卷稳定性

- 优先识别两排周期性齿孔，用孔距估计胶片尺度；正负片按片基与画面的明暗关系分别检测边界。
- 齿孔排布提供小角度拉直信息，可靠时自动修正，裁切框位于拉直后的坐标中。自动修正限于约 ±5°，并遵守原有拉直控制的 ±15° 总上限。
- 同一处理范围内，相同源尺寸、相同横竖方向使用检测尺度的中位数，逐帧只改变位置。明显偏离该尺度的帧跳过，不独立放大。
- 裁切大小使用 3×2 或 37×35 的整数倍，位置也落在源像素边界上，因此同组帧导出尺寸及宽高比完全一致，不受逐帧四舍五入影响。内容框无法放入检测画面时跳过该帧。
- 不能可靠识别、原片缺失或含边框区域超出输入范围时，保留该帧原裁切。状态栏报告应用和跳过的数量。请检查这些帧，而不是将「跳过」视为成功裁切。
- 所有检测完成后一次性应用；支持取消、撤销、重做和 `.ncproj` 保存。检测期间编辑参数或更换胶卷时不应用过期结果。

适用于一张翻拍照片包含一个完整 135 全幅画格的情况，可以含相邻部分画格。半格、XPan、没有完整画格的片头，以及同一扫描源已拆分出的多个虚拟画格，不属于该自动模式；仍使用现有手动预设 / 拖框裁切。夜景正片、过薄负片等画格与片基接近时可能跳过。识别不校正镜头畸变；明显畸变的边界需要手动检查。输入中已经缺失的齿孔或片边不会被补画。

## 本地启动

已将源码放在本项目目录。若已安装 .NET 8 SDK：

```bash
dotnet run --project src/OpenRevelare.Gui -c Release
```

本次工作额外在 `scratch/dotnet/` 安装了本地 SDK，在 `scratch/nuget/` 缓存了依赖，并构建了当前 macOS arm64 的 LibRaw、LittleCMS、HEIC 和计算后端。当前机器可在项目目录执行：

```bash
NUGET_PACKAGES="$PWD/scratch/nuget" ./scratch/dotnet/dotnet run --project src/OpenRevelare.Gui -c Release --no-restore
```

这些本地依赖和测试副本均位于 Git 忽略目录，换机仍按仓库原有构建说明准备环境。

## 验证

核心测试覆盖：横竖和翻转、正片 / 负片、倾斜齿孔拉直、低信息输入、尺度离群、缺失片边、混合分辨率以及像素级比例。工作流测试通过真实 TIFF 工程检查两种模式的整卷 / 选中帧应用、撤销重做、取消、工程保存和源文件 SHA-256 不变。

```bash
dotnet test src/OpenRevelare.Tests -c Release --filter 'FullyQualifiedName~FilmAutoCropTests|FullyQualifiedName~AutoCropWorkflowTests'
```

提供了独立的实拍检查工具，仅读取输入目录中的 RAW，生成逐帧裁切框预览、整卷概览和 `report.json`。绿色框为内容裁切，青色框为含齿孔裁切；没有框的图需要人工检查。报告列出实际导出的像素尺寸，并检查比例没有漂移。

```bash
dotnet run --project benchmarks/OpenRevelare.AutoCropVerification -c Release -- <RAW目录> <独立输出目录>
```

本次实拍验证使用复制到 `scratch/test-roll/RAW/` 的 28 张 C400 ARW，以及 `scratch/test-slide/261101_Film 5294/` 中的 81 张 ARW。后一个目录含正片与负片；工具逐帧判断，未仅按文件夹名称假定类型。结果保存在 `scratch/verification-negative/`、`scratch/verification-positive/`，未改动桌面原件，也未将样片加入 Git。

全量构建和测试记录分别位于 `scratch/build.log`、`scratch/tests.log`。在当前原生依赖环境中，上游已有的 `HeicExportTests.Large_photo_is_a_bounded_main10_grid_with_exact_dimensions_and_color` 两种 HDR 参数用例失败：编码的 HEVC level 为 150，而测试要求不超过 120。在未修改的上游提交 `0e638380d6a8d953ef8b970c6bce903a2410a19e` 上复现了相同失败，见 `scratch/baseline-heic-tests.log`。本次未修改 HEIC 编码代码或测试基线。

上游锁定的 ImageSharp 3.1.12 在首次 NuGet 还原时有 NU1903 审计提示；本次编译检查使用关闭 NuGet 审计后的还原缓存，将已有依赖审计与代码的零警告构建分开，未更改依赖版本。
