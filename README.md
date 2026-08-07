<p align="center">
  <img src="docs/assets/logo.png" width="104" alt="OpenRevelare">
</p>

<h1 align="center">OpenRevelare</h1>

<p align="center">
  <b>推导，不讨好。</b><br>
  翻拍 RAW 或扫描 TIFF 进来，依光学与数学逐层解算成正片。<br>
  物理归物理，审美归审美。
</p>

<p align="center">
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Download for Windows x64" src="https://img.shields.io/badge/Windows-x64%20installer-1677ff?style=for-the-badge&logo=windows11&logoColor=white"></a>
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Download for Linux x86_64" src="https://img.shields.io/badge/Linux-x86__64%20AppImage-e95420?style=for-the-badge&logo=linux&logoColor=white"></a>
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Download for macOS Apple Silicon" src="https://img.shields.io/badge/macOS-Apple%20Silicon-111111?style=for-the-badge&logo=apple&logoColor=white"></a>
</p>

<p align="center">
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/Toshihiko-Lin/Open-Revelare?display_name=tag&sort=semver"></a>
  <a href="https://github.com/Toshihiko-Lin/Open-Revelare/actions/workflows/ci.yml"><img alt="CI build status" src="https://github.com/Toshihiko-Lin/Open-Revelare/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="GNU GPL v3" src="https://img.shields.io/badge/license-GPL--3.0--only-2ea44f.svg"></a>
</p>

<p align="center">
  <a href="https://revelare.netlify.app/">官网</a> ·
  <a href="#这是什么">简介</a> ·
  <a href="#这个工具适合谁">适合谁</a> ·
  <a href="#界面">界面</a> ·
  <a href="#下载与安装">下载</a> ·
  <a href="#能做什么">功能</a> ·
  <a href="#它是怎么算的">原理</a> ·
  <a href="#从源码构建">构建</a> ·
  <a href="#english">English</a>
</p>

<p align="center">
  <img src="docs/assets/editor-filmbase.jpg" width="100%" alt="OpenRevelare 主窗口：整卷校准">
</p>

<p align="center"><sub>主窗口「整卷校准」——左边整卷缩略图，中间当前帧，右栏是这一卷共用的物理参数。<br>Windows 上的实际界面，没有修饰。</sub></p>

---

## 这是什么

OpenRevelare 把彩色负片的橙色片基**解算**掉，而不是靠拉曲线试出来。

翻拍或扫描得到的负片进来，程序先把它还原成线性光，修完镜头的光学瑕疵，
再转进对数密度域——胶片本来就是在这个域里工作的——按 Cineon 那一套做白平衡与反转，
最后输出一张正片。每个参数都有名字、有出处：同一卷底片，今天算和明年算、
这台机器算和那台机器算，结果一样。

C# / .NET 8 + Avalonia，**纯 CPU**，Windows / Linux / macOS 三平台同一份代码。
本地优先、非破坏性：源文件永不改动，参数存在随片放置的 `.ncproj` 工程里，
不需要联网，不需要账号。**界面中英双语**，跟随系统或手动锁定。

## 这个工具适合谁

**适合**

- 用相机翻拍或扫描仪出图，手上有整卷要处理，希望一卷之内色调统一的人
- 不满足于「拉根曲线凭手感」，想知道每一步在物理上做了什么的人
- 想要可复现结果——归档三年后重开工程，还能算出同一张图的人

**可能不适合**

- 只想一键出片、不打算理解任何参数的人：程序有自动标定，但它的价值在于**可以被检查和修正**
- 追求绝对严谨的场景——文物翻拍、商业存档、科研用途：
  OpenRevelare 不做逐卷色卡标定，这类需求请用 [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)

对 Gold 200 这类标准 C-41，默认参数与逐卷标定的差别小到屏幕上略可察觉、打印几乎分辨不出；
染料特性偏离基准较远的卷差得多一些，但改一下标定或在 SceneBase 阶段微调就能补回大部分。

## 界面

