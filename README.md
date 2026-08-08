<p align="center">
  <img src="docs/assets/logo.png" width="104" alt="OpenRevelare">
</p>

<h1 align="center">OpenRevelare</h1>

<p align="center">
  <b>从玄学，到物理。</b><br>
  彩色负片的翻拍 RAW 或扫描 TIFF，按光学与数学逐层解算成正片。<br>
  还原交给物理，审美留给你。
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
  <a href="#为什么做这个">故事</a> ·
  <a href="#三条原则">原则</a> ·
  <a href="#适合谁">适合谁</a> ·
  <a href="#和主流方案的区别">对比</a> ·
  <a href="#界面">界面</a> ·
  <a href="#下载与安装">下载</a> ·
  <a href="#快速上手">上手</a> ·
  <a href="#功能">功能</a> ·
  <a href="#工作原理">原理</a> ·
  <a href="#从源码构建">构建</a> ·
  <a href="#english">English</a>
</p>

<p align="center">
  <img src="docs/assets/editor-filmbase.jpg" width="100%" alt="OpenRevelare 主窗口：整卷校准">
</p>

<p align="center"><sub>主窗口「整卷校准」：左边整卷缩略图，中间当前帧，右栏是这一卷共用的物理参数。</sub></p>

---

## 这是什么

彩色负片有一层橙色的片基（色罩），翻拍或扫描出来的原始文件直接看是偏色的。OpenRevelare 做的事情就是把这层色罩**算掉**：先把输入还原成线性光，修掉镜头的光学问题，再转进对数密度域，按 Cineon 标准做白平衡和反转，最后输出正片。

整个流程里每个参数都有名字、有明确的物理含义。同一卷底片，今天处理、明年处理、换台机器处理，结果都一样——这是「计算」和「拉曲线试手感」的根本区别。

技术栈：C# / .NET 8 + Avalonia，**纯 CPU**，Windows / Linux / macOS 三平台同一份代码。本地优先、非破坏：源文件永不修改，参数存在图片旁边的 `.ncproj` 文件里；不联网、不需要账号。界面中英双语，跟随系统或手动锁定。

## 为什么做这个

作者自己拍胶片，翻拍负片后的去色罩一直很痛苦：主流方案是 Lightroom 插件（Negative Lab Pro、ColorPerfect 之类），锁死 Adobe 付费生态，处理过程是个黑盒——参数不可解释、结果不可复现；免费方案学习曲线又陡。社区里讨论最多的词是「玄学」：同一卷底片，换个人调、换个时间调，出来完全不一样。

OpenRevelare 的想法很简单：把去色罩从「调出来的」变成「算出来的」。彩色负片的色罩是物理存在——片基染料的吸收特性是测量对象，不是审美对象。把反转建立在 Cineon 密度域的物理与数学上，每个参数都有明确的物理含义，同一卷底片任何时候、任何机器上处理，结果都一样。

项目从自用工具起步，做了付费验证（买断制，8 位用户付费支持），再根据用户反馈用 C# 重构底层——速度提升约 13 倍、补齐三平台、老用户处理结果逐像素不变。2026 年 8 月开源，免费，希望也能帮到其他胶片玩家。

## 三条原则

1. **色罩是物理，不是审美**——片基染料的吸收特性是测量对象：采样、扣除、算完，不靠肉眼猜
2. **密度域是负片的母语**——在 log 密度域里，色罩是常量偏移、白平衡和反转是线性操作，结果可复现；在非线性域里这些操作互相纠缠，只能凭手感
3. **还原与创作分开**——物理还原（FilmBase）整卷共用，审美调整（SceneBase）每帧独立，互不污染

## 适合谁

**适合**

- 用相机翻拍或扫描仪出图，手上有整卷要处理，希望一卷之内色调统一
- 不满足于「拉根曲线凭手感」，想了解每一步在物理上做了什么
- 需要结果可复现——归档三年后重开工程，还能算出同一张图

**可能不适合**

- 只想一键出片、不打算理解任何参数：程序有自动标定，但它的价值在于**可以被检查和修正**
- 要求严格色彩还原的场景——文物翻拍、商业存档、科研用途：OpenRevelare 不做逐卷色卡标定，这类需求请用 [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)

对 Gold 200 这类标准 C-41 负片，默认参数与逐卷标定的差别在屏幕上略可察觉、打印几乎分辨不出；染料特性偏离基准较远的卷差别大一些，但改一下标定或在 SceneBase 阶段微调就能补回大部分。

## 和主流方案的区别

