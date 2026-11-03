# 镜头元数据自动校正畸变与暗角 (Lens Correction Metadata)

本文档说明 OpenRevelare 中自动读取相机 RAW 元数据并校正翻拍镜头畸变与暗角的设计原理、数学映射与使用方法。

---

## 1. 背景与问题

胶片翻拍（Camera Scanning）通常使用微距镜头或长焦微距变焦镜头（例如用户使用的 Sony FE 70-200mm F4 Macro G OSS II）。
翻拍镜头在翻拍距离下会产生轻微的几何畸变与边缘暗角：
- **枕形畸变**：使片夹导轨和胶片黑边向中心轻度弯曲；
- **边缘暗角**：镜头四角进光量比中心低约 0.15～0.30 EV，负片经高反差反相后会导致四个角落反常泛白、偏色。

在 OpenRevelare 原架构中，虽然内置了手动校正滑块（`畸变 DistortionK1`、`暗角 VignetteAmount`、`范围 VignetteFalloff`），但用户需要肉眼拉动滑块观察调整。

---

## 2. 镜头校正参数数学模型与映射

### 2.1 OpenRevelare 的校正模型

1. **畸变校正 (`LensCorrections.ApplyDistortion`)**：
   采用标准单参数径向畸变反向映射模型：
   $$\text{scale}(r) = 1 + k_1 \cdot \left(\frac{d}{\text{norm}}\right)^2$$
   其中 $\text{norm} = \min(c_x, c_y) = \frac{H - 1}{2}$。
   对于 3:2 全画幅图像，角隅半径平方为：
   $$\left(\frac{r_{\max}}{\text{norm}}\right)^2 = 1.5^2 + 1.0^2 = 3.25$$

2. **暗角校正 (`LensCorrections.ApplyVignette`)**：
   采用径向增益补偿：
   $$\text{gain}(r) = 1 + \text{amount} \cdot r^{\text{falloff}}, \quad r = \frac{d}{r_{\max}} \in [0, 1]$$
   默认 `falloff = 2.5`。

### 2.2 Sony ARW 固件校正参数映射

Sony 在 ARW 中内嵌了相机固件根据当前对焦距离与变焦焦距实时计算的镜头校正样条样点。
按实测，Sony **ILCE-7M5** 的 ARW 把这两个 tag 写在 `ExifSubIfdDirectory`（值为 17 个 `short[]`），
而 `SonyType1MakernoteDirectory`（该目录本身有上百个 tag）里并没有 `0x7037` / `0x7032`。
不同机型与写入器把 Sony 厂商 tag 放进哪个目录并不统一，因此两处都查，任一处命中即用固件参数。
参数是 16 位整型，MetadataExtractor 按文件里写的类型给出 `short[]`（SSHORT）或 `ushort[]`（SHORT），两种都接受。
- **Tag `0x7037` (`DistortionCorrParams`)**：包含 17 个 16 位整数，第一个为样点数 $n = 16$，后 16 个为从中心 $r=0$ 到边缘 $r=1$ 的畸变位移因子。
  Sony 样条位移因子为 $g(r) = 1 + \frac{f(r)}{16384}$。
  将其与 OpenRevelare 的 $\text{scale}(r) = 1 + k_1 \cdot 3.25 \cdot r^2$ 进行加权最小二乘拟合：
  $$k_1 = \frac{\sum_{i=1}^{n} x_i y_i}{\sum_{i=1}^{n} x_i^2}, \quad x_i = 3.25 \cdot \left(\frac{i - 1}{n - 1}\right)^2, \quad y_i = \frac{\text{knot}_i}{16384}$$
  以实测样片（FE 70-200mm F4 Macro G OSS II @ 126mm，样点见上）为例：
  拟合计算得 $k_1 = +0.0118$（$\sum x y / \sum x^2 = 0.01184$）。
  同一卷 109 张 ARW 实测：全部命中固件参数，$k_1$ 落在 $0.0118 \sim 0.0119$，角隅暗角随之为 0.168。

- **Tag `0x7032` (`VignettingCorrParams`)**：包含 17 个 16 位整数，代表各径向位置的曝光补偿增益。
  角隅处的理论光通量增益为：
  $$\text{amount} = 2^{\frac{\text{knot}_{16}}{16384}} - 1.0$$
  以实测样片（角隅样点 3664）为例，$\text{amount} = 2^{3664/16384} - 1 \approx 0.168$，
  与同卷 109 张 ARW 的实测输出一致。
  用观片灯全白空照样片交叉验证时，角隅亮度衰减约 0.85，对应需补偿增益 $1/0.85 - 1 = 0.176$，两者相差约 0.008。

### 2.3 DNG OpcodeList3 支持

对于 DNG 文件（如扫描仪导出或 Lightroom 线性 DNG），解析 Tag `51022` (`0xC74E` / `OpcodeList3`)。
Opcode 编号以 Adobe DNG SDK (`dng_opcodes.h`) 为准：**1 = `WarpRectilinear`**、**3 = `FixVignetteRadial`**、
4 = `FixBadPixelsConstant`（不是径向校正，遇到时忽略）：
- **Opcode 1 (`WarpRectilinear`)**：uint32 平面数（1 或 3），随后每平面 6 个 double（$k_0 \dots k_5$，
  按 $r^2$ 的多项式），最后是光学中心 2 个 double。取第一个平面的 $k_1$，
  按 $k_{1,\text{OpenRevelare}} = k_{1,\text{DNG}} / 3.25$ 折算至 OpenRevelare 归一化量纲。
- **Opcode 3 (`FixVignetteRadial`)**：$k_0 \dots k_4$ 共 5 个 double，随后是光学中心 2 个 double（共 56 字节）。
  规范数据把中心增益归一化为 1（$k_0 \approx 1$），故角隅增益为系数和，OpenRevelare 的
  `amount = 角隅增益 - 1`；少数写入器直接存"增益 - 1"（$k_0 \approx 0$），这种形状也接受。

### 2.4 翻拍镜头库预设回退 (Heuristic Fallback)

如果图像未包含厂商内嵌的 MakerNote 样条（例如经转接环转接、或元数据丢失）：
从标准 EXIF 提取 `LensModel` (`0xA434`)、`FocalLength`、`FNumber`，自动匹配翻拍常用镜头预设。
镜头型号只认 `0xA434`：`0xA431` 是机身序列号，拿它查预设既不会命中，还会把用户已手动调好的校正
覆盖成"未内置校正曲线"（$k_1 = 0$）。
- Sony FE 70-200mm F4 Macro G OSS II
- Sony FE 90mm F2.8 Macro G OSS
- Sony FE 50mm F2.8 Macro
- Sigma 105mm F2.8 DG DN Macro | Art
- Canon RF 100mm F2.8 L MACRO IS USM
- Nikon NIKKOR Z MC 105mm f/2.8 VR S
- AF-S Micro Nikkor 60mm f/2.8G ED 等。

---

## 3. UI 交互设计

在「输入与校正详情」面板中的「镜头与平场校正」部分：
1. **自动读取镜头校正**：
   点击后异步读取当前帧的 RAW 元数据，自动识别镜头型号与固件参数，直接填入「畸变」与「暗角」滑块，并在下方显示识别信息（例如：`FE 70-200mm F4 Macro G OSS II · Sony RAW 固件参数`）。
2. **应用到整卷**：
   将已检测的镜头参数一键同步给选中的帧或整卷所有帧，无需每张重新解析。
3. **完全无损与撤销支持**：
   所有操作均支持全局 Undo/Redo，且参数直接持久化至 `.revelare` 工程文件。
