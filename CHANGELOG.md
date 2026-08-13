# OpenRevelare — 更新日志

## 待发布

导入后不再弹窗，整卷校准面板顶上多了两个自动按钮。自动标定在几类以前测不准的底片上
测准了：没有灯板的卷、片基只剩边缘一条窄带的卷、画面里有挡光板的卷。

**变更**

- **白平衡改成「亮度 / 色温 / 色调 / 黑场」四个控件**。反相底层是六个绝对密度（两端各三
  通道），但那不是能拨的旋钮：亮端三个数同时装着「这张有多亮」和「偏什么色」，想调亮就
  必然连色偏一起动。现在它们被拆成两组**严格正交**的控件——

  | 控件 | 实际在动 | 副作用 |
  |---|---|---|
  | **亮度** | 三个亮端密度的几何均值 | 色偏**完全不变**（通道斜率比逐位相同）|
  | **色温 / 色调** | 通道间比例（几何均值保持）| 明暗基本不变（实测漂移 ~2%）|
  | **黑场** | 三个暗端密度的均值（加性平移）| 暗部色偏逐位保持 |

  拆法必须是**几何**的：三个端点同乘一个系数色偏严格不变，同加一个常数则会漂（实测 R/B
  比 1.0779 → 1.0713）。这就是「调亮端顺带改了亮度」的根源——之前没有沿正确的缝切。

  色温/色调复用【帧编辑】那一套 WbMath 基向量，同名同刻度同手感，只是作用在反相白端。
  六个绝对密度仍可在**【高级：六个端点密度】**里直接改，两边是同一份数据的两个视图。

- **【帧编辑】删除「色偏修正」整组**（色温 / 色调 / 灰点）。色彩平衡是反相白端的属性，只
  应该有一处；在帧编辑再调一次等于在已定好的端点上叠第二层，两处数值互相掩盖，出了偏色
  说不清是哪一处。曝光、反差、饱和度、曲线等审美控件不受影响。

  > 旧工程存过的那一层增益**仍然照常生效**，观感不变。**没有**把它折算进端点——线性域增益
  > 等价于密度域**加常数**，而端点决定的是**斜率**，两者只能在某一个密度上重合：实测折算后
  > R/B 比在薄部偏 −18%、中间调 +4%、浓部 +53%，旧卷会明显变色。帧编辑顶部会提示它的存在，
  > 并给一个「清除」按钮把色偏交还给亮端统一管理。

- **白平衡两端都改成显示真实密度**。界面上的「亮端」「暗端」现在是**该通道自己的密度读数**
  （亮端典型 1.8–2.4），不再是 `1,1,1` / `0,0,0` 这种「相对某个基准的修正系数」。三个通道
  之间的差**就是**色偏——这三个数本身即白平衡，不需要另一个参数来描述它。

  这不只是换个显示方式。修正系数只有相对于**另一处**对同一端点的陈述才有意义，于是必然
  存在两处描述互相打架：标定顺序变得有意义，同一个校正会被应用两遍。上一版删掉旧链路时
  已经堵住了叠加的路径，但代价是 wb_high 变成了没有任何东西读取的死字段——滑块拉动不改变
  任何像素。改成绝对密度后两处描述合并成一处，问题在结构上消失。

  「框选亮部」与「框选暗部」现在也没有先后之分了：两端各自独立测量，谁先谁后结果相同。

- **「最亮点=白」和「智能白平衡」修复**。这两个按钮此前写入的正是上面那个死字段，因此
  运行完毕、状态栏报出数值，画面却毫无变化（智能白平衡还会白跑最多 50 轮神经网络推理）。
  现在它们写入反相真正读取的亮端端点。

