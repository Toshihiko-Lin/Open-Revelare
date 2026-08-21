# OpenRevelare — 更新日志

## v1.6.0（2026-08-22）

优化了色彩管线，在此之上新增了印片模拟与扫描件分格，并修复了一批相关问题。

**新增**

- **新增【胶片风格】**。可以在【输出空间】旁边选一张印片（如 Kodak 2383、Fujifilm 3513DI），或导入自定义filmprint lut
  

- **扫描件分格现在可以识别双列**。平板扫描仪的片夹一次能放好几条片——6×12 的一版就是两列各六格——现在每一条都会被识别并各自独立分格。


**修复**

- **修复了某些源文件通道值为 0 时颜色不对的问题**。齿孔黑边、扫描件黑边、部分相机 RAW 的
  填充边此前会出现本不该有的偏色，现在正确地渲染为白。

- **修复了【自动白点】与【自动（整卷）】在同一张片子上给出不同亮端的问题**。

- **修复了智能白平衡把画面拉过曝的问题**。

- **修复了同步裁切让预裁切出来的各副本变成同一张照片的问题**。

- **修复了重开工程后分格卷第一帧预裁切失效的问题**。

- **理论上修复了 macOS 上使用裁切工具会闪退的问题**。

**改进**

- **优化了整卷标定的稳定性**。

- **自动色阶不再由任何自动流程调用**。

- **查看负片时使用相机的机内白平衡以获得更好的观感**


---

The colour pipeline has been reworked; on top of that, print-film emulation and scan splitting are
new, along with a batch of related fixes.

**Added**

- **New: film look.** Pick a print stock (Kodak 2383, Fujifilm 3513DI) beside "output space", or
  import your own film print LUT.

- **Scan splitting now recognises two-column scans.** A flatbed holder takes several strips at
  once — a 6×12 sheet is two columns of six — and every strip is now detected and split on its own.

**Fixed**

- **Fixed wrong colour where a source file has a channel at zero.** Sprocket edges, scan borders
  and the padding some camera RAWs carry showed a colour cast they should not have; these now
  render as white.

- **Fixed "Auto white point" and "Auto (whole roll)" giving different highlight ends on the same
  frame.**

- **Fixed smart white balance pushing the picture into overexposure.**

- **Fixed sync crop turning every pre-split copy into the same photograph.**

- **Fixed the first frame of a split roll losing its pre-crop after reopening the project.**

- **Should fix the crop tool crashing on macOS.**

**Improved**

- **More stable roll calibration.**

- **Auto levels is no longer invoked by any automatic flow.**

- **The negative is now viewed with the camera's own white balance, which reads more naturally.**

---

## v1.5.2（2026-08-19）

**修复**

- **macOS 上导出的印样颜色反相**，蓝色变成橙色。软件内的预览一直是正确的，只有导出的
  文件受影响。
- **分割帧裁切后翻转，裁切框会漂移**，胶条首尾帧上甚至会跑出画面。普通帧不受影响。

**改进**

- **切换裁切比例时保留当前构图**。以现有裁切框为基准生长，保持中心，横竖方向跟随现有框
  ——竖构图切到 3:2 会得到 2:3。此前每次切换预设都会丢弃已摆好的框。
- **曲线端点可以拖动**，用来在曲线上设黑白场。端点显示为方形，可沿所在边滑动，也可离开
  边形成褪色黑，右键复位。端点与【黑场】【白场】滑块相互独立，各调各的。同时调整黑白
  两端仍是严格直线。

  旧工程读入后渲染结果不变。

---

**Fixed**

- **On macOS the exported contact sheet had red and blue swapped** — blue skies came out
  orange. The in-app preview was always correct; only the saved file was affected.
- **Flipping a split frame after cropping made the crop rect drift**, badly enough on the
  first and last frame of a strip that it left the picture. Ordinary frames are unaffected.

**Improved**

- **Switching crop ratios keeps your composition.** The new rect grows from the existing one
  around the same centre and follows its orientation — a portrait crop switched to 3:2 gives
  you 2:3. Previously each preset threw away the rect you had placed.
- **Curve endpoints can be dragged**, so black and white points can be set on the curve
  itself. They show as squares, slide along their edge, can leave it for a faded black, and
  right-click resets them. They are independent of the Black point / White point sliders.
  Adjusting both ends still gives a strictly straight line.

  Existing projects render exactly as before.

---

## v1.5.1（2026-08-15）

**新增**

