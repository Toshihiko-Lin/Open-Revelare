# OpenRevelare 色彩管理链路修复架构

状态：**Windows PR 候选（M1/M2 与 M3/M4 Windows 切片已通过自动化回归）**。macOS M5 按目标延后；Windows 色度计/跨显示器人工验收仍待完成。

范围：输入表征、工作空间、输出渲染、导出 ICC、软打样预留、屏幕呈现与跨平台边界。

代码基线：`main` @ `b7cd46d61c2861c334e3af043126f8ead4831f5e`，.NET 8、Avalonia 11.2.3、SkiaSharp 2.88.9。

最后复核：2026-08-30。

规范语言：本文中的“必须 / 不得 / 应 / 可以”分别对应 MUST / MUST NOT / SHOULD / MAY。

> [!IMPORTANT]
> 本文是这个 fork 中色彩管理修复的设计真源，不是调研笔记。后续 session 开始前必须先读
> “不变量”“决策记录”“里程碑状态”和“开放决策”。如果实现需要改变已接受的决定，必须先
> 更新本文的决策记录和相关测试，不能只在代码注释里形成第二套架构。

---

## 1. 问题定义

用户在 Windows 11、硬件校色的 Adobe RGB 艺卓显示器上观察到：OpenRevelare 内预览与导出后
由色彩管理软件打开的图片不同，Adobe RGB 输出的红色尤其明显。macOS 也存在同类问题；系统
能力不是限制，当前应用没有给系统合成器一个完整、明确的色彩契约。

这不是一个孤立的“预览控件 bug”。色彩语义在五个边界上都可能丢失：

1. 输入文件数值属于什么 profile，或者是否根本未表征；
2. 解码后何时进入 linear ACEScg 工作空间；
3. 渲染完成后像素究竟采用什么原色、白点和 TRC；
4. 导出文件嵌入的 ICC 是否逐项描述实际像素；
5. 屏幕 surface 的输入空间是什么，最后一次 monitor transform 由谁执行。

只修第 5 项会得到“非常准确地显示错误像素”的广色域窗口；只修导出 ICC 也无法让应用内预览
与导出重开一致。本修复必须覆盖整条链路。

### 1.1 当前链路

```text
RAW camera-native / TIFF device RGB
          │  部分 TRC / matrix 手写解析；无统一状态
          ▼
ImageBuffer(float[]) ── 调用方约定它“现在是什么”
          │  未声明输入时实际直接按 ACEScg 使用
          ▼
linear ACEScg（约定，不是类型保证）
          │  输出空间转换 / print LUT / Stage 2
          ▼
ImageBuffer(float[]) ── 目标编码仍靠调用方记忆
       ┌──┴──────────────────────────────┐
       │                                 │
       ▼                                 ▼
BitmapConvert → 8-bit BGRA        JPEG/TIFF 量化
       │ 给 source SKImage 加 tag         │ 按空间名称重新生成 ICC
       ▼                                 ▼
Avalonia null-CS surface           色彩管理查看器按 ICC 显示
       │
       ▼
未定义 / 未管理的屏幕数值
```

### 1.2 目标链路

```text
SourceFrame(pixels + CharacterizedProfile | UncharacterizedCapture)
          │
          │  共享 CMM；只有明确表征的输入才能作色度转换
          ▼
WorkingFrame(linear ACEScg, float32)
          │
          │  反相、显示渲染、print LUT、Stage 2
          ▼
RenderedFrame(float32 + exact OutputProfile + OutputRecipe)
       ┌──┴────────────────────────────────────────────┐
       │                                               │
       ▼                                               ▼
导出器：只量化并嵌入同一 profile             共享 preview transform
                                                       │
                                                       ▼
                                   linear extended-sRGB / scRGB float
                                                       │
                                         共享 PreviewSceneRenderer
                                                       │
                                                       ▼
                                      PresentationBuffer + contract revision
                                                       │
                                                       ▼
                                           极薄的平台 presenter
                                                       │
                                      恰好一次 monitor transform
```

---

## 2. 目标与非目标

### 2.1 目标

- 相同 `RenderedFrame` 的直接预览，与其导出后重新读取 ICC 再预览，在量化误差内一致。
- 输入、工作、渲染、导出和呈现每个边界都携带机器可检查的色彩状态，不再依赖注释或调用顺序。
- Windows、macOS、Linux 和 CLI 使用同一套输入/输出 CMM、render/look 数学和测试向量。
- Windows Advanced Color 与 macOS 共享一种线性、扩展范围、FP16 的呈现中间格式。
- platform-specific code 只负责显示能力发现、surface 生命周期、纹理提交和系统事件。
- 支持窗口跨显示器、显示 profile 变化、DPI 变化和 Advanced Color 状态变化，不污染 render/export。
- 保留可诚实诊断的 SDR / 8-bit fallback；无法证明正确的路径不得冒充“色彩管理完成”。

### 2.2 非目标

- 不重写为 Electron，也不替换 Avalonia 的非色彩关键 UI。
- 不恢复已删除的图像处理 GPU backend。原生 D3D/Metal 只作最终 surface/present；Core 的反相、
  Stage 2 和导出仍为共享 CPU 实现，除非以后有独立测量与架构决策。
- 第一阶段不实现 HDR grading 或 HDR 输出。FP16 extended-linear 是 SDR 广色域的无损载体；HDR/EDR
  参考白和 tone mapping 是单独决策。
- 不把显示器 ICC 当成输出文件 ICC。显示 profile 描述设备，输出 profile 描述文件，两者职责不同。
- 不声称未表征的相机 RAW 具有绝对色准。未知必须是显式状态，不能偷偷贴 sRGB 或 ACEScg 标签。
- soft proof 不与“输出空间”混为一项。它是独立的 proof profile / intent / paper simulation 功能，
  本架构预留接口，但不属于第一轮修复的完成条件。
- 不在第一阶段给任意外部 LUT 型输出 profile 提供 UI。架构支持它之前，先把内建 RGB 输出和嵌入
  输入 ICC 做正确；外部 profile 必须经过单独兼容性测试后再开放。

---

## 3. 已证实的错误假设

下面这些不是待讨论观点，而是后续实现不得重新引入的已确认事实。

| 位置 | 当前假设 | 实际情况与后果 |
|---|---|---|
| `Gui/Controls/ColorManagedImage.cs` | Avalonia destination `SKColorSpace == null` 等于 sRGB | Skia 规则是 null destination 继承 source，跳过转换；只给 source 加 tag 并不产生显示变换 |
| Avalonia 11.2.3 macOS backend | 系统会自动识别内容空间 | `CAMetalLayer.colorspace` 未设置；Apple 规定 nil 时不做 color matching，OpenGL/software 路径也没有可靠契约 |
| `Gui/Interop/BitmapConvert.cs` | 校准显示器后 OS 会替应用转换 | 代码先把目标编码量化到 BGRA8，却没有向最终 surface 声明来源或目标，OS 无从正确转换 |
| `Tests/PreviewColorSpaceTests.cs` | Skia 转换测试代表真实预览 | 测试人工创建带 sRGB tag 的 destination；生产 destination 为 null，属于 false coverage |
| `Core/ImageBuffer.cs` | `ImageBuffer` 恒为 linear-light RGB | 同一类型同时承载 camera-native、linear ACEScg、output-encoded RGB；类型无法阻止漏转和双转 |
| `Core/RawDecode.cs` + `Core/Pipeline.cs` | RAW 可直接进入 ACEScg 链路 | RAW 明确输出 linear camera-native；`InputPrimaries` 目前通常为 null，实际未转换便按 working 数据使用 |
| `Core/TiffIO.cs` + `Core/IccRead.cs` | 手写 matrix/TRC 解析足以支持嵌入 ICC | 不支持 A2B/B2A/CLUT；parametric curve 只准确支持 type 0，其余被近似为 sRGB；TRC 与 matrix 还可能各自失败，形成半次转换 |
| `Core/TiffIO.cs` untagged 路径 | 8-bit inverse-sRGB 或 16-bit 原样即可进入 working | 8-bit 只解 TRC、没有 sRGB→ACEScg primaries transform；16-bit 原色仍未知，两者随后都可能被当 ACEScg |
| `FrameParams.InputWhitePoint` / `InputTransform` | 缺省白点约定一致 | 注释写 D65，实现使用 working white（约 D60）；这是 null/implicit semantics 已漂移的实例 |
| `Core/ColorPipeline.ToOutputSpaceVia` | 保留 print cube 的 Rec709 数值，再嵌目标 ICC 就能保留 look | 像素仍带 Rec709 TRC，而 sRGB/P3/Adobe RGB profile 声明另一条 TRC；像素/profile 不匹配，Stage 2 的 decode 也可能用错曲线 |
| `CubeLut` / `PrintLuts` | 任意用户 `.cube` 都可按 Cineon→Rec709 使用 | `.cube` 的**语法**没有这两个字段，但 Resolve 导出会把 output 写进头部注释（两个内置资产即如此），而解析器把注释丢掉了；当前 UI 接受任意 LUT 却套同一个隐含合同。output 曾由 D-018 改为读取文件自述、input 由 D-019 同法处理——但只有 Resolve 自带的 Film Looks 写这行注释，用户从 Resolve 生成的任何 LUT 都不写，于是整条路只剩 6 个文件能走。**D-033 改为由卷声明合同**（输入/输出编码），文件头只做预填 |
| legacy `DisplayReferredStage2=false` | 最后加目标 TRC 就是目标空间 | 路径没有完整的 primaries/display render，却仍可能嵌目标 ICC；旧项目需要明确兼容与迁移策略 |
| CLI JPEG export | GUI/CLI 导出契约相同 | CLI 当前调用默认无 ICC overload；wide-gamut JPEG 即使像素正确也会成为未标记文件 |
| 用户文档 `THEORY/GUIDE` | “预览数值原样提交，OS 根据已注册显示器 ICC 统一转换” | 对当前无标签 Avalonia surface 不成立；实现完成前这些段落必须视为已知过时说明 |