一个窗口走完一整卷。上面那张是右栏的第一个阶段「整卷校准」：片基透射率 `t_base` 与 `d_max`、
暗部与亮部两段白平衡、反差号数、齿孔遮罩、几何裁切。标定当前这帧，再应用到整卷，
其余帧共用同一套物理参数。

<p align="center">
  <img src="docs/assets/editor-scenebase.jpg" width="100%" alt="主窗口：帧编辑">
</p>

<p align="center"><sub>同一个窗口切到第二个阶段「帧编辑」：色温色调、曝光、黑场 / 阴影 / 高光 / 白场、反差与饱和度，<br>最下面是 W / R / G / B 四通道曲线，背景叠着实时直方图。审美只动这一页，地基不受影响。</sub></p>

<table>
  <tr>
    <td width="50%"><img src="docs/assets/library.jpg" width="100%" alt="图库卷墙"></td>
    <td width="50%"><img src="docs/assets/contactsheet-light.jpg" width="100%" alt="整卷印样"></td>
  </tr>
  <tr>
    <td valign="top"><sub><b>图库</b>　打开软件先看到卷，不是空编辑器。每卷一张印样封面，底下写着胶卷、机身、冲洗日期与帧数，双击进卷继续上次的进度。</sub></td>
    <td valign="top"><sub><b>印样</b>　冲印店风格的整版印样，带齿孔排版，卷信息随图一起烧进画面。深浅两种样式，导出的都是整张大图，可直接归档或打印。</sub></td>
  </tr>
</table>

## 下载与安装

发行包在 [Releases](https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest)，
自带 .NET 运行时，不需要另外装任何东西。

| 平台 | 包 | 要求 | 成熟度 |
|---|---|---|---|
| Windows 10/11 x64 | `setup.exe` | 无 | **正式版** —— 开发机就是它，每次发版实测 |
| Linux x86_64 | `.AppImage` | glibc ≥ 2.35（Ubuntu 22.04 / Debian 12 及以上） | **公测** |
| macOS Apple Silicon | `.dmg` | macOS 12 或更高 | **公测，从未在真机上跑过** |

<details>
<summary><b>Windows</b> —— 「Windows 已保护你的电脑」怎么办</summary>

双击安装包，一路下一步即可。

若弹出蓝色的「Windows 已保护你的电脑」，点 **更多信息 → 仍要运行**。
这是 SmartScreen 对未购买代码签名证书的软件的例行提示，不是病毒警告。

</details>

<details>
<summary><b>macOS</b> —— 「已损坏，无法打开」怎么办</summary>

打开 dmg，把 OpenRevelare 拖进「应用程序」。

首次打开会提示「已损坏」或「无法验证开发者」。**不是文件损坏**，是没有花 99 美元/年
买 Apple 开发者计划、因而未经公证。任选一种绕过：

```bash
xattr -dr com.apple.quarantine /Applications/OpenRevelare.app
```

或者双击一次（会被拦下），再到 **系统设置 → 隐私与安全性 → 仍要打开**。

> 别照着网上的「右键 → 打开」做：macOS 15 (Sequoia) 起 Apple 取消了那个入口。

**公测说明**：macOS 版由云端构建，作者没有 Mac 设备，没有任何一次真机运行记录。
已知短板：`SystemMemory` 无 mac 实现，所以解码并发是保守固定档；没有 Adobe DNG Converter
回退路径。**欢迎开 issue 回报**，尤其是 RAW 能不能导入。

</details>

<details>
<summary><b>Linux</b> —— AppImage 怎么跑</summary>

AppImage 是单文件绿色程序，不用安装，放哪都行。加上可执行权限后双击或在终端运行：

```bash
chmod +x OpenRevelare-*.AppImage && ./OpenRevelare-*.AppImage
```

（在文件管理器里：右键 → 属性 → 权限 → 勾「可执行」）

本包自带 FUSE，不需要额外装 libfuse2。若系统实在起不来，加
`--appimage-extract-and-run` 参数运行。

</details>

## 能做什么

### 成像

