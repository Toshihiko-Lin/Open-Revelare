# OpenRevelare — 更新日志

## v1.2.0（2026-08-11）

色彩管线大修。反相现在跑在宽色域的 ACEScg 里，帧编辑跑在你选定的输出空间里，
导出所见即所得。**既有工程的画面会与上一版不同**，且滑块数值的含义已改变。

**修复**

- **工作空间加宽到 ACEScg**：反相与三通道对齐此前跑在 sRGB 里，饱和的染料在
  输出变换有机会安置它之前就被截掉了。现在密度域上游是 ACEScg（场景参考、
  宽于任何输出空间），颜色能完整穿过对数域。这一步补上后，色域映射（朝等亮度
  中性轴收缩）才真正开始起作用——此前"更宽"的输出空间几乎是无操作
- **补上 Cineon 第 4 步**：标准流程的第 4 步是「色彩空间与 Gamma 一起转换」，
  此前只做了 Gamma 那一半，色彩空间的转换从未发生。现在反相结果经完整的
  第 4 步进入输出空间，帧编辑（色阶/对比度/曲线/饱和度）在该空间内进行——
  这些操作的定义本就依赖显示参考空间：对比度绕 0.5 转是因为 0.5 是中灰
- **导出不再二次转换**：帧编辑既然已在目标空间内跑完，导出便直接写出屏幕上的
  像素，只附上对应的 ICC。此前是"渲染成 sRGB、导出时再转一次"，屏幕与文件
  始终是两次不同的渲染

**变更**

- **输出空间移到主窗口**，不在导出弹窗里选。它改变渲染结果，因此是胶卷参数、
  会保存进工程。换空间时滑块**数值保留、画面随之改变**——这些数值的含义本就是
  「在当前输出空间里调这么多」
- **软打样移除**：它原本是对一次导出的模拟，而现在预览显示的就是真实结果
- **输出意图移到导出弹窗**，改为「导出为场景线性 ACEScg」勾选框。"线性"描述的
  是某一次导出（交给外部调色的中间文件），而不是工作方式，因此不该改变预览
- **显示器色彩管理移除**：预览位图原样交给系统。要让屏幕观感准确，正确做法是
  用校色仪实测生成 ICC 并注册为系统显示器配置文件，由操作系统统一转换
- **撤下 Kodak Endura Premier / Kodak 2383 输出空间**：实测其基色三角面积分别是
  sRGB 的 127% 与 141%，比 Adobe RGB 还宽，而实体相纸的可呈现色域窄于 sRGB。
  这两组数描述的是染料集的编码基色，不是介质能呈现的色域，选中它们执行的是
  色域扩张加白点偏移，而非复现暗房或院线观感——那种观感在密度曲线和 3D LUT
  里。想要院线观感，请导出场景线性 ACEScg 后到调色软件里套 Print LUT。
  旧工程指定这两个空间时会迁移到 sRGB 并在状态栏说明

---

A colour-pipeline overhaul. The inversion now runs in wide-gamut ACEScg and frame
edits run in the output space you pick, so the export is what you already see.
**Existing projects will render differently**, and the adjustment sliders have
changed meaning.

**Fixed**

- **Working space widened to ACEScg** — the inversion and three-channel alignment
  used to run in sRGB, so a saturated dye was clipped before the output transform
  could place it. The density domain is now fed from ACEScg (scene-referred, wider
  than any output space) and colour survives the log domain intact. Only with this
  in place does gamut mapping (shrinking toward the luminance-matched neutral) do
  any work — until now the "wider" output spaces were very nearly a no-op
- **Cineon step 4 restored** — the standard workflow's step 4 converts colour space
  AND gamma together; only the gamma half was ever done, and the colour-space half
  never happened. The inverted positive now goes through the whole of step 4 into
  the output space, and frame edits (levels, contrast, curves, saturation) run
  inside it — operations whose definitions require a display-referred space, since
  contrast pivots on 0.5 precisely because 0.5 is mid-grey
- **No second conversion on export** — with frame edits already finished in the
  target space, the export writes the pixels on screen and simply attaches the
  matching ICC. Previously it rendered to sRGB and converted again at export, so
  screen and file were always two different renders

**Changed**

- **Output space moved to the main window**, out of the export dialog. It changes
  the render, so it is a roll parameter and is saved with the project. Switching
  keeps the slider VALUES and lets the picture change — those numbers always meant
  "this much adjustment in the current output space"
- **Soft proofing removed** — it simulated an export the render was not performing;
  the preview now shows the real thing
- **Output intent moved to the export dialog** as an "export scene-linear ACEScg"
  checkbox. "Linear" describes one export (an intermediate for someone else's
  grading suite), not how a roll is worked on, so it should not alter the preview
- **Display colour management removed** — the preview bitmap goes to the system as
  is. For an accurate on-screen look, calibrate the display with a colorimeter and
  register the resulting ICC as the system display profile, letting the OS handle
  the conversion
- **Kodak Endura Premier / Kodak 2383 withdrawn as output spaces** — their primary
  triangles measure 127% and 141% of sRGB's area, wider than Adobe RGB, whereas
  real photographic paper reproduces a gamut narrower than sRGB. Those figures
  describe the dye set's encoding primaries, not what the medium can reproduce, so
  selecting them performed a gamut expansion plus a white-point shift rather than
  reproducing a darkroom or projection look — that look lives in density curves and
  a 3D LUT. For a projection look, export scene-linear ACEScg and apply a print LUT
  in a grading application. Older projects naming these spaces migrate to sRGB and
  say so in the status bar

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


