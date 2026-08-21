# OpenRevelare — 更新日志

## 未发布

**修复**

- **macOS 上使用裁切工具会闪退**。裁切模式下的悬停分支在**每一个**鼠标移动事件里都
  `new Cursor(...)` 造一个新光标——鼠标划过画面一秒就是几百个。每个光标都持有一份平台资源，
  只有等终结器跑到才归还，而 GC 只看见几十字节托管内存，根本不着急回收。于是光标在裁切工具
  开着的整段时间里一直堆积，直到进程撑不住。这也解释了为什么用户报的是「移动鼠标就崩」，
  而不是崩在某一次点击上。

  现在六种光标各建一次、全程复用，赋值前先比引用——与旁边 `UpdatePanCursor` 早就在用的写法
  一致（那处当初正是为同一类问题加的缓存，只是裁切这条路没跟上）。

  分格对话框（`SplitDialog`）的边界拖拽提示是同一个写法，一并改掉。

## v1.8.0（2026-08-21）

上一版把「标准」这一项补上了显示渲染，也把 D_max 交给了测光。实际用下来这两个决定都各自
带来了新问题：片基发灰、高光被切、挂上印片后整体偏暗一挡。这一版把标定与渲染的职责重新
划清——**标定只负责两端对齐，渲染只负责观感，测光退回仪表**。

**修复**

- **标定不再由测光反解 D_max**。上一版把画面的**中间**（平均值）钉在中灰 336，让**顶部**
  自由落体，于是画面最亮处够不到码值 1032，直方图右侧留空。而印片 LUT 的肩部正好活在那一
  段，所以挂上印片后整体偏暗、高光永远不被触发。

  现在 **D_min → 95、D_max → 1032，仅靠这两个端点让画面占满且两端都不切**——这才是标定该
  做的事。中性灰读数保留，但它只显示不写入：曝光不对的画面由用户看着读数自行调整。

- **高光端点不再让单通道过曝**。端点按**总密度**排序取尾部，保证三通道共位（色彩平衡才可信）；
  但偏色高光（钠灯、夕阳、红色霓虹）可能单通道很浓而总密度不够，进不了尾部，那个通道就冲出
  自己的端点被切掉。

  现在算完共位端点后，用最大超出比例把**三个端点同乘**一个系数。等比缩放不改变端点之间的
  比值，所以共位取样定下的色彩平衡分毫不动，而三个通道都保证不过曝。

- **「标准」的高光不再硬切**。此前码值 685 就到显示白，685–1032 这 **2.31 挡**宽容度全被
  编码器钳掉。切到印片时那段高光突然回来、被摊在 0.88–1.0 之间，看起来像是「印片把高光压暗
  了」——其实是标准路径一直在烧掉它们。

  现在加了肩部（膝点在码值 596），码值 685 落到 0.881，与实测的 Kodak 2383（0.880）对齐，
  1032 滚到 0.992 而非切顶。中调不受影响：码值 486 仍是 0.494。

- **片基在「标准」下归零**。渲染末尾把码值 95 归一化到显示黑。这是**渲染**的决定，不是编码
  的——`LogEncoding` 和端点一个字都没动，印片 LUT 那条路仍然拿到未经改动的 Cineon 信号。

  这推翻了上一版「片基应渲染为约 0.15 的灰」的说法，理由是实测：归一化后码值 250 输出 0.172、
  328 输出 0.259，比未归一化的 0.208 / 0.282 **更接近** Kodak 2383 的 0.10 / 0.18。上一版
  注释里「减法会让曲线偏离 cube」的举证方向反了。

- **Stage 2 移到显示渲染之后**。白平衡与曝光此前跑在 step 4 之前的线性域，于是增益会把已经
  标定好的片基顶离码值 95，让归一化失效（实测 +0.5 挡时片基渲染成 0.090、+1 挡 0.167）。

  现在 step 4 最先跑，Stage 2 全部在渲染之后。白平衡与曝光用一次「解码→相乘→编码」的往返
  保住线性语义——中灰 +1 挡仍是 0.259 而非编码域直乘的 0.923。

- **测光改用中位数**。此前取平均，于是刻意牺牲的高光（窗户、逆光、天空）会把读数拖高，
  D_max 跟着解低，**主体反被压暗**：实测 30% 过曝区代价 0.73 挡、50% 代价 1.21 挡。

  中位数对少数派过曝完全免疫（30% 时读数不动）。过曝区过半时它会跟随，那是诚实的——一张
  大半是天空的画面没有同时正确的曝光。整卷跨帧归约也改用中位数（曾考虑众数：只在「半室内
  半室外且两簇等大」时才与中位数分开，而那种卷本就该逐帧处理）。

