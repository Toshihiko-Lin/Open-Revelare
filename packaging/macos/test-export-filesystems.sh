#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo "Export filesystem tests require macOS." >&2
    exit 1
fi

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

run_on_filesystem() (
    local label="$1" filesystem="$2" case_sensitive="$3"
    shift 3
    local workspace mount image mounted=false actual_case_sensitive=yes
    workspace="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/revelare-export.XXXXXXXX")"
    mount="$workspace/Export Destination"
    image="$workspace/test.dmg"
    trap 'if "$mounted"; then hdiutil detach "$mount" || exit 1; fi
          rm -f "$image"
          rmdir "$mount" "$workspace"' EXIT
    mkdir "$mount"

    echo "Testing exports on $label"
    hdiutil create -size 256m -format UDRW -fs "$filesystem" -volname RevelareExportTest "$@" "$image"
    hdiutil attach -readwrite -nobrowse -noautoopen -mountpoint "$mount" "$image"
    mounted=true

    if ! /sbin/mount | /usr/bin/grep -F " on $(cd "$mount" && pwd -P) (" >/dev/null; then
        echo "Image is not mounted at $mount" >&2
        exit 1
    fi
    touch "$mount/CaseProbe"
    if [[ -e "$mount/caseprobe" ]]; then
        actual_case_sensitive=no
    fi
    if [[ "$actual_case_sensitive" != "$case_sensitive" ]]; then
        echo "Unexpected case sensitivity on $label" >&2
        exit 1
    fi

    OPENREVELARE_EXPORT_TEST_DIRECTORY="$mount" dotnet test src/OpenRevelare.Tests/OpenRevelare.Tests.csproj \
        -c Release --no-build --nologo --filter 'FullyQualifiedName~ExportFileTests|FullyQualifiedName~LinearDngTests'
)

run_on_filesystem APFS APFS no
run_on_filesystem "Case-sensitive APFS" APFS yes -fsargs '-e'
run_on_filesystem ExFAT ExFAT no