- **密度域六步反转**——片基 `t_base`、白平衡 `wb_high` / `wb_offset`、扫描曝光、`d_max`、
  gamma（反差号数）、色度补偿，六个参数全部可调，每个都有明确的物理含义
- **窄带光源解耦（Path A）**——用 LED / 荧光灯箱翻拍时，三通道之间的串扰可以靠一组
  R/G/B 标定帧解算出 3×3 矩阵消掉。做法源自
  [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)
- **自动标定**——从整卷估片基、齿孔阈值、暗端谷底、`d_max`、亮部白平衡
- **智能白平衡**——DeepWB 神经网络一键估算白点（模型单独授权，[见下](#智能白平衡模型--单独授权请读一下)）
- **预反转校正**——LCC 平场、镜头畸变、暗角、齿孔遮罩，全部在线性光域完成
- **Stage 2 调整**——曝光 / 色阶 / 对比度 / 高光阴影 / PCHIP 曲线 / 饱和度

### 工作流

- **按卷管理**——导入即建卷，图库卷墙用每卷的印样当封面，可按画幅、胶卷等分类筛选
- **无「保存」动作**——改完自动落盘。`.ncproj` 与源图像放在一起，拷盘换机跟着片子走
- **整卷同步**——虚拟副本、整卷或勾选帧同步标定与场景
- **画幅预设**——135 全幅（含边框）/ 半格 / XPan / 645 / 6×6 / 6×7 / 6×9 / 6×12
- **80 步撤销重做**（整卷快照，连续微调自动合并）
- **冲印店风格整版印样**，底部自带卷标识条

### 输入输出

| | |
|---|---|
| **RAW 输入** | DNG / NEF / CR2 / CR3 / ARW / RAF / RW2 / ORF / PEF / IIQ 等（LibRaw） |
| **其他输入** | TIFF / JPEG / PNG |
| **导出** | 8/16-bit TIFF、JPEG，可嵌 sRGB / Adobe RGB ICC profile |

## 它是怎么算的

### 两个阶段：FilmBase 与 SceneBase

|  | **FilmBase · 物理还原** | **SceneBase · 审美调整** |
|---|---|---|
| 描述的是 | 这卷胶片客观存在的物理属性：片基的颜色与密度、最大密度、通道平衡、反转对比度、色度还原系数 | 色温偏好、曝光亮度、对比度风格、最终饱和度 |
| 性质 | 不是审美选择，是测量结果。同一卷共用同一套 | 同一张底片可以有完全不同的设定，每帧各调各的 |
| 改的是 | 反转方程的**输入**——重算地基 | 反转方程的**输出**——在地基上装修 |

两阶段分开的意义在于：地基算对一次，整卷通用；后面怎么改都不会把物理基础搞乱。

### 一帧的处理管线

1. **还原成光**——翻拍 RAW 关掉相机的一切美化，由 LibRaw 解到线性；
   显示 gamma 的扫描件一键线性化。两种输入回到同一条线性光起跑线
2. **线性域校正**——畸变、LCC 平场、暗角，以及齿孔 / 灯板遮罩。
   光学瑕疵只有在「光」的状态下修才物理正确
3. **光源解耦**（可选）——白光直通，或走 RGB 三色分离，后者能精确测量并分离染料层间的串扰
4. **扣除色罩，进入密度域**——采样片基橙色透射率抵消色罩，信号转入对数密度域，
   算法建立在 Cineon 标准之上
5. **密度域白平衡**——暗部与亮部分别校正，且必须先暗后亮。
   这个顺序正是物理推算与随手拉曲线的根本区别
6. **反转**——补回负片故意留给相纸的对比度与饱和度
7. **输出**——一张物理上正确的正片。可以喂给调色软件继续创作，
   也可以在 Stage 2 里直接调完导出

程序内 **帮助 → 操作指引 / 技术原理** 有完整的使用说明与物理推导。

## 智能白平衡模型 —— 单独授权，请读一下

「智能白平衡」用到 Deep White-Balance Editing (CVPR 2020) 的网络权重
`models/net_awb.onnx`。它随仓库和发行包一起分发，但——

> [!IMPORTANT]
> **这个文件不在本项目 GPL-3.0 授权的范围内。**
> 它按原作者的 **CC BY-NC-SA 4.0**（署名 — 非商业 — 相同方式共享）分发。

OpenRevelare 免费、不销售、无订阅无内购，分发本身不以商业利益为目的，因此符合 NC 条款。
但**你从 GPL-3.0 拿到的「可以商业再分发」这项权利不适用于这个文件**——要商用请先删掉
`models/` 目录。程序照常构建运行，只有「智能白平衡」一个功能会提示模型未找到；
手动白平衡、自动亮部白平衡、Path A 解耦都不依赖它。

细节见 [models/README.md](models/README.md) 与
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) 第 13 条。作者要求引用其论文。

