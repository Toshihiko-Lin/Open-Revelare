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
| `CubeLut` / `PrintLuts` | 任意用户 `.cube` 都可按 Cineon→Rec709 使用 | `.cube` 的**语法**没有这两个字段，但 Resolve 导出会把 output 写进头部注释（两个内置资产即如此），而解析器把注释丢掉了；当前 UI 接受任意 LUT 却套同一个隐含合同。output 已由 D-018 改为读取文件自述；input 由 D-019 同法处理：声明为非 Cineon 的直接拒绝，未声明的仍按 Cineon 但标注为惯例 |
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

### 8.2 print LUT

print cube 的输入和输出 encoding 是资产本身的事实。output 的这个事实**通常已经写在文件头部注释里**
（Resolve 的 `# Display: ITU-Rec.709, Gamma 2.4`），因此由 `CubeLut` 在解析时读取，而不是靠外挂描述符
或内置白名单；头部什么都不声明的 cube 仍为 `Unknown` 并按 D-015 fail closed。判定刻意从严：`Rec709`
指 709 原色**与** 2.4 gamma 这一对，只写其一不算；第一行 display 声明即为准，避免后面无关注释反过来
改写文件已经说明的事。input encoding 仍是隐含的 Cineon 假设，尚未显式化。

若 cube 输出 Rec709/2.4，而用户选择 sRGB、Display P3 或 Adobe RGB：

1. cube 先生成其 native `RenderedFrame`；
2. 以 cube 的准确 output profile 解码；
3. 转换至选择的 exact target profile；
4. Stage 2 只能在它声明的 target encoding 中运行；
5. 导出嵌该 target profile。

颜色管理保持的是颜色/观感，不是跨不同 TRC 的相同 code value。当前“保留 Rec709 数值再贴目标 ICC”
明确禁止。

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

项目目标是 `net8.0` 而不是 `net8.0-macos`。建议用一个 Objective-C++ C ABI dylib 封装 AppKit/Metal，
沿用 `packaging/macos` 已有的 dylib 拷贝、install-name 和签名流程，避免在 C# 各处散落
`objc_msgSend` P/Invoke。

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
| M5 | 按当前目标延后 | macOS Metal presenter；ColorSync contract；跨 built-in/external screen | Windows PR 合入后另开修复；D-012 随该阶段关闭 |
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

| D-023 | Accepted | 扩展渲染只接受 Stage 2 的**白平衡与曝光**；色阶／对比度／高光阴影／曲线／饱和度非中性时**拒绝渲染**，而不是静默忽略 | 白平衡与曝光在线性光下是纯乘法，换到 scene-referred 目标上是同一个运算作用在同一个量上——托管的 display-referred 版本本来就先解码到线性再乘，这里只是数据本来就是线性的，解码与编码是"不存在"而非"跳过"。其余五项则是**按 display range 定义**的：对比度绕 0.5 取枢轴，色阶把黑白点映到 `[0,1]`，曲线是按归一化值索引的查表。把高光在 6.0 的 scene-referred 数据喂进去不会得到"略有不同的画面"，而是无意义的画面。静默丢弃用户的调整会交回一张不是他们做的图，且屏幕上没有任何东西说明这一点——所以按 D-015 的先例 fail closed。放宽是增量的，反过来不是 |
| D-024 | Accepted | 扩展渲染的输出空间**恒为 `LinearExtendedSrgb`**（D-005 的 canonical 载体），不跟随工程的 output space 选择 | output space 选择器选的是 display-referred 编码（sRGB / Adobe RGB / Rec709），每一个都同时断言了一个有界范围和一条传递曲线，而扩展渲染两者都没有。载体是线性、Rec709 原色、无界，并且能用 `[0,1]` 之外的分量表示这些原色之外的颜色——选"Adobe RGB HDR"不会让色域变宽（载体本来就比 Adobe RGB 宽），只会多一条需要撤销的曲线。像素因此必须贴载体自己的 profile：给线性数值贴带 TRC 的 ICC 正是 D-003 要防的"只换标签不做转换" |

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

### D-025（Open）：扩展渲染里 diffuse white 落在哪

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
