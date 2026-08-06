#!/usr/bin/env bash
# 备好 macOS 的 LibRaw 原生库 —— NuGet 上**没有** Sdcb.LibRaw 的 macOS runtime 包
# （只有 win64 / win32 / linux64），mac 这份得自己编。
#
#   ./packaging/macos/bundle-libraw.sh                          # 源码编译 0.21.4（默认）
#   ./packaging/macos/bundle-libraw.sh --libraw /path/to.dylib  # 用现成的
#
# 为什么是源码编译而不是 brew：**brew 的 libraw 已经是 0.22.x**，而 Sdcb 0.21.1.7 按
# 0.21 的 libraw_data_t 布局 marshal（`RawDecode.cs` 还从句柄偏移 8 读 sizes），0.22 的
# 结构体加过字段，偏移全错。锁死 0.21.4 也顺带消掉了「brew 哪天又升一版」这个隐患。
#
# 另外三条硬约束，别改：
#   1. **文件名必须是 libraw.23.dylib**。Sdcb 的加载器在 mac 上写死
#      `NativeLibrary.Load("libraw.23.dylib", assembly, …)`，assembly 重载会搜程序目录，
#      所以放进 .app 的 Contents/MacOS 就能被找到；名字对不上就是 DllNotFoundException。
#   2. **装进去的是线程安全的 libraw_r，改名而来**。我们并发解码整卷，非 _r 的那个
#      不保证安全。mac 这条路径要的名字恰好没有 _r，改名是刻意的。
#   3. 编译选项对齐 Windows 那份预编译库：**带 jpeg / lcms**（否则 lossy DNG 打不开，
#      与 Windows 行为不一致）、**不带 OpenMP**（Sdcb 的 win64 包也没有 libgomp，
#      而且我们是按帧并行，不靠库内并行）。
#
# 产物：native/osx-{arm64,x64}/libraw.23.dylib + 依赖，install_name 全改 @loader_path，
# 逐个 ad-hoc 重签（改过 install_name 的 dylib 签名失效，Apple Silicon 上直接拒载）。
#
# **只为当前架构出包**：交叉编译要一整套 x86_64 的 brew 依赖，不值。Intel 包请在 Intel
# 机器（或 CI 的 Intel runner）上跑本脚本。
set -euo pipefail

[ "$(uname -s)" = "Darwin" ] || { echo "错误：本脚本只能在 macOS 上跑（要 otool / install_name_tool / codesign）"; exit 1; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LIBRAW_VER="0.21.4"
LIBRAW_SHA="6be43f19397e43214ff56aab056bf3ff4925ca14012ce5a1538a172406a09e63"
SRC=""

while [ $# -gt 0 ]; do
  case "$1" in
    --libraw) SRC="$2"; shift 2 ;;
    *) echo "未知参数：$1"; exit 1 ;;
  esac
done

ARCH="$(uname -m)"
case "$ARCH" in
  arm64)  RID="osx-arm64" ;;
  x86_64) RID="osx-x64"   ;;
  *) echo "错误：不认识的架构 $ARCH"; exit 1 ;;
esac
DEST="$ROOT/native/$RID"

TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

