# OpenRevelare — 更新日志

## v1.1.2（2026-08-10）

扫描件色彩修复。1.0 至 1.1.1 期间导入的扫描件颜色不正确，建议重新导入。

**修复**

- **扫描件 ICC 色彩管理**：载入扫描件时重新按其嵌入的 ICC Profile 处理色彩——
  先用 Profile 自带的三条 TRC 曲线逐通道线性化（扫描仪各通道 gamma 往往不等，
  统一按 sRGB 逆变换会留下随亮度反向的色偏，任何线性白平衡都纠不回来），
  再用 rXYZ/gXYZ/bXYZ 矩阵映射到 sRGB 线性光。无 ICC 或只有 LUT 的 Profile
  相应跳过。这套处理在 1.0 重写时缺失
- **扫描件色度补偿回到 1.0**：承上，ICC 矩阵已展开通道间色度差值，扫描件不再
  套用为相机 sensor 串扰标定的 `chroma_grade = 3.05`（RAW 仍为 3.05）。
  1.0 至 1.1.1 期间导入的扫描件色彩偏浓
- **导出的 ICC Profile 曲线方向反了**：写入的 sRGB TRC 存的是「线性→编码」，
  而 ICC 规范中该曲线是「编码→线性」。像素数据本身没错，但色彩管理软件读到
  的曲线是反的，会把画面判读得过亮。AdobeRGB 一档用的是 gamma 形式，方向本就
  正确，不受影响

---

A scan-colour fix. Scans imported between 1.0 and 1.1.1 came out with the wrong
colour and are worth re-importing.

**Fixed**

- **ICC colour management for scans** — loading a scan again honours its embedded
  ICC profile: the profile's own three TRC curves linearise each channel (scanner
  channel gammas are often unequal, and a blanket sRGB inverse leaves a cast that
  reverses with luminance, which no linear white balance can undo), then the
  rXYZ/gXYZ/bXYZ matrix maps the result into linear sRGB. Files without an ICC,
  and LUT-only profiles, skip the corresponding step. This handling was lost in
  the 1.0 rewrite
- **Chroma compensation back to 1.0 for scans** — following from the above, the
  ICC matrix has already unfolded the inter-channel chroma differences, so scans
  no longer take `chroma_grade = 3.05`, which is calibrated for camera sensor
  crosstalk (RAW still uses 3.05). Scans imported between 1.0 and 1.1.1 came out
  oversaturated
- **Exported ICC profiles had their curve the wrong way round** — the sRGB TRC was
  written as linear→encoded, whereas an ICC curve is encoded→linear. The pixel data
  was always correct, but colour-managed software read the inverted curve and showed
  the image far too light. The AdobeRGB profile uses the gamma form, which was
  already in the right direction, and is unaffected

---

## v1.1.1（2026-08-09）

**新增**

- **扫描件自动分割**：一张扫描件装着一整条底片时，导入即自动切成单帧，
  每格是独立的帧。弹窗里可双击增删分隔线、拖动外框四条边，也可调裁切
  余量（0~50%，默认 15%）——留出的余量供之后在裁切工具里把画面往外拉回。
  检测失败时给等分猜测。扫描件与 RAW 不能混选导入
- **工具提示框可关闭**：裁切/取样提示固定在画面顶部，会挡住高幅裁切框的
  手柄，现在可以关掉（回车应用、Esc 取消依然可用），重新进入工具时提示回来

**修复**

- 齿孔遮罩在旋转或裁切过的帧上不再错位

**性能**

- LCC 平场载入快约 275×（60 MP 实测 ~126 s → 456 ms）


---

**Added**

- **Automatic scan splitting** — when one scan holds a whole strip of film, it is
  split into individual frames on import. In the dialog you can double-click to
  add or remove dividers, drag any of the four outer edges, and set the crop
  margin (0–50%, default 15%) — that margin is what lets you pull the picture
  back out later in the crop tool. Falls back to an even guess if detection
  fails. Scans and RAW files cannot be imported in the same selection
- **Dismissible tool hint** — the crop/sampling hint sits at the top of the image
  and covered the handles of tall crop boxes; it can now be closed (Enter to
  apply and Esc to cancel still work), and returns when you re-enter the tool

**Fixed**

- Sprocket mask alignment on rotated or cropped frames

**Performance**

- LCC flat-field loading ~275× faster (~126 s → 456 ms at 60 MP)


## v1.1.0（2026-08-06）

首个开源版本，基于 Revelare 1.0.0 重命名并开放全部功能。
C# / .NET 8 + Avalonia，Windows / Linux / macOS。



**新增**

- **界面中英双语**：默认跟随系统语言，偏好设置可锁定中文 / English，切换即时生效，
  已开着的窗口当场重绘；菜单栏两种语言下均保持英文
- **图库分类筛选**：侧栏按画幅 / 胶卷 / 相机 / 冲洗店 / 年份分面筛选，搜索覆盖所有字段；
  导入弹窗收集卷信息；印样标识条精简为相机 / 胶卷 / 卷号 + 冲洗店 / 日期 / 地点 / 备注
- **导出选项弹窗**：文件选取前先选格式 / 压缩 / 色彩空间 / 冲突策略；JPEG 支持嵌入 ICC profile
- **DNG 缓存跨会话持久化**：重载历史卷不再重跑 Adobe DNG Converter 转换；
  偏好设置可开关，支持目录 / 上限自定义
- **更新检测双镜像 race**
- **授权 / 激活系统全部移除**

**修复**

- 整卷片基估计改为同源采样并跳过削顶平台：三通道不再各自独立取极值，
  避免把虚假色偏烧进密度反转的基准
- 导出改为暂存后原子改名：写到一半崩溃或磁盘满不会损坏上一次成功的导出
- 整卷导出不再静默覆盖磁盘上已存在的同名文件


