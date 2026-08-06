#!/usr/bin/env bash
# 把 self-contained 产物装配成 OpenRevelare.app，可选出 .dmg。
#
#   ./packaging/macos/build-app.sh                          # 当前架构，ad-hoc 签名
#   ./packaging/macos/build-app.sh --arch x64 --dmg
#   ./packaging/macos/build-app.sh --sign "Developer ID Application: 名字 (TEAMID)" \
#       --notarize AC_PASSWORD --dmg                        # 正式签名 + 公证
#
# publish 可以在 Windows 上跨平台做，但**装配 .app 必须在 macOS 上**（要 sips /
# iconutil / codesign / hdiutil）。
set -euo pipefail

[ "$(uname -s)" = "Darwin" ] || { echo "错误：本脚本只能在 macOS 上跑"; exit 1; }

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/packaging/macos"
ARCH="$(uname -m)"; [ "$ARCH" = "x86_64" ] && ARCH=x64 || ARCH=arm64
SIGN="-"            # 默认 ad-hoc
NOTARY=""
MAKE_DMG=0
DO_PUBLISH=1

while [ $# -gt 0 ]; do
  case "$1" in
    --arch)       ARCH="$2"; shift 2 ;;
    --sign)       SIGN="$2"; shift 2 ;;
    --notarize)   NOTARY="$2"; shift 2 ;;
    --dmg)        MAKE_DMG=1; shift ;;
    --no-publish) DO_PUBLISH=0; shift ;;
    *) echo "未知参数：$1"; exit 1 ;;
  esac
done
case "$ARCH" in arm64|x64) ;; *) echo "错误：--arch 只接受 arm64 / x64"; exit 1 ;; esac

RID="osx-$ARCH"
PUB="$ROOT/publish/$RID"
APP="$ROOT/publish/OpenRevelare.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' \
  "$ROOT/src/OpenRevelare.Gui/OpenRevelare.Gui.csproj" | head -1)"
[ -n "$VERSION" ] || { echo "错误：没能从 OpenRevelare.Gui.csproj 读出 <Version>"; exit 1; }

# ⚠ 变量后面紧跟中文时必须加花括号。macOS 自带 bash 3.2 不是多字节安全的，
# `$VERSION，` 会把逗号的 UTF-8 字节读进变量名，set -u 下直接 "unbound variable"。
# Linux 的 bash 5 不会，所以这种写法在 CI 上只有 mac 那格会炸（已经炸过一次）。
echo "==> 版本 ${VERSION}，目标 ${RID}"

# ── RAW 原生库 ─────────────────────────────────────────────────────────────
if [ ! -f "$ROOT/native/$RID/libraw.23.dylib" ]; then
  echo "错误：native/$RID/libraw.23.dylib 不存在 —— 先跑 packaging/macos/bundle-libraw.sh。"
  echo "（硬要出一个只能开 TIFF 的包，把这个检查注释掉即可。）"
  exit 1
fi

# ── publish ────────────────────────────────────────────────────────────────
if [ "$DO_PUBLISH" = 1 ]; then
  echo "==> dotnet publish ($RID, self-contained)"
  dotnet publish "$ROOT/src/OpenRevelare.Gui" -c Release -r "$RID" \
    --self-contained true -o "$PUB"
fi
[ -f "$PUB/OpenRevelare" ] || { echo "错误：未找到 $PUB/OpenRevelare"; exit 1; }
# native/ 里有 dylib 不等于它进了产物——中间隔着一条 MSBuild 的坑（见 OpenRevelare.Gui.csproj
# 里那段注释）。在这里拦一道，比等到签名或用户导入 RAW 时才发现便宜得多。
[ -f "$PUB/libraw.23.dylib" ] || {
  echo "错误：$PUB/libraw.23.dylib 不在产物里。"
  echo "      native/$RID/ 里有文件不代表它被复制进 publish —— 检查 OpenRevelare.Gui.csproj"
  echo "      里那个按 RID 条件的 Content 项（放错项目会静默失效）。"
  exit 1
}

# ── 装配 .app ──────────────────────────────────────────────────────────────
echo "==> 装配 $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$PUB/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/OpenRevelare"      # 跨机拷来的产物会丢执行位
find "$APP/Contents/MacOS" -name '*.pdb' -delete

sed "s/@VERSION@/$VERSION/g" "$HERE/Info.plist.in" > "$APP/Contents/Info.plist"

# 第三方声明。LibRaw 走 LGPL-2.1，随二进制分发必须带许可声明 —— 不是可选项。
# 放 Resources 而不是 MacOS：那目录只该有可执行码，多余文件会给 codesign 添乱。
cp "$ROOT/THIRD_PARTY_NOTICES.txt" "$ROOT/LICENSE" "$APP/Contents/Resources/"

# ── 图标 ───────────────────────────────────────────────────────────────────
# 1024 的母版优先从 svg 渲染（brew install librsvg）；没有就拿 512 的 png 放大，
# 只有 @2x 那两档会略糊。
ICONS="$ROOT/src/OpenRevelare.Gui/Assets/icons"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
MASTER="$TMP/master.png"
if command -v rsvg-convert >/dev/null 2>&1; then
  rsvg-convert -w 1024 -h 1024 "$ICONS/app.svg" -o "$MASTER"
else
  echo "    （没装 rsvg-convert，用 app-512.png 放大母版；brew install librsvg 可去掉这一步）"
  sips -z 1024 1024 "$ICONS/app-512.png" --out "$MASTER" >/dev/null
fi

SET="$TMP/open-revelare.iconset"; mkdir -p "$SET"
for sz in 16 32 128 256 512; do
  sips -z $sz $sz "$MASTER" --out "$SET/icon_${sz}x${sz}.png" >/dev/null
  sips -z $((sz*2)) $((sz*2)) "$MASTER" --out "$SET/icon_${sz}x${sz}@2x.png" >/dev/null
