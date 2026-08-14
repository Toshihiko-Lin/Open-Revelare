# OpenRevelare macOS 专项检测报告

范围：`src/OpenRevelare.Gui`（GUI 侧 + 信号/事件连接）、`packaging/macos`、CI。
基线：`main` @ b11dd63，Avalonia 11.2.3 / .NET 8。`dotnet build -c Release` 通过，0 警告 0 错误。

---

## 根因先说

代码里已有多处 macOS 专项修复（`LetterboxRect` 让 Overlay 声明 Stretch、
`PreviewBitmap` 从 VM 读而非从控件读、csproj 里 RID/dylib 那段长注释），
说明 macOS 问题不是没人管，而是**发现渠道只有用户报障**。

结构性原因：**CI 只跑 `ubuntu-latest`**（`.github/workflows/ci.yml:20`），
macOS 只在 release 时构建一次（`release.yml:139`），且只验"能否打出包"，不验行为。
任何 macOS 专属的行为差异，都必然要等用户报。

下面按"确定性 × 影响面"排序。

> **状态**：P1–P5 全部已修（见文末「已修内容」）。

---

## P1 ── 快捷键在 macOS 上全部要按 Ctrl 而不是 ⌘（确定 bug，影响最大）✅ 已修

`src/OpenRevelare.Gui/Views/MainWindow.axaml.cs:861`

```csharp
bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
```

Avalonia 在 macOS 上把 **⌘ 映射为 `KeyModifiers.Meta`**，`Control` 是物理 Ctrl 键。
于是 mac 用户按 ⌘N / ⌘O / ⌘E / ⌘Z / ⌘Y / ⌘C / ⌘V / ⌘1 / ⌘, / ⌘⇧T **全部无反应**，
必须改按物理 Ctrl——这在 mac 上完全违反直觉。

其中 **⌘Z 撤销失效**对修片软件是最容易被报成"丢编辑 / 撤销没用"的那一类。

菜单里的 `InputGesture="Ctrl+N"`（`MainWindow.axaml:37` 起）也是硬写的，
`InfoDialog.axaml.cs:83` 的快捷键帮助文本同理。三处（接线 / 菜单 / 帮助）都按 Ctrl 写死。

代码里自己有条注释（840 行附近）："`InputGesture` 只渲染不接线，
菜单里显示了却没接上的手势就是在骗用户"——原则对，只是没考虑平台差异这一维。

**修法**：主修饰键一处定义，三处共用。

```csharp
// macOS 主修饰键是 ⌘(Meta)，其余平台是 Ctrl。
private static KeyModifiers Accel =>
    OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

bool ctrl = e.KeyModifiers.HasFlag(Accel);
```

菜单侧建议改用 `KeyBinding` + `Gesture`，让显示与接线同源——
正好也消掉上面那条注释担心的"显示与实际漂移"。

注：`bare`（863 行）用 `== KeyModifiers.None`，在 macOS 上是安全的，
N / K / F / G / D 这些单键不受影响。

---

## P2 ── 文件选择框在 macOS 上大小写敏感，看不见 `.CR2`（确定 bug）✅ 已修

`src/OpenRevelare.Gui/Services/ImageIo.cs:15` → `RawDecode.RawExtensions`（`RawDecode.cs:30` 起，全小写）

RAW 扩展名列表全小写（`.cr2` `.nef` `.arw` `.3fr`…），
但相机实际写出的文件**大量是大写**：Canon `.CR2`、Nikon `.NEF`、Hasselblad `.3FR`。

- Windows：Win32 通配大小写不敏感 → 正常
- Linux/GTK：Avalonia 生成不敏感 filter → 正常
- **macOS：`Patterns` 落到 `NSOpenPanel` 的类型过滤，大小写敏感 → 用户看不到自己的 `.CR2`**

而 `RawDecode.IsRawExtension` 用的是 `StringComparer.OrdinalIgnoreCase`——
也就是**解码器认这个文件，选择框却不让选**。
这正是 b11dd63（"界面清单与解码器脱节"）想解决的同一类问题，那次没覆盖大小写这一维。

