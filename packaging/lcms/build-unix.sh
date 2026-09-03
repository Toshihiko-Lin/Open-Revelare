#!/usr/bin/env bash
# Build the pinned official Little CMS shared library for the current Linux/macOS RID.
set -euo pipefail

LCMS_VERSION="2.19.1"
SOURCE_URL="https://github.com/mm2/Little-CMS/releases/download/lcms2.19.1/lcms2-2.19.1.tar.gz"
SOURCE_SHA256="BFC54F7BAB59FBC921012014A8032E4CBA4ABD46DB47D46B76416A8C0B2815C8"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
[ -f "$ROOT/THIRD_PARTY_NOTICES.txt" ] || {
  echo "错误：无法确定 OpenRevelare 仓库根目录：$ROOT" >&2; exit 1; }

OS="$(uname -s)"
MACHINE="$(uname -m)"
case "$OS:$MACHINE" in
  Linux:x86_64) RID="linux-x64" ;;
  Linux:aarch64|Linux:arm64) RID="linux-arm64" ;;
  Darwin:arm64) RID="osx-arm64" ;;
  Darwin:x86_64) RID="osx-x64" ;;
  *) echo "错误：不支持的 Little CMS 构建主机：$OS $MACHINE" >&2; exit 1 ;;
esac

DEST="$ROOT/native/$RID"
mkdir -p "$DEST"

TMP_BASE="${TMPDIR:-/tmp}"
WORK="$(mktemp -d "$TMP_BASE/openrevelare-lcms.XXXXXX")"
cleanup() {
  case "$WORK" in
    "$TMP_BASE"/openrevelare-lcms.*) rm -rf -- "$WORK" ;;
    *) echo "警告：拒绝清理意外的临时目录：$WORK" >&2 ;;
  esac
}
on_signal() {
  trap - HUP INT TERM
  exit 130
}
trap cleanup EXIT
trap on_signal HUP INT TERM

hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print toupper($1)}'
  else
    shasum -a 256 "$1" | awk '{print toupper($1)}'
  fi
}

ARCHIVE="$WORK/lcms2-$LCMS_VERSION.tar.gz"
echo "==> 下载 Little CMS $LCMS_VERSION"
curl --fail --location --retry 3 --retry-delay 2 --output "$ARCHIVE" "$SOURCE_URL"

ACTUAL_SOURCE_HASH="$(hash_file "$ARCHIVE")"
[ "$ACTUAL_SOURCE_HASH" = "$SOURCE_SHA256" ] || {
  echo "错误：Little CMS 源码 SHA-256 不匹配" >&2
  echo "      期望：$SOURCE_SHA256" >&2
  echo "      实际：$ACTUAL_SOURCE_HASH" >&2
  exit 1
}

tar -xzf "$ARCHIVE" -C "$WORK"
SOURCE="$WORK/lcms2-$LCMS_VERSION"
[ -x "$SOURCE/configure" ] || { echo "错误：官方 configure 脚本缺失" >&2; exit 1; }

PREFIX="$WORK/install"
JOBS=""
if [ "$OS" = "Darwin" ]; then JOBS="$(sysctl -n hw.ncpu)"; else JOBS="$(getconf _NPROCESSORS_ONLN)"; fi
case "$JOBS" in ''|*[!0-9]*) JOBS=1 ;; esac

echo "==> 使用官方 autotools 构建 shared lcms2（$RID）"
cd "$SOURCE"
./configure \
  --prefix="$PREFIX" \
  --enable-shared \
  --disable-static \
  --disable-dependency-tracking \
  --without-jpeg \
  --without-tiff
make -j "$JOBS"
make install

if [ "$OS" = "Darwin" ]; then
  INSTALLED="$PREFIX/lib/liblcms2.2.dylib"
  CANONICAL="liblcms2.2.dylib"
  ALIAS="liblcms2.dylib"
else
  INSTALLED="$PREFIX/lib/liblcms2.so.2"
  CANONICAL="liblcms2.so.2"
  ALIAS="liblcms2.so"
fi
[ -e "$INSTALLED" ] || { echo "错误：构建完成但找不到 $INSTALLED" >&2; exit 1; }

# Overwrite only the two exact app-owned library names. Never clear native/<rid>:
# LibRaw and its other dependencies share that directory.
cp -fL "$INSTALLED" "$DEST/$CANONICAL"
chmod 0755 "$DEST/$CANONICAL"
if [ "$OS" = "Darwin" ]; then
  install_name_tool -id "@loader_path/liblcms2.2.dylib" "$DEST/$CANONICAL"
  # install_name_tool invalidates the linker-provided arm64 ad-hoc signature.
  # Re-sign now so tests can load this dylib before the final app bundle is signed.
  codesign --force --sign - "$DEST/$CANONICAL"
fi
cp -f "$DEST/$CANONICAL" "$DEST/$ALIAS"
if [ "$OS" = "Darwin" ]; then
  codesign --force --sign - "$DEST/$ALIAS"
  codesign --verify "$DEST/$CANONICAL"
  codesign --verify "$DEST/$ALIAS"
fi

