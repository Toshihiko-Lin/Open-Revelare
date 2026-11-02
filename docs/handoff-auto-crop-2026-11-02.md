# 自动裁切交接记录（2026-11-02，用户要求停止）

## 当前指令与边界

用户最新指令：**先停止工作，将进度写成文档供下一个 agent 接手。** 已停止实现、测试和打包；下一位只有在用户要求继续后再推进。不要将下文的待办当作现在就执行的授权。

用户本轮需求：自动裁切不能正确定位照片，失败发生在已有 **C400 样片**；自动裁切的比例选择应与手动比例预设共用一个 dropdown。用户还要求每完成一个任务单独 commit。

当前定位修改尚未完成验收，**不要直接当作已修复发布，不要仅用检测数量宣称准确率**。尚未提交的代码保留在工作区，没有 reset、丢弃或暂存。

## 仓库和分支

- 项目：`/Users/i3nit/Sync Files/Programming/Open-Revelare`
- 当前分支：`fix/135-auto-crop-localization`
- 停止时功能基线 HEAD：`6919708 fix: 自动与手动裁切共用比例预设下拉框`
- 本交接文档单独提交；若 HEAD 是后续 docs 提交，功能基线仍为 6919708，定位修改仍在工作区、没有混入文档提交。
- 分支从 `72e72f7` 创建，**不含牛顿环代码**。
- `72e72f7`：翻转以裁切旋转后的画面为准，保留裁切草稿。
- `d64fa22`：第一版 135 自动裁切。
- `main` 仍保留后续牛顿环研究，到 `e24d52a`。用户之前明确要求切回自动裁切/翻转修复版本手测，所以**不要直接切 main 或将其合入当前分支**。

## 已完成并提交：共享比例 dropdown

提交 `6919708`，严格构建通过，`CropAspectPresetTests` 11 项通过。

- 删除 `AutoCropModeCombo`，自动与手动统一使用 `CropPresetCombo`。
- 比例框移到自动裁切按钮上方。
- `CropAspectPresets.AutoMode(index)`：自由或 3:2 对应内容裁切，37:35 对应含齿孔；同为 3:2 的重复预设也支持。
- 其他比例仍用于手动裁切，点击自动会给出说明，不静默改成 3:2。**目前半格预设不支持自动裁切**。
- 选择比例仍沿用原来的手动草稿行为；点击自动前读取同一选项，再退出草稿模式。
- 已更新英语翻译及 `docs/auto-crop-135.md` 的 dropdown 说明。

## 尚未提交的文件

停止时 `git status --short`：

```text
 M benchmarks/OpenRevelare.AutoCropVerification/Program.cs
 M src/OpenRevelare.Core/FilmAutoCrop.cs
 M src/OpenRevelare.Gui/ViewModels/MainViewModel.AutoCrop.cs
 M src/OpenRevelare.Tests/FilmAutoCropTests.cs
?? src/OpenRevelare.Tests/FilmAutoCropRegressionTests.cs
```

### 定位实现的改动