**改进**

- **【胶片风格】新增「Cineon log（纯 CST，未渲染）」**，与「标准显示渲染」并列，默认仍是后者。

  它只解编码、不做任何渲染：**18% 中灰出来正好是 0.180、90% 漫反射白正好是 0.900**，标定
  原样穿过。画面平且发灰是 log 本来的样子——观感请交给 LUT 或后期调色。这是达芬奇 CST 节点
  把色调映射设为 None 时的行为。

  同时第一项改名为「标准显示渲染（CST + 显示渲染）」。它从来不是一次单纯的容器转换：它折进
  了响应 gamma 并把片基归零，那是观感决策。两项并列才说得清区别。

- **自动色阶不再由任何自动流程调用**。三条渲染路径都会放置自己的两端——标准渲染归一化码值
  95 并在 685 之上滚降，印片有自己的趾部与肩部，纯 CST 刻意不做渲染——再测一次结果并拉回
  0..1，等于推翻用户刚选的那个渲染。

  片基归零后黑端百分位恒为 0，所以此前**只有白场在动**，把肩部刚滚掉的高光又推回去顶到 1。
  按钮与滑块照常可用，改变的只是默认行为。

- **切换胶片风格时重建 Stage 2 参数**。Stage 2 跑在渲染之后，数值是相对那次渲染的零点而言的；
  跨风格沿用等于把针对另一张画面的修正套上来。切换时归零，整卷参数会先进撤销栈，误切一次
  Ctrl+Z 即可。

**已知的行为变化**

- **所有已有工程的明暗都会变**，方向是变亮——D_max 不再被测光压低。重跑一次【自动（整卷）】
  或【自动（单张）】即可。

- **【自动（整卷）】与【自动（单张）】给出的结果不同，这是设计使然**。整卷从全部帧里挑
  **最浓的那一帧**作为全卷标定（跨帧逐通道取百分位会让 R/G/B 落在不同帧上，产生任何底片都
  没产生过的三元组）；单张只看当前帧。除非当前帧恰好是全卷最浓的那张，两者必然不同。

- **片基在「标准」下渲染为纯黑**，比真实印片更狠一点（Kodak 2383 在码值 95 处给 0.037）。
  代价是片基与比它更暗的东西（齿孔、遮光边）在显示上合并为同一个 0。

---

## v1.7.0（2026-08-21）

**修复**

- **片基在「标准」和选了印片时不一致**。选了印片时暗部大片报欠爆；为了救暗部去调小 D_min，
  切回「标准」片基又发灰。根源是黑位被两处规则决定：标定阶段擅自把片基按成纯黑，而印片
  LUT 按 Cineon 的码值 95 渲染它自己的暗部。

  现在标定阶段不再做黑位归一化。**Stage 1 的输出就是标准 Cineon log，两条路径消费同一份
  信号，只在「拿它做什么」上分岔。D_min 不再需要为了迁就印片而偏离实测片基。**

- **「标准」这一项此前没有显示渲染**。它解完 log 就直接交给输出空间的 gamma，等于什么都
  没做，画面读起来是一张发灰的 log 底片。现在走标准 Cineon 转换（参考白 685、响应 gamma
  0.6），反差由这条曲线提供。

**改进**

- **新增测光读数**（直方图下方）。显示画面平均落在 Cineon 码值轴的哪里，相对中灰 336。

  在**码值域**测，不在屏幕上测：屏幕上的亮度随所选印片变化，而码值是所有 Cineon LUT 共同
  认的坐标。取对数域平均（几何平均），所以一处高光点不会把读数拖走一整挡。**片基不参与
  计算**——它是正片里最暗的东西，大面积片基边框会把平均拉低，而读数偏低会让 D_max 解得偏低、
  画面反而过曝。

- **D_max 改为随测光求解**。【自动（整卷）】取全卷测光平均对齐中灰，【自动（单张）】只测
  当前帧。

  高光检测器仍然跑，但它只负责**三通道的相对跨度**（这是卷的色彩平衡，是对胶片的真实测量）；
  **画面放在哪**由测光决定。此前是把 99.9 百分位钉到固定码值，而那个百分位读的是镜面高光——
  它比漫反射白高多少取决于画面里有没有光源，于是同一卷里有窗户的那几张会被整体推暗。

  求解是闭式的，三个通道同一个比例，所以色彩平衡原样保留：这是放置，不是调色。

