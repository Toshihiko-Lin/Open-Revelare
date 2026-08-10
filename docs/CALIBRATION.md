# chroma_grade 的来源与现状

本文记录 `chroma_grade = 3.05` 这个默认值的实际出处、它与文档曾经的说法之间的差距，以及为什么下一步是做真正的色彩空间渲染。写下来的目的是：这个值曾经被多份文档以"实测标定"的确定语气描述，而追溯下来它的依据比那种语气所暗示的弱得多。

## 出处

追溯到 Python 原型仓库 NegativeConvert 的两个提交：

- `0cc7704`（2026-05-30）引入密度域亮度/色度分离，`chroma_grade` 首次出现，默认 2.75。
- `5f5d317`（同日）改为 3.05，commit message 写 "precise Gold 200 cc24 calibration"。

当时 README 记录的标定方式：

> - 数据来源：DiVERE 对 Kodak Gold 200 + ColorChecker 24 的密度测量（D55 光源，Endura Premier 相纸）
> - 标定方法：寻找使管线反转后平均饱和度（18 个彩色色块）恢复至真实场景水平（0.806）所需的 chroma_grade
> - 结果：chroma_grade ≈ 3.05（理论值 1.67 只能还原 79%，3.05 还原至约 97%）

数据文件是 DiVERE 的 `config/colorchecker/kodak_gold_200_kodak_endura_premier_d60_cc24data.json`。

## 与文档旧说法的四处出入

**其一，那不是"实测"，是光谱模拟。** 数据文件自己的描述字段写着"在 `kodak_gold_200_auc_noDIR` → `kodak_endura_premier_d60_uc` 打印流程下，相纸上得到的**理论密度**"，`"type": "DensityExp"`。整条链路的唯一实测输入是 ColorChecker 的标称 Lab 值（X-Rite `ColorChecker24_After_Nov2014.txt`），其余由胶片感光度曲线与相纸染料响应正向算出。没有相机、没有扫描仪、没有实际拍摄。

这不是说数据不可信 —— 光谱模拟避开了翻拍眩光、光源不均、传感器噪声、采样偏差，作为基准往往比自己拍一张色卡更干净。问题只在于文档把它称作"实测"。

**其二，归因与数据集自相矛盾。** `0cc7704` 把 21% 的缺口归给"dye inter-layer cross-talk and DIR couplers"，但所用文件名是 `auc_noDIR` —— DIR 效应在这份数据里被显式关闭。DiVERE 另备有 `portra_160_DIR` / `portra_400_DIR` 版本，说明 DIR 在其建模中是可选开关。因此这 21% 不可能来自 DIR。

**其三，标定域与输出域不一致。** 数据的 `required_working_colorspace` 是 `KodakEnduraPremier`，那个 0.79 是**相纸色域下**的饱和度。而管线输出 sRGB，中间并不存在相纸这一环。

**其四，它不含任何相机信息。** DiVERE 里补偿传感器串扰的是逐卷拍色卡求解的 CCM 矩阵（`divere/utils/ccm_optimizer/`），与这份 cc24 理论数据是彼此独立的部件。因此 THEORY.md 曾称 3.05 是"相机 sensor 特性的标定结果"、并据此让 RAW 用 3.05、扫描件用 1.0 —— 这个依据不成立。同一张底片用相机翻拍和用扫描仪扫会得到两种色度渲染，而这个差异没有物理依据。

## 两项测量

**`calibration/study_saturation.py`** —— 原型时期唯一的量化脚本，曾只存在于一个 delete commit 里，现已恢复。它在合成负片上正向建模 C-41、走真实反转管线、再测饱和度还原率。结论与 3.05 相反：

```
true scene saturation              : 0.8399
MODEL 1  纯逐通道 γ（无串扰）
  restoration ratio                : 113.9%
  → scalar comp needed             : 0.000
MODEL 2  γ + 代表性染料串扰
  restoration ratio                : 111.1%
  → scalar comp to hit scene sat   : 0.000
```

反转后饱和度不降反升 11–14%，所需补偿为 0。该脚本另测出：标量补偿后每色块饱和度误差 mean 0.093 / max 0.206 —— **标量补不平**。

需要说明的是，这是合成负片 + 当前管线，不是当年的原始数字，中间管线改过（线性域解耦等）。它不足以判定"3.05 从来就是错的"，但它是仓库里唯一可执行的证据，方向与 3.05 相反。

**`calibration/gamut_check.py`** —— 检验"21% 主要来自相纸窄色域"这个假设。结果是 sRGB/Endura 饱和度比 **1.062**，色域只解释约 6%。**该假设不成立**，缺口的主要来源至今未确认。

## 结论与去向

综合来看：3.05 是从一份相纸色域的光谱模拟数据里，用一个已被测出补不平的标量拟合出的经验值，其归因与所用数据集矛盾，且不含任何相机信息。**它缺乏文档曾赋予它的那种正当性。**

真正的问题是：反转输出的线性数据从来没有声明过自己的色彩空间 —— 隐含地"反正是 sRGB"。没有色域可以转换，一个作用在色度向量上的标量就成了"颜色不对"时唯一能拧的旋钮，而色域关系本质上是各向异性、逐色相的，标量无法表达。

