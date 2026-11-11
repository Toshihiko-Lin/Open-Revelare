#!/usr/bin/env bash
# CPU native filters; this script does not build a GPU model runtime.
# Usage: bash build-unix.sh /path/to/PURENeg /absolute/path/to/new-output
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 PURENeg-checkout new-output-directory" >&2
    exit 2
fi
revision=cbcc9fc825ba9aaf12304c7e9524643995faf4dd
source_root=$(cd "$1" && pwd -P)
output_parent=$(dirname "$2")
mkdir -p "$output_parent"
output_parent=$(cd "$output_parent" && pwd -P)
destination="$output_parent/$(basename "$2")"
[[ ! -e "$destination" ]] || { echo "Output already exists: $destination" >&2; exit 2; }
case "$(uname -s):$(uname -m)" in
    Darwin:arm64) platform=darwin-arm64 ;;
    Darwin:x86_64) platform=darwin-x64 ;;
    Linux:x86_64) platform=linux-x64 ;;
    Linux:aarch64|Linux:arm64) platform=linux-arm64 ;;
    *) echo "Build on the matching macOS/Linux host and CPU architecture." >&2; exit 2 ;;
esac
command -v node >/dev/null
command -v git >/dev/null
command -v tar >/dev/null
work=$(mktemp -d "${TMPDIR:-/tmp}/openrevelare-pureneg-build.XXXXXXXX")
stage=$(mktemp -d "$output_parent/.pureneg-stage.XXXXXXXX")
cleanup() {
    case "$work" in "${TMPDIR:-/tmp}"/openrevelare-pureneg-build.*) rm -rf -- "$work" ;; esac
    case "$stage" in "$output_parent"/.pureneg-stage.*) rm -rf -- "$stage" ;; esac
}
trap cleanup EXIT
git -C "$source_root" archive "$revision" LICENSE vendor/film-dust/native/build.sh \
    vendor/film-dust/native/src vendor/film-dust/native/node-api \
    vendor/film-dust/LICENSE.txt vendor/film-dust/NOTICES.txt | tar -xf - -C "$work"
bash "$work/vendor/film-dust/native/build.sh"
cp "$work/vendor/film-dust/native/build/filmdust.node" "$stage/filmdust.node"
cp "$work/LICENSE" "$stage/LICENSE-PURENeg.txt"
cp "$work/vendor/film-dust/LICENSE.txt" "$stage/LICENSE-film-dust.txt"
cp "$work/vendor/film-dust/NOTICES.txt" "$stage/NOTICES-film-dust.txt"
node - "$stage" "$platform" "$revision" <<'JS'
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const [directory, platform, revision] = process.argv.slice(2);
const addon = path.join(directory, 'filmdust.node');
if (!(require(addon).version() >= 1)) throw new Error('Native module smoke check failed.');
const sha256 = crypto.createHash('sha256').update(fs.readFileSync(addon)).digest('hex');
fs.writeFileSync(path.join(directory, 'manifest.json'), JSON.stringify({revision, platform, sha256}, null, 2) + '\n');
JS
mv -- "$stage" "$destination"
echo "$destination"
