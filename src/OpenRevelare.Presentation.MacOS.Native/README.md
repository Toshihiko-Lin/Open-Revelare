# OpenRevelare.Presentation.MacOS.Native

macOS 原生 presenter 垫片：一层 Objective-C++，对外只有 6 个 C ABI 导出（`orwm_*`），
是 `OpenRevelare.Presentation.Win32.Native`（`orwp_*`）在 macOS 上的对应物。

## 它做什么、不做什么

- **做**：在 Avalonia 的 `NativeControlHost` 给出的 `NSView` 里挂一个 `CAMetalLayer`，
  `pixelFormat = RGBA16Float`、`colorspace = kCGColorSpaceExtendedLinearSRGB`，按托管侧契约
  决定是否 `wantsExtendedDynamicRangeContent`；`orwm_present` 把 RGBA-half 行原样
  `replaceRegion` 进 drawable 并 present。
- **不做**：任何色彩数学。没有 shader、没有 TRC、没有 gamut map、没有 ICC。最后一跳归
  ColorSync（D-006 / I3 / §11.3）。
- `orwm_probe` 读 `NSScreen` 的 EDR 三个值、`backingScaleFactor`、`colorSpace` 与显示器名。
  托管侧据此建 `DisplayContract`（D-026）。

## 状态：**尚未在真机上编译**

本目录的源码是按 AppKit / Metal / Core Animation 文档写的，第一次真实编译要在 Mac 上跑
`packaging/macos/build-macos-presenter.sh`。托管侧对它的全部依赖——结果码、结构体布局、
create/present 的合同——已由两边的测试钉住（`MacOSNativeAbiTests` 的 `Marshal.SizeOf` ↔ 头文件
的 `static_assert`），所以第一次编译应当是一次编译，而不是一次设计。

真机上要验的（对应 D-012 与 D-026）：

1. `wantsExtendedDynamicRangeContent = YES` 时 SDR 白（canonical 1.0）的亮度是否与 `NO` 时一致；
2. 负值与 `>1` 分量是否原样到达面板（送 0.18 / 1.0 / P3 色块与 `(-0.1, 1.5, 0.2)`）；
3. 外接 Adobe RGB 显示器上 ColorSync 的处理是否符合"每个 surface 恰好一次 monitor transform"；
4. `maximumExtendedDynamicRangeColorComponentValue` 随亮度滑块变化时，托管侧的 `Refresh()` 是否
   触发新 revision（`MacOSDisplayEnvironmentTests.Headroom_change_publishes_a_new_revision`
   证明了托管半边；真机要证明 AppKit 通知真的到达）。

## 构建

```bash
./packaging/macos/build-macos-presenter.sh
```

只需 Xcode Command Line Tools。产物 `native/osx-<arch>/libOpenRevelare.Presentation.MacOS.Native.dylib`，
`build-app.sh` 会把它与 libraw 一起拷进 `Contents/MacOS`。
