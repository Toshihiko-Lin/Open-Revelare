# OpenRevelare — 更新日志

## v1.5.0（2026-08-15）

整卷校准面板重构：反相只由密度端点驱动，两端各三通道，面板上冗余控件全部移除。导入
流程简化，自动标定算法更可靠。macOS 快捷键、菜单栏和内存管理补齐平台适配。



**变更**

- **反相参数精简为六个自由度**。删除旧链路、
  部分控件移除，色偏改为直接展开三个通道的绝对密度。

- **两端统一为绝对密度显示**，两端各带手动 + 自动按钮。

- **「框选亮部」与「框选 D_max」合并为【高光采样】**，「框选片基」与「框选暗部」
  合并为【片基采样】。两端标定不再有先后顺序要求。

- **导入后不再弹「齿孔遮罩确认」窗**，阈值自动测出并应用。



**新增**

- **「自动（整卷）」「自动（单张）」两个按钮**。「整卷」
  汇总全卷参数；「单张」只算当前帧。

- **macOS 顶端原生菜单栏**。File / Edit / View / Help 以及应用菜单（关于 / 偏好设置），
  ⌘Q、⌘W 由系统自动提供。窗口内菜单在 macOS 上隐藏，其余平台不变。

**改进**

- **非胶片区域的识别与排除**
  

- **片基的自动识别与采样**，测不到时提示手动标定。

**修复**


- **D-max 被挡光板和片边带偏**。
- **多条同时分割时，第一张的预裁切被抹掉**。
- **部分相机 RAW 无法顺利导入**
- **macOS 上 ⌘ 快捷键全部失效**，统一为一处定义，macOS 显示 ⌘、其余平台显示 Ctrl。
- **macOS 文件选择框选不进大写扩展名的 RAW**（.CR2、.NEF、.3FR 等）。
- **macOS 上导入大 RAW 容易卡死**，补上可用内存探测的 macOS 实现。
- **整卷自动后再点「自动（单张）」或「自动黑点」，结果被覆盖到其他帧**。片基估算改为
  单帧路径时只写当前帧，其余帧保持不变。

---

The roll-calibration panel is rebuilt: the inversion is driven by density endpoints alone —
three channels at each end — and redundant controls are gone. The import flow is simpler,
automatic calibration is more reliable, and macOS gets keyboard shortcuts, a native menu
bar and proper memory management.



**Changed**

- **Inversion parameters reduced to six degrees of freedom.** Legacy chain removed,
  redundant controls removed; colour cast is now adjusted by expanding three channels
  of absolute density directly.

- **Both ends display absolute densities**, each with manual + automatic buttons.

- **"Select highlight" and "Select D_max" merged into "Sample the highlight"**;
  "Sample the film base" and "Sample the shadow" merged. Calibration order no longer matters.

- **No more sprocket-mask dialog after an import** — the threshold is measured and applied
  automatically.



**Added**

- **"Auto (whole roll)" and "Auto (this frame)" buttons.** "Whole roll" pools the
  roll-wide parameters; "this frame" solves the current frame alone.

- **macOS native menu bar.** File / Edit / View / Help plus the application menu (About /
  Preferences). ⌘Q and ⌘W are provided by the system. The in-window menu is hidden on macOS;
  other platforms are unchanged.

**Improved**

- **Non-film-area detection and exclusion.**

- **Automatic film-base detection and sampling**; prompts manual calibration when not found.

**Fixed**

- **D-max was biased by the light blocker or film edge.**
- **Splitting multiple strips at once erased the first frame's pre-crop.**
- **Some camera RAW formats could not be imported.**
- **macOS: ⌘ shortcuts did not work** — unified to one definition, showing ⌘ on macOS and
  Ctrl elsewhere.
- **macOS: the file picker rejected uppercase RAW extensions** (.CR2, .NEF, .3FR, etc.).
- **macOS: importing large RAWs could freeze the app** — macOS memory detection added.
- **After "Auto (whole roll)", clicking "Auto (this frame)" or "Auto black point" overwrote
  other frames' results.** The film-base estimator no longer broadcasts when called from a
  per-frame path; only the current frame is updated.

---

## v1.3.0（2026-08-13）