# ── glibc 符号下限（Linux only）────────────────────────────────────────────────
#
# 在本脚本出现之前，Linux 产物里没有任何「在 CI runner 上编出来的」原生库：.NET 运行时是
# 微软构建的，LibRaw 来自 NuGet 预编译包。现在 lcms2 是现编的，于是产物能跑在哪些发行版上
# 变成了跟着 runner 镜像走 —— 而 README 的支持矩阵写着一个具体数字。两者之间原本没有任何
# 东西守着，镜像哪天从 22.04 滚到 24.04 再滚到 26.04，承诺会静默作废。
#
# 这条断言不替谁做决定，只把「产物到底要求多新的 glibc」从未知变成 CI 里的一个事实。
# 真红了，三个方向都在 PR4-HANDOFF §8.4：钉镜像 / 调低使用面 / 改 README。
GLIBC_FLOOR="2.35"      # 与 README.md 与 README.en.md 的支持矩阵同源，改一处必须改三处
if [ "$OS" = "Linux" ]; then
  echo "==> 校验 glibc 符号下限（承诺 ≤ $GLIBC_FLOOR）"
  # 能走到这里说明 autotools 刚刚成功链接过一次，binutils 必然在场，所以硬失败是安全的：
  # 找不到工具意味着环境不对，而不是「这台机器不方便检查」。
  if command -v readelf >/dev/null 2>&1; then
    SYMBOL_DUMP="readelf --version-info --wide"
  elif command -v objdump >/dev/null 2>&1; then
    SYMBOL_DUMP="objdump -T"
  else
    echo "错误：找不到 readelf / objdump，无法校验 glibc 下限" >&2
    echo "      安装 binutils 后重跑；不要跳过这一步" >&2
    exit 1
  fi

  # 版本号按 major/minor/patch 归一成可比整数：2.35 → 2035000，2.2.5 → 2002005。
  # 缺省字段在 awk 里是空串，%d 取 0，正是想要的。
  glibc_key() { echo "$1" | awk -F. '{ printf "%d%03d%03d\n", $1, $2, $3 }'; }

  REQUIRED_VERSIONS="$($SYMBOL_DUMP "$DEST/$CANONICAL" 2>/dev/null \
    | grep -o 'GLIBC_[0-9][0-9.]*' | sed 's/^GLIBC_//' | sed 's/\.$//' | sort -u || true)"
  if [ -z "$REQUIRED_VERSIONS" ]; then
    # 没有任何带版本的 glibc 符号并不必然是错的，但它不该悄悄发生：要么是静态链接，
    # 要么是符号表被 strip 过头，两种都值得看一眼再放行。
    echo "    警告：$CANONICAL 里没有任何 GLIBC_ 版本符号，跳过下限校验" >&2
  else
    WORST_VERSION="$(printf '%s\n' "$REQUIRED_VERSIONS" \
      | awk -F. '{ k = $1*1000000 + $2*1000 + $3; if (k > best) { best = k; v = $0 } }
                 END { if (v != "") print v }')"
    echo "    需要的 glibc 版本：$(printf '%s' "$REQUIRED_VERSIONS" | tr '\n' ' ')"
    echo "    其中最高：$WORST_VERSION"
    if [ "$(glibc_key "$WORST_VERSION")" -gt "$(glibc_key "$GLIBC_FLOOR")" ]; then
      echo "错误：$CANONICAL 要求 glibc $WORST_VERSION，高于 README 承诺的 $GLIBC_FLOOR" >&2
      echo "      这份 AppImage 在 Ubuntu 22.04 / Debian 12 上会加载失败。" >&2
      echo "      三个方向（见 PR4-HANDOFF §8.4）：" >&2
      echo "        1. 把 release 的 linux job 钉到与承诺相符的 runner 镜像；" >&2
      echo "        2. 降低使用面，让产物不再需要这么新的符号；" >&2
      echo "        3. 改 README 的支持矩阵，明确降级承诺。" >&2
      exit 1
    fi
  fi
fi

CANONICAL_HASH="$(hash_file "$DEST/$CANONICAL")"
ALIAS_HASH="$(hash_file "$DEST/$ALIAS")"
MANIFEST="$DEST/lcms2.manifest.json"
cat > "$MANIFEST" <<EOF
{
  "schemaVersion": 1,
  "component": "Little CMS",
  "version": "$LCMS_VERSION",
  "rid": "$RID",
  "source": {
    "url": "$SOURCE_URL",
    "sha256": "$SOURCE_SHA256"
  },
  "build": {
    "system": "autotools",
    "configureArgs": [
      "--enable-shared",
      "--disable-static",
      "--disable-dependency-tracking",
      "--without-jpeg",
      "--without-tiff"
    ]
  },
  "artifacts": [
    { "file": "$CANONICAL", "sha256": "$CANONICAL_HASH" },
    { "file": "$ALIAS", "sha256": "$ALIAS_HASH" }
  ]
}
EOF

echo "==> Little CMS 已装配到 $DEST"
echo "    $CANONICAL  SHA-256 $CANONICAL_HASH"
echo "    $ALIAS      SHA-256 $ALIAS_HASH"
echo "    manifest    $MANIFEST"