## 数据放在哪

| 内容 | Windows | Linux / macOS | 可否自定义 |
|---|---|---|---|
| 设置、卷目录索引 | `%APPDATA%\OpenRevelare` | `$XDG_CONFIG_HOME`（缺省 `~/.config`）下的 `OpenRevelare/` | 固定 |
| 卷封面印样缓存 | `%LOCALAPPDATA%\OpenRevelare\sheets` | `$XDG_CACHE_HOME`（缺省 `~/.cache`）下的 `OpenRevelare/sheets/` | ✅ 目录 + 上限（默认 1 GB） |
| 线性 DNG 解码缓存 | 默认跟着源文件放 `.revelare-cache/` | 同左 | ✅ 目录 + 上限（默认 5 GB），会话级 |
| 工程 `.ncproj` | 随片放在源图像文件夹 | 同左 | 由你放照片的位置决定 |

DNG 缓存默认贴着源文件而不是系统盘，是因为 60 MP 一帧转出来约 349 MB。
两个缓存都能改位置、都有上限、都在偏好设置里显示当前占用。卸载不碰以上任何目录。

macOS 与 Linux 共用 XDG 路径，没有改用 `~/Library/Application Support`——全线一条代码路径。

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
git clone https://github.com/Toshihiko-Lin/Open-Revelare.git
cd Open-Revelare
dotnet build -c Release
dotnet run --project src/OpenRevelare.Gui
```

命令行前端（无 GUI，同一个 Core）：

```bash
dotnet run --project src/OpenRevelare.Cli -- -i neg.tiff -o pos.tiff --grade 1.65 --d-max 2.0
dotnet run --project src/OpenRevelare.Cli -- --help
```

### 打包

```bash
# Windows —— 需 Inno Setup 6
dotnet publish src/OpenRevelare.Gui -c Release -r win-x64 --self-contained true -o publish/win-x64
ISCC.exe open-revelare.iss                     # → installer/OpenRevelare-{版本}-setup.exe

# Linux —— 需在 Linux 上跑（脚本会自动下载 appimagetool）
./packaging/linux/build-appimage.sh            # → installer/OpenRevelare-{版本}-x86_64.AppImage