**修法**（两条都做）：

1. 大小写变体一起列：
   ```csharp
   .SelectMany(e => new[] { "*" + e, "*" + e.ToUpperInvariant() })
   ```
2. 兜底"所有文件"项：`ImportDialog.axaml.cs:71` 已有，
   但 `MainWindow.axaml.cs:1741`、`1786` 和 `LibraryView.axaml.cs:138` 三处没有。

---

## P3 ── 「在访达中显示」的进程调用方式不稳 ✅ 已修（附：我上一版报告的两处错误）

`LibraryView.axaml.cs:157`（原）

```csharp
Process.Start("open", $"-R \"{roll.ProjectPath}\"");
```

用的是 `Process.Start(fileName, arguments)` 重载。Unix 上 .NET **不经过 shell**，
而是用自己的解析器（Windows 命令行文法）把整串拆成 argv 再 execve。

### ⚠️ 先更正我上一版报告里的两处错误

这次在本机用一个"记录 argv 的 xdg-open 桩程序"实测了新旧两种写法，结论和我先前写的不一样：

1. **"Linux 分支确定是错的、`xdg-open` 会拿到带字面引号的路径"——这是错的。**
   .NET 的解析器**确实会剥掉外层引号**。普通路径、带空格路径、中文路径，
   新旧两种写法产生的 argv **完全一致**。
2. **"带空格路径大概率能用"——这半句是对的**，实测确认。

也就是说：**日常路径下旧写法没有 bug**，我先前把它说重了。

### 但仍然该改：两个实测出来的真实破绽

| 输入目录 | 旧写法拿到的 argv | 新写法 |
|---------|------------------|--------|
| `/tmp/a/quote"inside` | `/tmp/a/quoteinside` ❌ 引号被吃掉 | `/tmp/a/quote"inside` ✅ |
| `/tmp/a/trailing\` | `/tmp/a/trailing"` ❌ 反斜杠转义了结束引号 | `/tmp/a/trailing\` ✅ |

`"` 和结尾的 `\` 在 macOS 和 Linux 文件名里都是**合法字符**。
撞上时文件管理器会被要求打开一个不存在的路径，而且**失败是静默的**。
这不是高频场景，但修复成本几乎为零。

### 实际改法

```csharp
else if (OperatingSystem.IsMacOS())
    Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", roll.ProjectPath } });
else if (Path.GetDirectoryName(roll.ProjectPath) is { } dir)
    Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { dir } });
