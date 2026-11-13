#!/bin/bash
# Black-box check that the built game creates no ONNX Runtime telemetry, with the neural upscaling ON.
#
# Runs the real binary (an offscreen --capture run that initializes the model session, warms the 2x atlas
# and exits), under an isolated $HOME so that nothing of the player's profile is touched. If Microsoft's
# 1DS telemetry were active it would leave its SQLite event queue and persistent device ID under that home
# (~/.cache/Microsoft/DeveloperTools/.onnxruntime on Linux, ~/Library/Application Support/... on macOS);
# per the package's Privacy.md the ORT_DISABLE_TELEMETRY opt-out prevents the uploader, the events and the
# device identifier from being created at all - so "no artifacts" means nothing was ever queued to send.
# (On Windows the backend is ETW, which the variable does not cover; run this check on Linux or macOS.)
#
# Usage: tools/check-telemetry.sh [binary]   (default: src/Homestead.Client/bin/Release/net9.0/Homestead.Client)
# Needs: HOMESTEAD_GAME_DIR pointing at a Settlers III installation, dotnet for a default build.
set -euo pipefail

binary=${1:-src/Homestead.Client/bin/Release/net9.0/Homestead.Client}
if [ ! -x "$binary" ] && [ "$binary" = "src/Homestead.Client/bin/Release/net9.0/Homestead.Client" ]; then
    echo "building the client (pass a binary path to check a specific build)..."
    dotnet build src/Homestead.Client/Homestead.Client.csproj -c Release
fi
if [ ! -x "$binary" ]; then
    echo "no runnable binary at $binary (publish or build first, then pass its path)" >&2
    exit 2
fi
if [ -z "${HOMESTEAD_GAME_DIR:-}" ]; then
    echo "HOMESTEAD_GAME_DIR must point at a Settlers III installation" >&2
    exit 2
fi

home=$(mktemp -d "${TMPDIR:-/tmp}/homestead-telemetry.XXXXXX")
trap 'rm -rf "$home"' EXIT
mkdir -p "$home/.config/homestead"
# A fresh player who switched the feature on; everything else (saves, settings) stays in the sandbox.
cat > "$home/.config/homestead/settings.json" <<EOF
{ "display": { "neuralUpscale": true, "introVideo": false } }
EOF

echo "running the game (offscreen capture, feature on) under HOME=$home ..."
# STATS ends the run early; the session init, the warm-up worker and the exit path have all run by then.
# A framework-dependent apphost may need help finding the runtime under the redirected HOME: point DOTNET_ROOT
# at the dotnet on PATH, and roll forward only where no 9.x runtime is installed (a dotnet 10 machine can
# still run the net9.0 app). Self-contained binaries ignore both.
game_env=(env -u ORT_DISABLE_TELEMETRY HOME="$home" HOMESTEAD_CAPTURE_EVERY=999999 HOMESTEAD_CAPTURE_STATS=120)
if command -v dotnet >/dev/null 2>&1; then
    game_env+=("DOTNET_ROOT=$(dirname "$(realpath "$(command -v dotnet)")")/../libexec")
    dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft.NETCore.App 9\." || game_env+=("DOTNET_ROLL_FORWARD=Major")
fi
"${game_env[@]}" "$binary" --capture "$home/capture" > "$home/run.log" 2>&1 || {
    echo "the game itself failed (see $home/run.log):" >&2
    tail -5 "$home/run.log" >&2
    exit 2
}
grep -m1 "Neural upscale" "$home/run.log" || echo "note: no Neural upscale line in the log" >&2

found=$(find "$home" \( -iname "*DeveloperTools*" -o -iname "*onnxruntime*" \) 2>/dev/null | head -5)
if [ -n "$found" ]; then
    echo "TELEMETRY LEAK - ONNX Runtime left artifacts despite the opt-out:" >&2
    echo "$found" >&2
    exit 1
fi
echo "OK: a full run with the feature on created no ONNX Runtime telemetry (no device ID, no event queue)."