- **过曝/欠曝指示器**。直方图下方的按钮或快捷键 J 开启，过曝区域标红、欠曝区域标蓝。
  纯预览诊断，不影响导出。

**修复**

- **整卷自动后再点「自动（单张）」或「自动黑点」，结果会覆盖到其他帧**。现在只改当前帧。

---

**Added**

- **Over/under-exposure indicator.** Toggle with the button below the histogram or the J key
  — over-exposed areas are overlaid in red, under-exposed in blue. View-only; exports are
  unaffected.

**Fixed**

- **After "Auto (whole roll)", clicking "Auto (this frame)" or "Auto black point" overwrote
  other frames.** Only the current frame is changed now.

---

## v1.5.0（2026-08-15）

整卷校准面板重构：反相只由密度端点驱动，冗余控件全部移除。导入流程简化，自动标定更可靠，
macOS 平台适配补齐。

**变更**

- **反相参数精简为六个自由度**，色偏改为直接展开三个通道的绝对密度。
- **两端统一显示为绝对密度**，各带手动 + 自动按钮。
- **采样按钮合并**：「框选亮部」「框选 D_max」合为【高光采样】，「框选片基」「框选暗部」
  合为【片基采样】。两端标定不再有先后顺序要求。
- **导入后不再弹「齿孔遮罩确认」窗**，阈值自动测出并应用。

**新增**

- **「自动（整卷）」「自动（单张）」两个按钮**：前者汇总全卷参数，后者只算当前帧。
- **macOS 顶端原生菜单栏**。File / Edit / View / Help 及应用菜单，⌘Q、⌘W 由系统提供。
  其余平台不变。

**改进**

- **自动识别并排除非胶片区域**。
- **自动识别并采样片基**，测不到时提示手动标定。

**修复**

- D-max 被挡光板和片边带偏。
- 多条同时分割时，第一张的预裁切被抹掉。
- 部分相机 RAW 无法顺利导入。
- macOS 上 ⌘ 快捷键全部失效。
- macOS 文件选择框选不进大写扩展名的 RAW（.CR2、.NEF、.3FR 等）。
- macOS 上导入大 RAW 容易卡死。

---

The roll-calibration panel is rebuilt: the inversion is driven by density endpoints alone and
redundant controls are gone. Import is simpler, automatic calibration is more reliable, and
macOS platform support is filled in.

**Changed**

- **Inversion parameters reduced to six degrees of freedom** — colour cast is now adjusted by
  expanding three channels of absolute density directly.
- **Both ends display absolute densities**, each with manual and automatic buttons.
- **Sampling buttons merged**: "Select highlight" and "Select D_max" become **Sample the
  highlight**; "Select film base" and "Select shadow" become **Sample the film base**.
  Calibration order no longer matters.
- **No more sprocket-mask dialog after import** — the threshold is measured and applied
  automatically.

**Added**

- **"Auto (whole roll)" and "Auto (this frame)" buttons** — the first pools roll-wide
  parameters, the second solves the current frame alone.
- **macOS native menu bar.** File / Edit / View / Help plus the application menu; ⌘Q and ⌘W
  come from the system. Other platforms are unchanged.

**Improved**

- **Non-film areas are detected and excluded automatically.**
- **The film base is detected and sampled automatically**, with a prompt to calibrate by hand
  when it cannot be found.

**Fixed**

- D-max was biased by the light blocker or film edge.
- Splitting multiple strips at once erased the first frame's pre-crop.
- Some camera RAW formats could not be imported.
- macOS: ⌘ shortcuts did not work.
- macOS: the file picker rejected uppercase RAW extensions (.CR2, .NEF, .3FR, etc.).
- macOS: importing large RAWs could freeze the app.

---

## v1.3.0（2026-08-13）

反相改用逐通道密度端点：黑白两端量出来，中间是算出来的。胶片条的帧顺序现在可以自己排。
程序内的文档按 Markdown 渲染，公式真正排版。

**变更**

- **反相由密度端点决定**。片基是黑端，D-max 是白端，三个通道各自归一化。面板上「反差
  （相纸号数）」换成只读的「密度端点」；整卷自动标定会一并量出端点，正常流程下无需额外
  操作。标定没有先暗后亮的顺序要求。

  已有工程渲染结果不变；重跑整卷自动标定或采样一次 D-max 即可切换到新模型。

- **「快捷键与上手…」改为「快捷键…」**，只留快捷键表和三条操作提示，流程说明统一在
  「操作指引」里。

**新增**

