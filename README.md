# OpenRevelare

彩色负片去色罩工具 —— 把翻拍或扫描得到的彩色负片，在密度域里还原成正片。
C# / .NET 8 + Avalonia，纯 CPU，Windows / Linux / macOS。

本地优先、非破坏性：源文件永不改动，调整参数存在随片放置的 `.ncproj` 工程里；
不需要联网，不需要账号。

---

## 能做什么

**成像**

- 密度域六步反转：片基 t_base、白平衡 wb_high / wb_offset、扫描曝光、d_max、
  gamma、色度补偿，全部可调
- **窄带光源解耦（Path A）**：用 LED / 荧光灯箱翻拍时，三通道之间的串扰可以靠一组
  R/G/B 标定帧解算出 3×3 矩阵消掉。做法源自
  [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)
- **自动标定**：从整卷估片基、齿孔阈值、暗端谷底、d_max、亮部白平衡
- 预反转校正：LCC 平场、镜头畸变、暗角、齿孔遮罩
- Stage 2 调整：曝光 / 色阶 / 对比度 / 高光阴影 / PCHIP 曲线 / 饱和度

**工作流**

- 按卷管理：导入即建卷，图库卷墙用每卷的印样当封面
- 无「保存」动作——改完自动落盘；`.ncproj` 与源图像放在一起，拷盘换机跟着片子走
- 虚拟副本、整卷/勾选帧同步标定与场景
- 冲印店风格的整版印样，底部自带卷标识条

**输入输出**

- RAW：DNG / NEF / CR2 / CR3 / ARW / RAF / RW2 / ORF / PEF / IIQ 等（LibRaw）
- 另有 TIFF / JPEG / PNG
- 导出 8/16-bit TIFF、JPEG，可嵌 sRGB / Adobe RGB ICC profile

---

## 下载

发行包在 [Releases](https://github.com/Toshihiko-Lin/Open-Revelare/releases)。
自带 .NET 运行时，不需要另外安装任何东西。

| 平台 | 包 | 成熟度 |
|---|---|---|
| Windows 10/11 x64 | `setup.exe` | **正式** —— 开发机就是它，每次发版实测 |
| Linux x86_64 | `.AppImage` | **Beta** —— 需 glibc ≥ 2.35（Ubuntu 22.04 / Debian 12 及以上） |
| macOS Apple Silicon | `.dmg` | **Beta，从未在真机上跑过** —— 见下 |

### macOS 首次打开

本软件**未经 Apple 公证**，系统会提示「已损坏」或「无法验证开发者」——不是文件损坏，
是没有花 99 美元/年买开发者计划。拖进「应用程序」后任选一种：

```bash
xattr -dr com.apple.quarantine /Applications/OpenRevelare.app
```

或者双击一次（会被拦），然后到 **系统设置 → 隐私与安全性 → 仍要打开**。

> 别照着网上的「右键 → 打开」做：macOS 15 (Sequoia) 起 Apple 取消了那个入口。

macOS 版没有任何一次真机运行记录，已知短板：`SystemMemory` 无 mac 实现所以解码并发是
保守固定档；无 Adobe DNG Converter 回退路径。**欢迎开 issue 回报**，尤其是 RAW 能不能导入。

---

## 智能白平衡模型 —— 单独授权，请读一下

「智能白平衡」用到 Deep White-Balance Editing (CVPR 2020) 的网络权重
`models/net_awb.onnx`。它随仓库和发行包一起分发，但

> ⚠ **这个文件不在本项目 GPL-3.0 授权的范围内。**
> 它按原作者的 **CC BY-NC-SA 4.0**（署名 — 非商业 — 相同方式共享）分发。

OpenRevelare 免费、不销售、无订阅无内购，分发本身不以商业利益为目的，因此符合 NC 条款。
但**你从 GPL-3.0 拿到的"可以商业再分发"这项权利不适用于这个文件** —— 要商用请先删掉
`models/` 目录（程序照常构建运行，只有「智能白平衡」一个功能会提示模型未找到；手动白平衡、
自动亮部白平衡、Path A 解耦都不依赖它）。

细节见 [models/README.md](models/README.md) 与
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) 第 13 条。作者要求引用其论文。

---

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

**macOS 的 LibRaw 必须锁 0.21.x**：Sdcb.LibRaw 0.21.1.7 按 0.21 的 `libraw_data_t` 布局
marshal，brew 上的 0.22 加过字段，偏移全错。`bundle-libraw.sh` 因此锁 0.21.4 源码编译。

---

## 数据放在哪

| 内容 | Windows | Linux / macOS | 可否自定义 |
|---|---|---|---|
| 设置、卷目录索引 | `%APPDATA%\OpenRevelare` | `$XDG_CONFIG_HOME` ?? `~/.config` → `/OpenRevelare` | 固定 |
| 卷封面印样缓存 | `%LOCALAPPDATA%\OpenRevelare\sheets` | `$XDG_CACHE_HOME` ?? `~/.cache` → `/OpenRevelare/sheets` | ✅ 目录 + 上限（默认 1 GB） |
| 线性 DNG 解码缓存 | 默认跟着源文件放 `.revelare-cache/` | 同左 | ✅ 目录 + 上限（默认 5 GB），会话级 |
| 工程 `.ncproj` | 随片放在源图像文件夹 | 同左 | 由你放照片的位置决定 |

DNG 缓存默认贴着源文件而不是系统盘，是因为 60 MP 一帧转出来约 349 MB。两个缓存都能改
位置、都有上限、都在偏好设置里显示当前占用。卸载不碰以上任何目录。

macOS 与 Linux 共用 XDG 路径，没有改用 `~/Library/Application Support` —— 全线一条代码路径。

---

## 许可证

本项目的代码以 **GPL-3.0-only** 授权，见 [LICENSE](LICENSE)。

**例外**：`models/net_awb.onnx` 是第三方资产，按 CC BY-NC-SA 4.0 单独授权，
不在上述 GPL-3.0 范围内。见 [models/README.md](models/README.md)。

随二进制分发的第三方组件及其许可见
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) —— 其中 LibRaw 走 LGPL-2.1，
带上这份声明不是可选项。

## 致谢

- [LightSourceDecouple](https://github.com/karasuyasabou/LightSourceDecouple)（MIT）—— 窄带 RGB 解耦（Path A）的做法出自这里
- [DiVERE](https://github.com/flipswitchingmonkey/DiVERE)（MIT）—— 密度域色彩模型的参照
- darktable 的 `negadoctor` 模块 —— 只参照其数学模型 `D_corr = D × wb_high + wb_offset`，未复制任何代码

程序内 **帮助 → 操作指引 / 技术原理** 有完整的使用说明与物理推导。

---

**Note for non-Chinese speakers:** the application UI, in-app documentation and this README are
currently Chinese-only. The code and comments are mixed English/Chinese. Translations welcome.