| | 生态插件（NLP / ColorPerfect 等） | 硬件校正（DiVERE） | OpenRevelare |
|---|---|---|---|
| 形态 | Lightroom/PS 插件 | 独立软件 | 独立软件 |
| 生态 | 锁死 Adobe，$99+ | 免费开源 | 免费开源 |
| 处理 | 黑盒，不可解释 | 物理可解释 | 物理可解释 |
| 门槛 | 低 | 需色卡 + 窄谱光源 | 零门槛，翻拍直出 |
| 结果 | 不可复现 | 可复现 | 可复现（每参数有物理含义） |

一句话：生态插件把去色罩当滤镜卖，硬件校正把精度建立在额外器材上，OpenRevelare 走的是「免硬件 + 可解释 + 可复现」的路。

## 界面

一个窗口走完一整卷。第一页「整卷校准」：片基透射率 `t_base`、`d_max`、暗部与亮部两段白平衡、反差号数、齿孔遮罩、几何裁切。先标定当前帧，再应用到整卷，其余帧共用同一套物理参数。

<p align="center">
  <img src="docs/assets/editor-scenebase.jpg" width="100%" alt="主窗口：帧编辑">
</p>

<p align="center"><sub>第二页「帧编辑」：色温色调、曝光、黑场 / 阴影 / 高光 / 白场、反差与饱和度，底部是 W / R / G / B 四通道曲线，背景叠着实时直方图。审美只动这一页，物理还原的结果不受影响。</sub></p>

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

发行包在 [Releases](https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest)，自带 .NET 运行时，不需要另外装任何东西。

| 平台 | 包 | 要求 | 成熟度 |
|---|---|---|---|
| Windows 10/11 x64 | `setup.exe` | 无 | **正式版** —— 开发机就是它，每次发版实测 |
| Linux x86_64 | `.AppImage` | glibc ≥ 2.35（Ubuntu 22.04 / Debian 12 及以上） | **公测** |
| macOS Apple Silicon | `.dmg` | macOS 12 或更高 | **公测，从未在真机上跑过** |

<details>
<summary><b>Windows</b> —— 「Windows 已保护你的电脑」怎么办</summary>

双击安装包，一路下一步即可。

若弹出蓝色的「Windows 已保护你的电脑」，点 **更多信息 → 仍要运行**。这是 SmartScreen 对未购买代码签名证书的软件的例行提示，不是病毒警告。

</details>

<details>
<summary><b>macOS</b> —— 「已损坏，无法打开」怎么办</summary>

打开 dmg，把 OpenRevelare 拖进「应用程序」。

首次打开会提示「已损坏」或「无法验证开发者」。**不是文件损坏**，是没有买 Apple 开发者计划（99 美元/年）、未经公证。任选一种方式绕过：

```bash
xattr -dr com.apple.quarantine /Applications/OpenRevelare.app
```

或者双击一次（会被拦下），再到 **系统设置 → 隐私与安全性 → 仍要打开**。

> 别照着网上的「右键 → 打开」做：macOS 15 (Sequoia) 起 Apple 取消了那个入口。

**公测说明**：macOS 版由云端构建，作者没有 Mac 设备，没有任何一次真机运行记录。已知短板：`SystemMemory` 无 mac 实现，解码并发是保守固定档；没有 Adobe DNG Converter 回退路径。**欢迎开 issue 回报**，尤其是 RAW 能不能导入。

</details>

<details>
<summary><b>Linux</b> —— AppImage 怎么跑</summary>

AppImage 是单文件绿色程序，不用安装，放哪都行。加上可执行权限后双击或在终端运行：

```bash
chmod +x OpenRevelare-*.AppImage && ./OpenRevelare-*.AppImage
```

（在文件管理器里：右键 → 属性 → 权限 → 勾「可执行」）

本包自带 FUSE，不需要额外装 libfuse2。若系统实在起不来，加 `--appimage-extract-and-run` 参数运行。

</details>

## 快速上手

1. **导入**——把翻拍或扫描的底片文件拖进窗口，输入卷信息（胶卷、机身、冲洗日期）
2. **整卷校准**——在「整卷校准」页标定当前帧：自动标定会估片基、白平衡、反差等，不满意可手动修正
3. **应用到整卷**——把这套物理参数同步给整卷，其余帧共用
4. **帧编辑**——逐帧在「帧编辑」页做审美调整：色温、曝光、对比度、饱和度、曲线
5. **导出**——8/16-bit TIFF 或 JPEG，可嵌 ICC profile

全程没有「保存」按钮——改动自动落盘，`.ncproj` 与源文件放在一起。

## 功能

### 成像

- **密度域六步反转**——片基 `t_base`、白平衡 `wb_high` / `wb_offset`、扫描曝光、`d_max`、gamma（反差号数）、色度补偿，六个参数全部可调，每个都有明确的物理含义
- **窄带光源解耦（Path A）**——用 LED / 荧光灯箱翻拍时，三通道之间的串扰可以靠一组 R/G/B 标定帧解算出 3×3 矩阵消掉。做法源自 [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)
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