# ── 源码编译 ───────────────────────────────────────────────────────────────
if [ -z "$SRC" ]; then
  JPEG="$(brew --prefix jpeg-turbo 2>/dev/null || true)"
  LCMS="$(brew --prefix little-cms2 2>/dev/null || true)"
  if [ -z "$JPEG" ] || [ -z "$LCMS" ]; then
    echo "错误：缺少编译依赖。先跑：brew install jpeg-turbo little-cms2"
    exit 1
  fi

  echo "==> 下载 LibRaw $LIBRAW_VER"
  curl -fL --retry 3 -o "$TMP/libraw.tar.gz" \
    "https://www.libraw.org/data/LibRaw-${LIBRAW_VER}.tar.gz"
  echo "$LIBRAW_SHA  $TMP/libraw.tar.gz" | shasum -a 256 -c - \
    || { echo "错误：校验和不符，拒绝继续。"; exit 1; }

  tar xzf "$TMP/libraw.tar.gz" -C "$TMP"
  cd "$TMP/LibRaw-${LIBRAW_VER}"

  # 花括号不能省：macOS 的 bash 3.2 会把紧随其后的中文字节读进变量名（见 build-app.sh 顶部）
  echo "==> configure + make（${ARCH}）"
  ./configure --prefix="$TMP/out" \
    --disable-examples --disable-jasper --disable-openmp \
    --enable-jpeg --enable-lcms \
    CPPFLAGS="-I$JPEG/include -I$LCMS/include" \
    LDFLAGS="-L$JPEG/lib -L$LCMS/lib" \
    > "$TMP/configure.log" 2>&1 || { tail -30 "$TMP/configure.log"; exit 1; }
  make -j"$(sysctl -n hw.ncpu)" > "$TMP/make.log" 2>&1 || { tail -30 "$TMP/make.log"; exit 1; }
  make install > /dev/null

  SRC="$TMP/out/lib/libraw_r.23.dylib"
  cd "$ROOT"
fi

[ -f "$SRC" ] || { echo "错误：找不到 $SRC"; exit 1; }

# 架构闸门：别把 x86_64 的库塞进 arm64 的包
if ! lipo -archs "$SRC" | tr ' ' '\n' | grep -qx "$ARCH"; then
  echo "错误：$SRC 的架构是 [$(lipo -archs "$SRC")]，与本机 $ARCH 不符。"
  exit 1
fi

echo "==> 源：$SRC"
echo "==> 目标：$DEST/libraw.23.dylib"

rm -rf "$DEST"; mkdir -p "$DEST"
cp "$SRC" "$DEST/libraw.23.dylib"
chmod u+w "$DEST/libraw.23.dylib"
install_name_tool -id "@loader_path/libraw.23.dylib" "$DEST/libraw.23.dylib"

# ── 递归收依赖，install_name 改成 @loader_path ──────────────────────────────
# 用下标推进而不是切片弹出：macOS 自带的是 bash 3.2，`set -u` 下对空数组做
# ${arr[@]:1} 会报 unbound variable。
queue=("$DEST/libraw.23.dylib")
i=0
while [ $i -lt ${#queue[@]} ]; do
  cur="${queue[$i]}"; i=$((i + 1))
  while read -r dep; do
    case "$dep" in
      ""|/usr/lib/*|/System/*|@*) continue ;;   # 系统库与已改好的都跳过
    esac
    base="$(basename "$dep")"
    if [ ! -f "$DEST/$base" ]; then
      echo "    + 依赖 $base"
      cp "$dep" "$DEST/$base"
      chmod u+w "$DEST/$base"
      install_name_tool -id "@loader_path/$base" "$DEST/$base"
      queue+=("$DEST/$base")
    fi
    install_name_tool -change "$dep" "@loader_path/$base" "$cur"
  done < <(otool -L "$cur" | tail -n +2 | awk '{print $1}')
done

# ── 重签 + 自检 ────────────────────────────────────────────────────────────
for f in "$DEST"/*.dylib; do codesign --force --sign - "$f"; done

bad=0
for f in "$DEST"/*.dylib; do
  while read -r dep; do
    case "$dep" in
      ""|/usr/lib/*|/System/*|@loader_path/*) ;;
      *) echo "自检失败：$(basename "$f") 仍引用机外路径 $dep"; bad=1 ;;
    esac
  done < <(otool -L "$f" | tail -n +2 | awk '{print $1}')
done
[ "$bad" = 0 ] || exit 1

echo
echo "==> 完成，$DEST 内容："
ls -la "$DEST"
echo "（这些文件不进 git —— .gitignore 已排除 /native/；换机重跑本脚本即可）"