Skia 的 null 规则见 [Skia Color Management](https://skia.org/docs/user/color/)。Apple 对 Metal layer
的规则见 [`CAMetalLayer.colorspace`](https://developer.apple.com/documentation/quartzcore/cametallayer/colorspace)。

实现优先级上，裸 buffer 语义、legacy/LUT 的 pixels/profile mismatch 和无 destination contract 都是
P0 correctness blocker；完整 ICC input 是紧随其后的 P1。P0 没完成前，不得把单独出现一个 F16 native
surface 宣称为“bug 已修复”。

---

## 4. 不变量

以下不变量优先级高于具体类名和 API 选择。

### I1：像素与 profile 一一对应

`RenderedFrame.Pixels` 的 primaries、white point、TRC、reference state 必须全部由
`RenderedFrame.OutputProfile` 准确描述。不得“保留数值，只换 ICC 标签”。

### I2：色彩状态不能是 null 约定

每个跨模块图像对象必须是以下二者之一：

- `Characterized`：携带精确 profile / calibration identity；
- `Uncharacterized`：明确记录未知来源和 compatibility policy。

不得用 `null` 同时表示 sRGB、ACEScg、camera-native、无需转换或“以后再说”。

### I3：每个 surface 恰好一次 monitor transform

- system-managed surface：应用不得先转到 monitor device RGB；
- app-managed surface：共享 CMM 转到 monitor device RGB 后，surface 不得再被系统按 source profile 转换；
- 无法证明次数为 1 的路径必须标为 `UnmanagedEmergency` 并向用户显示原因。

注意 contract 属于具体 surface，不是整个操作系统。macOS 主预览可以是 ColorSync 管理的 native
layer，而同一窗口中的 Avalonia 缩略图 surface 仍可能需要 app-managed device RGB。

### I4：主预览在最后一跳前不得收窄

进入主预览 presenter 前不得：

- 量化为 8-bit；
- clamp 到 `[0,1]`；
- 以普通 sRGB destination profile 作 gamut clipping；
- 丢掉负分量或大于 1 的分量。

linear extended-sRGB 使用 sRGB/D65 原色坐标，但依靠 float 的负值和大于 1 值承载 P3、Adobe RGB
等超出 sRGB 三角形的颜色；它不是“更高精度的窄 sRGB”。

### I5：显示环境不能改变渲染和导出

显示器、ICC、DPI、窗口所在屏幕、Advanced Color/EDR 状态只能使 presentation generation 失效；
不得改变 `RenderedFrame`、导出像素或导出 profile hash。

唯一的口子是 D-027：**新建**卷的 `HdrPeakNits` 初值取自导入那一刻的显示器。它随即写进工程，
成为卷自己的参数；此后显示器变化对它无效，已存在的卷更是丝毫不受影响。

D-028 的高光软校样**不是**口子：它发生在 presentation generation（组合根，`RenderedFrame` 之后），
正是本条允许显示环境进入的那一步。

### I6：共享 render 在各平台相同

相同源数据、项目版本、参数、profile bytes 和 CMM 版本，在 Windows、macOS、Linux、CLI 上必须产生
相同 `RenderedFrame`。允许容器写入非决定性 metadata，但像素与 ICC payload 必须可比较。

### I7：旧项目由显式版本路由

旧项目行为不得由“缺字段时默认 false/null”偶然决定。新增 `color_pipeline_version`；任何视觉迁移都要
有 fixture、golden 和用户可见策略。

### I8：平台 presenter 不懂业务场景

Windows/macOS presenter 不得知道 crop、sharp patch、齿孔、clipping、曲线或输出空间。它只接受
已经合成的 `PresentationBuffer`，并验证 encoding、target id 和 revision。

---

## 5. 规范数据模型

最终命名可以随实现调整，但以下语义字段不得省略。

### 5.1 Profile identity

```csharp
public sealed record ColorProfileRef(
    ReadOnlyMemory<byte> IccBytes,
    ProfileId Sha256,
    string Description,
    ProfileRole Role,
    ProfileOrigin Origin);
```

- ICC bytes 或生成这些 bytes 的同一个 immutable object 是真源；字符串名称只用于 UI。
- profile hash 对 exact bytes 计算，参与 project、transform cache、diagnostics 和测试。
- 内建 sRGB/P3/Adobe RGB/ACEScg profile 必须确定性生成并有固定 hash fixture。
- 将来允许用户选择外部输出 profile 时，项目必须保存该版本的 bytes 或内容寻址副本；只保存文件路径
  会让系统 profile 更新后旧项目重渲染，违反可复现承诺。
- monitor profile 是设备当前状态，不写进 roll，也不参与 render hash。

### 5.2 Pixel encoding

```csharp
public abstract record ColorEncoding;

public sealed record CharacterizedEncoding(
    ColorProfileRef Profile,
    ColorReference Reference,
    TransferState Transfer,
    NumericRange Range) : ColorEncoding;

public sealed record UncharacterizedEncoding(
    CaptureKind Kind,
    string StableSourceId,
    CompatibilityPolicy Policy) : ColorEncoding;
```

`ColorReference` 至少区分 scene-referred、display-referred 和 monitor-device；`TransferState` 至少区分
profile-encoded 与该 profile primaries 下的 linear；`NumericRange` 记录 normalized 或 extended。

### 5.3 Frame types

```csharp
public sealed record SourceFrame(ImageBuffer Pixels, ColorEncoding Encoding);
public sealed record WorkingFrame(ImageBuffer Pixels, WorkingSpaceId Space); // linear ACEScg/F32
public sealed record RenderedFrame(
    ImageBuffer Pixels,
    ColorProfileRef OutputProfile,
    OutputRecipe Recipe,
    RenderFingerprint Fingerprint);

public sealed record PresentationScene(
    ReadOnlyMemory<Half> LinearExtendedSrgbRgba,
    PixelSize Size,
    float ReferenceWhiteScale);
```

`ImageBuffer` 可以继续作高效存储，但不得再裸着穿过色彩边界。内部 hot loop 可以接收 `Span<float>`；
模块边界必须接收上述 typed wrapper。

---

## 6. CMM 与转换所有权

### 6.1 单一应用级 CMM

输入 profile、输出 profile、proof transform、profile 校验与 Windows legacy 的 monitor transform 均由
共享 LittleCMS 实现。必须锁定 LittleCMS 版本、native binary、flags 和构建选项，并在三平台打包。

不得分别用 Windows WCS 与 macOS ColorSync 执行文件级输入/输出转换。两套 CMM 对 intent、BPC、
CLUT 插值的细微差异会破坏跨平台可复现性。ColorSync/DWM 只在系统管理的 surface 上负责最后一跳。

### 6.2 初始转换政策

| Edge | 默认 intent / policy | BPC | Clamp | 所有者 |
|---|---|---:|---:|---|
| characterized input → linear ACEScg | relative colorimetric；保留输入测量 | off | 不在中间 clamp | shared CMM |
| linear ACEScg → 内建 RGB output | 当前显式 gamut policy，记录进 `OutputRecipe` | policy-defined | 只在最终目标 gamut resolve | shared render/CMM |
| print cube native output → selected output | 按 cube 的准确 source profile 解码并色度转换 | 由 `OutputRecipe` 固定 | 不以“保数字”代替转换 | shared CMM |
| RenderedFrame → linear extended-sRGB | colorimetric connection；保留 extended values | off | **禁止** | shared preview transform |
| proof | proof profile + proof intent + optional paper/black simulation | explicit | proof policy | shared CMM |
| linear extended-sRGB → monitor device RGB8 | relative colorimetric | on，除非测试推翻 | 输出设备边界允许 | shared CMM |
| tagged F16 surface → physical display | OS contract | OS | OS | DWM / ColorSync |

若实测要求改变某个 intent、BPC 或 adaptation，必须修改表格、Decision Log 和 fixture；不得在调用点传
匿名 flag。

### 6.3 Canonical preview 转换

不能简单把普通 sRGB ICC 当 LittleCMS destination 后假定 float 会自动保留超范围。稳妥的初始实现是：

1. 用共享 CMM 把 `OutputProfile` 解码到 D50 PCS XYZ float；
2. 共享代码作 Bradford D50 → D65；
3. D65 XYZ → linear sRGB 3×3；
4. 不 clamp，写入 float/half-float；
5. 用独立测试向量确认 Adobe RGB/P3 饱和色的负值和 `>1` 分量仍存在。

矩阵/TRC 内建 profile 可以走经验证的 fast path，但结果必须与 CMM path 在容差内一致。CMM 是语义
真源，fast path 只是优化。

### 6.4 Cache、线程与生命周期

- transform cache key 至少包括 source profile hash、destination/proof hash、intent、BPC、adaptation、
  pixel format、CMM version 和相关 flags。
- 不跨线程无保护共享可变的 native transform handle；使用每 worker instance 或受控 lease/pool。
- profile/transform handle 必须确定性释放；应用退出不是资源管理策略。
- presentation frame 是 immutable ownership；队列采用 newest-wins/backpressure，旧 revision 和过期帧直接丢弃。
- 禁止恢复当前每次 `Render` 都复制整幅 bitmap 才给 source 加 tag 的模式。

---

## 7. 输入政策

输入准确性与显示准确性是两条独立工作线。显示修好后，未知输入仍然未知；应用必须诚实表达。

| 输入 | 目标状态 | 行为 | 失败策略 |
|---|---|---|---|
| TIFF + 有效 matrix/TRC ICC | `Characterized` | LittleCMS 完整转换为 linear ACEScg | profile 无法建立 transform 时阻止“已表征”路径并报告原因 |
| TIFF + A2B/CLUT ICC | `Characterized` | LittleCMS 使用 profile LUT；不拆成半个 TRC + 半个 matrix | transform 失败则显式错误/override，不静默猜 sRGB |
| malformed ICC | `Uncharacterized` 或用户明确指定的 `Characterized` | 不执行半次转换；只有已选择的 roll fallback 可接管，并在 decode recipe 记录拒绝原因 | 无显式 fallback 时拒绝；不得静默忽略坏 profile |
| untagged 8/16/32-bit TIFF | `UncharacterizedCapture`、检测出的 exact profile，或用户显式覆盖 | `TiffInputDetector` 依次读 SampleFormat、TIFF 6.0 色度标签、Exif ColorSpace、Software；都问不出才落到有标注的 sRGB 惯例；旧项目走 versioned compatibility | 不按位深偷偷决定原色；也不把「文件已经声明过的事」当成必须问用户的问题；惯例回退必须可见且可事后改判 |
| RAW negative | `CameraNativeUncharacterized` | UniWB、linear、camera-native 解码保持不变；等待 rig/film 联合表征 | 不自动套普通场景相机 ColorMatrix，不贴 ACEScg/sRGB |
| RAW + 经验证的 rig/film calibration | `CharacterizedCapture` | 使用与 `t_base`/endpoints 联合求得的输入变换 | calibration identity 随 roll 保存 |
| 用户指定 capture profile | `Characterized` | 保存 exact bytes/hash；重新建立依赖该输入的 roll calibration | profile 变化显式使相关 calibration stale |

当前 `RawDecode.CameraToSrgbMatrix` 可以保留为诊断或未来的明确 opt-in 实验，但不得在没有产品决策、
fixture 和重标定策略时偷偷接入主链。负片染料 + 光源 + sensor 的等效原色不等于相机拍摄普通场景时的
ColorMatrix。

Windows/GUI 的新 TIFF 卷即使当前文件带 ICC，也必须选定无/坏 ICC 时的整卷 fallback；有效嵌入 ICC
始终优先。选择存入 `roll_meta.tiff_is_linear`，并进入 full/preview/region/calibration/split 的 decode
cache identity。linear fallback 保持原色未表征并只作数值透传；sRGB fallback 使用 exact built-in sRGB
经一次完整 LittleCMS transform。CLI 的互斥 `--input-linear` / `--input-srgb` 现在是**覆盖开关**而非必填项；两个都不给即为自动检测。无可用
ICC 且两者都未给出时 fail closed。缺字段的旧工程才使用按位深的冻结兼容路径。

---

## 8. RenderedFrame 与导出契约

### 8.1 唯一输出对象

preview 和 export 必须从同一个 immutable `RenderedFrame` 分叉。exporter 不再接收“像素 + 一个可能
匹配的空间名称”，只接收：

- `RenderedFrame`；
- container/bit depth/compression/quality；
- 是否允许省略 profile 的明确 export policy。

exporter 只能量化、写 metadata、嵌入 `RenderedFrame.OutputProfile.IccBytes`。它不得重新生成、查找或
按名称推测另一个 profile。

### 8.2 print LUT 与 LUT 合同（D-033）

`.cube` 的语法没有色彩空间。Resolve 的做法是让用户在 LUT 前后放色彩空间转换节点来声明它；本项目同理，
但**输入这一端没有选择**：本程序的图像处理基于 Cineon，喂给 cube 的永远是 Cineon（`LutInputEncoding` 只有
这一个成员；文件头声明别的输入——ACEScct、Log-C、DaVinci Intermediate——在解析时拒绝，因为用户的声明救不了它）。
要声明的只有**输出**：`FrameParams.PrintLutOutput`（持久化 `print_lut_output`），由 `FrameParams.LutContractFor(lut)`
解析——卷有声明取卷的，没有取文件头预填的（Resolve Film Looks 的 `# Display: ITU-Rec.709, Gamma 2.4`），再没有
（Resolve 生成的 LUT 一律不写）：`Unknown` 并 fail closed；GUI 选片时按卷的状态预填——SDR 下 Rec709、HDR 下
Rec2020 PQ——写进卷并在状态栏说明。

| 端 | 成员 | 本程序做的事（= Resolve 里对应的 CST） |
|---|---|---|
| 输入 | `Cineon`（唯一） | working → Rec709 原色（Clip）→ `LogEncoding.ToCineon`。v1 冻结的那条，逐位不变 |
| 输出 | `Rec709` / `DciP3` / `Srgb` | 以对应 exact profile（`ColorSpaces.Rec709` / `DciP3`（DCI 白、γ2.6）/ `Srgb`）为源，CMM 相对色度转换到卷所选 exact output profile；Stage 2 在目标编码中运行 |
| 输出 | `Rec2020Pq` | `Pq.DecodeToCarrier`（ST 2084 → nits ÷ 203）→ BT.2020→载体原色（`PreserveExtended`，负分量保留）。**不再套 `HighlightRolloff`**：LUT 自带肩部；卷的 `HdrPeakNits` 只做 `BoundAbove` 的顶与母版标签 |

**LUT 怎么参与由输出决定**（`LutContract.AppliesTo(target)`，唯一裁决点 `ColorPipeline.PrintLutFor(cal, target)`，
渲染与 recipe 都问它）：SDR 输出的印片在 SDR 目标上就是渲染本身，在扩展目标上按 **D-034** 参与——
`ToExtendedOutputTargetViaPrint`：印片照 SDR 路径渲染并经 CMM 解进载体线性光，再逐像素乘**一个标量**
`g = Y(解析 HDR 渲染) / Y(解析 SDR 渲染)`（两者是 D-021 的同一族曲线，拐点以下相同 ⇒ g = 1，印片逐位透传；
拐点以上 g 向 headroom 增长，印片被自己肩部折进纸白的高光按同一族曲线打开；Y(SDR)=0 处 g := 1）。标量而非逐通道，
因为印片的 RGB 比例（色相、饱和度、高光去饱和）就是用户选的 look，只让亮度打开；这也让 D-031 的 SDR rendition
= 印片本身、拐点以下逐位相同，亮度单通道增益图（D-030）承载全部差异——`SdrRendition()` 因此**保留** SDR 印片、
只剔除 PQ LUT。PQ 输出只在扩展目标上参与、SDR 下让位给标准显示渲染。**GUI**：SDR 下选片器不列 PQ 输出的 cube，
HDR 下全列；输出按钮两种状态都有（v2），PQ 一项只在 HDR 下可见；未声明的 cube 一律预填 Rec709（世上绝大多数
cube 是 SDR 的）；带 PQ LUT 关 HDR 的卷仍列出并注明让位；HDR 下选印片旁边注「印片色 · HDR 影调」。

recipe 的 `PrintLutIdentity`：内置为记号，外部文件为 `sha256:` + `CubeLut.ContentIdentity`（表的内容哈希，
注释与 TITLE 不计）；合同进 `GamutPolicy`。此前 `DescribeManagedPixels` 对任何外部文件抛
"must carry a portable built-in identity"，即 D-018 放行的文件在渲染时又被拒——这就是用户看到的
"达芬奇成品 LUT 用不了"。

**随程序发行的 LUT 文件夹**（2026-09-13 傍晚追加）：`Assets/Luts/*.cube` 原样复制到产物的 `luts/`（`PrintLuts.BundledDir`），
选片器列在最前，工程存 `:luts/<文件名>` 记号（可移植，`IsBuiltin` 为真、进 fingerprint）。内容：Resolve 的六张
Rec709 Film Looks（2383 / 3513DI × D55 / D60 / D65）。**不自带 HDR LUT**：市面上没有 Cineon 进、PQ 出的 3D 成品，
曾烘过两张"HDR Standard N nits"（= 无 LUT 渲染本身），用户指出它和【HDR 上限】重复、选了没变化，已删；HDR LUT 由
调色人在 Resolve 里以 Cineon Film Log 时间线、Rec.2100 ST2084 输出生成。两张 D65 仍嵌进程序集，`:kodak-2383` /
`:fujifilm-3513di` 旧记号照旧解析，不再作为固定行出现在选片器里。用户自己的 cube 仍放 per-user 的 `Settings.LutDir`。

颜色管理保持的是颜色/观感，不是跨不同 TRC 的相同 code value。"保留 native 数值再贴目标 ICC"明确禁止。
PQ 是绝对量：以 203 nits 母版白的 LUT 落在载体 1.0 上、拐点以下与 SDR rendition 一致；以 100 nits 为白的
落在 1 档之下——那是作者的决定，原样携带。**已知代价**：PQ LUT 卷的 SDR rendition（D-031：缩略图、印样、
增益图基底）不走 LUT（`SdrRendition()` 清空 `PrintLut`），两个 rendition 不再是同一张画的两个余量。

### 8.3 文件政策

| 输出 | Profile 政策 |
|---|---|
| sRGB JPEG/TIFF | 默认嵌 exact sRGB；允许用户明确省略，但诊断中记录 untagged |
| Display P3 / Adobe RGB / Rec709 JPEG/TIFF | 强制嵌 exact profile；不提供会产生不可移植文件的普通 UI |
| scene-linear ACEScg TIFF | 32-bit IEEE floating-point（`SampleFormat=IEEEFP`），保留负值与 `>1` 分量并嵌 deterministic linear ACEScg ICC；若 VFX 工具链要求无 ICC，作为单独 expert preset，而非默认 |
| JPEG scene-linear | 不支持 |
| CLI export | 与 GUI 使用同一个 `RenderedFrame`/exporter，不得走无 ICC 的第二条实现 |
| contact sheet export | 明确固定输出 profile（初始为 sRGB）并嵌同一 bytes |

JPEG 的 YCbCr 编码与有损压缩意味着不能作逐码值等价测试；normalized 输出用 TIFF16 作 round-trip 主验收载体，extended/scene-linear 输出必须用 TIFF32F 验证负值与 `>1` 分量不被钳制。

### 8.4 legacy project

缺失 `color_pipeline_version` 的项目视为 v1，不得自动改 look。M2 开始前必须在以下方案中作产品决策：

- **L1 compatibility profile**：保留旧像素，生成准确描述旧混合 primaries/TRC 的 legacy profile；UI 明确
  标示 compatibility，允许用户 opt-in 重渲染；
- **L2 opt-in migration**：默认仍按 v1 渲染，用户迁移后写 v2 并接受 golden 变化；
- **L3 forced correction**：加载即迁移。除非有强证据和回滚方案，否则不采用。

无论选择 L1/L2/L3，都不得继续把不匹配的旧像素标为用户所选目标 ICC。最终决定记录到第 17 节。

---

## 9. Presentation contract

### 9.1 Surface 级 contract

```csharp
public enum PresentationEncoding
{
    LinearExtendedSrgbRgba16F,
    MonitorDeviceBgra8,
    UnmanagedEmergencySrgb8,
}

public enum FinalTransformOwner
{
    SystemCompositor,
    Application,
    None,
}

public sealed record DisplayContract(
    string DisplayId,
    long Revision,
    PresentationEncoding Encoding,
    FinalTransformOwner TransformOwner,
    ColorProfileRef? DeviceProfile,
    float SdrReferenceWhite,
    float ExtendedHeadroom,
    string DiagnosticName);

public sealed record PresentationBuffer(
    ReadOnlyMemory<byte> Bytes,
    PixelSize Size,
    PresentationEncoding Encoding,
    string TargetDisplayId,
    long ContractRevision);
```

约束：

- `SystemCompositor + LinearExtendedSrgbRgba16F` 不得携带已应用的 monitor transform；
- `Application + MonitorDeviceBgra8` 必须携带 exact device profile identity，且 surface 必须是 passthrough；
- presenter 必须拒绝 encoding 不匹配、display id 不匹配或 revision 过期的 frame；
- `UnmanagedEmergency` 必须有用户可见告警，不能命名成“正确 sRGB fallback”。

### 9.2 平台模式

| Surface / 模式 | 共享层输出 | `ReferenceWhiteScale` | 最后一次 monitor transform |
|---|---|---|---|
| Windows Advanced Color **WCG**（SDR 屏）主预览 | linear extended-sRGB RGBA16F | `1.0` | DWM |
| Windows Advanced Color **HDR** 主预览 | linear extended-sRGB RGBA16F | `SdrWhiteNits / 80`（D-020） | DWM |
| Windows legacy SDR 主预览 | monitor-device BGRA8 | `1.0` | shared LittleCMS |
| macOS native 主预览 | linear extended-sRGB RGBA16F | SDR/WCG `1.0`；EDR 待 D-012 | Core Animation / ColorSync |
| macOS/Windows Avalonia 次要图像 surface | **ACM 开启时必然是 sRGB**（见下）；否则优先 monitor-device BGRA8，能力不足则 emergency | `1.0` | ACM 开：DWM 按 sRGB；否则 shared LittleCMS 或明确无管理 |
| Linux 初期 | 已验证 profile provider 时 monitor-device BGRA8，否则 emergency | `1.0` | shared LittleCMS 或无 |

> [!IMPORTANT]
> **次要 surface 那一格是 OS 约束，不是应用选择。** Advanced Color 激活时，Windows 的 profile
> 管理 API 对 `STANDARD` subtype **一律返回「无 profile」**，无论实际装了什么；未打标签、走整数
> 像素格式的内容一律被当作 sRGB。也就是说 Avalonia 画的任何图像 surface 在 ACM 下拿不到显示器
> profile，也无法超出 sRGB 色域。微软提供的出口是 per-exe 的兼容助手（可执行文件属性 →
> 兼容性 → *Use legacy display ICC color management*），而它**没有任何编程启用方式**。
> 主预览逃过这一条，唯一原因是它打了标签（scRGB FP16 + `SetColorSpace1`）。
> 若要在 AC 激活时取得**真实**面板 profile（做色域警告/软打样用），须查
> `CPST_EXTENDED_DISPLAY_COLOR_MODE` 而非当前代码使用的 `CPST_STANDARD_DISPLAY_COLOR_MODE`——
> 查 `EXTENDED` 等于向 OS 声明本应用是 Advanced Color-aware。

Windows 与 macOS 可以共享同一 D65/linear/RGBA-half 内存布局，但 `1.0` 的物理亮度语义不能默认完全
相同。`SdrReferenceWhite`/scale 是 contract 数据，由共享 presentation builder 应用，平台 presenter
不得暗藏曝光乘数。

---

## 10. 共享 PreviewSceneRenderer

当前主视口同时包含主图、full-resolution sharp patch、齿孔 mask、clipping overlay、裁切 dim/frame 和
selection。原生 `HWND/NSView` 存在 airspace，不能只把 `PreviewImg` 换成 native child 后继续指望
Avalonia overlay 盖在上面。

目标结构：

```text
RenderedFrame ── preview transform ──▶ linear extended-sRGB base
SharpPatch ──────────────────────────▶ 同一 canonical encoding
Masks / crop geometry / background ─▶ PreviewScene
                                         │
                                  shared renderer
                                         │
                              final viewport RGBA16F
                                         │
                            platform presenter: fixed blit
```

规范：

- 所有可见的 color-critical layer 在共享代码中合成一次；不得写 HLSL/MSL 两套 crop renderer。
- 初始实现可以用共享 Skia CPU/offscreen F16 surface；平台 GPU 只上传并 blit 最终纹理。
- 合成在 linear light 中进行，使用 premultiplied alpha；Avalonia/UI 定义的 sRGB 常量先线性化。
- scaling/filtering 在共享 renderer 中完成，避免两平台 sampler 差异。
- crop/zoom/pan/selection 的状态与命中测试继续由共享 C#/ViewModel/controller 管理。
- native child 只需很薄的 input-transparent/hit-test hook，让 pointer/scroll/magnify 回到共享控制层。
- 若整帧重合成性能不足，可输出 `base texture + premultiplied overlay texture + affine transform`；
  两个平台仍只做固定双纹理合成，不获得业务几何。

次要缩略图不阻塞主 presenter 交付，但在 M6 前必须明确处理：在当前未管理的 Avalonia surface 上转为
monitor-device RGB，或迁到可证明 system-managed 的 surface；不能永久保留“tag source 后碰碰运气”。

---

## 11. 平台边界

### 11.1 共享接口

```csharp
public interface IDisplayEnvironment : IDisposable
{
    DisplayContract Current { get; }
    event EventHandler<DisplayContract> ContractChanged;
}

public interface IPreviewPresenter : IDisposable
{
    PresentationEncoding AcceptedEncoding { get; }
    void Resize(PixelSize pixels, double scale);
    void Present(PresentationBuffer frame);
}
```

GUI composition root 选择实现。Core、ColorManagement、Preview 和 ViewModel 中不得散落
`OperatingSystem.IsWindows/IsMacOS`。

### 11.2 Windows

允许的 platform code：

- 按窗口取得 HMONITOR、显示 profile identity、DPI 和 Advanced Color 状态；
- Advanced Color：建立 `R16G16B16A16_FLOAT` swapchain/surface，并声明 scRGB 对应的 DXGI color space；
- legacy SDR：接收共享层已经转换好的 device BGRA8，原样 present；
- 监听窗口换屏、profile、DPI 和 Advanced Color 变化，递增 contract revision；
- create/resize/present/destroy。

不得在 Windows adapter 中实现 ICC、TRC、gamut mapping 或 overlay。

### 11.3 macOS

主路径是一个很小的 `NSView + CAMetalLayer` presenter：

- `pixelFormat = MTLPixelFormatRGBA16Float`；
- `colorspace = kCGColorSpaceExtendedLinearSRGB`；
- 把 final transform 交给 Core Animation/ColorSync；
- 监听 window screen/profile/headroom 变化并递增 revision；
- `NSScreen.colorSpace` 默认只用于诊断、能力与次要 app-managed surface，不在主 tagged layer 上再手动转一次。

项目目标是 `net8.0` 而不是 `net8.0-macos`。实现为一个 Objective-C++ C ABI dylib
（`src/OpenRevelare.Presentation.MacOS.Native`，导出 `orwm_probe/create/resize/present/query_diagnostics/destroy`），
沿用 `packaging/macos` 已有的 dylib 拷贝、install-name 和签名流程（`build-macos-presenter.sh`），
托管侧 `OpenRevelare.Presentation.MacOS` 镜像 Win32 那一层：`MacOSDisplayEnvironment`（语义键含
显示器 id、色彩空间名、backing scale、当前/potential EDR 值）、`MacOSDisplayContractProvider`（D-026）、
`MacOSPreviewPresenter`（stale revision 拒绝、换屏/换 EDR 请求即要求重建）。

`CAMetalLayer.wantsExtendedDynamicRangeContent` 暂不在本文写死。Apple 的 HDR/EDR 示例会设置它，
但“允许 extended RGB 分量以承载 SDR 广色域”和“让内容使用高于 SDR 白的亮度”不是同一个产品决定。
必须通过 M3 spike 验证负值/`>1` 分量、reference white 和外接 Adobe RGB 显示器行为，再更新 D-012。

### 11.4 Linux

Linux 第一轮不是硬件 WCG 验收平台，但仍走相同 contract：

- 能可靠取得当前窗口 monitor ICC 并证明 surface passthrough 时，用 app-managed device BGRA8；
- 否则使用 `UnmanagedEmergencySrgb8`，在诊断中写明 display management unavailable；
- 不为了“跨平台一致”静默假定所有 X11/Wayland compositor 都管理颜色。

---

## 12. 依赖与目录边界

目标依赖图：

```text
OpenRevelare.ColorManagement   (net8.0, no UI/no platform)
          ▲
          │
OpenRevelare.Core              (existing render; no Avalonia)
          ▲
          │
OpenRevelare.Preview           (shared scene/compositor; Skia allowed, no OS API)
          ▲
          │
OpenRevelare.Gui               (coordination + Avalonia shell)
          │
          ├── OpenRevelare.Presentation.Win32
          └── OpenRevelare.Presentation.MacOS
```

可先用项目内目录/namespace 建立边界，再拆 assembly；但最终依赖必须满足：

- `Core` 和 CLI 不引用 Avalonia、AppKit、Metal、DXGI；
- `ColorManagement` 不引用 GUI 或 platform assembly；
- `Preview` 不引用 Win32/macOS presenter；
- platform assembly/native shim 不包含色彩数学或 render/look；
- macOS/Windows shim 可共享同一窄 C ABI：create、resize、present、query/notify、destroy。

LittleCMS 是新增 runtime dependency；必须同步更新 `THIRD_PARTY_NOTICES.txt`、三平台打包脚本和 CI
产物检查。现有 LibRaw 在 macOS 已带 lcms support，不代表应用可以依赖或直接调用那份私有依赖；
应用 CMM 必须有自己锁定、可验证的装配方式。

---

## 13. Diagnostics 是验收功能

每份颜色 bug 报告必须能复制以下诊断，而不是靠截图猜：

- source kind、embedded/override profile description + SHA-256，或 `Uncharacterized` 原因；
- working space/version；
- output profile description + SHA-256；
- output recipe：intent、BPC、gamut policy、print LUT identity；
- `color_pipeline_version` 和 render fingerprint；
- presenter mode、surface encoding、final-transform owner；
- 当前 window/display id、monitor profile 名称/hash（若应用可见）；
- SDR reference white、EDR/headroom、contract revision；
- fallback 原因及是否 WYSIWYG 可保证。

建议状态栏简写，例如：

```text
macOS ColorSync · linear ext-sRGB FP16 · EIZO CG… · Output AdobeRGB [a1b2…]
Windows app-managed · EIZO…icc [c3d4…] · BGRA8
Unmanaged fallback · sRGB8 · wide-gamut preview unavailable
```

完整信息提供“复制色彩诊断”入口。没有 diagnostics，M4/M5 不算完成。

---

## 14. 测试不变量与验收矩阵

### 14.1 Core/CMM 测试

1. `canonical(RenderedFrame) ≈ canonical(read(export(RenderedFrame)))`。
   - float 内部 path：逐通道目标 `<= 1e-5`，最终阈值由独立向量验证后冻结；
   - TIFF16：预算包含 16-bit 量化；
   - JPEG：用 patch/Delta-E 统计，不作逐码值断言。
2. 测试至少使用一个独立 CMM 或公开 ICC test vector，不能让 LittleCMS 只与自己 round-trip 自证。
3. sRGB、Display P3、Adobe RGB、Rec709、linear ACEScg 各有 profile hash、TRC 和 primaries fixture。
4. ICC 输入覆盖 `curv`、`para` type 0–4、matrix/TRC、A2B/CLUT、malformed/truncated profile。
5. print LUT 覆盖 cube native output → sRGB/P3/Adobe RGB；重开后 PCS 颜色一致，允许明确 gamut policy。
6. canonical F16 饱和 P3/Adobe patches 保留负值和 `>1`，测试禁止提前 clamp。
7. source 未表征时必须停留 `Uncharacterized`，不得因缺字段变成 ACEScg/sRGB。
8. 相同 fixture 在 Win/mac/headless 得到相同 `RenderedFrame`、ICC bytes 和 canonical float。

### 14.2 Presentation contract 测试

- system-managed path：断言 app monitor transform count 为 0，surface tag 非空且匹配 buffer；
- app-managed path：断言 shared CMM monitor transform count 为 1，surface 不再声明会触发 OS 二次转换的 source；
- stale revision / wrong display id / wrong encoding 的 frame 被拒绝；
- display/profile/DPI 变化只使 presentation cache 失效，不改变 render/export fingerprint；
- overlay 在 linear premultiplied alpha 下有 golden patches；crop/patch/mask 坐标与当前 viewport 一致。

### 14.3 平台集成与实机

| 平台 | 自动检查 | 实机检查 |
|---|---|---|
| Windows Advanced Color | swapchain format/color-space、revision、无 app monitor transform | Advanced on；艺卓 Adobe RGB；与重新打开的 TIFF 对照 |
| Windows legacy SDR | monitor ICC provider、一次 lcms transform、passthrough surface | Advanced off；跨两个不同 profile 显示器移动 |
| macOS | CAMetalLayer `colorspace != nil`、RGBA16F、screen/profile event | built-in P3 + 外接艺卓；与 Preview/Photos 对照；验证 reference white/extended 分量 |
| Linux | fallback 状态与告警 | 已知 compositor/profile 环境记录结果，不冒充统一保证 |

参考查看器比较必须使用同一个导出文件及其嵌入 ICC。系统截图只能辅助定位，不能单独证明物理显示色准；
有条件时使用校色仪/色度计测试 patch。

### 14.4 Windows 当前实机与产物证据（2026-08-30）

- 当前显示器探针：EIZO CG2700X，Windows 11 Advanced Color `WideColorGamut`，10 bpc，SDR reference white 80 nits；选出的 surface contract 为 `LinearExtendedSrgbRgba16F`、`SystemCompositor` owner、无 fallback warning。
- native smoke 在真实 D3D11 adapter 上分别创建 legacy BGRA8 与 Advanced `R16G16B16A16_FLOAT`/scRGB swapchain，各完成 26 次 present；计时受 DWM/vsync 主导，中位数约 16.67 ms。
- reproducible native DLL SHA-256 为 `57E90A68D9356421F94C7217BDBA36178D2DABFA877495AAD6377E0DA790B0D5`；source-tree SHA-256 为 `8F00DC11C9932ED660AC43794213859D9A951AB52D78B3FFD16F1353707D8A72`。校验覆盖 AMD64 PE、精确五个 C ABI exports、静态 CRT 与双构建字节一致。
- 上述证据只证明 contract、格式、所有权和执行路径；与参考查看器的肉眼/色度计 patch 比较，以及 Advanced off 与第二台不同 profile 显示器的跨屏人工比较仍是 M4 未关闭的硬件门槛。

---

## 15. 迁移里程碑

每个阶段单独 PR/commit 组，完成条件全部满足后才更新状态。M4 与 M5 可在 M3 后并行，但不得各写一套
compositor。

| ID | 状态 | 内容 | Exit gate |
|---|---|---|---|
| A0 | **完成** | 本架构、远端 fork 基线 | 文档进入仓库并由后续 session 引用 |
| M0 | **完成** | 冻结复现文件、ICC、synthetic patches、旧项目 fixtures；让现有 null-destination false-positive 测试失败 | Win11+艺卓案例有可重复数值；现状错误被测试钉住 |
| M1 | **完成** | typed frames/profile identity；锁定并打包 LittleCMS；transform cache/diagnostics skeleton | app-owned LittleCMS 2.19.1 固定来源/哈希/加载路径；typed source/working/render 边界与 canonical fixture 已测试 |
| M2 | **完成** | 完整 ICC 输入；修 print LUT/TRC/output ICC、legacy、CLI/export；写 project version/migration | 有效 ICC 原子转换；untagged/坏 ICC 显式 fallback 与项目往返；normalized TIFF16 与 extended TIFF32F round-trip；exact embedded ICC；L2 显式迁移；稳定 render fingerprint |
| M3 | Windows 切片完成；macOS 待办 | RenderedFrame → unclipped canonical；共享 F16 scene compositor；presentation abstractions；reference-white/EDR spikes | Windows 主图/patch/masks/crop 单一合成且末跳前无 8-bit/clamp；D-011 已关闭，D-012 留给 macOS |
| M4 | 代码/自动化完成；硬件人工验收待办 | Windows legacy + Advanced Color presenter；跨屏/profile/Advanced 变化 | transform-count、ABI、recovery/fallback 与当前 EIZO 契约探针已验证；色度计/参考查看器和双屏人工比较待完成 |
| M5 | **托管半边完成，原生半边待真机**（2026-09-12） | macOS Metal presenter；ColorSync contract；跨 built-in/external screen | `OpenRevelare.Presentation.MacOS`（环境 / 契约 / presenter）+ 25 条假件测试全绿；Avalonia 宿主 `MacOSPreviewHost` + backend（5 条 reconcile/recovery 测试）已接入组合根（`IPreviewHost` 按平台选宿主，Windows 回归实测不变）；`Presentation.MacOS.Native` 的 Obj-C++ 源码与 `build-macos-presenter.sh` 已写但**未在 Mac 上编译**；D-026 定了 mac 的 reference-white/headroom 政策，D-012 仍 Open，候选规则见 D-026 |
| M6 | 部分完成 | Linux/secondary surfaces/fallback honesty；perf/memory/security；CI；删除旧桥接与 feature flag；更新用户文档 | Windows fallback/diagnostics、native CI/发布校验、性能基线和 GUIDE/THEORY 已完成；Linux/macOS 部分待后续 |

### 15.1 提交边界

- M0 测试提交不得顺手修生产代码。
- M1 语义类型引入尽量保持像素不变；若 golden 改变，先解释为何类型重构改变了数学，否则视为回归。
- M2 本来就会修正错误像素/profile，必须单列 golden 统计、旧项目策略和迁移说明。
- M3 只改变 preview/presentation，不得改变 export bytes。
- M4/M5 不得修改 shared render 以“让某个平台看起来对”；平台差异必须在 contract 或 presenter 解决。

---

## 16. 风险与防线

| 风险 | 防线 |
|---|---|
| app 与 OS 双重 monitor transform | `FinalTransformOwner` + transform-count contract tests |
| FP16 但已在 sRGB gamut clamp | canonical negative/`>1` fixtures |
| print LUT 数值/profile 再次漂移 | exact source/output profile objects；export round-trip invariant |
| arbitrary ICC 解析出半次 transform | 不再手拆；LittleCMS transform 原子创建，失败即失败 |
| display move 时旧 device-RGB frame 闪现 | display id + monotonic revision，presenter 拒绝 stale frame |
| NativeControlHost 遮住 overlay | shared scene compositor；native presenter 不单独替换 base image |
| 每次 paint 全帧复制 / GC 抖动 | immutable frame ownership、buffer pool、present queue backpressure |
| platform adapter 长成第二套引擎 | 依赖图与禁止清单；平台测试只检查 surface contract |
| Avalonia 升级后 null layer 行为改变，device RGB fallback 双转 | pin/version integration test；无法证明 passthrough 时降为 emergency |
| 未表征 RAW 被“顺手修成”普通 camera ColorMatrix | explicit `UncharacterizedCapture`；input policy/golden/product decision |
| HDR/EDR 改变 SDR 亮度 | reference-white contract；Windows 由 D-020 按模式分流并测试钉死，macOS 仍待 D-012 spike |
| 外部 profile 文件被替换 | exact bytes/hash 随项目或内容寻址保存 |
| **进程里已存在同名 lcms2，app-owned 那份被加载器去重掉** | 见下方附注：目前靠**同名覆盖**成立，不是靠隔离 |

**附注（D-002 的执行边界，三平台不等强）。** `LittleCmsBundle` 校验的是**磁盘文件的字节**，
而不是最终被映射进进程的那份镜像。Linux 的 `dlopen` 按 SONAME、macOS 的 dyld 按 install name
去重：若进程里已经存在同名的 lcms2，按全路径加载会拿回**已加载的那一份**，校验照样通过。
这不是假想——`Sdcb.LibRaw.runtime.linux64` 包里自带 `liblcms2.so`，win64 包里自带 `lcms2.dll`，
macOS 的 `bundle-libraw.sh` 会把 Homebrew 的 `liblcms2.2.dylib` 一并复制进 `native/<rid>`。

目前是安全的：三条流水线都用同名覆盖（`install` / `Copy-Item` / 顺序排在 `bundle-libraw.sh`
之后）把它们统一成 app-owned 的那一份，全进程只有一份字节。**但这是靠覆盖而不是靠隔离**——
一旦 LibRaw 改用带版本后缀的私有依赖、或换成静态链接自己的 CMM，这条保证会静默失效而不会
有任何测试变红。真要收紧，得让运行时能证明「加载到的就是我校验的那份」（例如比对已加载模块
的路径），而不是只证明磁盘上那份是对的。

---

## 17. 决策记录

已接受决定只能通过新增“supersedes D-xxx”的记录更改，不能覆写历史原因。

| ID | 状态 | 决定 | 原因 |
|---|---|---|---|
| D-001 | Accepted | 保留 Avalonia；只替换 color-critical presentation boundary | 不需要重写应用，且问题位于像素语义和最终 surface |
| D-002 | Accepted | LittleCMS 是唯一应用级文件/profile CMM | 三平台结果和 flags 可统一；避免 WCS/ColorSync 双实现漂移 |
| D-003 | Accepted | exact ICC bytes/hash 随 pixels 走；名称不是真源 | 防止导出按名称重建出不匹配 profile |
| D-004 | Accepted | 工作空间继续使用 linear ACEScg/F32 | 保留现有反相数学与宽色域 headroom；本修复不重做 look |
| D-005 | Accepted | canonical presentation 为 D65 linear extended-sRGB/scRGB RGBA16F | Windows Advanced 与 macOS 有共同标准载体，且可保留 sRGB gamut 外颜色 |
| D-006 | Accepted | macOS/Windows Advanced 由 OS 做最后一跳；Windows legacy 由 shared CMM | 每条路径恰好一次 monitor transform，平台代码最少 |
| D-007 | Accepted | overlay/patch/crop 在共享 F16 scene 中合成 | 解决 native airspace，避免 D3D/Metal 两套业务 renderer |
| D-008 | Accepted | native GPU API 只 present，不恢复 Core GPU processing backend | 符合现有 CPU 架构与已测性能结论 |
| D-009 | Accepted | 未表征输入保持 `Uncharacterized`，不得假贴 sRGB/ACEScg | 不用一个新猜测替换旧猜测；保证诊断诚实 |
| D-010 | Accepted | print LUT native output 必须色度转换到 exact selected output profile | 保颜色而非保 code value，消除 TRC/profile mismatch |
| D-011 | **Superseded by D-020** | Windows scRGB `ReferenceWhiteScale=1.0`；canonical `1.0` 表示 Windows 报告的 SDR reference white（不可用时 80 nits） | 当前 EIZO/WCG 探针报告 80 nits；DWM 拥有唯一 monitor transform；macOS 亮度语义不在此决定中，仍由 D-012 验证 |
| D-012 | Open | macOS SDR WCG 是否/何时设置 `wantsExtendedDynamicRangeContent` | 需实机验证 extended channel 与 SDR 亮度，不能把 HDR 示例直接当 WCG 规范 |
| D-013 | Accepted | 采用 L2 显式 opt-in migration：缺字段为 v1 且不自动改写；新工程为 v2；只有用户迁移才写 v2 | 保持旧 look 和工程可逆性，同时让新工程使用完整 managed 链路 |
| D-014 | Accepted | 首版采用共享自有 CPU F16 compositor；native presenter 只作固定格式上传 | 1600×900 compose/pack 约 12.8/1.2 ms，3840×2160 约 41.8/6.9 ms；场景语义仍只有一份，未引入平台 shader 分叉 |
| D-015 | Accepted | 首期支持标准 RGB 与声明为 Rec709 output 的内置 print LUT；ManagedV2 对 output encoding 未知的任意外部 LUT fail closed | 没有可靠 output profile 就不能把像素冒充成用户选择的 ICC；后续 CLUT 支持需独立资产描述与对照测试 |
| D-016 | Accepted | typed `Extended` TIFF 导出必须为 32-bit IEEE float 并嵌 exact profile；TIFF16 只接收 `Normalized` | 16-bit unsigned 会静默丢掉负值和 `>1` scene-linear headroom，违反 I1/I4 |
| D-018 | Accepted | 「声明为 Rec709 output」由解析 cube 头部注释认定，而非内置白名单；读不到声明的仍按 D-015 fail closed | 两个内置资产的 Rec709 原本来自人工阅读同一行注释再硬编码，而解析器对所有其他文件丢弃了这行——用户从 Resolve 导出的 LUT 写着与内置完全相同的话却被拒。读取它不放宽 D-015 的判据，只是让判据可以被文件自己满足；内置资产随之走同一条路径，解析回归会在测试中立刻暴露而不是等用户撞上 |
| D-019 | Accepted | LUT 的 input encoding 同样由 cube 头部注释认定：声明为非 Cineon 的 cube 在解析时即拒绝，未声明的仍按 Cineon 渲染但记为 `ConventionalDefault` | D-018 只修了 output 一端，而 input 才是无声失败的那一端——ACEScct / Log-C 的 cube 被按 Cineon 喂进去不会报错，只会给出一张看起来正常、颜色是错的图。同一份 Resolve 头部在 `# Display:` 上一行就写着 `#   Input: Cineon Log`，解析器同样把它丢了。拒绝而不是放宽：判据与 D-015 一致，只是让文件能自己推翻一个原本不可见的假设。未声明的不收紧，因为那样会让今天能用的 cube 全部失效，而并没有获得任何新信息——判定刻意从严（注释须以 `input` 开头且紧跟冒号），漏判退回原行为是安全的，误判则是回归 |
| D-017 | Accepted | ManagedV2 的无/坏 ICC TIFF 使用 roll 级显式 fallback：linear 保持原色未表征并数值透传，sRGB 作为用户指定 exact profile；有效嵌入 ICC 永远优先 | 位深不是色彩声明；选择必须随工程与所有 decode cache 传播，旧项目才保留按位深 compatibility |
| D-020 | Accepted（**supersedes D-011**） | Windows 的 `ReferenceWhiteScale` 按 Advanced Color **模式**分流，不再是常量：WCG（SDR AC）与 legacy/emergency 仍为 `1.0`；**HDR 为 `SdrWhiteNits / 80`**。HDR 显示不再 fail-closed 到 emergency，走与 WCG 相同的 `LinearExtendedSrgbRgba16F` + `SystemCompositor` | 两种 Advanced Color 的亮度语义是**相反**的，而 D-011 只描述了其中一种。微软规范：SDR AC 是 display-referred，`1.0` 恒为该屏能达到的最大白，reference white **不适用**；HDR 是 scene-referred，`1.0` 恒为 80 nits，应用必须自行把 SDR 内容乘 `SdrWhiteLevelInNits / 80`。D-011 把 `1.0` 钉死，对 WCG 正确、对 HDR 错误，于是 HDR 分支只能 fail-closed——代价是 **HDR 屏用户比普通 SDR 屏用户体验更差**（连 legacy 的 LittleCMS 正确转换都拿不到，直接落无管理 emergency）。分流之后这个倒挂消失，且 `SdrWhiteNits` 早已由 `DISPLAYCONFIG_SDR_WHITE_LEVEL` 探到，管线的三处精确相等校验原样守住"恰好施加一次" |
| D-021 | Accepted | 显示渲染的肩部改为**一族曲线**，SDR 是其 `asymptote = 1` 的成员：拐点（0.5）以下两者逐位相同，拐点之上 SDR 把负片宽容度压进 `[0.5, 1)`，扩展目标把同样的宽容度铺到 `[0.5, headroom)`。扩展渲染不走印相 LUT。SDR 输出**继续**走印相 LUT，D-010 / D-015 / D-018 / D-019 一字不改 | 印相纸没有镜面高光。把印相 roll-off 原样放进 HDR 容器，得到的只是"一张更亮的 SDR"——HDR headroom 一点没被使用，(a) 等于白做。负片本身约 13 档宽容度，**被印相曲线压掉的高光正是 HDR 唯一有内容可放的地方**，所以放开高光不是加特效，是不再丢弃已经拍到的信息。SDR 保留印相有两条硬理由：它是产品既有的渲染身份，且 D-013 要求旧工程可逆——删掉 SDR 那条会让每个已有工程的渲染结果改变、让全部 golden 无故变红。两者共存的代价是零：D-022 的 target 参数化本来就是一条代码路径 |
| D-022 | Accepted | 三平台的统一点是**输出变换**（`scene-linear → OutputTarget{primaries, transfer, peakNits, refWhiteNits}`），不是 GPU API。平台 presenter 保持三份、各自原生、只做定格式上传 | 平台之间真正不同的不是"怎么把字节送过去"，是**最后一跳归谁**——`FinalTransformOwner` 那三个值就是这个差异，而 DWM / ColorSync / Wayland CM 各有自己的合成语义、reference white 定义和 profile 来源。统一 GPU API 消不掉任何一个平台相关决定，只会多一层翻译（理由见 §17.1）。反过来，输出变换是唯一**平台无关、且 (a) 预览与 (b) 导出共用**的东西：它住在 `Core`，按 §11.1 的禁令自动三平台一致。今天的行为必须是它的一个特例——`OutputTarget{Rec709, sRGB-TRC, 100, 100}` 逐位复现现有 golden，否则不许合 |

| D-023 | **Superseded by D-032** | 扩展渲染只接受 Stage 2 的**白平衡与曝光**；色阶／对比度／高光阴影／曲线／饱和度非中性时**拒绝渲染**，而不是静默忽略 | 白平衡与曝光在线性光下是纯乘法，换到 scene-referred 目标上是同一个运算作用在同一个量上——托管的 display-referred 版本本来就先解码到线性再乘，这里只是数据本来就是线性的，解码与编码是"不存在"而非"跳过"。其余五项则是**按 display range 定义**的：对比度绕 0.5 取枢轴，色阶把黑白点映到 `[0,1]`，曲线是按归一化值索引的查表。把高光在 6.0 的 scene-referred 数据喂进去不会得到"略有不同的画面"，而是无意义的画面。静默丢弃用户的调整会交回一张不是他们做的图，且屏幕上没有任何东西说明这一点——所以按 D-015 的先例 fail closed。放宽是增量的，反过来不是 |
| D-024 | Accepted | 扩展渲染的输出空间**恒为 `LinearExtendedSrgb`**（D-005 的 canonical 载体），不跟随工程的 output space 选择 | output space 选择器选的是 display-referred 编码（sRGB / Adobe RGB / Rec709），每一个都同时断言了一个有界范围和一条传递曲线，而扩展渲染两者都没有。载体是线性、Rec709 原色、无界，并且能用 `[0,1]` 之外的分量表示这些原色之外的颜色——选"Adobe RGB HDR"不会让色域变宽（载体本来就比 Adobe RGB 宽），只会多一条需要撤销的曲线。像素因此必须贴载体自己的 profile：给线性数值贴带 TRC 的 ICC 正是 D-003 要防的"只换标签不做转换" |
| D-027 | Accepted | **新卷**的 `HdrPeakNits` 缺省取当前显示器能完整显示的最高档（`RecommendedHdrPeakIndex`），无 HDR 显示器或读不到面板峰值则 SDR；只在导入那一刻读一次，随后写进工程，显示器变化不再触碰它。已有工程按存的值打开，不受影响。选择器各行同时标注"本机推荐 / 超出本机 x×"——只是标注，不选择 | 用户提出"预览应按检测到的显示屏自动匹配"。三种做法里选了最窄的：显示端软映射（presentation 层 tone-map，I5 不破）代价约一天且要改 WYSIWYG 徽章语义；让 `HdrPeakNits` 跟随显示器则同一卷换台机器导出就不同，工程里存的值失去意义，正是 I5 要防的。"新卷缺省"把对显示器的依赖压缩到创建一刻，之后卷是自己的：I5 的措辞"显示环境不能改变渲染和导出"对**已存在的**卷仍然逐字成立，对新卷则是"决定了初值"而非"改变"。档位同时加了 400：400/203 ≈ 1.97×，在 SDR 白留在 Windows 默认亮度（≈240 nits）的 DisplayHDR 400 面板上（余量 ≈2.2×），它是唯一能完整显示的一档 |
| D-029 | Accepted | HDR 的 GUI 形态对齐 Lightroom：底栏只有 **SDR / HDR 开关**；开启后直方图下方出现 **HDR 上限**滑块，单位是 SDR 白之上的**档数**（0.5 … +4，步进 0.1，默认 +2.3 = 1000 nits），读数同时给出 nits。工程仍存 `hdr_peak_nits`（= 203 × 2^档），持久化形式不变；超出范围的存档值拉到最近端并公告。D-027 的"新卷缺省"改为：显示器余量向下取到 0.1 档作为上限，无余量则关。四个 nits 预设（400/600/1000/4000）**废止** | 照片交付没有母版监视器那几台设备可挂靠（电影的 1000/4000 由此而来），照片这边的同行——LR 的 HDR Limit、ISO 21496-1 gain map 的 headroom、ACES 2.0 的参数化输出——全是连续值。预设的原始理由是"文本框邀请用户微调一个无从校验的值"，D-028 之后这个值在屏幕上、在直方图直尺上都能校验，理由不再成立。滑块放在直方图下而不是底栏，因为它管的正是直方图右侧那四档；底栏只剩"要不要"这一个决定 |
| D-030 | Accepted | HDR 卷的 JPEG 导出写成 **gain-map JPEG**（ISO 21496-1 / Adobe `hdrgm`，即 Lightroom 与 Android Ultra HDR 的容器）：SDR 基底 JPEG + MPF 索引 + XMP `Container:Directory`，后接增益图 JPEG（**亮度单通道灰度图**，全分辨率，同品质；逐通道三通道保留为 `GainMapChannels.PerChannel` 选项，GUI 不暴露）。基底 = **同参数、去掉 HDR 峰值、不走印片 LUT** 的 SDR 渲染，即肩部曲线族 `asymptote = 1` 的成员（D-021）；增益在基底的**线性**空间里按亮度取 `log2((Y_hdr + 1/64) / (Y_sdr + 1/64))`（Y 用基底空间自己的权重），范围按内容量出并恒含 0，`HDRCapacityMax = 内容实际最大增益`（libultrahdr 约定：屏幕余量够显示文件里全部高光即全量应用；母版标称峰值仍在卷与 float32 TIFF 上），增益钳到 ≥ 0；HDR 侧超出基底原色的颜色先按亮度守恒**去饱和**进基底色域（`HdrInBaseGamut`），不做硬裁。基底所在的显示空间（sRGB / Display P3）是**导出对话框**里的容器参数（`ExportOptions.HdrBaseSpace`，持久化，缺省 sRGB），不是卷的输出空间；ICC 政策对基底空间求值。TIFF 仍是 float32 载体母版（D-016），HDR 下强制嵌 ICC | 这是唯一"任何看图软件都能打开、HDR 屏上恢复高光、余量不足的屏幕按比例回落"的照片容器，且**不需要新的原生依赖**——两条 JPEG 流由 ImageSharp 出，MPF 段与两段 XMP 手写。基底不走印片 LUT，因为 gain-map 文件的两个 rendition 是**同一张画在两个余量下**，读者按显示余量在两者之间插值：印片基底 + 解析 HDR 会让中等余量的屏幕看到一半印片一半解析、SDR 屏看到 HDR 屏永远看不到的印片。同一族的两个成员在拐点以下逐位相同，所以增益图在阴影与中间调恒为 0，只有高光带增益，文件也因此小。缺省单通道而非逐通道：肩部是逐通道的、逐通道才能精确复现高光色相，但 LR 与所有手机相机写的都是单通道灰度图，读者只在这种形式上被充分验证过，且灰度图小一半以上；单通道精确复现的是亮度，色相取基底的。逐通道形式代码保留。增益图 JPEG 天然受基底原色约束（基底空间外的颜色是负分量，没有 SDR 值也没有对数），这与 D-024 不冲突：载体不裁，母版在 float32 TIFF，JPEG 是交付件。元数据同时以 `hdrgm` XMP 和 **ISO 21496-1 二进制段**（`urn:iso:std:iso:ts:21496:-1`）写出：基底只带版本字段（4 字节），增益图带完整结构——与标准及 Skia 参考文件一致；同一条记录两种形式，libultrahdr 1.3 亦如此；Apple 的读者认后者 |
| D-031 | Accepted | **SDR 界面统一显示卷的 SDR rendition**：HDR 卷在片夹缩略图、图库封面、印样窗口预览与印样导出（缺省）上一律渲染 `FrameParams.SdrRendition()`——同参数去掉 HDR 峰值、不走印片 LUT、输出空间 sRGB（可指定基底空间），即 D-030 增益图基底的同一定义，SDR 卷返回自身、逐位不变。当前帧的缩略图不再是扩展渲染的缩放副本，HDR 卷下单独渲染一次 256 px 的 SDR rendition。HDR 上限滑块不再重建缩略图（rendition 不含峰值），只有开关变化才重建。**印样有自己的 HDR 开关**（`ContactSheetDialog.WriteHdr`，仅 HDR 卷可见，随卷缺省开、不持久化）：开时印样按 `ContactSheet.WithExtendedCells` 生成——已合成的 SDR 页面线性化，帧格子按 `SheetComposer.GridOrigin` 的同一几何换成扩展渲染，纸面/页眉/框线/帧号钉在 SDR 白——JPEG 写成 gain-map JPEG（基底 = SDR 印样），TIFF 写成 float32 载体。`BuildContactThumbsAsync` 在 HDR 卷上一趟渲染两套 900 px 缩略图（SDR + 扩展），开关只决定文件 | 这些界面都是 Avalonia 的 SDR 位图，任何平台都没有余量：把扩展渲染在 1.0 处裁掉，预览保留的高光在缩略图和印样上就全部炸白，HDR 屏上预览窗口与其余界面、与最终印样三者不一致（用户 2026-09-13 提出）。SDR rendition 是同一肩部曲线族的 `asymptote = 1` 成员，拐点以下逐位相同，所以它是**同一张画的 SDR 版**而非另一套调色；这与 D-030 增益图基底同一定义，因此印样的 HDR 文件与逐帧导出对同一显示器给出同一高光。印样的 HDR 开关独立于卷的开关，因为印样是独立交付件——HDR 卷要一张 SDR 的实验室印样是正常需求；预览窗口是 SDR 界面，开关不改变预览只改变文件，文案说明了这一点。纸面钉在 SDR 白与齿孔填充同理（2026-09-13 的 `cd544d3`）：那里没有测量到任何高于 diffuse white 的东西，增益图在纸面处恒为 0 |
| D-035 | Accepted | **预览用 16-bit 优化变换，导出精确**：`ColorTransformRequest.Precision` 分 `Exact`（原契约：float 进出、`NoOptimize|NoCache`，逐位即 CMM 的答案；导出、整张解码、全分辨率区域、ROI 均值、锐化补丁全部沿用）与 `Preview`（LittleCMS 16-bit 域 `TYPE_RGB_16→TYPE_RGB_16`，优化开、`HighResPrecalc`，lease 在 `Apply` 里把 [0,1] 浮点往返 16-bit 码，输出量化到 1/65535 并夹到 [0,1]）。只有 `TiffIO.LoadWorkingRegion(s)` 接受 precision，且只影响走 CMM 的路径；预览帧的 DecodeRecipe 追加 `[preview precision: 16-bit optimised transform]`；写文件的路径永远拿不到 Preview 帧 | 嵌 ICC 的大 TIFF 预览瓶颈是 CMM 逐像素 float 求值（矩阵型 profile 约 400 ns/px；121 MP 单线程 49 s）。lcms 2.19 对 float 流水线不做任何优化——`OptimizeByJoiningCurves` / `MatrixShaper` / `Resampling` 全部拒绝 float 格式——所以 `NoOptimize` 在 float 上本就是空操作，扩展范围靠的是 float 本身，提速只能换域。用户 2026-09-14 定：预览精度可以降，导出精度不变。实测与 Exact 最大偏差 3.3e-4（线性光）、均值 7e-5，同一预览上 t_base 差 0.7%、D-max 差 ≤ 0.003，8-bit Adobe RGB 121 MP 预览 3.8 s → 0.87 s。8/16-bit 源码值往返 16-bit 精确，所以 Preview 不引入源侧误差；量化与夹取只在输出侧，且预览路径本就不承载扩展范围。Precision 进 transform key，两种变换各自缓存，不会串 |
| D-034 | Accepted（细化 D-021、D-031） | **HDR 下印片：印片的颜色，HDR 的影调。** 扩展目标上选了 SDR 输出的印片 LUT 时，渲染 = 印片的 SDR 渲染（cube → CMM 解进载体线性光）× 逐像素标量 `g = Y(解析 HDR) / Y(解析 SDR)`，g 在拐点以下恒为 1（印片逐位透传）、拐点以上按 D-021 的同一族曲线增长到 headroom，Y(SDR)=0 处取 1。`SdrRendition()` 保留 SDR 印片（只剔除 PQ LUT）；recipe 记 `print-LUT … colour x analytic luminance ratio (D-034)`。切换 LUT / 从标准渲染切换**不再重置 Stage 2** | 用户明确要"HDR 下用 LUT 调颜色"并在两条路（Resolve 里用 `Gamma 2.4 to HDR N nits` 上变换拼一张 / 程序内做新渲染）里选了后者。印片的肩部在 cube 里、不可逆，纸白之上什么都不剩，所以"把印片放进 HDR 容器"没有信息可放（D-021 的理由不变）；能说出"这个像素该亮多少"的只有仍握着负片全部宽容度的解析渲染，而它的 SDR/HDR 两个成员拐点以下相同，比值正好是"只在高光处打开"的那条曲线。用**标量**（亮度比）而非逐通道：逐通道会把印片从未显示过的颜色放回高光——印片的高光去饱和是 look 的一部分，用户选的是它；代价是 HDR 高光的颜色不会比印片里更饱和，明说。用解析族的比值而不是反解印片肩部：后者不存在。同一族、拐点以下逐位相同，正是 D-030 增益图"同一张画两个余量"的定义，所以 SDR rendition 改为保留印片，增益图仍是亮度单通道且拐点以下为 0。Stage 2 不再重置：比较印片是在已调好的帧上做的，每切一次丢一次调色让这件事没法做；数值按新渲染重新解读，与换输出空间时"在当前空间里调这么多"同一逻辑 |
| D-033 | Accepted（supersedes D-015 / D-018 / D-019 的形式，保留其精神；细化 D-021） | **LUT 合同由卷声明，输入恒为 Cineon**：`FrameParams.PrintLutOutput` 声明 cube 的输出（`Rec709` / `DciP3` / `Srgb` / `Rec2020Pq`），文件头注释只做预填，未声明输出仍 fail closed（GUI 按卷状态预填）；输入没有选择——本程序的处理基于 Cineon，声明别的输入的 cube 在解析时拒绝（用户 2026-09-13 明确：输入只能匹配 Cineon；DWG/DI 与 Rec709 输入路径曾实现，已删）；PQ 用 ST 2084；**LUT 只在其输出所属的动态范围下参与**（SDR 输出 HDR 下让位 = D-021；PQ 输出 SDR 下让位，扩展目标下 LUT 即显示渲染，不再套解析肩部，PQ 以 203 nits = 1.0 解进载体、2020 原色旋进载体不裁）；外部 cube 的 recipe 身份为内容哈希 `sha256:…`，v1 冻结不受合同影响 | `.cube` 没有色彩空间字段，靠头部注释准入只让 Resolve 自带的 6 个 Film Looks 能用——用户在 Resolve 里调完色生成的 LUT 一个都不写这行，另 6 个 DCI-P3 Film Looks 因 γ2.6 也被拒；且 D-018 放行的文件在 `DescribeManagedPixels` 又因"非内置"抛异常，所以此前外部 LUT 在 ManagedV2 上从未真正渲染过。Resolve 本身就是让用户在 LUT 前后放 CST 来声明的，把同一责任交给用户是对齐而不是放宽；误声明的代价是可见、可改的一张错色图，比整条路关门强。HDR 侧：一张出口为 PQ 的 LUT 本身就是 HDR 显示渲染、自带肩部，D-021 拒绝的是把 SDR 印片放进 HDR 容器，不是拒绝 LUT；接口用 BT.2020+PQ 是因为那是 Resolve 的 Rec.2100 ST2084 输出，载体仍是 D-024 的线性扩展 sRGB、不裁。HLG 不做：到 nits 要经 OOTF，依赖显示峰值与系统 γ，一张 cube 说不清自己是多少 nits |
| D-032 | Accepted（supersedes D-023） | 扩展渲染接受**全部** Stage 2：白平衡与曝光仍在线性光下相乘；色阶／对比度／高光阴影／曲线／饱和度**按卷自己的量程 `[0, headroom]` 定义**——载体先除以 headroom 归一到 `[0,1]`，再用 sRGB 曲线的解析延伸（`Srgb.LinearToSrgbExtended`：上不封顶、关于零镜像）进编码域，跑与 SDR 完全相同的 `ApplyOperationChain`，解码、乘回 headroom，最后仍由 `HighlightRolloff.BoundAbove` 收顶、填充钉回 1.0。SDR 路径逐字节不变。GUI：HDR 上限悬停不再说"会拒绝"，改为说明五项按上限定义、改上限会一起改影调 | D-023 拒绝的理由是"这五项按 display range 定义，喂无界的 scene-referred 数据无意义"；但扩展目标不是无界的——它有 headroom，`[0, headroom]` 与 SDR 终点的 `[0,1]` 一样是一个 display range，正是 HDR 上限声明文件将容纳的量程。归一化后对比度仍绕编码中点取枢轴、曲线右端就是 HDR 峰值、高光滑块能压到 SDR 白以上的高光，与 Lightroom 的 HDR 定义同构（其全部影调控制都跨越 HDR Limit）。**代价明说**：同一组参数在扩展目标与 SDR 目标上得到的 SDR 范围影调不同，因为定义所依的量程不同——SDR rendition（D-031）保留 SDR 定义，因此 gain-map JPEG 的两个 rendition 各按自己的量程调色，差异由增益图承载，不再是"同一张画的两个余量"这一强形式（拐点以下逐位相同不再成立）；改上限档数也会改 HDR 渲染的影调。用户 2026-09-13 在两条路里选了这条而非"只作用于 [0,1]、以上连续透传"：压不到 HDR 高光的高光滑块不是高光滑块。编码域用 sRGB 曲线而非输出空间曲线，因为 HDR 下没有输出空间（D-024），载体是 sRGB 原色；负分量镜像编码是为了让 D-005 保留的域外颜色穿过仿射的三项，曲线与高光阴影按各自定义仍钳负——与 SDR 相同 |
| D-025 | Accepted（2026-09-12 深夜实机结案） | 扩展渲染**保持 D-021 现状**：拐点以下与 SDR 逐位一致，diffuse white 不另行锚定（code 685 在 SDR 落 0.75、在 +2.3 档落 0.949） | 用户在 VG27AQ1A（HDR 开）上把 `0-RAW` / `21-KG200` 在 SDR 与 HDR 间并排切换，判定通过：中间调没有被抬高的观感，纸白与 SDR 版对得上，高光细节按余量展开。锚定 diffuse white 的替代方案（只让其上展开）不再需要 |
| D-028 | Accepted | **预览高光软校样**：扩展渲染的目标余量超过当前显示器余量时，组合根在 presentation generation 里用 `HighlightSoftProof.Fit` 把 `(1, 目标余量]` 单调压进 `(1, 显示余量]`——拐点恰在 diffuse white（canonical 1.0），拐点处斜率 1，目标顶端精确落在面板顶端；`RenderedFrame`、直方图、导出一律不动。显示器无余量（SDR 模式 / 滑块拉满）时不做、照旧裁切。状态徽章相应标"（高光软校样）"或"（高光裁切）"。`HdrPeakNits` 仍是工程参数（母版），不随显示器 | 用户要求预览对齐 Lightroom：LR 的 HDR Limit 由用户定档，显示余量由系统探到，预览把超出部分压进可显示范围而不是裁掉。这正好落在 I5 允许的那一格——"显示环境只能影响 presentation generation"——所以不需要改 I5，只需要把这一步放在组合根、放在 RenderedFrame 之后。拐点钉在 1.0 是为了让 SDR 范围逐位不变：软校样只能重塑"因为 HDR 目标才存在"的那部分，不能重新调中间调或移动纸白。无余量时不做，是因为把 `(1, 目标]` 压进"零"只能把拐点挪到 1 以下，那就动了 SDR 范围；诚实的做法是裁切并在徽章上说出来。区分"母版"与"预览"也回答了"既然预览跟屏幕走，为什么还要档位"：档位决定导出文件，屏幕决定你此刻看到多少 |
| D-026 | Accepted（待真机复核） | macOS 的 `ReferenceWhiteScale` **恒为 1**，`ExtendedHeadroom` = `NSScreen.maximumExtendedDynamicRangeColorComponentValue`（当前值，不取 potential）；只有一种模式（系统所有的 FP16 载体），探不到屏幕即 emergency。presenter 在且仅在 headroom > 1 时置 `wantsExtendedDynamicRangeContent`——这是 D-012 的**候选**答案 | Apple 的 extended-linear-sRGB 载体把 SDR 白定在 canonical 1.0（相对亮度滑块，不是 nits），所以没有什么可乘——这与 Windows HDR（D-020，1.0 = 80 nits，应用自己抬 diffuse white）正好是镜像：同一份载体字节，抬白的归属相反，契约记录归属，builder 按契约行事。headroom 直接用 AppKit 报的当前值：potential 是别的亮度下能到的，不是现在会显示的；错的"安全线"比没有更糟。EDR 请求跟随 headroom：无余量时请求它一无所获且可能多一次 tone map，有余量时不请求则 >1 全被 layer 裁掉。D-012 保持 Open 直到真机证明开启该标记不改变 SDR 亮度 |

### 17.1 拒绝的替代方案

- **只修 `ColorManagedImage` source tag**：destination/surface 仍无 contract，已被 Skia null 规则否定。
- **所有平台预览先转 sRGB8**：可以作为降级，但永久丢掉用户购买广色域显示器的价值。
- **macOS 全部用 ColorSync、Windows 全部用 WCS**：文件级 CMM 结果会随平台漂移。
- **应用在 macOS 先转显示器 ICC，再给 tagged Metal layer**：会双重转换。
- **维护 Avalonia fork，把整个 top-level 改为 F16**：会把所有 UI bitmap/blend 都带入色彩审计，并长期承担
  framework merge 成本；除非 native preview 方案实证不可行，不采用。
- **Windows/macOS 分别实现 overlay shader**：业务逻辑和几何必然漂移，airspace 问题也没有被统一解决。
- **用 Electron/Chromium 绕过**：不能消除色彩契约问题，且违背现有基础上的修复目标。
- **用一套 Vulkan presenter 统一三平台**（`VK_EXT_swapchain_colorspace` / `VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT`）：范畴错误。它统一的是"怎么把字节送过去"，而平台差异全在"送过去之后谁做什么"，一个平台相关决定都消不掉。三条具体理由：Windows 上 `EXTENDED_SRGB_LINEAR` 在 **ACM-SDR-WCG**（本应用的头号场景，HDR 关）下能否取得存疑，可能严格劣于 DXGI；macOS 上 MoltenVK 最终仍落到 `CAMetalLayer`，而 EDR 的 `wantsExtendedDynamicRangeContent` / `maximumExtendedDynamicRangeColorComponentValue` 是 layer 属性，**逃不掉原生层**；Linux 上 Mesa 的 Vulkan colorspace **本来就是靠 Wayland CM 协议实现的**，不会让我们提前拿到任何东西。外加丢失现有 WARP 软件回退、对 (b) 导出零帮助、以及一个需要按 D-002 纪律做 app-owned 校验的大型原生依赖。
- **把预览搬进 Avalonia 自己的合成器**（`ICompositionGpuInterop` + `CompositionDrawingSurface`）：能消掉 airspace 税、让叠加层变成真正的 Avalonia 控件，但导入之后像素归 Avalonia 合成器管，而它是 8-bit sRGB 的——广色域在导入那一刻就死了。**airspace 与色域在 Avalonia 下二选一**，本应用选色域。这也是 D-007（叠加层在共享 F16 场景里合成）的最终理由。
- **HDR 用 `R10G10B10A2` + HDR10/BT.2100 swapchain**（微软的 Option 2）：只是性能优化，且要求无 alpha 混合、仅 HDR 屏，并**放弃负值与 sRGB 色域外表示**——那正是 D-005 要保的东西。FP16 + scRGB 是唯一对 SDR / WCG / HDR 三者通用的载体。

---

## 18. 开放决策的 spike 要求

### D-025（已结案，见 §17）：扩展渲染里 diffuse white 落在哪

> 2026-09-12 深夜用户在真 HDR 屏并排比对通过，维持现状。下文是当时的 spike 要求，留作记录。

D-021 的曲线族让拐点以下逐位一致，但**没有**把 diffuse white 钉在与 SDR 相同的位置：
code 685（肩部前的线性 1.0）在 `asymptote = 1` 下渲染到 0.75，在 `asymptote = 4.926`
下渲染到 0.949。也就是说 HDR 渲染整体比 SDR 亮，而不只是高光更高——这正是「HDR 看起来
就是更亮的 SDR」这个常见陷阱的形状。

单调曲线无法同时满足「拐点以下与 SDR 一致」和「diffuse white 与 SDR 同位」，所以这是一个
**取舍**而不是缺陷。当前实现选了前者。是否应该改为把 diffuse white 锚定、只让其上的宽容度
展开，必须在真的 HDR 显示器上并排比对才能判断——**本机（VG27AQ1A）只有 WCG，没有 HDR**。

必须记录：同一张底片在 SDR 与各档 HDR 峰值下的并排观感；中间调是否感觉被抬高；
纸白是否与 SDR 版本对得上；以及高光细节相对于 `asymptote` 的可见增益。

### D-012：macOS EDR（Windows 侧已由 D-020 结案）

> **候选答案已实现，等真机判定。** D-026 把 `wantsExtendedDynamicRangeContent` 定为"当且仅当
> 契约 headroom > 1"，`MacOSDisplayContractProvider.ShouldRequestExtendedRange` 就是那条规则，
> presenter 在它翻转时要求重建（`MacOSPreviewPresenterTests.Headroom_appearing_flips_the_edr_request_and_requires_recreation`）。
> 下面的 spike 清单现在有了一个具体的被试对象。

> **Windows 的 reference white 不再是开放决策。** D-020 已按 Advanced Color 模式分流并落地：
> WCG `1.0`、HDR `SdrWhiteNits / 80`，由 `WindowsDisplayEnvironmentTests` 的三条用例钉住。
> 下面这份 spike 清单只对 **macOS** 仍然有效；其中「Windows Advanced Color 同一 canonical patch
> 的相对白表现」保留为 macOS 的对照基准，而不再是 Windows 自己的待验项。

必须记录：

- 送入 neutral `0.18`、`1.0` 和具有负/`>1` channel 的 P3/Adobe patch；
- built-in P3 与外接艺卓上的 layer 配置、`NSScreen` headroom、物理/参考查看器结果；
- `wantsExtendedDynamicRangeContent` on/off；
- 是否发生 component clamp、整体亮度变化或 tone mapping；
- Windows Advanced Color 同一 canonical patch 的相对白表现。

结论必须更新 contract 的 scale 语义和 D-012，不允许只留下实验代码。

### D-013：legacy migration

至少准备：

- 一个无 `display_referred_stage2` 字段的旧项目；
- 一个显式 false 项目；
- 无 LUT、2383 LUT、sRGB 和 Adobe RGB 组合；
- 当前导出 ICC 和像素 hash；
- L1/L2 方案对视觉、可移植性与工程文件的影响报告。

### D-014：compositor

在 1600 px preview 和高 DPI 4K viewport 上测：compose time、upload time、额外 copy、峰值内存和拖拽
latency。只比较共享实现；不得以“平台 shader 更快”为由把场景语义移入 platform adapter。

---

## 19. 跨 session 工作规约

### 19.1 Session 开始

1. 读本文第 4、15、17、18 节。
2. 运行 `git status --short --branch`，确认没有覆盖前一 session 的未提交工作。
3. 确认当前 `color_pipeline_version`、里程碑状态和最后一个相关 commit。
4. 只选择一个 milestone 内可独立验收的切片；不要同时改输入、输出和 presenter。
5. 先写能证明当前错误的测试，再改生产代码。

### 19.2 Session 结束

1. 更新第 15 节状态，但只有 exit gate 全满足才写“完成”。
2. 在下方 Session Log 记录 commit/working state、测试、尚未解决的具体 blocker 和下一个最小动作。
3. 新发现的事实进入“已证实的错误假设”或风险表；新取舍进入 Decision Log。
4. 运行与风险相称的 build/test；若 golden 改变，记录统计和原因。
5. 不以“代码已经能跑”代替 profile round-trip 和 display-transform-count 验收。

### 19.3 Session Log

| 日期 | 基线/结果 | 完成内容 | 验证 | 下一步 |
|---|---|---|---|---|
| 2026-08-30 | `b7cd46d` + 文档工作区改动 | 建立 A0 架构基线；未修改产品代码 | 代码只读审计、远端/fork 验证 | M0：加入失败用例、fixture 与诊断基线 |
| 2026-08-30 | `480a702`、`7af8dc9` | 完成 M0：冻结 built-in ICC exact bytes/hash、canonical extended-range patches、Win11/EIZO WCG reproduction TIFF、legacy missing/false fixtures；钉住 null-destination 与 print-LUT profile mismatch；未修改产品代码 | M0 fixture tests 12/12 通过；characterization tests 8 项中 sRGB/Rec709 controls 2 项通过，目标错误 6 项按预期失败 | M1：引入 profile identity、typed frames、LittleCMS 与 transform diagnostics |
| 2026-08-30 | `b7a3324`、`c61c355` | 完成 M1：固定并打包 app-owned LittleCMS 2.19.1；建立 exact profile identity、typed frame 与 transform diagnostics 基础 | 固定来源/哈希/加载路径验证；typed source/working/render 与 canonical fixtures 通过 | M2：统一 managed 输入、渲染、导出和迁移语义 |
| 2026-08-30 | `a3e82a5` | 完成 M2 主体与 M3/M4 Windows 切片：统一 managed ICC 链、TIFF32F extended export、L2 显式迁移、稳定 fingerprint、共享 F16 compositor，以及 Advanced/legacy/emergency presenter、recovery 和 fallback | Release `-warnaserror` 0 warning/0 error；managed 402/402、Win32 30/30；native ABI/repro/smoke 通过，DLL SHA-256 `57E90A68D9356421F94C7217BDBA36178D2DABFA877495AAD6377E0DA790B0D5`；EIZO CG2700X 探针选出 FP16/scRGB system-owned contract | 收口 untagged TIFF 与导出 ICC 的 fail-closed UI/CLI 契约 |
| 2026-08-30 | `98fc462` | 收口 M2：新 TIFF 卷显式选择 linear/sRGB fallback，有效 ICC 原子优先，坏 ICC 仅由显式 fallback 接管；选择随项目、完整图/预览/区域/标定/分割/导出及 cache identity 传播；导出 UI 与 exporter 仅允许 exact display-sRGB 省略 ICC；工程/新卷长预处理采用二次 flush 后原子接管，避免跨卷污染或丢编辑 | Release `-warnaserror` 0 warning/0 error；focused TIFF/ICC 16/16、managed 424/424、Win32 30/30；CLI 互斥/fail-closed 进程级检查通过；两轮独立只读复审 0 P0/P1；native ABI 与 legacy/Advanced smoke 再通过 | 推送并提交上游 Windows PR；用参考查看器/色度计与第二显示器关闭 M4 硬件人工门槛；macOS M5 延后 |
| 2026-09-12 | 分支 `feat/hdr-output-target`，`1deb5a2`…`f4bfae1`（13 个提交，基于 `main`@`a32c868`），工作区干净 | D-020（HDR 不再 fail-closed，scale 按 AC 模式分流）；D-021～D-024（参数化输出终点、肩部一族曲线、Stage 2 扩展路径、载体恒为 LinearExtendedSrgb）；HDR 峰值选择器 + 工程持久化 + v1 工程禁用；DXGI 读面板峰值 → 真 `ExtendedHeadroom`；直方图扩展区与裁切线；D-026 macOS 政策 + `Presentation.MacOS` 托管半边 + `MacOSPreviewHost` 接入组合根 + Obj-C++ 垫片源码与构建脚本 | Release `-warnaserror` 0/0；managed 528、Win32 40、MacOS 25 全绿；Windows 真机（VG27AQ1A，HDR 开、SDR 白 120 nits、面板 520 nits）：HDR 屏走 system-owned FP16 WYSIWYG，真底片 HDR 600 高光出细节，直方图裁切线在 +2.1 档 | **D-025（Open）diffuse white 落点**要在真 HDR 屏并排比对；**Mac**：跑 `build-macos-presenter.sh` 首次编译、按 Native/README 四项验 D-012；之后 PQ/CICP 导出（(b) 的消费端）与合并到 `main` |
| 2026-09-12 晚 | 分支 `feat/hdr-output-target` rebase 到 `main`@`81678f4`（用户重做了 PR #4 合并提交以修 message 乱码，树相同），20 个提交，工作区干净 | D-027 新卷缺省取显示器余量；D-028 预览高光软校样（`HighlightSoftProof`，presentation generation）；D-029 GUI 对齐 Lightroom：SDR/HDR 开关 + 档数上限滑块，nits 预设废止；直方图细刻度移到图下直尺、扩展轴固定 4 档；HDR 下输出空间/胶片风格槽位让位给说明；显示空间进色彩徽章健康态悬停 | Release `-warnaserror` 0/0；managed 550、Win32 40、MacOS 25 全绿；用户真机看过开关、滑块、直尺、软校样 | **C. PQ/CICP 导出**（先做零原生依赖的 gain-map JPEG，再 `TransferState` PQ/HLG + CICP，最后 AVIF/JXL）；D-025 并排比对仍开；远端分支需 `--force-with-lease`，由用户决定 |
| 2026-09-12 深夜 | 分支 `feat/hdr-output-target`，续于 `60e50bf`，工作区改动待提交 | **C(b) 第一步：gain-map JPEG 导出**（D-030）：`HdrGainMap`（逐通道 log2 增益 + 元数据、`Reconstruct`/`WeightFor` 读者侧算法）、`GainMapJpegContainer`（MPF APP2 + 两段 XMP 手写）、`JpegIO.ExportGainMapJpeg`；GUI：HDR 卷下 JPEG 自动写成增益图 JPEG，导出对话框多出「增益图基底」（sRGB / Display P3），`ExportOptions.HdrLimitStops` 随卷带入，`ExportIccUiPolicy` 加 `hdrMaster`（HDR TIFF 强制嵌 ICC——此前未勾 ICC 的 HDR TIFF 导出会抛异常）；VM 的 `RenderAndWriteExport` 为增益图第二次渲染 SDR 基底（去峰值、去印片 LUT、换基底空间） | `-warnaserror` 0/0；managed 561、Win32 40、MacOS 25 全绿；`GainMapJpegTests` 含从文件字节走一遍读者算法的往返；Pillow 12 独立解析 MPF 正确。实机：**Chrome 与手机正常，Windows 相册一直只有极微弱效果**——文件与 Skia 参考文件逐字节同形，9 个单变量变体在相册里无一变好，判定为相册问题，不追。追相册期间留下的独立改动：缺省亮度单通道灰度图、HDR 侧超色域去饱和（`HdrInBaseGamut`）、基底 ISO 段只留版本、`HDRCapacityMax` 取内容最大增益并钳增益 ≥ 0。**D-025 用户实机并排比对通过，结案** | 提交本轮改动；PQ/CICP + AVIF/JXL 降为"有需求再做"（跨端观看 gain map 已足够，PQ 是专业交付需求）；Mac 阶段 B 等机器 |
| 2026-09-13 | 分支 `feat/hdr-output-target`，续于 `cd544d3`，工作区改动待提交 | **D-031：SDR 界面统一显示 SDR rendition + 印样 HDR 开关**：`FrameParams.SdrRendition(baseSpace?)`（Core，D-030 的 `GainMapBaseParams` 改为委托它）；片夹缩略图（`RenderThumbnailAsync` / `RefreshThumbnail` / `RenderPreviewAsync` 的顺带缩略图）、图库封面（`RenderSheetCells`）、印样缩略图全部走它；`ContactSheet.PasteCells` / `WithExtendedCells`（Core）、`SheetComposer.GridOrigin`；`BuildContactThumbsAsync` 返回 `ContactThumbs(Sdr, Extended?, Target)`；`ExportContactSheetAsync(thumbs, hdr, …)` 在 HDR 下写 gain-map JPEG / float32 TIFF；`ContactSheetDialog` 加「动态范围 / HDR 印样」勾选（HDR 卷可见，随卷缺省开）；HDR 上限滑块不再重建缩略图 | `-warnaserror` 0/0；managed 572、Win32 40、MacOS 25 全绿；新增 `SdrRenditionTests`（6 条：SDR 自身/HDR 去峰值去 LUT/拐点以下与扩展渲染一致/格子替换与纸面钉白/异原色拒绝/印样增益图纸面处恒 0）。未实机 | 用户实机看片夹与印样在 HDR 屏上是否与预览一致；提交本轮改动 |
| 2026-09-13 晚 | 分支 `feat/hdr-output-target`，续于 `a0d2fee`，工作区改动待提交（与 D-033 同一工作区） | **D-034：HDR 下印片 = 印片色 × 解析族亮度比**：`ColorPipeline.ToExtendedOutputTargetViaPrint`（三份数据：解析 SDR、解析 HDR、印片经 CMM 进载体；`ParallelSweep.OverPixels` 逐像素标量）；`LutContract.AppliesTo` SDR 输出对两种目标都为真；`FrameParams.SdrRendition` 保留 SDR 印片、剔除 PQ LUT（需解析 cube）；`Pipeline.DescribeManagedPixels` recipe 三分支；GUI：选片器 SDR 下不列 PQ、HDR 下全列，输出按钮两态可见（PQ 项只 HDR 可见），未声明一律预填 Rec709，「印片色 · HDR 影调」注记；**`ApplyPrintLut` 不再 `ResetScene`**（用户要求）。用户另问"SDR LUT 的输出空间是否该跟着卷的输出空间走"——答否：LUT 输出是文件的事实（Rec709/DCI-P3 之类），卷的输出空间是容器，两者之间本来就由 CMM 转换（D-010），绑在一起就是"保留 Rec709 数值贴 sRGB 标签"那个被禁止的 bug | `-warnaserror` 0/0；managed 613、Win32 40、MacOS 25 全绿；`LutContractTests` 加 `A_print_stock_under_HDR_keeps_its_colour_and_takes_the_HDR_tone`（真 2383 文件：拐点以下与印片逐位相同、以上为同一标量 × 印片、顶端被抬）、`The_SDR_rendition_keeps_a_print_stock_and_drops_an_HDR_LUT`；`OutputTargetTests.Extended_target_does_not_consult_the_print_lut` 改为 `…renders_a_print_stock_as_its_colour_with_the_hdr_tone`。实机两处修复：`BuildParams()` 补 `PrintLutOutput`（此前改输出声明预览无反应）；选片器重建改为行不变不动集合、围绕当前选中行重建（此前 HDR 开关后下拉空白）；`PrintLutPickerTests` 4 条；输出按钮只对文件头未声明输出的 cube 出现（自带印片声明了 Rec709，改成 PQ 只会把 γ2.4 码值当绝对亮度解——用户撞上了）。617 全绿 | 用户实机：HDR 屏上开 HDR 选 2383，看高光是否打开、缩略图/印样是否与预览拐点以下一致；提交 |
| 2026-09-13 傍晚 | 分支 `feat/hdr-output-target`，续于 `a0d2fee`，工作区改动待提交 | **D-033：LUT 合同由卷声明，对齐 Resolve 色彩规范**：`CubeLut` 输出枚举扩到 4 个 + `LutContract` + `ContentIdentity`（输入仍只有 Cineon）；`Pq.cs`；`ColorSpaces.DciP3 / Rec2020`；`BuiltInProfileId.DciP3`；`ColorPipeline.PrintLutFor`（唯一裁决点）/ `EncodeLutInput` / `NativeSpaceOf` / `ToExtendedOutputTargetVia`；`Pipeline.DescribeManagedPixels` 去掉"非内置即抛"，外部 cube 记 `sha256:`；`FrameParams.PrintLutOutput` + `LutContractFor`，`Project` 持久化；GUI 底栏「输出」按钮（SDR 下三选一；HDR 下无按钮）、让位说明、**选片器按 SDR/HDR 状态过滤**、选片按状态预填+状态栏说明、旧工程未声明输出的迁移提示；CLI `--lut-output`。**同一轮内用户三次收窄**：① "只想用 Cineon 成品 cube"→ 合同按钮只在需要时出现；② "内置换成标准 cube + 安装带 LUT 文件夹"→ `Assets/Luts` 六张 Resolve Rec709 Film Looks（原名）复制到产物 `luts/`，`:luts/<文件名>` 记号，旧记号仍解析；③ "输入只能是 Cineon / 按状态过滤 / LUT 里为什么有 HDR 档位"→ 删 DWG/DI 与 Rec709 输入路径、删烘出来的 HDR Standard cube 与 `CubeBaker`、选片器按状态过滤。THIRD_PARTY_NOTICES §16 更新 | `-warnaserror` 0/0；managed 624、Win32 40、MacOS 25 全绿；新增 `LutContractTests`（24 条：PQ 锚点、头部预填、非 Cineon 输入拒绝、卷声明优先级、工程往返、范围规则、外部 cube 渲染与内容哈希、Cineon 路径逐位不变、DCI-P3 经 CMM、HDR LUT 参与/让位双向、PQ 出口手算复现、随程序六张与预填、旧记号=同一内容哈希）。未实机 | 用户实机：用 Resolve 生成一张 DWG/DI → Rec709 的 LUT 与一张 → Rec.2100 ST2084 的 LUT 各试一次；提交本轮改动 |
| 2026-09-13 午后 | 分支 `feat/hdr-output-target`，续于 `17e8203`，工作区改动待提交 | **D-032：HDR 下 Stage 2 五项按卷的量程可用**（supersedes D-023）：`Stage2.ApplyManagedToExtendedTarget(d, cal, headroom)` 不再抛 `NotSupportedException`，归一化 → `Srgb.LinearToSrgbExtended` → `ApplyOperationChain`（`LinearExtendedSrgb` 权重）→ 解码 × headroom；`Srgb.LinearToSrgbExtended` / `SrgbToLinearExtended` 新增（公开）；`Pipeline.Render` 与 `RegionRender`（sharp patch）传 `target.HighlightHeadroom`；`HdrLimitHint` 的"会拒绝"改为"按上限定义、改上限一起改影调"（en.json 同步） | `-warnaserror` 0/0；managed 587、Win32 40、MacOS 25 全绿；`OutputTargetTests` 拒绝用例替换为三条：五项各自生效且不越 headroom（5 变体）、高光滑块压得到 SDR 白以上像素、扩展编码往返（含负与 >1，[0,1] 上与 `LinearToSrgb` 逐位相同）。未实机 | 用户实机：HDR 卷拉对比度/高光/曲线看预览与增益图 JPEG；提交本轮改动 |

---

## 20. 官方参考

- [Skia Color Management：null source/destination 规则](https://skia.org/docs/user/color/)
- [Apple `CAMetalLayer.colorspace`](https://developer.apple.com/documentation/quartzcore/cametallayer/colorspace)
- [Apple extended linear sRGB](https://developer.apple.com/documentation/coregraphics/cgcolorspace/extendedlinearsrgb)
- [Apple Metal tone-mapping 示例](https://developer.apple.com/documentation/metal/performing-your-own-tone-mapping)
- [Apple `NSScreen.colorSpace`](https://developer.apple.com/documentation/appkit/nsscreen/colorspace)
- [Microsoft Advanced Color / HDR / scRGB](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)
- [Microsoft Advanced Color 与 ICC profiles](https://learn.microsoft.com/en-us/windows/win32/wcs/advanced-color-icc-profiles)
- [Avalonia issue #14599：bitmap color-space API](https://github.com/AvaloniaUI/Avalonia/issues/14599)
- [LittleCMS](https://github.com/mm2/Little-CMS)