## 工作原理

### 为什么是密度域

彩色负片的信号天然是密度。透射率 T 取负对数——`D = -log10(T)`——得到对数密度，这正是 Cineon 胶片扫描标准采用的域。在这个域里，R/G/B 三通道的关系是线性的、可预测的：片基色罩近似一个常量偏移，扣除它只需一次减法；白平衡、反转都是线性操作。在非线性域里这些操作互相纠缠，只能靠手感试——这就是「玄学」的来源，也是 OpenRevelare 选择密度域的根因。

### 核心公式

从采样到正片，关键步骤都可以写成一行：

**片基归一化**——每通道除以采样的片基透射率，橙色色罩一步扣掉：

$$T_\text{norm} = T / T_\text{base}$$

**转入密度域**——透射率取负对数（下限截断防溢出）：

$$D = -\log_{10}\!\bigl(\max(T_\text{norm},\ 10^{-D_\text{max}})\bigr)$$

**密度域白平衡**——阴影端加法项 + 高光端乘法项（Negadoctor 双端模型）：

$$D_\text{corr}[c] = D[c] \times w_\text{high}[c] + w_\text{offset}[c]$$

**反转**——亮度与色度分离后分别控制（grade 管对比度，chroma_grade 管色度还原）：

$$D_\text{adj} = \text{pivot} + (D_\text{mean} - \text{pivot}) \times \text{grade} + D_\text{chroma} \times \frac{\text{chroma\_grade}}{\text{chroma\_amp}} - D_\text{max}$$

$$T_\text{pos} = 10^{D_\text{adj}}$$

每个参数的完整推导见应用内「帮助 → 技术原理」。

### 两个阶段：FilmBase 与 SceneBase

|  | **FilmBase · 物理还原** | **SceneBase · 审美调整** |
|---|---|---|
| 描述的是 | 这卷胶片客观存在的物理属性：片基的颜色与密度、最大密度、通道平衡、反转对比度、色度还原系数 | 色温偏好、曝光亮度、对比度风格、最终饱和度 |
| 性质 | 不是审美选择，是测量结果。同一卷共用同一套 | 同一张底片可以有完全不同的设定，每帧各调各的 |
| 改的是 | 反转方程的**输入**——重算物理还原 | 反转方程的**输出**——在还原结果上调整 |

两阶段分开的意义：物理还原算对一次，整卷通用；后面怎么改，都不会把物理基础搞乱。这里的「物理还原」指仅依据这卷胶片本身的信息（片基、最大密度、通道平衡）推算出的去色罩结果，不含任何主观调整。

### 单帧处理管线

1. **还原成光**——翻拍 RAW 关掉相机的一切美化，由 LibRaw 解到线性；带显示 gamma 的扫描件一键线性化。两种输入回到同一条线性光起跑线
2. **线性域校正**——畸变、LCC 平场、暗角，以及齿孔 / 灯板遮罩。光学瑕疵只有在「光」的状态下修才物理正确
3. **光源解耦**（可选）——白光直通，或走 RGB 三色分离，后者能精确测量并分离染料层间的串扰
4. **扣除色罩，进入密度域**——用采样的片基橙色透射率抵消色罩，信号转入对数密度域，算法建立在 Cineon 标准之上
5. **密度域白平衡**——暗部与亮部分别校正，且必须先暗后亮。这个顺序正是物理推算与随手拉曲线的根本区别
6. **反转**——补回负片故意留给相纸的对比度与饱和度
7. **输出**——得到一张物理上正确的正片。可以喂给调色软件继续创作，也可以在 Stage 2 里直接调完导出

程序内 **帮助 → 操作指引 / 技术原理** 有完整的使用说明与物理推导。

## 智能白平衡模型 —— 单独授权，请读一下

「智能白平衡」用到 Deep White-Balance Editing (CVPR 2020) 的网络权重 `models/net_awb.onnx`。它随仓库和发行包一起分发，但——

> [!IMPORTANT]
> **这个文件不在本项目 GPL-3.0 授权的范围内。**
> 它按原作者的 **CC BY-NC-SA 4.0**（署名 — 非商业 — 相同方式共享）分发。

OpenRevelare 免费、不销售、无订阅无内购，分发本身不以商业利益为目的，因此符合 NC 条款。但**你从 GPL-3.0 拿到的「可以商业再分发」这项权利不适用于这个文件**——要商用请先删掉 `models/` 目录。程序照常构建运行，只有「智能白平衡」一个功能会提示模型未找到；手动白平衡、自动亮部白平衡、Path A 解耦都不依赖它。

细节见 [models/README.md](models/README.md) 与 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) 第 13 条。作者要求引用其论文。