# macOS —— 需在 mac 上跑
./packaging/macos/bundle-libraw.sh             # 编译 LibRaw 0.21.4（NuGet 没有 mac runtime 包）
./packaging/macos/build-app.sh --dmg           # → installer/OpenRevelare-{版本}-{arch}.dmg
```

`dotnet publish -r linux-x64` / `-r osx-arm64` **在 Windows 上也能跑**，但 `appimagetool`、
`codesign`、`hdiutil` 必须在对应系统上执行。三平台产物由
[`.github/workflows/release.yml`](.github/workflows/release.yml) 打 tag 自动构建。

> **macOS 的 LibRaw 必须锁 0.21.x**：Sdcb.LibRaw 0.21.1.7 按 0.21 的 `libraw_data_t` 布局
> marshal，brew 上的 0.22 加过字段，偏移全错。`bundle-libraw.sh` 因此锁 0.21.4 源码编译。

## 许可证

本项目的代码以 **GPL-3.0-only** 授权，见 [LICENSE](LICENSE)。

**例外**：`models/net_awb.onnx` 是第三方资产，按 CC BY-NC-SA 4.0 单独授权，
不在上述 GPL-3.0 范围内，见 [models/README.md](models/README.md)。

随二进制分发的第三方组件及其许可见
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)——其中 LibRaw 走 LGPL-2.1，
带上这份声明不是可选项。

## 致谢

- [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)（MIT）—— 窄带 RGB 解耦（Path A）的做法出自这里
- [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)（MIT）—— 密度域色彩模型的参照
- darktable `negadoctor` 模块 ——— 数学模型 `D_corr = D × wb_high + wb_offset`参照
- 感谢以下各位对本项目提供的研讨/测试/意见支持：
- Caramello_焦糖玛奇朵
- 测试版支持者：
- 小红书用户：jamais,REPEATER000,杂鱼睡不醒，hhe
  

反馈与 bug 请开 [issue](https://github.com/Toshihiko-Lin/Open-Revelare/issues)，
附上系统版本、相机或扫描仪型号、输入格式与错误信息。请不要上传含隐私内容的原片。

## 支持

Revelare 最早是自己做着玩的小工具，那时候 vibe coding 花了不少成本，就想着开放大部分功能，只把几个进阶的翻拍工作流定个价，多少补贴一点。结果真的有人愿意购买支持——非常感谢大家。

后来听取了很多人的意见，重构了一遍，补上了三平台，做到觉得完成度差不多了，可能对其他的玩家或者开发者也能带来一些实在的启发，因此开源,胶片圈子不大,工具也不嫌多,算是我对社区的一点回馈。如果觉得有用，可以请我喝杯咖啡。

<p align="center">
  <img src="docs/assets/donate-wechat.png" width="220" alt="微信支付">
</p>

---

<a id="english"></a>

## English

**OpenRevelare** turns colour negatives — camera-scanned or scanner-produced — into positives by
*solving* for the orange mask rather than eyeballing curves. Input is linearised, lens-corrected,
moved into the log-density domain, white-balanced and inverted on top of the Cineon standard,
and written out as a positive. Every parameter is named, physically meaningful and reproducible:
the same roll gives the same result next year, on another machine.

Built with C# / .NET 8 and Avalonia. **CPU only. Bilingual UI (Chinese/English)** — follows
system locale or can be locked manually. Local-first and non-destructive — source files are never
modified, parameters live in a `.ncproj` next to the images, and nothing requires an account or
a network connection.

> [!NOTE]
> **The application UI is now available in both Chinese and English.** Most of this README and the
> in-app help documentation are still Chinese-only. Code and comments are mixed English/Chinese.
> Further translations and documentation are welcome — please open an issue or a pull request.

- **Website** — [revelare.netlify.app](https://revelare.netlify.app/) (Chinese), with screenshots
  of the library, develop and contact-sheet views.
- **Download** — [Releases](https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest).
  Windows x64 `setup.exe` (stable), Linux x86_64 `.AppImage` (beta, glibc ≥ 2.35),
  macOS Apple Silicon `.dmg` (beta, never run on real hardware). The .NET runtime is bundled.
  Packages are unsigned and the macOS build is not notarised; see the install notes above.
- **Input** — RAW via LibRaw (DNG / NEF / CR2 / CR3 / ARW / RAF / RW2 / ORF / PEF / IIQ …),
  plus TIFF, JPEG and PNG. **Output** — 8/16-bit TIFF and JPEG, with an embedded sRGB or
  Adobe RGB ICC profile.
- **Build** — .NET 8 SDK, then `dotnet build -c Release` and
  `dotnet run --project src/OpenRevelare.Gui`. A CLI front-end shares the same Core.
- **Licence** — GPL-3.0-only, **except** `models/net_awb.onnx`, which is a third-party asset under
  CC BY-NC-SA 4.0 and is therefore **not** covered by the GPL grant. Delete `models/` before any
  commercial redistribution; everything but the "smart white balance" feature keeps working.
  See [models/README.md](models/README.md) and [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
- **Donate** — Free, open-source, no ads. If it's been useful, a coffee is always welcome (WeChat Pay QR in the Chinese section above).