1. `FilmAutoCrop.Prepare(source, parameters)` 统一检测前几何：先镜头畸变，再 90° 旋转/翻转，再拉直。**旋转填充用 0，不用显示用的纯白 1**。暗 RAW 中白角会超过真实齿孔亮度、污染 99% 分位阈值，可能触发错误的矩形回退。
2. GUI 自动裁切改用 `Prepare`；检测内部进一步拉直也用黑色填充。
3. 修正两排齿孔的横向间距：旧代码错误使用 **29.5 mm**，现在为 **28.17 mm**。孔距仍用 4.75 mm。横向像素尺度独立从两排距离估计，沿片方向尺度从孔距估计。
4. 新增 `FindFrameGutters`：比较画面与未曝光片基肩部的 log RGB，生成沿胶片方向的曝光距离剖面，匹配左右边界；允许约 34.5–36.8 mm 的实际曝光宽度。不是只找画面最强明暗边。
5. 片基肩部采样位于中心线两侧约 12.20、12.35、12.50 mm，避免把照片或齿孔本身当作片基。标准 24 mm 画面与齿孔内缘之间空间很窄。
6. 画面上下边界先由齿孔标尺给出，双侧有边界证据时再微调。画面暗部贴近片基时，不再强制四边都高对比。
7. 内部窄片基色间隔目前是 **0.5 倍分数惩罚，而非硬拒绝**。硬拒绝会误伤 C400 暗场照片；但软惩罚可能允许合并两张窄画格，见下文未解决疑点。
8. 片基突变保护通过回调也用于旧的四边缘回退：同时要求 log 跳变大于 0.45、线性亮度跳变大于参考白的 18%。仅看 log 跳变会误跳过大量正常黑片基正片，已修正。
9. 齿孔分割依次尝试阈值比例 `.72, .84, .92, .97, .60`；每次都需找到两排周期结构。目前仍返回**第一个合格标尺**，没有选择最大覆盖率标尺。
10. `Detection.HasPerforationRuler` 为诊断元数据。局部 helper 输出经主函数修正为原输入尺寸，避免内部降采样改变 `Width/Height`。
11. 整卷内容裁切：剔除相对中位尺度超出 8% 的离群项后，使用**最小可靠尺度 × 0.975**作为共享尺寸，以免中位尺寸不能装入略小画格而跳过。含齿孔仍使用可靠尺度中位数。保持整数 3×2 / 37×35 像素尺寸。

**不要误恢复一次已撤回的尝试：** 曾在 `Prepare` 加入工程的暗角亮度校正，但未改善 DSC01936，且影响其他帧；最终已移除。当前 `Prepare` 只匹配几何，不受暗角修图设置影响。回归中验证了这一点及不写穿共享解码缓存。

### 标准依据

