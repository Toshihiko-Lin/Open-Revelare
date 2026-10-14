#!/usr/bin/env bash
# Compatibility entry point; all platforms now build the same pinned LibRaw bridge.
set -euo pipefail
[ "$(uname -s)" = Darwin ] || { echo "Run this entry point on macOS" >&2; exit 1; }
[ "$#" = 0 ] || { echo "Prebuilt LibRaw dylibs cannot replace the application ABI bridge. Build from source." >&2; exit 1; }
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exec bash "$ROOT/packaging/libraw/build-unix.sh"
