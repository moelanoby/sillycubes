#!/usr/bin/env bash
# Build Polytoria as a SINGLE standalone executable.
#
# Usage:
#   ./build.sh linux     # → build/Polytoria.x86_64
#   ./build.sh windows   # → build/Polytoria.exe
#   ./build.sh macos     # → build/Polytoria.dmg
#   ./build.sh all       # → all three
#
# First-time setup (one-time, requires internet):
#   1. Install Godot 4.6.2 Mono from https://godotengine.org
#   2. Open Godot → Project → Export → Install export templates
#      (or run: godot --headless --install-android-build-template)
#   3. Set the GODOT env var if not in PATH:
#      export GODOT=/path/to/Godot_v4.6.2-stable_mono_linux.x86_64
#
# The resulting binary is self-contained with embed_pck=true.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/Polytoria"
OUTPUT_DIR="$SCRIPT_DIR/build"
if [ -n "${GODOT:-}" ]; then
    : # use GODOT env var as-is
elif command -v godot &>/dev/null; then
    GODOT="godot"
else
    # auto-discover in project root
    candidates=( "$SCRIPT_DIR"/Godot*/Godot*linux*x86_64 "$SCRIPT_DIR"/Godot*/godot* )
    for c in "${candidates[@]}"; do
        if [ -x "$c" ]; then
            GODOT="$c"
            break
        fi
    done
fi

if [ -z "${GODOT:-}" ]; then
    echo "Error: Godot binary not found."
    echo "Set the GODOT env var or place Godot in a Godot*/ directory."
    exit 1
fi

build_webui() {
    local webui_dir="$PROJECT_DIR/scripts/network/p2p/webui"
    if [ ! -f "$webui_dir/dist/assets/index.js" ]; then
        echo "==> Building web UI..."
        (cd "$webui_dir" && npm install && npm run build)
    fi
}

deploy_webui() {
    local target_dir="$OUTPUT_DIR/webui"
    mkdir -p "$target_dir"
    cp -r "$PROJECT_DIR/scripts/network/p2p/webui/dist/"* "$target_dir/"
}

build_platform() {
    local preset="$1"
    local output_name="$2"
    local rid="$3"

    echo "==> Building $preset → $OUTPUT_DIR/$output_name ($rid)"

    dotnet publish "$PROJECT_DIR/Polytoria.csproj" -c Release -r "$rid" --self-contained

    "$GODOT" --headless \
        --path "$PROJECT_DIR" \
        --export-release "$preset" \
        "$OUTPUT_DIR/$output_name"

    deploy_webui

    echo "==> Done: $(ls -lh "$OUTPUT_DIR/$output_name" | awk '{print $5}')"
}

build_webui

mkdir -p "$OUTPUT_DIR"

case "${1:-all}" in
    linux)
        build_platform "Linux"   "Polytoria.x86_64" "linux-x64"
        ;;
    windows)
        build_platform "Windows" "Polytoria.exe"    "win-x64"
        ;;
    macos)
        build_platform "macOS"   "Polytoria.dmg"     "osx-x64"
        ;;
    all)
        build_platform "Linux"   "Polytoria.x86_64"  "linux-x64"
        build_platform "Windows" "Polytoria.exe"     "win-x64"
        build_platform "macOS"   "Polytoria.dmg"     "osx-x64"
        ;;
    *)
        echo "Usage: $0 {linux|windows|macos|all}"
        exit 1
        ;;
esac