- **拖动缩略图调整帧顺序**。高亮横线指示落点，拖到顶端或底端会自动滚动，虚拟副本跟着母帧
  一起移动。顺序随工程保存，也决定印样的排列，右键「按文件名排序」可恢复。
- **文档查看器渲染 Markdown，公式按 LaTeX 排版**。左侧目录由文档标题自动生成。

**改进**

- **「操作指引」按现行界面重写**，顺序照实际操作走：输入路径 → 翻拍要求 → 导入 → 整卷
  校准 → 几何 → 帧编辑 → 整卷操作 → 导出 → Path A → 快捷键。
- **「技术原理」按现行实现重写**，并新增「已知限制：输入原色未声明」一节。

**修复**

- **导入后缩略图不按文件名排序**。现在统一按文件名排，数字段按数值比较；向已有的卷追加
  图像时只排新增的这批，不动已有顺序。
- **片基采样误报「采样区偏暗」**。橙色片基的蓝通道天然偏低，正常框选也会触发提示。现在按
  总透光量判断，误采到画面或深阴影仍会提示。
- **自动 D-max 检测被片边干扰**导致偏色。齿孔与片边不再计入。

---

The inversion model changed: one gamma shared across three channels gives way to per-channel
density endpoints, and the "contrast" knob goes with it — the two ends are measured and the
middle follows. Frame order in the film strip is now yours to set, and the in-app documents
are rendered rather than shown as source.

**Changed**

- **The inversion is decided by density endpoints.** The film base is the black end, D-max the
  white end, each channel normalised on its own. On screen, "Contrast (paper grade)" gives way
  to a read-only "Density endpoints"; the roll-wide auto calibration measures them along the
  way, so the ordinary route needs no extra step, and calibration no longer has to go dark
  before light.

  Existing projects render as they did. Re-run the roll-wide auto calibration, or sample D-max
  once, to move them onto the new model.

- **"Keys and getting started…" is now "Keyboard shortcuts…"** — the key table plus three
  notes, with the workflow documented in the user guide alone.

**Added**

- **Drag a thumbnail to reorder frames.** A highlighted line shows where it would land,
  dragging to an edge scrolls the strip, and a virtual copy travels with its parent frame. The
  order is saved with the project and sets the contact sheet's layout; right-click → "Sort by
  file name" puts it back.
- **The document viewer renders Markdown, and formulas are typeset as LaTeX.** The table of
  contents on the left is built from the document's own headings.

**Improved**

- **The user guide was rewritten around the current interface**, following the order you work
  in: input paths → copy-stand requirements → import → roll calibration → geometry → frame
  edit → roll-wide operations → export → Path A → keys.
- **"How it works" was rewritten against the current implementation**, with a new "Known
  limitation: input primaries are not declared" section.

**Fixed**

- **Thumbnails were not sorted by file name after an import.** Everything is now sorted by file
  name with digit runs compared as numbers; adding to an existing roll sorts only the new batch
  and appends it.
- **Film-base sampling wrongly reported "the sampled region looks dark".** The orange base has
  a naturally low blue channel, so a perfectly good selection could set the warning off. It now
  judges by total transmission, and still warns if you land on the picture or a deep shadow.
- **Automatic D-max detection was thrown off by the film edge** and left a cast. Sprockets and
  film edge are now excluded.

---

## v1.2.2（2026-08-11）

裁切与片基采样修好了，转过向的照片也不再出错。另有一处 TIFF 色彩管理的修正。

**修复**

- **带 ICC 配置文件的 TIFF，饱和度被放大**，中性灰还会偏色。RAW 与不带配置文件的 TIFF
  不受影响。

  **注意**：已按旧行为调过的 TIFF 卷，画面会变，片基（t_base）需要重新取样。
- **裁切后比例和位置都不对**。macOS 上尤其明显，转过向的照片上必然出错。

  **注意**：在转过向的帧上存过裁切的工程需要重裁；未转向时存的不受影响。
- **转过向的照片，片基采样取错位置**。现在负片视图跟随转向与翻转，框选、D-max 和偏移采样
  都取到框住的地方。
- **负片视图下放大，看到的是去色罩后的正片**。按住对比看原片时同理。
- 裁切或清除裁切后回到「适应窗口」，画面不再放大着偏在一边。

**改进**

- **拖角改变裁切框大小时，预设比例可在横竖之间切换**（参考 Lightroom）。选了 3:2 想要
  2:3，往竖长方向拖过一定幅度即可翻转，往回拖再翻回来。只有拖角才触发。

---

**Fixed**