done
iconutil -c icns "$SET" -o "$APP/Contents/Resources/open-revelare.icns"

# ── 签名 ───────────────────────────────────────────────────────────────────
MAIN="$APP/Contents/MacOS/OpenRevelare"

# ⚠ ad-hoc 路线**不做 bundle 级签名**，这是想清楚之后的选择，不是偷懒：
#
# · codesign 把 Contents/MacOS 下的所有文件都当嵌套代码，而 .NET 的托管 DLL 是 PE，
#   于是必然报「code object is not signed at all / In subcomponent: …Contracts.dll」。
#   这个目录布局改不了——apphost 要求托管 DLL 与可执行文件同目录。
#   （更阴的是：给 codesign 传 bundle 的**主可执行文件**路径，它会自动升级成签整个
#   bundle，所以连"只签主程序"都会撞上同一堵墙。v1.0.0 第 2、3 次 CI 都死在这儿。）
# · **不签也能分发**：.NET SDK 在 macOS 上 publish 时已给 apphost 和自带 dylib 打了
#   ad-hoc 签名（日志里的 "replacing existing signature" 就是证据），arm64 内核要的
#   就是这个。同一台机器上的 LightsouceDecouple 三平台 CI 也是这么干的——PyInstaller
#   出 .app、chmod +x、ditto 打包，全程没有 codesign，实测可分发。
# · bundle 签名只有公证才需要，而我们不做公证（用户 2026-08-04 决定）。
#
# 所以这里只补签**我们自己改动过**的原生库（libraw 及其依赖被 install_name_tool 改过，
# bundle-libraw.sh 已重签；这里再兜一次），主可执行文件一个字都不碰。
echo "==> 签名（$( [ "$SIGN" = "-" ] && echo "ad-hoc：只补签原生库，不签 bundle" || echo "$SIGN" )）"
OPTS=(--force --sign "$SIGN")
if [ "$SIGN" != "-" ]; then
  # 公证要求 hardened runtime；.NET 需要的三个豁免在 entitlements.plist 里
  OPTS+=(--options runtime --timestamp --entitlements "$HERE/entitlements.plist")
fi

# 按 `file` 判断而不是扩展名：publish 里还有 createdump 这种没有扩展名的 Mach-O。
while IFS= read -r f; do
  if [ "$f" = "$MAIN" ]; then continue; fi     # 碰它就会升级成 bundle 签名，见上
  case "$(file -b "$f")" in
    *Mach-O*) codesign "${OPTS[@]}" "$f" ;;
  esac
done < <(find "$APP/Contents/MacOS" -type f)

if [ "$SIGN" != "-" ]; then
  # Developer ID 路线：公证要求整个 bundle 有签名，这一步必须做。届时若同样撞上
  # 托管 DLL 那堵墙，再研究把 payload 挪进 Contents/Resources + 启动器的方案。
  codesign "${OPTS[@]}" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
fi

# 自检：只验能验的东西。主可执行文件走 -dv（只显示不校验）——校验同样会升级成 bundle
# 校验，而我们本来就没签 bundle。libraw 是我们自己塞进去的，它必须有有效签名。
codesign -v "$APP/Contents/MacOS/libraw.23.dylib"
# `| head` 在 set -o pipefail 下会因 SIGPIPE 把整条流水线判成失败（141），加 || true
codesign -dv "$MAIN" 2>&1 | head -3 || true

# ── dmg ────────────────────────────────────────────────────────────────────
DMG="$ROOT/installer/OpenRevelare-${VERSION}-${ARCH}.dmg"
if [ "$MAKE_DMG" = 1 ]; then
  echo "==> 生成 dmg"
  mkdir -p "$ROOT/installer"
  STAGE="$TMP/dmg"; mkdir -p "$STAGE"
  cp -a "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"       # 拖进去即安装
  rm -f "$DMG"
  hdiutil create -volname "OpenRevelare $VERSION" -srcfolder "$STAGE" \
    -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
  [ "$SIGN" = "-" ] || codesign --force --sign "$SIGN" --timestamp "$DMG"
  du -h "$DMG"
fi

# ── 公证 ───────────────────────────────────────────────────────────────────
# 前置：xcrun notarytool store-credentials <profile> --apple-id … --team-id … --password <app 专用密码>
if [ -n "$NOTARY" ]; then
  TARGET="$DMG"; [ "$MAKE_DMG" = 1 ] || { echo "错误：--notarize 需要配 --dmg（要有个可提交的文件）"; exit 1; }
  echo "==> 提交公证（${NOTARY}）"
  xcrun notarytool submit "$TARGET" --keychain-profile "$NOTARY" --wait
  xcrun stapler staple "$TARGET"
  xcrun stapler validate "$TARGET"
fi

echo
echo "==> 完成：$APP"
# 注意别写成 `[ … ] && echo`：不出 dmg 时它是脚本的最后一条命令，返回 1 会让整个
# 脚本以失败退出（set -e 之外的坑）。
if [ "$MAKE_DMG" = 1 ]; then echo "         $DMG"; fi
if [ "$SIGN" = "-" ]; then
  cat <<'EOF'

⚠ ad-hoc 签名（本项目不买 Apple Developer Program，刻意如此），Gatekeeper 会拦。
  下载页必须原样带上这段，两条二选一，新旧系统都有效：
    1. xattr -dr com.apple.quarantine /Applications/OpenRevelare.app
    2. 双击一次被拦后 → 系统设置 → 隐私与安全性 → 仍要打开
  别只写「右键 → 打开」：macOS 15 (Sequoia) 起 Apple 取消了这个绕过入口。
EOF
fi
