# OpenRevelare — 更新日志

## v1.2.2（2026-08-11）

收尾 1.2.1 没修完的裁切问题，连带把**转过向**之后裁切与片基采样的错位一并修
掉——同一类问题的根子。外加一处 TIFF 色彩管理的修正。

**修复**

- **带 ICC 配置文件的 TIFF，色度被放大**。这类文件在加载时被转换到 sRGB 原色，但管线
  的工作空间是 ACEScg；第 4 步随后又按 ACEScg → sRGB 转出去，等于逆了一个从未施加过的
  变换。结果是色域被往外撑：红、蓝饱和度约 ×1.13，绿约 ×1.4，中性灰还会偏色。现在 ICC
  矩阵直接落到工作空间。RAW 不受影响；不带配置文件的 TIFF 也照旧原样通过。

  **注意**：已经按旧行为调过的 TIFF 卷，这次改动后画面会变，片基（t_base）需要重新取样。
- **裁切后画面显示不正确**。裁切框在屏幕上是对的，应用后比例会变、位置也会漂：
  macOS 上是框与指针不在同一个盒子里（选 1:1 预设尤其明显），转过向的帧上则是
  裁切矩形被存成了原始轴向。后者本该存在「已转向」的坐标系里——管线先转向后裁切，
  旋转按钮也按这个前提让裁切跟着画面走。往返自洽掩盖了它：重新打开裁切工具时又
  转了回去，真正吃亏的是直接读这个矩形的渲染管线。

  **注意**：在转过向的帧上存过裁切的工程文件，里面存的就是错的矩形。这次修复后
  按正确的读法解释，位置相当于变了，需要重裁；未转向时存的不受影响。
- **负片视图与正片朝向对不上**。把横躺的扫描转正之后再点片基采样，预览会翻回未
  转向的样子——负片视图原本完全跳过几何链，而正片走的是完整管线。现在跟随 90°
  转向和翻转（拉直和裁切仍然不施加：前者会带进填充角，后者会把要采样的片基边缘
  挡掉）。承上，框选坐标此前也没跟着映射，取样取的是原始缓冲区里对应的另一块——
  通常是画面对角线另一头，几乎必然触发「采样区偏暗」的警告；D-max 和偏移采样同理。
- **负片视图下放大，看到的是去色罩后的画面**。框选片基时放大到触发局部高清渲染，
  那一块会变成处理完的正片，而不是正在取样的原始负片。按住对比看原片时同理。
- 裁切或清除裁切后回到「适应窗口」，画面不再放大着偏在一边。

**改进**

- **拖角改变裁切框大小时，预设比例可以在横竖之间切换**（参考 Lightroom）。选了
  3:2 之后想要 2:3，以前只能去按旋转按钮——但那转的是画面，不是画幅。现在往竖长
  方向拖过一定幅度，锁定的比例就翻成竖构图，往回拖再翻回来。只有**拖角**才触发：
  边 handle 只动一个轴，那个「形状」是另一个轴不动造成的假象。切换带迟滞，指针
  停在对角线附近时画框不会来回跳。

---

**Fixed**

- **Profiled TIFFs came out with amplified chroma.** A TIFF carrying an ICC profile was
  converted into sRGB primaries on load, but the pipeline works in ACEScg — so step 4
  then converted it ACEScg → sRGB, undoing a transform that had never been applied.
  That stretched the gamut outward: saturation rose ~1.13× on red and blue, ~1.4× on
  green, and neutrals picked up a cast. The ICC matrix now lands in the working space
  directly. RAW was never affected, and TIFFs without a profile still pass through
  untouched.

  **Note**: rolls of TIFFs already adjusted against the old behaviour will shift, and
  their film base (t_base) needs re-sampling.
- **The crop was not displayed correctly after applying it.** The frame looked right on
  screen, but the applied crop came out with a different ratio and drifted out of
  position. On macOS the frame and the pointer were not in the same box (most visible
  with the 1:1 preset); on a rotated frame the crop rect was stored against the raw
  axes instead. It is meant to be stored against the ORIENTED frame — the pipeline
  orients before it crops, and the rotate buttons carry the crop with the picture on
  that assumption. A self-consistent round trip hid it: re-opening the crop tool
  re-applied the turn, so what actually suffered was the render pipeline, which reads
  the rect directly.

  **Note**: projects with a crop saved on a rotated frame hold a wrongly-stored rect.
  After this fix those crops are read the correct way, which effectively moves them —
  they need re-cropping. Crops saved with no rotation applied are unaffected.
- **The negative view did not agree with the positive's orientation.** Straightening a
  sideways scan and then arming the film-base tool flipped the preview back to its
  un-rotated orientation — the negative view skipped the geometry chain entirely while
  the positive went through the whole pipeline. It now follows the quarter turns and
  flips (straighten and crop are still not applied: the first would bring in fill
  corners, the second would hide the very film base being sampled). Following from
  that, selection coordinates were not mapped with the picture either, so the sample
  came from a different part of the raw buffer — usually the opposite corner, which
  almost always tripped the "region looks too dark" warning. Same for the D-max and
  offset samplers.
