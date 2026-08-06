#!/usr/bin/env bash
# 把 self-contained 的 linux-x64 产物打成单文件 AppImage。
#
#   ./packaging/linux/build-appimage.sh            # publish + 打包
#   ./packaging/linux/build-appimage.sh --no-publish   # 复用已有的 publish/linux-x64
#
# publish 这一步**可以在 Windows 上跨平台做**（dotnet publish -r linux-x64 不需要 Linux
# 主机，实测 231 文件 / 138 MB），但 appimagetool 只能在 Linux 上跑。跨机拷来的产物
# 会丢执行位，本脚本统一补 chmod。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RID="linux-x64"
PUB="$ROOT/publish/$RID"
APPDIR="$ROOT/publish/OpenRevelare.AppDir"
TOOL="$ROOT/tools/appimagetool-x86_64.AppImage"
# 内置 FUSE 的静态 runtime：用它打出的 AppImage 在只有 FUSE3 的新发行版也能直接双击。
# （Python 版踩过这个坑：旧 runtime 依赖 libfuse2，用户双击毫无反应。）
RUNTIME="$ROOT/tools/runtime-x86_64"

DO_PUBLISH=1
[ "${1:-}" = "--no-publish" ] && DO_PUBLISH=0

# 版本号唯一真源 = csproj 的 <Version>，与 Windows 发版同一个数字
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' \
  "$ROOT/src/OpenRevelare.Gui/OpenRevelare.Gui.csproj" | head -1)"
[ -n "$VERSION" ] || { echo "错误：没能从 OpenRevelare.Gui.csproj 读出 <Version>"; exit 1; }
OUT="$ROOT/installer/OpenRevelare-${VERSION}-x86_64.AppImage"

echo "==> 版本：$VERSION"

# ── publish ────────────────────────────────────────────────────────────────
if [ "$DO_PUBLISH" = 1 ]; then
  echo "==> dotnet publish ($RID, self-contained)"
  dotnet publish "$ROOT/src/OpenRevelare.Gui" -c Release -r "$RID" \
    --self-contained true -o "$PUB"
fi
[ -f "$PUB/OpenRevelare" ] || { echo "错误：未找到 $PUB/OpenRevelare"; exit 1; }

# RAW 解码的原生库。缺了照样能打包，但一导入 RAW 就炸，所以这里明说。
[ -f "$PUB/libraw_r.so.23" ] || echo "警告：产物里没有 libraw_r.so.23 —— RAW 解码会失败。"

# ── 前置检查 ───────────────────────────────────────────────────────────────
for t in "$TOOL" "$RUNTIME"; do
  [ -f "$t" ] && continue
  cat >&2 <<EOF
错误：缺少 $t

  mkdir -p "$ROOT/tools"
  curl -fL -o "$ROOT/tools/appimagetool-x86_64.AppImage" \\
    https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$ROOT/tools/appimagetool-x86_64.AppImage"
  curl -fL -o "$ROOT/tools/runtime-x86_64" \\
    https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
EOF
  exit 1
done
chmod +x "$TOOL"

# ── 组装 AppDir ────────────────────────────────────────────────────────────
echo "==> 组装 AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
cp -a "$PUB/." "$APPDIR/usr/bin/"

# 跨机（尤其是从 Windows）拷来的产物没有执行位。.so 不需要 +x，主程序需要。
chmod +x "$APPDIR/usr/bin/OpenRevelare"
# 调试符号不进分发包
find "$APPDIR/usr/bin" -name '*.pdb' -delete

# 第三方声明。LibRaw 走 LGPL-2.1，随二进制分发必须带许可声明 —— 不是可选项。
cp "$ROOT/THIRD_PARTY_NOTICES.txt" "$ROOT/LICENSE" "$APPDIR/usr/bin/"

# 图标：AppImage 约定根目录放一个与 .desktop 的 Icon= 同名的 png
cp "$ROOT/src/OpenRevelare.Gui/Assets/icons/app-512.png" "$APPDIR/open-revelare.png"

cat > "$APPDIR/open-revelare.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=OpenRevelare
Comment=Color negative film de-masking tool
Comment[zh_CN]=彩色负片去色罩工具
Exec=OpenRevelare %f
Icon=open-revelare
Categories=Graphics;Photography;
MimeType=application/x-ncproj;
Terminal=false
DESKTOP

# AppRun：AppImage 的启动入口，转发到真正的可执行文件。
# LD_LIBRARY_PATH 不用设 —— Sdcb 的加载器会先按程序目录 NativeLibrary.TryLoad
# libjpeg.so.8 / liblcms2.so / libgomp.so.1，再加载 libraw_r.so.23，届时
# 这几个 soname 已在进程里，动态链接器直接复用。
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "${0}")")"
exec "$HERE/usr/bin/OpenRevelare" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# ── 打包 ───────────────────────────────────────────────────────────────────
echo "==> 调用 appimagetool"
mkdir -p "$ROOT/installer"
# --appimage-extract-and-run：appimagetool 自己要 FUSE2，让它自解压跑，只影响打包机
ARCH=x86_64 "$TOOL" --appimage-extract-and-run \
  --runtime-file "$RUNTIME" "$APPDIR" "$OUT"

echo
echo "==> 完成：$OUT"
du -h "$OUT"