- [SMPTE ST 93，官方公开 PDF](https://pub.smpte.org/pub/st93/st0093-2005_stable2015.pdf)，第 2 页表格列出 lateral perforation displacement 28.17 mm、孔距 4.750/4.740 mm 等。
- [ISO 491:1988 预览](https://cdn.standards.iteh.ai/samples/4540/cbe2dc7a26dc4c1d85216e84b1bd5a18/ISO-491-1988.pdf) 的 N/P 型尺寸表也给出 28.17 mm。
- [Kodak 官方参考手册](https://www.kodak.com/content/products-brochures/Film/kodak-essential-reference-guide-for-filmmakers.pdf)，Film Specifications 给出 KS/BH 孔宽约 2.794 mm 等。注意不要把规范尺寸与画幅或孔的内侧间距混用。

旧合成 `Perforated` fixture 自己也使用了错误 29.5 mm：原孔排中心 92.5/387.5，现改为 99.15/380.85，10 px/mm 下相距 281.7 px。它仍保持 36×24 mm 画面与约 35 mm 片宽。不是改 golden 来掩盖图像差异。

## 已新增的回归与结果

`FilmAutoCropRegressionTests.cs` 新增 12 个用例：

- 暗 RAW 值、已保存旋转和翻转组合仍恢复齿孔标尺（4 个参数组合）。
- 浅薄负片的片基不能与亮齿孔合并。
- 画面上下暗部贴近片基时仍定位（正/负两例）。
- 统一内容尺寸装入所有可靠曝光区域。
- 内部降采样不改变报告的源尺寸。
- 两种明显片基混合的过曝片头不输出可信画格。
- 黑正片片基微小亮度变化不等于过曝片头。
- 几何准备不写穿输入缓存，暗角调节不改变检测输入。

另修正旧中心误差测试在**恰好半像素**时浮点 ULP 越界：容差仅加 `1e-12` 归一化坐标，没有改变半像素的实际几何约束。

最后完整构建：`scratch/crop-localization-build.log`，**0 warning / 0 error**。
最后全量测试：`scratch/crop-localization-regression.log`：

- Core/GUI 测试 1191 通过、12 跳过、2 失败，共 1205。
- 仅剩两项已知上游 HEIC level 失败（hdr false/true，实际 150、期望 <=120），之前已在原始上游复现，不属于本任务。
- 两个呈现项目另有 25、40 项通过。
- 新增比例映射测试已在 UI 提交中通过；定位相关旧日志 `scratch/crop-localization-tests.log` 26 项为最后两例添加前的筛选结果。最后全量已包含完整 12 例新增测试。
- 没有改 golden 图像或 HEIC 实现。

## 实拍数据与复现路径

原件只读，不要覆盖用户项目或 RAW：

- `/Users/i3nit/Desktop/film/261101_Film C400_yc/RAW`
- `/Users/i3nit/Desktop/film/261101_Film 5294`

已有副本：

- `scratch/test-roll/RAW/`：28 张 C400 ARW，另有最初的 `RAW.ncproj`。
- `scratch/test-slide/261101_Film 5294/`：81 张 ARW，包含正片与负片。
- `scratch/c400-manual-test.ncproj`：本轮只读复制的**用户实际测试后**桌面 C400 工程。它包含当前旋转、翻转、90° 旋转以及错误的旧自动框。不要与最初副本混淆。

基线与最终输出：

| 路径 | 含义 |
| --- | --- |
| `scratch/verification-negative/`、`scratch/verification-positive/` | 最初仅裸 RAW 的旧检查，未应用工程几何 |
| `scratch/crop-before-localization-fix/` | 本轮旧实现，使用用户工程几何；28 张中检测 22、整卷应用 21 |
| `scratch/crop-after-localization-fix/` | **最后完成的 C400 检查**，27/28 内容与含齿孔可应用，跳过 `DSC01936.ARW` |
| `scratch/crop-positive-localization-check/` | 最后完成的 5294 检查，77/81 内容、76/81 含齿孔；内容跳过 02015、02059、02088、02096 |
| `scratch/crop-probe/`、`scratch/crop-probe.log` | 忽略目录内临时反射诊断，不要当产品代码提交 |

C400 最后 27 个内容框尺寸均为 **3276×2184**；尺寸一致不能当定位准确率。逐帧 `.jpg`：绿色内容、青色含齿孔。`report.json` 记录检测、初始旋转/翻转、畸变/暗角参数、源像素尺寸等；暗角参数仅记录，不应用到检测。

### 确认的实际旧错误

用户测试工程 `DSC01913.ARW`：QuarterTurns=2、FlipH=true、Rotation 约 −1.1558°。旧保存的含齿孔框宽约为源宽的 **57.55%**，明显超过实际画格。旧复现也给出约 56.16% 的错误画面宽（预览分辨率不同导致数值不完全相同）。

新检测画面宽约 47.95%，新整卷含齿孔框宽约 **49.63%**；检查图中边界已回到照片及齿孔附近。`DSC01936` 仍未找到可靠框，保持原裁切，没有强行输出成功。

## 最关键的未解决风险（下位 agent 优先核对）

### 1. 检查工具与真实 GUI 的预览分辨率不同

**当前 AutoCropVerification 解码最长边是 900，GUI `PreviewMaxEdge` 是 1600。** 整数 box downsample 使前者实际 876→438、后者约 1168→584，再进入 640 上限检测，可能影响阈值、孔中心和细边界。

虽然现在 GUI 与工具都调用 `FilmAutoCrop.Prepare`，但还没有用 1600 重跑所有用户工程几何。优先增加 `--preview-edge` 或把验证工具默认值与 GUI 对齐，尤其核对 DSC01913、DSC01936。**不要把现有 900 结果直接称作 GUI 全面验收通过。**

### 2. 5294 中 DSC02051 可能仍错误合并窄画格

放大检查 `scratch/crop-positive-localization-check/DSC02051.jpg`，绿色全幅框可能横跨两个窄画格/部分画格；之前的 `review-cases.jpg` 是更早一轮的图，不应当作最终输出。

最后记录中 DSC02051：Image X≈.52055、W≈.46575、HasPerforationRuler=true，内容框 X≈.52754、W≈.45163。单看检测数量会把它记作成功，**但位置/画幅还未确认**。

刚停止时的新疑点（仅假设，未验证）：这些窄画格可能是 **135 半格、其他窄幅，或过曝片头造成的部分画格**。从缩略图估计实际图宽可能约为全幅的一半。必须用原始图、孔距和真实曝光边界量一下，不能据缩略图就断言半格。

当前 `FilmCropMode` 只支持全幅内容 3:2 与全幅含齿孔 37:35；共享 dropdown 的半格项仍是手动模式。若确为半格，不能继续将两张拼成一个全幅。是否扩展自动画幅需结合用户范围判断，先验证，不要立即大改。

可能的研究方向，**均未实现**：

- 重新审视内部窄间隔的软惩罚，区分真实暗部与独立画格间隔。
- 保存匹配到的两排孔位置，要求候选画格两侧有足够孔点/跨度，避免拿局部伪标尺或不可见片头补足全幅。
- 当前标尺返回首个合格阈值；若增加孔覆盖检查，可能也需比较多个阈值的证据，而不是因首个标尺不完整就跳过。
- 若保存孔坐标，需考虑自动拉直后的坐标变换/容差，不能直接假定原孔列位置完全不变。
- 不要硬编码文件名跳过，也不要仅为提高成功数量放松边界保护。

## 工具、命令和打包状态

本机 SDK 和 NuGet 在忽略目录，通常需通过工具的 escalated 执行才能避免沙箱导致 .NET 卡住；用户不喜欢反复确认，已授权本地实现/测试。环境依赖按现有项目准备，不需新建 AI 环境。

```sh
NUGET_PACKAGES="$PWD/scratch/nuget" ./scratch/dotnet/dotnet build -c Release -warnaserror -p:NuGetAudit=false
NUGET_PACKAGES="$PWD/scratch/nuget" ./scratch/dotnet/dotnet test -c Release --no-build --no-restore
NUGET_PACKAGES="$PWD/scratch/nuget" ./scratch/dotnet/dotnet run \
  --project benchmarks/OpenRevelare.AutoCropVerification -c Release -p:NuGetAudit=false -- \
  scratch/test-roll/RAW scratch/crop-after-localization-fix --project=scratch/c400-manual-test.ncproj
```

- 新 `--project=` 让验证工具使用真实保存的旋转/翻转/畸变；原件不写入。
- Native LibRaw、LCMS、HEIC、Compute、macOS Presenter 已存在于 `native/osx-arm64/`，不要误提交。
- `publish/OpenRevelare.app` **仍是之前给用户的 72e72f7 测试包**；不含 UI 合并或当前定位修改。不要把它当新测试包发给用户。
- 已准备但**未执行** `scratch/manualpack/build-app.sh`：是原打包脚本的本地副本，仅把 APP 输出改成 `publish/OpenRevelare-AutoCrop-Test.app`；目录深度保持 ROOT 计算正确。新测试包尚不存在。
- 用户可能仍运行旧 `.app`，不要覆盖或擅自关闭它。完成验证/提交后，先 self-contained publish，再使用该本地脚本另存测试包。打开新包时提醒退出旧窗口，避免同 bundle identifier 激活旧实例。

```sh
NUGET_PACKAGES="$PWD/scratch/nuget" ./scratch/dotnet/dotnet publish src/OpenRevelare.Gui \
  -c Release -r osx-arm64 --self-contained true -o publish/osx-arm64 \
  -p:NuGetAudit=false -p:TreatWarningsAsErrors=true
bash scratch/manualpack/build-app.sh --no-publish
```

本轮尚未写最终定位说明、未提交定位代码、未构建新测试包。后续完成一个独立任务再 commit，保留用户要求的整洁提交历史。不要为清理工作区而把未验收逻辑直接提交为完成。
