#!/usr/bin/env bash
# 编 macOS 原生 presenter —— src/OpenRevelare.Presentation.MacOS.Native 那份 Obj-C++ C ABI 垫片，
# 是 Windows 那份 build-win32-presenter.ps1 的对应物。
#
#   ./packaging/macos/build-macos-presenter.sh            # 当前架构，产物进 native/osx-<arch>/
#
# 只需要 Xcode Command Line Tools（clang、AppKit/Metal/QuartzCore 框架都在 SDK 里），不需要
# Xcode 工程。产物：native/osx-{arm64,x64}/libOpenRevelare.Presentation.MacOS.Native.dylib，
# install_name 为 @loader_path/<名字>，ad-hoc 重签（与 bundle-libraw.sh 同一套约束：改过
# install_name 的 dylib 签名失效，Apple Silicon 上直接拒载）。
#
# 硬约束：
#   1. **文件名必须是 libOpenRevelare.Presentation.MacOS.Native.dylib** —— 托管侧的
#      MacOSNativeLibraryResolver.LibraryName 写死了它，且只在程序目录与 runtimes/osx-*/native
#      下找，不做裸名搜索。
#   2. **导出必须是 6 个 orwm_* 符号**，本脚本用 nm 校验；少一个就是 EntryPointNotFoundException。
#   3. **-fvisibility=hidden**：除 ORWM_API 标记的以外一律不导出，与 Win32 垫片的 dllexport 对等。
#   4. 头文件里的 static_assert 与托管测试的 Marshal.SizeOf 互为对方的钉子；改结构体先改两边。
#
# 与 build-app.sh 的衔接：publish 之后把 native/$RID 下这个 dylib 与 libraw 一起拷进
# Contents/MacOS（build-app.sh 已按 native/$RID/*.dylib 处理，无需改动）。
set -euo pipefail

[ "$(uname -s)" = "Darwin" ] || { echo "错误：本脚本只能在 macOS 上跑（要 clang + AppKit/Metal SDK）"; exit 1; }
command -v clang++ >/dev/null || { echo "错误：找不到 clang++，先装 Xcode Command Line Tools：xcode-select --install"; exit 1; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$ROOT/src/OpenRevelare.Presentation.MacOS.Native"
NAME="libOpenRevelare.Presentation.MacOS.Native.dylib"

case "$(uname -m)" in
  arm64)  RID="osx-arm64"; ARCH="arm64" ;;
  x86_64) RID="osx-x64";   ARCH="x86_64" ;;
  *) echo "错误：不支持的架构 $(uname -m)"; exit 1 ;;
esac
OUT_DIR="$ROOT/native/$RID"
OUT="$OUT_DIR/$NAME"
mkdir -p "$OUT_DIR"

echo "== 编译 $NAME ($ARCH) =="
clang++ \
  -std=c++17 -ObjC++ -fobjc-arc \
  -O2 -fvisibility=hidden -fvisibility-inlines-hidden \
  -mmacosx-version-min=11.0 \
  -arch "$ARCH" \
  -dynamiclib \
  -install_name "@loader_path/$NAME" \
  -framework AppKit -framework Metal -framework QuartzCore -framework CoreGraphics \
  -o "$OUT" \
  "$SRC_DIR/OpenRevelarePresentationMacOS.mm"

echo "== 校验导出 =="
for sym in orwm_probe orwm_create orwm_resize orwm_present orwm_query_diagnostics orwm_destroy; do
  nm -gU "$OUT" | grep -q " _${sym}$" || { echo "错误：缺少导出 $sym"; exit 1; }
done
# 不该导出的东西一个都不能漏出去：除 orwm_* 与 Obj-C 运行时元数据外，nm 应当是空的。
if nm -gU "$OUT" | grep -v " _orwm_" | grep -v " _OBJC_" | grep -q " T "; then
  echo "错误：有 orwm_* 之外的符号被导出（-fvisibility=hidden 未生效？）"; nm -gU "$OUT"; exit 1
fi

echo "== ad-hoc 重签 =="
codesign --force --sign - "$OUT"
codesign --verify --verbose=2 "$OUT"

echo "== 产物 =="
ls -la "$OUT"
otool -L "$OUT" | head -8
echo "完成：$OUT"