- **反相只剩密度端点一种模型**。此前为了让旧工程逐位不变，端点模型与更早的 grade/pivot
  链路并存，高光平衡因此有两处可以写——端点的通道间斜率差，以及 wb_high。整卷自动标定
  两处都写了，同一个校正做了两遍，画面偏红约三分之一，且发生在反相内部，「帧编辑」里
  任何滑块都拉不回来。旧链路已删除。

  > **旧工程照常打开，画面不变。** 载入时会把旧的 wb_high 乘数与加性 offset 换算进两端
  > 端点，渲染结果与旧版逐位一致。但 2026-08-12 之前保存的工程本就没有逐通道端点，仍会
  > 以一组中性端点打开，观感与当初不同——跑一次「自动（整卷）」或采样一次 D-max 即可。

- **导入后不再弹「齿孔遮罩确认」窗**。阈值自动量出并应用到整卷，导入完直接看到画面。
  要核对或微调，去「整卷校准 → 齿孔遮罩」，勾「显示遮罩」看红色叠加，和以前弹窗里
  是同一个控件。

- **移除「密度端点」面板**。它没有可操作的东西，只显示一行读数。相关说明保留为片基
  分组下的一行注解。

**新增**

- **「自动（整卷）」「自动（单张）」两个按钮**，在整卷校准面板最上方。自动去色罩的
  能力一直都在，但只藏在导入对话框的一个复选框里，导入完就没有入口，也无法重跑。

  「整卷」遍历全卷汇总成一套参数写给所有帧；「单张」只算当前帧，用于卷里混了一张
  光源不同的片子。两者都不改动裁切。

**改进**

- **灯板识别改按物理特征判断**。以前只要求「亮端有峰、峰下有谷」，而任何直方图都满足。
  现在要求灯板同时是独立亮簇、占据一定画面、与胶片有真实间隙、连到画幅边缘，且亮度
  达到裸光源的水平——片基透过色罩只有灯板的三分之一亮，不会再被当成灯板。

  受影响的三类卷：120 这类灯板面积大的、画面本身明暗分明的、以及画面里只有一条裸片基
  窄带的。

- **没有灯板时也能量出片基**。扫描件常常只在边缘留一条裸片基，占画面不到 1%，按分位数
  取值会落在画面高光上，量出的片基偏高两倍多。现在会专门找这条窄带——独立亮簇、贴着
  边缘、且是橙色，三条都满足才采信。

- **测不到片基时会说出来**。C-41 片基必然是橙色（R > G > B 且相差明显）。测出接近中性
  说明画面里没有裸露片基（例如已经裁到画面区域的扫描件），此时自动结果只是画面最亮处，
  界面会提示改用「框选片基」手动标定。

**修复**

- **「自动（单张）」会把片基广播给整卷**。它调用的是整卷估计器，会写给每一帧；名字说
  只管当前帧，实际改了整卷。

- **「应用标定到整卷」重开工程后失效**。参数写进了内存但没有触发保存——自动保存只跟随
  当前帧的滑块，其余帧是直接改的，程序不知道它们变了。

- **整卷分析测片基前先裁切，把片基裁掉了**。片基就在裁切要去掉的那圈边缘上，2% 的裁切
  就足以让它测不到而退回分位估计。这是「导入时第一张正常、整卷分析完就偏色」的直接
  原因。现在片基单独用未裁切的画面测，其余统计仍用裁切后的。

- **D-max 被挡光板和片边带偏**。挡光板比任何曝光区都密，直接成了 D-max；不透光的片边
  又把三个通道不等地抬高。现在两端都排除在统计之外，画面里的暗部主体不受影响。

- **暗端检测在纯黑处失效**。扫描件黑边三个通道相差 0.001，肉眼纯黑，按相对色偏算却是
  15%，于是被当作画面保留了下来。

- **暗端检测的一处数组越界崩溃**。

- **重采片基后暗端没有跟着换算**。两端都是相对片基的密度，换片基就得同步平移。亮端一直
  有做，暗端此前是加性偏移量、与片基无关，所以不需要；现在它也是绝对密度了。

