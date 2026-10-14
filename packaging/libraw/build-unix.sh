#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
case "$(uname -s):$(uname -m)" in
  Darwin:arm64) RID=osx-arm64 ;;
  Darwin:x86_64) RID=osx-x64 ;;
  Linux:x86_64) RID=linux-x64 ;;
  Linux:aarch64|Linux:arm64) RID=linux-arm64 ;;
  *) echo "Unsupported LibRaw build host" >&2; exit 1 ;;
esac
WORK="$ROOT/scratch/libraw-build-$RID"
DEST="$ROOT/native/$RID"
cmake -S "$ROOT/packaging/libraw" -B "$WORK" \
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$DEST"
cmake --build "$WORK" --target OpenRevelare.Raw.Native --parallel 4
cmake --install "$WORK" --component Raw
if [[ "$RID" == osx-* ]]; then
  codesign --force --sign - "$DEST/libOpenRevelare.Raw.Native.dylib"
fi