- **Profiled TIFFs came out oversaturated**, with a cast on neutrals. RAW and TIFFs without a
  profile are unaffected.

  **Note**: rolls of TIFFs already adjusted against the old behaviour will shift, and their
  film base (t_base) needs re-sampling.
- **A crop applied with the wrong ratio and position.** Most visible on macOS, and always wrong
  on a rotated photo.

  **Note**: projects with a crop saved on a rotated frame need re-cropping. Crops saved with no
  rotation are unaffected.
- **Film-base sampling picked the wrong region on a rotated photo.** The negative view now
  follows the turns and flips, and the selection, D-max and offset samplers all sample where
  you drew.
- **Zooming in on the negative view showed the de-masked positive.** Same for hold-to-compare.
- Applying or clearing a crop now returns to fit, instead of leaving the picture magnified and
  off to one side.

**Improved**

- **Dragging a corner handle can flip a preset ratio between landscape and portrait** (as
  Lightroom does). To get 2:3 out of a 3:2 preset, drag far enough toward portrait; drag back
  and it returns. Only a corner triggers it.

---

## v1.2.1（2026-08-11）

**新增**

- **导入后自动整卷分析去色罩**：勾选后自动完成片基、白平衡、密度与色阶的标定，无需框选任何
  区域。整卷汇总成一组参数应用到所有帧，因此同一卷观感一致，不会把夕阳或钨丝灯的氛围当成
  偏色修掉。

  当前帧先出结果，整卷分析在后台继续，视帧数和机器约每帧数秒；完成前缩略图与参数仍会变动。

  开关在导入弹窗里（偏好设置 → 导入 设的是它的默认值）。不勾选则完全不做自动测量，整卷标定
  交给手动。

**修复**

- macOS：裁切后画面显示不正确。

---

**Added**

- **Roll-wide mask removal on import** — tick it and the film base, white balance, density and
  levels are calibrated for you, with no region selection anywhere. One parameter set is pooled
  from the whole roll and applied to every frame, so a roll stays visually consistent and a
  sunset or tungsten interior is not corrected away as if it were a cast.

  The current frame is solved first and the roll-wide pass continues in the background —
  roughly a few seconds per frame — with thumbnails and parameters changing until it finishes.

  The switch is in the import dialog (Preferences → Import sets its default). Left unticked,
  nothing is measured and the whole calibration is yours to do by hand.

**Fixed**

- macOS: the crop was not displayed correctly after applying it.

---

## v1.2.0（2026-08-11）

色彩管理重做，`chroma_grade = 3.05` 标量随之取消。

> **既有工程的画面会与本版不同，建议重新处理。** 滑块数值本身保留，但含义变了——现在是
> 「在当前输出空间里调这么多」。1.1.2 及更早版本调好的卷，反差与饱和度都会有可见变化。

**修复**

- **取消 `chroma_grade`（默认 3.05）**：管线补上色彩空间声明后，颜色由真实的色域变换给出，
  这个参数整体移除（不是改默认值）。
- **工作空间加宽到 ACEScg**：反相与三通道对齐颜色能完整穿过对数域。

**变更**

- **输出空间移到主窗口**（sRGB / Display P3 / Adobe RGB），不再在导出弹窗里选。它改变渲染
  结果，因此是胶卷参数，会保存进工程。
- **输出意图移到导出弹窗**，改为「导出为场景线性 ACEScg」勾选框。它只影响某一次导出，不改
  变预览。

---

Colour management rebuilt, and the `chroma_grade = 3.05` scalar retired with it.

> **Existing projects will render differently and are worth reprocessing.** Slider values are
> preserved, but their meaning has changed — they now mean "this much adjustment in the current
> output space". Rolls graded on 1.1.2 or earlier will show visible differences in contrast and
> saturation.

**Fixed**

- **`chroma_grade` (default 3.05) retired** — now that the pipeline declares colour spaces,
  colour comes from real gamut conversion and the parameter is removed outright, not merely
  defaulted away.
- **Working space widened to ACEScg** — the inversion and three-channel alignment can pass
  through the log domain with colour intact.

**Changed**

- **Output space moved to the main window** (sRGB / Display P3 / Adobe RGB), out of the export
  dialog. It changes the render, so it is a roll parameter saved with the project.
- **Output intent moved to the export dialog** as an "export scene-linear ACEScg" checkbox. It
  describes one export, not how a roll is worked on, so it does not alter the preview.

---

## v1.1.2（2026-08-10）

扫描件色彩修复。1.0 至 1.1.1 期间导入的扫描件颜色不正确，建议重新导入。