- **`FrameParams.Clone()` 漏掉逐通道端点**。撤销快照与之共用这条路径，因此撤销一步会把
  亮端端点悄悄换成默认值。

---

The import no longer stops to ask a question, and the roll-calibration panel has two
automatic buttons at the top. Automatic calibration now measures correctly on several
kinds of negative it used to get wrong: rolls with no light panel, rolls where the film
base survives only as a sliver at the edge, and rolls with a light blocker in shot.

**Changed**

- **White balance is now brightness / temperature / tint / black level.** Underneath the inversion
  has six absolute densities (three per end), but those are not knobs anyone wants to turn: the
  three highlight densities carry both "how bright this is" and "which way it is cast", so reaching
  for brightness inevitably moved the colour too. They are now split into two **strictly
  orthogonal** groups —

  | Control | What moves | Side effect |
  |---|---|---|
  | **Brightness** | The geometric mean of the three highlight densities | Cast **completely unchanged** (slope ratios identical to the last digit) |
  | **Temperature / tint** | The ratios between channels (geometric mean held) | Lightness essentially unchanged (~2% measured) |
  | **Black level** | The mean of the three shadow densities (additive) | Shadow cast preserved exactly |

  The split has to be **geometric**: multiplying all three endpoints by one factor leaves the cast
  untouched, while adding a constant drifts it (R/B measured 1.0779 → 1.0713). That is the root of
  "adjusting the highlight end also changed the brightness" — the seam was simply in the wrong place.

  Temperature and tint reuse the same WbMath basis as Frame edit, so the names, scale and feel are
  identical; only the domain differs. The six absolute densities remain editable under **Advanced:
  the six endpoint densities** — two views of the same data.

- **The "Colour cast" group is gone from Frame edit** (temperature / tint / grey point). Colour
  balance is a property of the inversion's white end and should exist in exactly one place;
  adjusting it again in Frame edit stacked a second layer on settled endpoints, where the two masked
  each other and a cast could not be traced to either. Exposure, contrast, saturation and the curves
  are untouched.

  > A layer stored by an old project **still applies**, so those rolls look as they did. It is
  > deliberately **not** folded into the endpoints: a linear-domain gain is an *additive* shift in
  > density while an endpoint sets a *slope*, and the two can only agree at one density — folding
  > measured −18% on the thin end, +4% in the midtones and +53% on the dense end. Frame edit shows a
  > notice and a "clear" button that hands colour back to the highlight end.

- **Both white-balance ends now show real densities.** The "highlight" and "shadow" fields
  are each channel's own measured density (highlights typically 1.8–2.4), no longer a
  `1,1,1` / `0,0,0` correction factor relative to some baseline. The differences between the
  three channels ARE the cast — these numbers are the white balance itself, and nothing else
  is needed to describe it.

  This is not only a change of display. A correction factor is meaningful only relative to
  some *other* statement of the same endpoint, so there were necessarily two descriptions
  competing: calibration order mattered, and the same correction could be applied twice.
  Removing the old chain last version closed the double-application path, but at the cost of
  leaving wb_high a dead field nothing read — moving its sliders changed no pixels. With
  absolute densities the two descriptions collapse into one and the problem is gone
  structurally.

  Sampling the two ends no longer has an order either: each is measured independently, so
  either one may be taken first.

- **"Brightest = white" and "Deep white balance" fixed.** Both wrote to exactly that dead
  field, so they would run, report numbers in the status bar, and change nothing at all
  (Deep WB burning up to 50 rounds of network inference to do it). They now write the
  highlight endpoint the inversion actually reads.

