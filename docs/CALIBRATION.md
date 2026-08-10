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

`ColorSpaces.cs` 是这一步的地基，矩阵已对照公开参考值验证（sRGB / AdobeRGB / ACEScg 的 RGB→XYZ 均吻合到 6 位小数，往返为单位阵，白点跨 D65/D60 适配后仍映到白）。

**兼容性**：现存工程文件里存着 `chroma_grade: 3.05`，载入时保持原值不变，输出与从前一致 —— 不静默改变用户已完成的作品。