**修复**

- **扫描件 ICC 色彩管理**：载入扫描件时按其嵌入的 ICC Profile 处理色彩——先用 Profile 自带
  的三条 TRC 曲线逐通道线性化，再映射到 sRGB 线性光。无 ICC 或只有 LUT 的 Profile 相应跳过。
  这套处理在 1.0 重写时缺失。
- **扫描件色度补偿回到 1.0**：扫描件不再套用为相机 sensor 串扰标定的 `chroma_grade = 3.05`
  （RAW 仍为 3.05）。1.0 至 1.1.1 期间导入的扫描件色彩偏浓。
- **导出的 ICC Profile 曲线方向反了**：像素数据本身没错，但色彩管理软件读到的曲线是反的，会
  把画面判读得过亮。AdobeRGB 一档不受影响。

---

A scan-colour fix. Scans imported between 1.0 and 1.1.1 came out with the wrong colour and are
worth re-importing.

**Fixed**

- **ICC colour management for scans** — loading a scan again honours its embedded ICC profile:
  the profile's own three TRC curves linearise each channel, then the matrix maps the result
  into linear sRGB. Files without an ICC, and LUT-only profiles, skip the corresponding step.
  This handling was lost in the 1.0 rewrite.
- **Chroma compensation back to 1.0 for scans** — scans no longer take `chroma_grade = 3.05`,
  which is calibrated for camera sensor crosstalk (RAW still uses 3.05). Scans imported between
  1.0 and 1.1.1 came out oversaturated.
- **Exported ICC profiles had their curve the wrong way round.** The pixel data was always
  correct, but colour-managed software read the inverted curve and showed the image far too
  light. The AdobeRGB profile was unaffected.

---

## v1.1.1（2026-08-09）

**新增**

- **扫描件自动分割**：一张扫描件装着一整条底片时，导入即自动切成单帧。弹窗里可双击增删分隔
  线、拖动外框四条边，也可调裁切余量（0~50%，默认 15%）——留出的余量供之后在裁切工具里把画面
  往外拉回。检测失败时给等分猜测。扫描件与 RAW 不能混选导入。
- **工具提示框可关闭**：裁切/取样提示会挡住高幅裁切框的手柄，现在可以关掉（回车应用、Esc
  取消依然可用），重新进入工具时提示回来。

**修复**

- 齿孔遮罩在旋转或裁切过的帧上不再错位。

**性能**

- LCC 平场载入快约 275×（60 MP 实测 ~126 s → 456 ms）。

---

**Added**

- **Automatic scan splitting** — when one scan holds a whole strip of film, it is split into
  individual frames on import. In the dialog you can double-click to add or remove dividers,
  drag any of the four outer edges, and set the crop margin (0–50%, default 15%) — that margin
  is what lets you pull the picture back out later in the crop tool. Falls back to an even guess
  if detection fails. Scans and RAW files cannot be imported in the same selection.
- **Dismissible tool hint** — the crop/sampling hint covered the handles of tall crop boxes; it
  can now be closed (Enter to apply and Esc to cancel still work), and returns when you re-enter
  the tool.

**Fixed**

- Sprocket mask alignment on rotated or cropped frames.

**Performance**

- LCC flat-field loading ~275× faster (~126 s → 456 ms at 60 MP).

---

## v1.1.0（2026-08-06）

首个开源版本，基于 Revelare 1.0.0 重命名并开放全部功能。C# / .NET 8 + Avalonia，
Windows / Linux / macOS。

**新增**

- **界面中英双语**：默认跟随系统语言，偏好设置可锁定中文 / English，切换即时生效。
- **图库分类筛选**：侧栏按画幅 / 胶卷 / 相机 / 冲洗店 / 年份分面筛选，搜索覆盖所有字段；
  导入弹窗收集卷信息。
- **导出选项弹窗**：文件选取前先选格式 / 压缩 / 色彩空间 / 冲突策略；JPEG 支持嵌入 ICC
  profile。
- **DNG 缓存跨会话持久化**：重载历史卷不再重跑 Adobe DNG Converter 转换；偏好设置可开关，
  支持目录 / 上限自定义。
- **更新检测双镜像。**
- **授权 / 激活系统全部移除。**

**修复**

- 整卷片基估计改为同源采样并跳过削顶平台，避免把虚假色偏烧进密度反转的基准。
- 导出改为暂存后原子改名：写到一半崩溃或磁盘满不会损坏上一次成功的导出。
- 整卷导出不再静默覆盖磁盘上已存在的同名文件。