因此方向是做真正的色彩空间渲染：工作空间（默认 ACEScg，色域足够大，中间步骤不裁信息）+ 可选输出空间（sRGB / AdobeRGB / DisplayP3 / Endura Premier / Kodak 2383）+ gamut mapping。渲染到相纸色域会以正确的方式给出相纸观感，`chroma_grade` 随之失去存在理由。

## 色彩空间渲染的现状

已落地的部分：

- **`ColorSpaces.cs`** —— 六个空间的原色定义（sRGB / AdobeRGB / DisplayP3 / ACEScg / Endura Premier / Kodak 2383，取自 DiVERE）与 Bradford 白点适配。矩阵对照公开参考值验证：三个标准空间的 RGB→XYZ 吻合到 6 位小数，往返为单位阵，跨 D65/D60 白点适配后白仍映到白。
- **`OutputRender.cs`** —— 色域变换 + gamut mapping（`Desaturate` 保色相保亮度，`Clip` 逐通道截断）+ 各空间的编码曲线。`FromSrgbEncoded` 处理管线已经烘进 sRGB TRC 的情况：逆变换回线性、变换、再按目标空间编码；sRGB 目标是逐位不变的 no-op。
- **`IccProfiles.Build(ColorSpaceDef)`** —— 为任意注册空间生成 ICC，D50 适配后的原色由色度坐标推导。sRGB / AdobeRGB 仍走原有硬编码路径，产物逐字节不变。
- **导出对话框**与 CLI `--color-space` 提供选择，嵌入的 Profile 与实际写入的像素一致。

验证覆盖：色域变换的往返、中性轴保持中性、in-gamut 像素不被 mapper 触碰、窄化方向上 desaturate 的亮度误差为 0 而 clip 会抬高亮度、ICC gamma 标签与 `Encode()` 实际施加的曲线一致、以及 TIFF/JPEG 落盘后 Profile 确实嵌入且正确。

**一个当前的实情**：gamut mapping 选项目前是**惰性的**，已在界面上禁用并注明。原因是所有可选目标空间都比 sRGB 宽，而反转结果目前就落在 sRGB 里，不存在超出色域的颜色。它要到工作空间换成 ACEScg 之后才开始起作用 —— 那正是下一步。

## 输入端表征：chroma_grade 真正的替代品

原本的计划是把工作空间换成 ACEScg。动手后发现这个方向站不住：

- **Stage 2 是 display-referred 的**：对比度以 0.5 为轴、gamma 与曲线钳制在 [0,1]、luma 权重就是 sRGB 的 0.2126/0.7152/0.0722。把 ACEScg 的值喂进去，每个滑块的含义都会静默改变，既有工程的 Stage 2 设置会渲染出不同结果。
- **更要紧的是,反转输出的本来就不是 sRGB**。它是相机/扫描仪的原生数据跑完密度运算的结果。管它叫 sRGB 是权宜的标签,改叫 ACEScg 同样武断 —— 数字没变,只是换了个说法。

真正的缺口是**输入端从未被表征**。这才是 chroma_grade 生根的地方:输入的原色未知,就没有变换可做,标量于是成了唯一的旋钮。

因此改为从相机自身的色彩数据取矩阵（LibRaw 的 `RgbCamera`，即相机 cam_xyz 与 XYZ→sRGB 的复合），在反相之后、Stage 2 之前应用：

- **位置**：必须在反相之后。密度运算（`t_base` 归一化、`wb_high`/`wb_offset`、`d_max`）全部标定在传感器自己的数字上，提前换空间会让这些测量全部失效。也必须在 Stage 2 之前，因为 Stage 2 的定义都以 sRGB 为准。
- **中性轴不动**：矩阵行和为 1，反相确立的白点原样保留。
- **实测效果**（Olympus E-M5 III）：色度整体展宽约 **1.32×**，且**逐色相不同**（六个探针上 1.14–1.53）。这正是 chroma_grade 用一个各向同性标量在模仿的事 —— 而各向异性是标量原理上做不到的。

扫描件不参与：ICC 路径已经完成表征，再做一次就是双重变换。LibRaw 不认识的相机也不参与，退回未表征的旧行为，而不是猜一个矩阵。

**迁移**：`roll_meta.characterise_input`，文件里没有这个字段就是 `false`。表征会明显改变颜色，因此**旧工程默认关闭**、保持原样，新建卷默认开启。用户可在「输入色彩表征」面板自行切换。

## 软打样

预览此前是"sRGB 编码后直接送屏"，中间没有任何色彩管理 —— `WriteableBitmap` 不带 ICC，Avalonia 也不做显示器变换。因此在广色域屏上看到的一直偏艳，这与本次改动无关，是既有事实。

现在底部工具条有「软打样」下拉：选中某个空间后，预览会走 sRGB → 该空间（含 gamut mapping）→ 回 sRGB 的往返，屏幕上呈现的就是导出到该空间会得到的样子。默认关闭，只影响预览，不影响导出文件，也不写进工程。

尚未做的：工作空间本身仍是隐含的 sRGB。要让 gamut mapping 在导出时真正起作用，仍需把工作空间加宽 —— 但如上所述，那一步的前提是先重做 Stage 2 的 display-referred 假设，不是简单换个标签。

**兼容性**：现存工程文件里存着 `chroma_grade: 3.05`，载入时保持原值不变；导出色彩空间默认 sRGB，即默认路径与从前逐位一致 —— 不静默改变用户已完成的作品。