- **Zooming in on the negative view showed the de-masked picture.** Zooming far enough
  to trigger the sharp-patch render while picking the film base replaced that region
  with the finished positive instead of the negative being sampled. Same for the
  hold-to-compare view.
- Applying or clearing a crop now returns to fit, instead of leaving the picture
  magnified and off to one side.

**Improved**

- **Dragging a corner handle can now flip a preset ratio between landscape and
  portrait** (as Lightroom does). Getting 2:3 out of a 3:2 preset previously meant
  reaching for the rotate buttons — but those turn the PICTURE, not the format. Drag
  far enough toward portrait and the locked ratio flips; drag back and it returns.
  Only a CORNER triggers it: an edge handle moves one axis only, so its "shape" is an
  artefact of the other axis standing still. The swap has hysteresis, so the frame
  does not flutter while the pointer tracks the diagonal.

## v1.2.1（2026-08-11）

**新增**

- **导入后自动整卷分析去色罩**：勾选后自动完成片基、白平衡、密度与色阶的标定，
  无需框选任何区域。分析整卷并汇总成一组参数应用到所有帧，因此同一卷观感一致，
  不会把夕阳或钨丝灯的氛围当成偏色修掉。

  当前帧先出结果，整卷分析在后台继续——需要解码全卷，视帧数和机器约每帧数秒，
  完成前缩略图与参数仍会变动。

  开关在导入弹窗里（偏好设置 → 导入 设的是它的默认值）。不勾选则完全不做自动
  测量，所有参数保持默认，整卷标定交给手动。

**修复**

- **macOS：裁切后画面显示不正确**。

---

**Added**

- **Roll-wide mask removal on import** — tick it and the film base, white balance,
  density and levels are all calibrated for you, with no region selection anywhere.
  It analyses the whole roll and pools one parameter set for every frame, so a roll
  stays visually consistent and a sunset or tungsten interior is not corrected away
  as if it were a cast.

  The current frame is solved first and the roll-wide pass continues in the
  background — it decodes every frame, roughly a few seconds each, and thumbnails
  and parameters keep changing until it finishes.

  The switch is in the import dialog (偏好设置 → 导入 sets its default). Left
  unticked, nothing is measured and the whole calibration is yours to do by hand.

**Fixed**

- **macOS: the crop was not displayed correctly after applying it.**

## v1.2.0（2026-08-11）

色彩管理重做。
现在色彩管理完善，`chroma_grade = 3.05` 标量随之取消。

> **既有工程的画面会与本版不同，建议重新处理。** 滑块数值本身保留，但它们的含义
> 变了——现在是「在当前输出空间里调这么多」。1.1.2 及更早版本调好的卷，反差与
> 饱和度都会有可见变化。

**修复**

- **取消 `chroma_grade`（默认 3.05）**：这个参数用一个各向同性的标量去补偿一个
  各向异性、随色相变化的色域关系，本就表达不了。它存在的原因是管线缺少色彩空间
  声明；声明补上后，颜色由真实的色域变换给出，参数整体移除（不是改默认值）
- **工作空间加宽到 ACEScg**：反相与三通道对齐颜色能完整穿过对数域

**变更**

- **输出空间移到主窗口**（sRGB / Display P3 / Adobe RGB），不在导出弹窗里选。
  它改变渲染结果，因此是胶卷参数、会保存进工程
- **输出意图移到导出弹窗**，改为「导出为场景线性 ACEScg」勾选框。"线性"描述的是
  某一次导出（交给外部调色的中间文件），而不是工作方式，因此不该改变预览

---

Colour management rebuilt.
Now that colour management is in place, the `chroma_grade = 3.05` scalar is retired.

> **Existing projects will render differently and are worth reprocessing.** The
> slider values are preserved, but their meaning has changed — they now mean "this
> much adjustment in the current output space". Rolls graded on 1.1.2 or earlier
> will show visible differences in contrast and saturation.

**Fixed**

- **`chroma_grade` (default 3.05) retired** — an isotropic scalar cannot express
  what is an anisotropic, hue-dependent gamut relationship. It existed because the
  pipeline declared no colour spaces; now that it does, colour comes from real
  gamut conversion and the parameter is removed outright, not merely defaulted away
- **Working space widened to ACEScg** — the inversion and three-channel alignment
  can now pass through the log domain with colour intact

**Changed**

- **Output space moved to the main window** (sRGB / Display P3 / Adobe RGB), out of
  the export dialog. It changes the render, so it is a roll parameter saved with the
  project
- **Output intent moved to the export dialog** as an "export scene-linear ACEScg"
  checkbox. "Linear" describes one export (an intermediate for someone else's
  grading suite), not how a roll is worked on, so it should not alter the preview

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


