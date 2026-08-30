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