- **【胶片风格】的第一项从「无（直通）」改名为「标准（Cineon → 输出空间）」**。它从来不是
  「什么都不做」——那一项就是 Cineon 的标准显示转换，只是不加印片风格。

  不写成「Rec709」，是因为转换落到哪个空间由旁边的【输出空间】决定。

- **输出空间去掉两个染料基色**（Kodak 2383、Kodak Endura Premier）。它们描述的是染料编码
  基色，而非实际能呈现的色域，选择器早已不提供；旧工程指定它们时会迁移到 sRGB。

**已知的行为变化**

- **片基在「标准」下渲染为约 0.15 的灰，不是纯黑。** 这是 Cineon 标准转换的正确输出——
  码值 95 是编码域的底，不是显示域的底。画面的黑来自画面自己的暗部内容，它落在片基之上。
  真实印片也是如此：Kodak 2383 在码值 95 处输出 0.037，同样不是纯黑。

- **印片的暗部比「标准」压得更低。** 那是印片的趾部，是选择它的理由之一，不是故障。

  这一版的渲染结果与旧版不同，已有工程打开后画面会有变化。重跑一次【自动（整卷）】即可。

---

## v1.6.0（2026-08-21）

**新增**

- **胶片风格**。在「输出空间」旁边选一张印片（如 Kodak 2383、Fujifilm 3513DI），画面就按那张
  胶片的反差和色彩渲染。选「无」则和以前一样。

  需要自备 `.cube` 文件，且必须是**以 Cineon log 为输入**的印片 LUT——软件不附带，这类文件
  由各厂商单独授权。装了 DaVinci Resolve 的话，其安装目录 `LUT/Film Looks/` 下就有。

  想调胶片之前的画面，去【整卷校准】拉 D_max / D_min。

**修复**

- **黑位没有真正落到纯黑**。片基采样得越准，黑位反而浮得越高——采过片基的工程会发现暗部
  发灰、不够沉。现在采样为黑的位置就是纯黑。
- **某些源文件的通道值为 0 时，该处颜色不对**。齿孔黑边、扫描件黑边、部分相机 RAW 的填充
  边会出现本不该有的偏色；现在这些位置正确地渲染为白。
- **【自动白点】与【自动（整卷）】在同一张片子上可能给出不同的亮端**。两者现在用同一套
  测量方法。测不到高光时会明确提示，而不是静默保持原值。

**改进**

- **反差对齐 Cineon 标准**。黑白两端之间的密度跨度改为 Cineon 的 95–1032，此前略宽。
  画面整体反差略降，中间调略提亮。

  这一版的渲染结果与旧版不同，已有工程打开后画面会有变化——主要是暗部更沉。如果对某卷的
  成品不满意，重跑一次【自动（整卷）】即可。

---

**Added**

- **Film look.** Pick a print stock (Kodak 2383, Fujifilm 3513DI, …) beside "output space" and the
  picture is rendered with that film's contrast and colour. "None" renders exactly as before.

  You supply the `.cube` yourself, and it must be a print LUT that takes **Cineon log** in — none
  ship with the app, as these are licensed individually by their vendors. If you have DaVinci
  Resolve, look under `LUT/Film Looks/` in its install directory.

  To adjust the picture *before* the film, set D_max / D_min in roll calibration.

**Fixed**

- **Blacks never reached true black.** The more accurately you sampled the film base, the
  higher the black floated — projects with a sampled base looked washed out in the shadows.
  What you sample as black is now black.
- **Wrong colour where a source file has a channel at zero.** Sprocket edges, scan borders
  and the padding some camera RAWs carry showed a colour cast they should not have; these
  now render as white.
- **"Auto white point" and "Auto (whole roll)" could disagree** on the same frame. Both now
  use the same measurement. When no highlight can be found you are told, instead of the
  value silently staying put.

**Improved**

- **Contrast now matches the Cineon standard.** The density span between the two endpoints
  is Cineon's 95–1032, slightly narrower than before. Overall contrast is a little lower and
  midtones a little brighter.

  This release renders differently from previous ones — existing projects will look
  different, mainly deeper shadows. If a roll no longer looks right, re-run
  "Auto (whole roll)".

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