## 数据放在哪

| 内容 | Windows | Linux / macOS | 可否自定义 |
|---|---|---|---|
| 设置、卷目录索引 | `%APPDATA%\OpenRevelare` | `$XDG_CONFIG_HOME`（缺省 `~/.config`）下的 `OpenRevelare/` | 固定 |
| 卷封面印样缓存 | `%LOCALAPPDATA%\OpenRevelare\sheets` | `$XDG_CACHE_HOME`（缺省 `~/.cache`）下的 `OpenRevelare/sheets/` | ✅ 目录 + 上限（默认 1 GB） |
| 线性 DNG 解码缓存 | 默认跟着源文件放 `.revelare-cache/` | 同左 | ✅ 目录 + 上限（默认 5 GB），会话级 |
| 工程 `.ncproj` | 随片放在源图像文件夹 | 同左 | 由你放照片的位置决定 |

DNG 缓存默认贴着源文件而不是系统盘，是因为 60 MP 一帧转出来约 349 MB。两个缓存都能改位置、都有上限、都在偏好设置里显示当前占用。卸载不碰以上任何目录。

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

`dotnet publish -r linux-x64` / `-r osx-arm64` **在 Windows 上也能跑**，但 `appimagetool`、`codesign`、`hdiutil` 必须在对应系统上执行。三平台产物由 [`.github/workflows/release.yml`](.github/workflows/release.yml) 打 tag 自动构建。

> **macOS 的 LibRaw 必须锁 0.21.x**：Sdcb.LibRaw 0.21.1.7 按 0.21 的 `libraw_data_t` 布局 marshal，brew 上的 0.22 加过字段，偏移全错。`bundle-libraw.sh` 因此锁 0.21.4 源码编译。

## 许可证

本项目的代码以 **GPL-3.0-only** 授权，见 [LICENSE](LICENSE)。

**例外**：`models/net_awb.onnx` 是第三方资产，按 CC BY-NC-SA 4.0 单独授权，不在上述 GPL-3.0 范围内，见 [models/README.md](models/README.md)。

随二进制分发的第三方组件及其许可见 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)——其中 LibRaw 走 LGPL-2.1，带上这份声明不是可选项。

## 致谢

- [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)（MIT）—— 窄带 RGB 解耦（Path A）的做法出自这里
- [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)（MIT）—— 密度域色彩模型的参照
- darktable 的 `negadoctor` 模块 —— 参照其数学模型 `D_corr = D × wb_high + wb_offset`