反相改用逐通道密度端点：黑白两端量出来，中间是算出来的。胶片条的帧顺序现在是可控的。
程序内的文档按 Markdown 渲染，公式真正排版。

**变更**

- **反相由密度端点决定**。片基是黑端，D-max 是白端，三个通道各自归一化，斜率由两端算出。
  面板上「反差（相纸号数）」换成只读的「密度端点」；整卷自动标定会一并量出端点，正常
  流程下无需额外操作。白平衡 wb_high / wb_offset 照旧可调，两端各自独立确定，标定没有
  先暗后亮的顺序要求。原理见程序内「技术原理」。

  已有工程渲染结果不变；重跑整卷自动标定或采样一次 D-max 即可切换到新模型。

- **「快捷键与上手…」改为「快捷键…」**，只留快捷键表和采样 / 滑块 / 预览三条操作提示，
  流程说明统一在「操作指引」里。首次启动的引导打开「操作指引」。

**新增**

- **拖动缩略图调整帧顺序**。高亮横线指示落点，拖到顶端或底端会自动滚动。虚拟副本
  跟着母帧一起移动。顺序随工程保存，也决定印样的排列，右键「按文件名排序」可恢复。

- **文档查看器渲染 Markdown，公式按 LaTeX 排版**。标题、列表、表格、引用块、代码与公式
  各按其形态显示；公式有横线分数、斜体变量、真正降位的下标和带括号的矩阵。左侧目录由
  文档里实际存在的标题生成。

**改进**

- **「操作指引」按现行界面重写**。章节对应当前的面板划分（整卷校准 / 帧编辑），顺序照实际
  操作走：输入路径 → 翻拍要求 → 导入 → 整卷校准 → 几何 → 帧编辑 → 整卷操作 → 导出 →
  Path A → 快捷键。

- **「技术原理」按现行实现重写**。结构改为「三条并行输入前端 → Cineon 核心」：Path A /
  Path B / TIFF 各自如何得到线性光，汇合后进入统一的密度域反相。另补「已知限制：输入
  原色未声明」一节。

**修复**

- **导入后缩略图不按文件名排序**。手选多个文件、或分多次添加攒起来的卷会乱序。现在统一
  按文件名排，数字段按数值比较；向已有的卷添加图像时只把新增的这批排好追加在末尾，不动
  已有的顺序。

- **片基采样误报「采样区偏暗」**。橙色片基的蓝通道天然低于不带色罩的齿孔/灯板，正常的
  片基框选也会触发提示。现在按总透光量判断，误采到画面或深阴影仍会提示。

- **自动 D-max 检测被片边干扰**，不透光的片边会把三个通道不等地抬高，结果偏色。现在与
  整条自动标定链一致，齿孔与片边不再计入。

---

The inversion model changed: one gamma shared across three channels gives way to
per-channel density endpoints. The "contrast" knob goes with it — the two ends are
measured and the middle follows. The frame order in the film strip is now yours to set.
The in-app documents are no longer one block of source, and their formulas are typeset.

**Changed**

- **The inversion is decided by density endpoints.** The film base is the black end,
  D-max is the white end, each channel is normalised on its own, and the slope follows
  from the two ends. On screen, "Contrast (paper grade)" gives way to a read-only "Density
  endpoints"; the roll-wide auto calibration measures the endpoints along the way, so the
  ordinary route needs no extra step. White balance (wb_high / wb_offset) adjusts as
  before, and with each end pinned independently, calibration no longer has to go dark
  before light. See "How it works" in the app for the reasoning.

  Existing projects render as they did. Re-run the roll-wide auto calibration, or sample
  D-max once, to move them onto the new model.

- **"Keys and getting started…" is now "Keyboard shortcuts…"** — the key table plus three
  notes on sampling, sliders and the preview, with the workflow documented in the user
  guide alone. The first-run prompt opens the user guide.

**Added**

- **Drag a thumbnail to reorder frames.** A highlighted line shows where it would land,
  and dragging to an edge scrolls the strip. A virtual copy travels with its parent frame.
  The order is saved with the project and sets the contact sheet's layout; right-click →
  "Sort by file name" puts it back.