```

Windows 分支**保持原样**：explorer.exe 不收正常 argv，
它要的就是 `/select,"C:\path"` 这个字面形式，逗号和引号都得留着。

顺带修掉两处小问题：

- 原 Linux 分支的 `GetDirectoryName` 结果**没判 null**（路径是根目录时会返回 null）；
- 外层 `catch { }` 吞掉一切，用户体感是"点了没反应"。
  现在仍**不弹对话框**（没有文件管理器不值得打断用户），但补了一行 `Debug.WriteLine`，
  至少日志里留得下痕迹——这正是这类问题难定位的原因。

---

## P4 ── 没有 macOS 原生菜单栏（设计缺口，非 bug）✅ 已修

全项目搜不到 `NativeMenu` / `NativeMenuBar`；菜单是画在窗口内的 `Menu`。
在 macOS 上意味着：屏幕顶部系统菜单栏基本是空的；
没有标准的 **OpenRevelare → 关于 / 偏好设置 / 退出** 三件套；
⌘, 即使修好 P1 也仍不在系统菜单里；⌘Q / ⌘W 缺失。

对 mac 用户这会被直接感知为"这不是个 mac 应用"。

**修法**（已实施，细节见文末）：mac 上挂 `NativeMenu` 并把窗口内的 `Menu` 藏掉，
其余平台保留原样。⌘Q / ⌘W 不用自己写——Avalonia 和 NSWindow 各自会补上；
偏好设置 / 关于 按 mac 约定搬进应用菜单，`设置` 那一栏在 mac 上随之消失。

---

## P5 ── 内存探测在 macOS 上退化为总量估算 ✅ 已修

`Services/SystemMemory.cs:44` 只实现 Windows / Linux，macOS 返回 false，
`ImageIo.AutoLimit()`（`ImageIo.cs:82`）回落到按**总内存**估并发。

注释写明是有意为之（mach `host_statistics64` 成本高、"free" 语义误导）。
后果：Apple Silicon 统一内存机器上按总量估会**高估**可用量——
16 GB M1 会算出 2 槽 × 1.2 GB，而实际可用常远低于此。
mac 用户报"导入大 RAW 卡死 / 被系统杀掉"时先看这里。

### 上一版建议的 `vm.page_free_count` 单用是错的

我先前写"`sysctlbyname("vm.page_free_count")` + `hw.pagesize` 就够"——**这条建议有问题**，
按它实现会做出一个比现状更差的东西。

`page_free_count` 正是 macOS 版的 Linux `MemFree`，而 `TryLinux` 的注释**明确拒绝**了
`MemFree` 而改用 `MemAvailable`，理由是它不含可回收的页缓存。macOS 是这个问题的最坏情况：
统一缓冲区缓存会把空闲内存吃满，开机一小时后 free 页数就贴近 0。

按真实 `vm_stat` 量级模拟（脚本在 scratchpad，未进仓库）：

| 场景 | 真实可用 | 只看 free | 只看 free 会选 | 现方案 | 旧的总量估 |
|------|---------|----------|--------------|--------|-----------|
| 32 GB M1 Pro 日常 | 12.5 G | **0.6 G** | **1 槽** | 4 槽 | 3 槽 |
| 64 GB M3 Max 日常 | 27.6 G | 2.7 G | **1 槽** | 8 槽 | 3 槽 |
| 16 GB M1 高压 | 0.5 G | 0.0 G | 1 槽 | **1 槽** | 2 槽 ❌ |

也就是说只看 free 会把所有 mac **永久钉在 1 槽**，比它要取代的回退还糟。

### 实际改法

可用量 = free + speculative +（purgeable + external）× 0.5。
后两项是可回收但**不是白拿**的（清掉干净的文件页要付重读代价），所以打五折——
不打折的话，刚开机的 16 GB M1 会算出 11.8 G、开 8 槽 ≈ 9.6 G，正好是这道闸想避免的换页风暴。
脏的匿名页（inactive 里非 external 的部分）**不计入**：回收它们要写 swap，
而那正是我们在规避的停顿。

修好后 16 GB M1 高压场景从 2 槽降到 **1 槽**——这就是"导入大 RAW 卡死"的那一格。

---

## 查过但没问题的地方

- **线程模型**：`Task.Run` + `await` 用法一致，无 `.Result` / `.Wait()` 死锁风险
  （唯一同步点 `FlushRollNow` 在 `OnClosing`，刻意为之且有注释）。
  跨线程改 `StatusText`（`MainViewModel.cs:2575`）正确走了 `Dispatcher.UIThread.Post`。
- **事件订阅生命周期**：`ContactSheetDialog` 有配对 `-=`（50↔92）。
  `MainWindow` 的订阅与窗口同生命周期，不构成泄漏。
- **`PointerCaptureLost` 兜底**：`MainWindow.axaml.cs:78` 与 `FilmStrip` 都接了——
  这恰是 macOS 上最易出"拖拽状态卡死"的地方，已经处理对了。
- **`DataContextChanged` 内订阅 VM**：理论上重复设 DataContext 会叠加订阅，
  实际只在启动设一次，当前不是 bug。
- **打包脚本**：`build-app.sh` 质量很高——bash 3.2 多字节陷阱、
  codesign 会"升级"成 bundle 签名、dylib 未进产物的拦截，都有注释和防线。
  ad-hoc 签名 + 引导 `xattr -dr` 是明确产品决定，不是疏漏。

---

## 建议动作顺序

1. ~~**P1 + P2**~~ ✅ 已修。
2. ~~**P3**~~ ✅ 已修。
3. ~~**CI 加一格 macOS**~~ ✅ 已加（见下）。
4. ~~**P4**~~ ✅ 已修。
5. ~~**P5**~~ ✅ 已修。

---

## 已修内容（P1 + P2 + P3 + P4 + P5）

`dotnet build -c Release` 通过，0 警告 0 错误。**未提交**，改动都在工作区。

### P1 快捷键

| 文件 | 改动 |
|------|------|
| `Views/MainWindow.axaml.cs` | 新增 `Accel` 属性（mac 取 `Meta`，其余 `Control`），`OnKeyDown` 改用它 |
| `Markup/AccelExtension.cs`（新增） | XAML 侧 `{i18n:Accel 'N'}`，返回真正的 `KeyGesture` 而非字符串——写错在解析期就炸，不会渲染成一行死文本 |
| `Views/MainWindow.axaml` | 8 处 `InputGesture="Ctrl+X"` → `{i18n:Accel 'X'}`。单键手势（G/D/N/K/F）和 `Alt+F4` **未动** |
| `Services/Loc.cs` | 新增 `Keys()`，在查表**之后**把 `Ctrl` 改写成 `Cmd` |

`Loc.Keys()` 放在查表之后是关键：翻译键就是中文原文，必须保持写作 `Ctrl+…`，
否则 mac 上会查不到 `en.json` 里的任何一条。改写只作用于**结果值**，
所以中英文两种界面都能正确显示 `Cmd`。

一个坑值得记：`Cmd` 比 `Ctrl` 短一个字符，而快捷键帮助是**等宽对齐的表格**。
直接 replace 会让所有带快捷键的行比 `N / K / F / Esc` 那几行左移一格，表格视觉上就破了。
所以 `Keys()` 会把吃掉的那个字符**补回到后面的空格串里**——
实测中英文两版说明列都仍对齐在第 19 列；句中的「（Ctrl+1）」这种没有尾随空格的则自然缩短一格。

### P2 文件选择框

| 文件 | 改动 |
|------|------|
| `Services/ImageIo.cs` | `OpenPatterns` 每个扩展名同时给出大小写两种形式（31 种 → 62 条 pattern） |
| `Views/MainWindow.axaml.cs` | 「添加图像」「LCC 平场图」两处补「所有文件」兜底项 |
| `Views/ImportDialog.axaml.cs` | LCC 选择器补同样的兜底项 |

已验证 `*.CR2` / `*.NEF` / `*.3FR` 都在列表里。
`.ncproj` 选择器**没动**——那是程序自己写出的文件，恒为小写。
「所有文件」在 `en.json` 里已有 "All files"，无需补翻译。

### P3 在访达中显示

| 文件 | 改动 |
|------|------|
| `Views/LibraryView.axaml.cs` | macOS / Linux 两个分支改用 `ArgumentList`；Linux 分支补 null 判断；`catch` 补一行 `Debug.WriteLine` |

Windows 分支保持原样（explorer.exe 要的就是那个字面格式）。
这条是**唯一在本机实测过**的修复——用记录 argv 的桩程序对比了新旧写法，
也正因为实测，才发现我上一版报告把 Linux 那条说重了（详见 P3 正文的更正）。

### P4 原生菜单栏

| 文件 | 改动 |
|------|------|
| `Views/MainWindow.NativeMenu.cs`（新增） | 整棵 `NativeMenu` 在 C# 里建；应用菜单挂 `Application.Current`，File/Edit/View/Help 挂 `Window` |
| `Views/MainWindow.axaml.cs` | `DataContextChanged` 里调 `SetUpNativeMenu()`；`OnClosing` 里配对退订；`SyncViewerBgChecks` 多同步一份原生项 |
| `Views/MainWindow.axaml` | 窗口内 `Menu` 加 `Name="WindowMenu"`（mac 上把它藏掉） |
| `App.axaml` | 加 `Name="OpenRevelare"`——否则系统菜单里是「Hide Application」 |
| `Assets/i18n/en.json` | 补 `实际像素 100%`（原表只有带「（Ctrl+1）」的那条） |

**为什么用 C# 建而不是再写一份 XAML**：窗口内那条 `Menu` 在 Windows / Linux 上仍要用，
两份菜单必须点出同一件事。写成两段 XAML 就是两份独立清单，改一处忘一处是迟早的。
现在原生菜单复用**同一批 Click 处理器**和同一批 VM 绑定，行为上不可能漂移——
和 `OnKeyDown` 那条注释在意的是同一件事，只是换到了菜单与菜单之间。

**没有自己加 Quit**：反编译 `AvaloniaNativeMenuExporter` 确认，Avalonia 会在应用菜单尾部
自动补 Services / Hide / Hide Others / Show All / Quit ⌘Q 一整套。自己再加就是两个 Quit。
⌘W 同理不写——mac 上 NSWindow 自己管。

**过程中查出并修掉的一个真 bug**：`NativeMenuItem` 派生自 `AvaloniaObject` 而非
`StyledElement`——它**没有 DataContext**，也不在逻辑树上。最初写的
`new Binding("HasImage")` 会静默失效，`IsEnabled` 停在默认值 `true`：
没开卷时「导出当前帧…」照样可点，且只在 mac 上出现。改成显式 `Source = Vm` 才对。
这条是靠下面那个 headless 探针跑出来的，不是看出来的。

### P5 内存探测

| 文件 | 改动 |
|------|------|
| `Services/SystemMemory.cs` | 新增 `TryMacOS()`：`sysctlbyname` 读 4 个页计数 + `hw.pagesize`；新增 `ReclaimableShare = 0.5` |
| `.github/workflows/ci.yml` | macos job 加一步，在**真 Apple Silicon** 上验这 5 个 sysctl 键存在且读得出数 |

只有 `vm.page_free_count` 是必需的，其余三项缺了就少算一项而不是整个失败——
一个键名写错就让整个平台静默退回总量估算，正是 P5 本身的毛病。

`Sysctl()` 用 `out ulong` 接 4 字节或 8 字节的键（这些 `vm.*` 键在不同版本宽度不同）。
**这一条本机实测过**：写了个 C 桩库验证 P/Invoke 的 `out` 参数在调用前确实清零，
所以 4 字节写入落在低半部、高半部不会留下上次调用的残留——同一个栈槽先被写成
`0xAAAA…` 再走 4 字节写入，读回来仍是干净的 `0xDEADBEEF`。

CI 那一步的 shell 逻辑也在本机用假 `sysctl` 跑过：正常路径 EXIT=0，
键名缺失 / 四项全 0 / 值非数字三种坏情况都正确失败。

---

## 五条修复的验证程度（重要）

| 修复 | 验证方式 | 置信度 |
|------|---------|--------|
| P1 快捷键 | 仅编译通过 + 文案对齐用 Python 模拟验证 | 依据是 Avalonia 把 ⌘ 映射为 `Meta` 这一确定行为，**未在 mac 上实测** |
| P2 选择框 | 仅编译通过 + 生成的 pattern 列表已核对 | 依据是 `NSOpenPanel` 类型过滤大小写敏感，**未在 mac 上实测** |
| P3 访达 | **本机实测**（argv 桩程序，新旧对比 7 组路径） | Linux 分支已证实；macOS 分支同理但未在 mac 上跑 |
| P4 菜单栏 | **本机实测**（headless 探针，见下）+ 反编译核对 Avalonia 行为 | 菜单树、绑定、手势已证实；**顶端菜单栏本身没在 mac 上看过** |
| P5 内存探测 | **本机实测**（P/Invoke `out` 清零的 C 桩库、CI 步骤的假 `sysctl`）+ 按真实 `vm_stat` 量级模拟 | 算术与 CI 守卫已证实；**sysctl 键本身要等 CI 的 macos 格跑一次**——那一步就是为此加的 |

本机是 Kubuntu，P1/P2/P4/P5 的 `OperatingSystem.IsMacOS()` 分支在这里跑不到。
**落地前建议在 mac 上过一遍**。P5 与前四条的差别在于：它的关键未知项（sysctl 键存不存在）
已经交给 CI 自动验证，不再依赖人工过一遍。

### P4 是怎么测的

写了个 headless 探针（`Avalonia.Headless` + 反射调 `BuildAppMenu` / `BuildWindowMenu`），
在 Linux 上把整棵菜单树建出来打印，并断言绑定确实是活的：

- `HasImage=false` 时「导出当前帧…」为 disabled → **这一步第一次跑就红了**，
  暴露出上面那条 `Source = Vm` 的 bug；
- 改完后置 `HasImage=true`，同一项转为 enabled → 绑定确认是响应式的，不是一次性赋值；
- 逐条比对原生菜单与 XAML 菜单的 Click 处理器和标题，差异只剩**有意为之**的那几处
  （Quit 交给 Avalonia、偏好设置/关于 搬进应用菜单、背景色走闭包）。

另外两处显示细节是反编译确认的，不是猜的：`Ctrl+OemComma` / `Ctrl+D1` 在
`KeyGesture.ToString()` 里是难看的枚举名，但原生层走的是 `(AvnKey)gesture.Key`，
`AvnKeyOemComma = 142`、`AvnKeyD1 = 35` 与 Avalonia 的 `Key` 值**逐一对应**，
所以 mac 上渲染出来就是 ⌘, 和 ⌘1。

探针本身在 scratchpad 里，没进仓库——它依赖反射私有方法，作为长期测试太脆。

---

## CI 加的那一格 macOS（报告第 3 条）

`.github/workflows/ci.yml` 新增 `macos` job（`runs-on: macos-latest`），
和现有的 `build`（ubuntu）并行。

### 它守什么、不守什么

**守**：托管代码在 `osx-arm64` RID 下编不编得过（带 `-warnaserror`），
以及 publish 的产物齐不齐——主程序、`libraw.23.dylib`、`net_awb.onnx`。

最后一条正是 v1.0.0 栽过好几次的地方：dylib 的复制条件如果写在 `Core` 里，
`$(RuntimeIdentifier)` 是空的，条件永不成立 → **编译不报错、包照打**，
直到 mac 上导入 RAW 才 `DllNotFoundException`。这一格能在几十秒内拦住它。

**不守**：不编真的 LibRaw，也不打 `.app` / `.dmg`。
`bundle-libraw.sh` 要 brew 装依赖再从源码编 0.21.4，几分钟起步，
而它守的是「原生库编得对不对」——那是 release 的事，`release.yml` 的 macos job 原样保留。

### 一个必须放的占位文件

```yaml
- name: 放一个占位 libraw.23.dylib
  run: |
    mkdir -p native/osx-arm64
    echo "placeholder …" > native/osx-arm64/libraw.23.dylib
```

不是可省的：`Gui.csproj` 的 `WarnMissingMacLibRaw` 在该文件缺失时发 **Warning**，
而这一步带 `-warnaserror`，于是缺了它整格会以一条**与本次改动无关的错误常红**。

这条是实测出来的，不是推的——我在本机分别跑了带 / 不带占位文件的
`dotnet publish -r osx-arm64 -warnaserror`，前者 EXIT=0，后者 EXIT=1。
占位文件内容无所谓（这一格不运行程序，只看装配结果），`native/` 也已在 `.gitignore` 里，
不会误提交。

### 代价

macOS runner 计费是 Linux 的 10 倍，但这一格只 build + publish，
不装 brew 依赖、不编原生库，属于秒级到十几秒的量级。
换来的是**下一个 macOS 差异不必等用户报障**——这次审计里最有价值的一条。