反馈与 bug 请开 [issue](https://github.com/Toshihiko-Lin/Open-Revelare/issues)，附上系统版本、相机或扫描仪型号、输入格式与错误信息。请不要上传含隐私内容的原片。

## Roadmap 与已知限制

**规划中**

- ECN-2 电影负片的独立标定数据（基于 ColorChecker 24，当前用 C-41 基准近似）
- macOS 真机验证与 `SystemMemory` 实现（macOS 版从未在真机运行，解码并发为保守固定档）

**已知限制**

- 不做逐卷色卡标定：追求严格色彩精度（文物翻拍、商业存档、科研）请用 DiVERE
- 8-bit TIFF 输入暗部可能出现轻微色带，建议扫描时导出 16-bit
- Lensfun 镜头库未收录的镜头需手动指定型号

## 打赏

Revelare 最早是自己做着玩的小工具。当时开发花了不少成本，就把大部分功能开放，只给几个进阶的翻拍工作流定价，补贴一点。后来真的有人愿意购买支持——非常感谢。

之后听取了很多人的意见，重构了一遍，补上三平台支持，做到觉得完成度差不多了，可能对其他玩家或开发者也能带来一些实在的启发，因此开源。胶片圈子不大，工具也不嫌多，算是对社区的一点回馈。如果觉得有用，可以请我喝杯咖啡。

<p align="center">
  <img src="docs/assets/donate-wechat.png" width="220" alt="微信支付">
</p>

---

<a id="english"></a>

## English

> **The mask is math, not magic.** — same spirit as the Chinese tagline「从玄学，到物理」.
> **Physics does the restoring; you do the taste.** — the sub-tagline.

**OpenRevelare** converts colour negatives — camera-scanned RAW or scanner TIFF — into positives by *computing* the orange mask away instead of eyeballing curves. Input is linearised, lens-corrected, moved into the log-density domain, white-balanced and inverted on top of the Cineon standard, and written out as a positive. Every parameter is named and physically meaningful, so the same roll produces the same result today, next year, or on a different machine.

Built with C# / .NET 8 + Avalonia. **CPU only. Bilingual UI (Chinese/English)** — follows the system locale or can be locked manually. Local-first and non-destructive: source files are never modified, settings live in a `.ncproj` next to the images, and nothing requires an account or a network connection.

## What it is

Colour negatives carry an orange base — the mask. Camera-copied or scanned, they look colour-shifted until the mask is removed. OpenRevelare removes it by *computation*: the input is restored to linear light, lens defects are corrected, the signal is moved into the log-density domain, white-balanced and inverted on the Cineon standard, and a positive comes out.

Every parameter has a name and a physical meaning. The same roll gives the same result today, next year, or on a different machine — that is the difference between *computation* and *eyeballing curves*.

Tech stack: C# / .NET 8 + Avalonia, **CPU only**, one codebase for Windows / Linux / macOS. Local-first and non-destructive: source files are never modified, parameters live in a `.ncproj` next to the images; no network, no account. The UI is bilingual (Chinese/English), following the system locale or locked manually.

## Why this project

The author shoots film and was fed up with the mask-removal workflow: the mainstream options are Lightroom plugins (Negative Lab Pro, ColorPerfect, …) locked into the paid Adobe ecosystem with opaque, unreproducible processing; the free options have a steep learning curve. The word the community uses most is "dark magic": the same roll comes out different depending on who, and when, is doing the adjustment.

The idea behind OpenRevelare is simple: make mask removal *computed* instead of *tuned*. The mask is physical — the absorption of the base dyes is something you measure, not something you judge by taste. Built on the Cineon density domain, every parameter maps to a real physical quantity, and the same roll gives the same result every time.

The project started as a self-use tool, was validated with real paying users (8 paid, buy-once), then rewritten in C# based on user feedback — roughly 13× faster, three platforms, pixel-identical results for existing users. Open-sourced in August 2026, free, in the hope that it helps other film shooters too.

## Three principles

1. **The mask is physics, not taste** — the base dyes' absorption is something you measure: sample it, subtract it, done. Not something you eyeball
2. **Density is the negative's native language** — in the log-density domain the mask is a constant offset and white balance/inversion are linear operations, so results reproduce; in a non-linear domain those operations interfere and you can only tune by feel
3. **Restoration and creation are separate** — physical restoration (FilmBase) is shared by the roll; aesthetic edits (SceneBase) are per-frame; neither pollutes the other

## Who it's for

**Good fit**

- People copying negatives with a camera or a scanner, processing whole rolls and wanting consistent tones across the roll
- People not satisfied with "pull a curve and hope", who want to know what each step does physically
- People who need reproducible results — reopen a project in three years and get the same image

**Probably not a fit**

- People who want one-click output and don't want to understand any parameter: there is auto-calibration, but the point of the tool is that everything *can* be inspected and corrected
- Strict colour-accurate work — heritage copying, commercial archiving, research: OpenRevelare does not do per-roll colour-chart calibration. For that, use [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)

For standard C-41 stocks like Gold 200, the difference between the defaults and a per-roll calibration is barely visible on screen and essentially indistinguishable in print; stocks further from the reference need a calibration tweak or a SceneBase touch-up to close most of the gap.

## How it compares to the mainstream

| | Ecosystem plugins (NLP / ColorPerfect …) | Hardware calibration (DiVERE) | OpenRevelare |
|---|---|---|---|
| Form | Lightroom/PS plugin | Standalone app | Standalone app |
| Ecosystem | Locked to Adobe, $99+ | Free, open-source | Free, open-source |
| Processing | Black box, unexplainable | Physically explainable | Physically explainable |
| Barrier | Low | Needs colour chart + narrowband light | None — copy and go |
| Reproducibility | No | Yes | Yes (every parameter has a physical meaning) |

In one line: plugins sell mask removal as a filter, hardware calibration builds precision on extra gear, OpenRevelare goes "no hardware, explainable, reproducible".

## Interface

One window takes a whole roll from start to finish. The first stage, "Roll calibration": base transmittance `t_base`, `d_max`, shadows/highlights white balance, grade, sprocket mask, geometry crop. Calibrate the current frame, apply to the roll, and the other frames share the same physical parameters.

<p align="center">
  <img src="docs/assets/editor-scenebase.jpg" width="100%" alt="Main window: frame edit">
</p>

<p align="center"><sub>Second stage, "Frame edit": colour temperature/tint, exposure, black point / shadows / highlights / white point, contrast and saturation, with W/R/G/B curves at the bottom over a live histogram. Aesthetic edits stay on this page; the physical restoration is untouched.</sub></p>

<table>
  <tr>
    <td width="50%"><img src="docs/assets/library.jpg" width="100%" alt="Library roll wall"></td>
    <td width="50%"><img src="docs/assets/contactsheet-light.jpg" width="100%" alt="Contact sheet"></td>
  </tr>
  <tr>
    <td valign="top"><sub><b>Library</b>　You open the app and see your rolls, not an empty editor. Each roll has a contact-sheet cover labelled with film stock, camera, processing date and frame count; double-click to resume where you left off.</sub></td>
    <td valign="top"><sub><b>Contact sheet</b>　Lab-style full-roll contact sheets with sprocket layout and roll info burned in. Light and dark styles; exports a full-size image ready for archiving or printing.</sub></td>
  </tr>
</table>

## Download & install

Builds are on [Releases](https://github.com/Toshihiko-Lin/Open-Revelare/releases/latest). The .NET runtime is bundled — nothing else to install.

| Platform | Package | Requirements | Maturity |
|---|---|---|---|
| Windows 10/11 x64 | `setup.exe` | none | **Stable** — developed on it, tested on every release |
| Linux x86_64 | `.AppImage` | glibc ≥ 2.35 (Ubuntu 22.04 / Debian 12+) | **Beta** |
| macOS Apple Silicon | `.dmg` | macOS 12+ | **Beta, never run on real hardware** |

<details>
<summary><b>Windows</b> — "Windows protected your PC"</summary>

Run the installer and click through.

If a blue "Windows protected your PC" dialog appears, click **More info → Run anyway**. This is SmartScreen's routine notice for software without a code-signing certificate — not a virus warning.

</details>

<details>
<summary><b>macOS</b> — "is damaged and can't be opened"</summary>

Open the dmg and drag OpenRevelare into Applications.

The first launch may report "damaged" or "unidentified developer". **The file is not damaged** — the build is simply not notarised (no Apple Developer Program membership, $99/year). Either bypass:

```bash
xattr -dr com.apple.quarantine /Applications/OpenRevelare.app
```

Or try opening once (it will be blocked), then **System Settings → Privacy & Security → Open Anyway**.

> Don't follow the old "right-click → Open" advice: macOS 15 (Sequoia) removed that entry.

**Beta notes**: the macOS build is produced in CI; the author has no Mac hardware and it has never been run on a real machine. Known gaps: `SystemMemory` has no macOS implementation (decode concurrency is a conservative fixed value) and there is no Adobe DNG Converter fallback. **Issues welcome**, especially RAW import reports.

</details>

<details>
<summary><b>Linux</b> — running the AppImage</summary>

The AppImage is a single green executable — no installation. Make it executable and double-click, or run from a terminal:

```bash
chmod +x OpenRevelare-*.AppImage && ./OpenRevelare-*.AppImage
```

(In a file manager: right-click → Properties → Permissions → check "Executable".)

FUSE is bundled; no libfuse2 needed. If it still won't start, run with `--appimage-extract-and-run`.

</details>

## Quick start

1. **Import** — drag your copied/scanned negatives into the window; enter roll info (stock, camera, processing date)
2. **Roll calibration** — calibrate the current frame: auto-calibration estimates base, white balance, grade, etc.; fix anything by hand
3. **Apply to the roll** — sync these physical parameters to the whole roll
4. **Frame edit** — per-frame aesthetic edits: colour temperature, exposure, contrast, saturation, curves
5. **Export** — 8/16-bit TIFF or JPEG, with an optional embedded ICC profile

There is no Save button — everything is written automatically to a `.ncproj` next to your images.

## Features

### Imaging

- **Six-step density-domain inversion** — base `t_base`, white balance `wb_high` / `wb_offset`, scan exposure, `d_max`, grade, chroma compensation; all adjustable, each with a clear physical meaning
- **Narrowband source decoupling (Path A)** — for LED / fluorescent light-box copying, inter-channel crosstalk is solved out with a 3×3 matrix from a set of R/G/B calibration frames. Method from [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)
- **Auto-calibration** — estimates base, sprocket threshold, dark-end valley, `d_max`, highlight white balance from the roll
- **Smart white balance** — DeepWB neural network estimates the white point in one click (model separately licensed, [see below](#smart-white-balance-model--separate-licence-read-this))
- **Pre-inversion corrections** — LCC flat-field, lens distortion, vignetting, sprocket mask; all done in the linear-light domain
- **Stage 2 adjustments** — exposure / levels / contrast / shadows-highlights / PCHIP curves / saturation

### Workflow

- **Roll-based management** — importing creates a roll; the library wall uses a contact sheet as cover art, filterable by format, stock, etc.
- **No "Save" action** — changes are written automatically. `.ncproj` sits next to the source images and travels with them
- **Roll sync** — virtual copies, whole-roll or per-frame parameter sync
- **Format presets** — 135 full frame (with borders) / half frame / XPan / 645 / 6×6 / 6×7 / 6×9 / 6×12
- **80-step undo/redo** (roll snapshots, consecutive tweaks merged)
- **Lab-style full-roll contact sheets** with a roll-identifier strip at the bottom

### Input & output

| | |
|---|---|
| **RAW input** | DNG / NEF / CR2 / CR3 / ARW / RAF / RW2 / ORF / PEF / IIQ etc. (LibRaw) |
| **Other input** | TIFF / JPEG / PNG |
| **Export** | 8/16-bit TIFF, JPEG, with an embedded sRGB or Adobe RGB ICC profile |

## How it works

### Why the density domain

A colour negative's signal is density by nature. Taking the negative log of transmittance — `D = -log10(T)` — gives log density, the domain of the Cineon film-scanning standard. In this domain the R/G/B channels behave linearly and predictably: the mask is close to a constant offset (one subtraction removes it), and white balance and inversion are linear operations. In a non-linear domain those operations interfere with each other and you can only tune by feel — that is where the "dark magic" comes from, and why OpenRevelare works in density.

### Core formulas

The path from sample to positive, in a few lines:

**Base normalisation** — divide each channel by the sampled base transmittance; the orange mask is gone in one step:

$$T_\text{norm} = T / T_\text{base}$$

**Into density** — negative log of transmittance (clamped to avoid overflow):

$$D = -\log_{10}\!\bigl(\max(T_\text{norm},\ 10^{-D_\text{max}})\bigr)$$

**Density-domain white balance** — a shadow-side additive term plus a highlight-side multiplicative term (the Negadoctor two-end model):

$$D_\text{corr}[c] = D[c] \times w_\text{high}[c] + w_\text{offset}[c]$$

**Inversion** — luminance and chroma separated and controlled independently (grade = contrast, chroma_grade = chroma recovery):

$$D_\text{adj} = \text{pivot} + (D_\text{mean} - \text{pivot}) \times \text{grade} + D_\text{chroma} \times \frac{\text{chroma\_grade}}{\text{chroma\_amp}} - D_\text{max}$$

$$T_\text{pos} = 10^{D_\text{adj}}$$

The full derivation of every parameter lives in the in-app **Help → Theory**.

### Two stages: FilmBase and SceneBase

|  | **FilmBase · physical restoration** | **SceneBase · aesthetic edits** |
|---|---|---|
| Describes | The roll's objective physical properties: base colour & density, maximum density, channel balance, inversion contrast, chroma-recovery coefficient | Colour-temperature preference, exposure, contrast style, final saturation |
| Nature | Not a taste decision — a measurement. Shared by the whole roll | The same negative can have completely different settings, per frame |
| Changes | The *inputs* of the inversion equation — recompute the restoration | The *output* of the inversion equation — adjust on top of the restoration |

The point of separating the two: get the physical restoration right once and the whole roll shares it; everything you do later cannot corrupt the physics underneath. Here "physical restoration" means the mask-removal result computed only from the roll's own information (base, maximum density, channel balance), with no subjective adjustment.

### The per-frame pipeline

1. **Back to light** — camera-copied RAW is decoded by LibRaw with all in-camera beautification disabled, to linear; display-gamma scans are linearised in one click. Both inputs meet at the same linear-light starting line
2. **Linear-domain corrections** — distortion, LCC flat-field, vignetting, and sprocket/light-panel masks. Optical defects are only physically correct to fix in the "light" state
3. **Light-source decoupling** (optional) — white light passes through, or RGB three-colour separation precisely measures and removes inter-dye crosstalk
4. **Mask removal, into density** — sampled base transmittance cancels the mask; the signal moves to log density, built on the Cineon standard
5. **Density-domain white balance** — shadows and highlights corrected separately, shadows first. That order is exactly what separates physical computation from eyeballing curves
6. **Inversion** — restores the contrast and saturation the negative deliberately saved for the paper
7. **Output** — a physically correct positive, ready for a grading suite or for direct export after Stage 2

The in-app **Help → Guide / Theory** has the full usage instructions and derivations.

## Smart white balance model — separate licence, please read

"Smart white balance" uses the Deep White-Balance Editing (CVPR 2020) network weights `models/net_awb.onnx`, distributed with the repo and the installers — but:

> [!IMPORTANT]
> **This file is NOT covered by the project's GPL-3.0 grant.**
> It is distributed under the original author's **CC BY-NC-SA 4.0** (Attribution — NonCommercial — ShareAlike).

OpenRevelare is free, unsold, no subscription or in-app purchases, so redistribution itself is non-commercial and consistent with the NC clause. But the **right you get from GPL-3.0 to redistribute commercially does not extend to this file** — for commercial use, delete the `models/` directory first. The app still builds and runs; only "smart white balance" reports a missing model. Manual white balance, auto highlight white balance and Path A decoupling do not depend on it.

Details in [models/README.md](models/README.md) and item 13 of [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt). The authors require citation of their paper.

## Where your data lives

| Content | Windows | Linux / macOS | Customisable |
|---|---|---|---|
| Settings, roll index | `%APPDATA%\OpenRevelare` | `OpenRevelare/` under `$XDG_CONFIG_HOME` (default `~/.config`) | fixed |
| Contact-sheet cache | `%LOCALAPPDATA%\OpenRevelare\sheets` | `OpenRevelare/sheets/` under `$XDG_CACHE_HOME` (default `~/.cache`) | ✅ folder + cap (default 1 GB) |
| Linear DNG decode cache | `.revelare-cache/` next to the sources by default | same | ✅ folder + cap (default 5 GB), per session |
| Project `.ncproj` | next to the source images | same | wherever your photos live |

The DNG cache sits next to the sources rather than on the system drive because a single 60 MP frame expands to ~349 MB. Both caches can be moved and capped, and the preferences show their current usage. Uninstalling touches none of these.

macOS and Linux share the XDG paths instead of using `~/Library/Application Support` — one code path everywhere.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/Toshihiko-Lin/Open-Revelare.git
cd Open-Revelare
dotnet build -c Release
dotnet run --project src/OpenRevelare.Gui
```

Command-line front-end (no GUI, same Core):

```bash
dotnet run --project src/OpenRevelare.Cli -- -i neg.tiff -o pos.tiff --grade 1.65 --d-max 2.0
dotnet run --project src/OpenRevelare.Cli -- --help
```

### Packaging

```bash
# Windows — requires Inno Setup 6
dotnet publish src/OpenRevelare.Gui -c Release -r win-x64 --self-contained true -o publish/win-x64
ISCC.exe open-revelare.iss                     # → installer/OpenRevelare-{version}-setup.exe

# Linux — run on Linux (script downloads appimagetool automatically)
./packaging/linux/build-appimage.sh            # → installer/OpenRevelare-{version}-x86_64.AppImage

# macOS — run on macOS
./packaging/macos/bundle-libraw.sh             # build LibRaw 0.21.4 (no macOS runtime package on NuGet)
./packaging/macos/build-app.sh --dmg           # → installer/OpenRevelare-{version}-{arch}.dmg
```

`dotnet publish -r linux-x64` / `-r osx-arm64` also works on Windows, but `appimagetool`, `codesign` and `hdiutil` must run on their own OS. All three platform artifacts are built automatically by [`.github/workflows/release.yml`](.github/workflows/release.yml) on tag.

> **macOS must pin LibRaw to 0.21.x**: Sdcb.LibRaw 0.21.1.7 marshals against the 0.21 `libraw_data_t` layout; the 0.22 shipped by brew adds fields and shifts every offset. `bundle-libraw.sh` therefore builds 0.21.4 from source.

## Licence

The project code is **GPL-3.0-only**, see [LICENSE](LICENSE).

**Exception**: `models/net_awb.onnx` is a third-party asset under CC BY-NC-SA 4.0, outside the GPL-3.0 grant above — see [models/README.md](models/README.md).

Third-party components shipped with the binaries and their licences are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) — LibRaw is LGPL-2.1; keeping that notice is not optional.

## Credits

- [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple) (MIT) — the narrowband RGB decoupling (Path A) approach
- [DiVERE](https://github.com/flipswitchingmonkey/DiVERE) (MIT) — reference for the density-domain colour model
- darktable's `negadoctor` module — its model `D_corr = D × wb_high + wb_offset`

Feedback and bugs: open an [issue](https://github.com/Toshihiko-Lin/Open-Revelare/issues) with OS version, camera or scanner model, input format and error message. Please don't upload original photos containing private content.

## Roadmap & known limitations

**Planned**

- Independent ECN-2 calibration data (ColorChecker 24-based; currently approximated from the C-41 baseline)
- Real-hardware macOS validation and a `SystemMemory` implementation (the macOS build has never been run on a real machine; decode concurrency is a conservative fixed value)

**Known limitations**

- No per-roll colour-chart calibration: for strict colour-accurate work (heritage copying, commercial archiving, research) use DiVERE
- 8-bit TIFF input may show slight banding in shadows; export 16-bit from the scanner when possible
- Lenses missing from the Lensfun database must be specified manually

## Donate

Revelare started as a small self-use tool. Development cost real money, so most features were kept open while a few advanced copying workflows were priced to cover some of it — and people actually paid, which is much appreciated.

After listening to feedback, it was rewritten, three platforms were added, and once it felt complete enough it was open-sourced — in the hope that it helps other players and developers. The film community is small, and tools are never too many. If it's been useful, a coffee is always welcome.

<p align="center">
  <img src="docs/assets/donate-wechat.png" width="220" alt="WeChat Pay">
</p>