- **The document viewer renders Markdown, and formulas are typeset as LaTeX.** Headings,
  lists, tables, quotes, code and formulas each look like what they are, and a formula
  gets a real fraction bar, italic variables, subscripts that sit below the line and
  bracketed matrices. The table of contents on the left is built from the headings in the
  document.

**Improved**

- **The user guide was rewritten around the current interface.** Its sections match the
  panels as they now stand (roll calibration / frame edit) and follow the order you work
  in: input paths → copy-stand requirements → import → roll calibration → geometry →
  frame edit → roll-wide operations → export → Path A → keys.

- **"How it works" was rewritten against the current implementation**, restructured as
  three parallel input front ends feeding one Cineon core: how Path A, Path B and TIFF
  each arrive at linear light before meeting in the shared density-domain inversion. A
  "Known limitation: input primaries are not declared" section is new.

**Fixed**

- **Thumbnails were not sorted by file name after an import.** Hand-picking several files,
  or building a roll up over several adds, left it scrambled. Everything is now sorted by
  file name with digit runs compared as numbers; adding to an existing roll sorts only the
  new batch and appends it, leaving the existing order alone.

- **Film-base sampling wrongly reported "the sampled region looks dark".** The orange
  base has a naturally low blue channel next to unmasked sprockets and light panel, so a
  perfectly good base selection could set the warning off. It now judges by total
  transmission, and still warns if you land on the picture or a deep shadow.

- **Automatic D-max detection was thrown off by the film edge**, where opaque border
  lifted the three channels unequally and left a cast. It now matches the rest of the
  automatic calibration chain, with sprockets and film edge left out.

## v1.2.2（2026-08-11）

裁切与片基采样修好了，转过向的照片也不再出错。另有一处 TIFF 色彩管理的修正。

**修复**

- **带 ICC 配置文件的 TIFF，色度被放大**。红、蓝饱和度约 ×1.13，绿约 ×1.4，中性灰
  还会偏色。RAW 与不带配置文件的 TIFF 不受影响。

  **注意**：已经按旧行为调过的 TIFF 卷，画面会变，片基（t_base）需要重新取样。
- **裁切后比例和位置都不对**。macOS 上尤其明显（选 1:1 预设时），转过向的照片上必然出错。

  **注意**：在转过向的帧上存过裁切的工程文件需要重裁；未转向时存的不受影响。
- **转过向的照片，片基采样取错位置**。负片预览会翻回未转向的样子，框选到的是画面上
  另一块。现在负片视图跟随转向与翻转，框选、D-max 和偏移采样都取到框住的地方。
- **负片视图下放大，看到的是去色罩后的正片**。按住对比看原片时同理。
- 裁切或清除裁切后回到「适应窗口」，画面不再放大着偏在一边。

**改进**

- **拖角改变裁切框大小时，预设比例可在横竖之间切换**（参考 Lightroom）。选了 3:2 之后
  想要 2:3，往竖长方向拖过一定幅度即可翻转，往回拖再翻回来。只有**拖角**才触发。

---

**Fixed**

- **Profiled TIFFs came out with amplified chroma.** Saturation rose ~1.13× on red and
  blue, ~1.4× on green, and neutrals picked up a cast. RAW and TIFFs without a profile
  are unaffected.

  **Note**: rolls of TIFFs already adjusted against the old behaviour will shift, and
  their film base (t_base) needs re-sampling.
- **A crop applied with the wrong ratio and position.** Most visible on macOS (with the
  1:1 preset), and always wrong on a rotated photo.

  **Note**: projects with a crop saved on a rotated frame need re-cropping. Crops saved
  with no rotation applied are unaffected.
- **Film-base sampling picked the wrong region on a rotated photo.** The negative
  preview flipped back to its un-rotated orientation and the selection landed elsewhere
  in the picture. The negative view now follows the turns and flips, and the selection,
  D-max and offset samplers all sample where you drew.
- **Zooming in on the negative view showed the de-masked positive.** Same for the
  hold-to-compare view.
- Applying or clearing a crop now returns to fit, instead of leaving the picture
  magnified and off to one side.

**Improved**

- **Dragging a corner handle can now flip a preset ratio between landscape and
  portrait** (as Lightroom does). Getting 2:3 out of a 3:2 preset: drag far enough
  toward portrait and the locked ratio flips; drag back and it returns. Only a CORNER
  triggers it.

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