- **One inversion model: density endpoints.** The endpoint model had been living alongside
  the older grade/pivot chain so that existing projects would render bit-identically, which
  left two places able to state the highlight balance — the between-channel difference in
  the endpoints' slope, and wb_high. The roll-wide calibration wrote both, applying the same
  correction twice: about a third too much red, applied inside the inversion where no Frame
  edit slider can reach it. The old chain is gone.

  > **Existing projects open unchanged.** Loading converts the old wb_high multiplier and
  > additive offset into the two endpoints, reproducing the previous render bit-for-bit. A
  > project saved before 2026-08-12 still carries no per-channel endpoints and opens on a
  > neutral set, so it will not look as it did — run "Auto (whole roll)" once, or sample
  > D-max, to bring it back.

- **No more sprocket-mask dialog after an import.** The threshold is measured and applied to
  the roll, so an import goes straight to a picture. To check or adjust it, go to Roll
  calibration → Sprocket mask and tick "show mask" — the same control the dialog offered.

- **The "Density endpoints" panel is gone.** It had nothing to operate, only a readout. The
  explanation stays as a note under the film-base group.

**Added**

- **"Auto (whole roll)" and "Auto (this frame)"**, at the top of the roll-calibration panel.
  The automatic mask removal always existed, but only as a checkbox in the import dialog:
  once the import was done there was no way back to it.

  "Whole roll" pools the roll into one parameter set and writes it to every frame; "this
  frame" solves the current frame alone, for a picture shot under a different light. Neither
  touches the crop.

**Improved**

- **The light panel is now identified by what it physically is.** The old test only asked for
  a peak at the bright end with a dip below it, which every histogram satisfies. A panel must
  now also be a separate cluster, occupy a real share of the frame, stand clear of the film,
  reach the frame's edge, and be as bright as a bare light source — film base seen through the
  orange mask is a third as bright and is no longer mistaken for one.

  Three kinds of roll were affected: 120, where the panel can fill a third of the frame;
  pictures that are themselves strongly bimodal; and frames whose only bright region is a
  sliver of bare base.

- **The film base can be measured with no light panel present.** A scan often keeps a strip of
  bare rebate under 1% of the frame — far too small for a percentile, which lands on the
  picture's highlights and reports a base more than twice too dense. That sliver is now looked
  for directly, and believed only when it is a separate cluster, at the edge, and orange.

- **When no film base can be measured, it says so.** A C-41 base is orange by construction
  (R > G > B, by a clear margin). A near-neutral result means there is no bare base in frame —
  a scan already cropped to the picture, for instance — and the panel now says as much and
  points at the manual film-base sample.

**Fixed**

- **"Auto (this frame)" broadcast the film base to the whole roll.** It calls the roll-wide
  estimator, which writes to every frame — the opposite of what the button's name promises.

- **"Apply calibration to the whole roll" did not survive reopening the project.** The values
  reached memory but nothing triggered a save: autosave follows the current frame's sliders,
  and the other frames were written directly.

- **The roll-wide pass cropped each frame before measuring the film base, cropping the base
  away.** The base sits in exactly the margin a crop removes, and a 2% crop is enough to lose
  it and fall back to a percentile. This is why a roll looked right on the first frame during
  import and drifted the moment the roll-wide pass finished. The base is now measured on the
  uncropped frame; everything else still measures the kept picture.

- **D-max was set by the light blocker or the film edge.** A blocker is denser than any exposed
  area and simply became D-max, while an opaque film edge lifted the three channels unequally.
  Both ends are now excluded, and a dark subject inside the picture is left alone.

- **The dark-end detection failed on true black.** A scanner's black border separates its
  channels by 0.001 — visually pure black — which a relative test reports as a 15% cast, so it
  was kept in as picture.

- **An out-of-bounds crash in the dark-end detection.**

- **Re-sampling the film base did not rebase the shadow end.** Both ends are densities measured
  relative to the base, so a new base shifts them. The highlight end always did this; the
  shadow end used to be an additive offset, independent of the base, and did not need to —
  now that it is an absolute density, it does.

- **`FrameParams.Clone()` dropped the per-channel endpoint.** Undo snapshots share that path,
  so a single undo silently reset the highlight endpoint to its default.

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


