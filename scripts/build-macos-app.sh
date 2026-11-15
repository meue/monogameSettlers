#!/bin/sh
# Builds a runnable macOS app bundle: self-contained publish -> monoSettlers.app -> ad-hoc codesign
# -> strict verification -> quarantine hygiene. The macOS counterpart of the CI package job, but
# for local use where the result opens WITHOUT any Gatekeeper dialog:
#
# - arm64 requires every binary to carry a signature, so an unsigned build does not run at all;
#   ad-hoc signing ("codesign -") satisfies it without a paid Apple developer account.
# - A bundle created on this machine never gets the quarantine flag, so Gatekeeper never looks at
#   it. Downloaded CI artifacts lack that luck: if such a bundle is ever moved via browser,
#   AirDrop etc., strip the flag once with:
#       xattr -dr com.apple.quarantine monoSettlers.app
#
# Usage: scripts/build-macos-app.sh [--rid osx-arm64|osx-x64] [--out DIR] [--skip-smoke]
# Output: <out>/monoSettlers.app (default dist/), ad-hoc signed and verified.
# --skip-smoke: do not run the 30-frame headless capture that proves the bundle starts.
set -eu

RID=osx-arm64
OUT=""
SMOKE=1
while [ $# -gt 0 ]; do
    case "$1" in
    --rid) RID=$2; shift 2 ;;
    --out) OUT=$2; shift 2 ;;
    --skip-smoke) SMOKE=0; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
case "$RID" in
osx-arm64 | osx-x64) ;;
*) echo "rid must be osx-arm64 or osx-x64, not $RID" >&2; exit 2 ;;
esac

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=${OUT:-$ROOT/dist}
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)
STAGE=$(mktemp -d "${TMPDIR:-/tmp}/monosettlers-build.XXXXXX")
trap 'rm -rf "$STAGE"' EXIT

# Version like the CI package job: MinVer from git tags, plain numbers without pre-release suffix.
VERSION=$(dotnet msbuild "$ROOT/src/Homestead.Client/Homestead.Client.csproj" -restore -t:MinVer \
    -getProperty:MinVerVersion -p:Configuration=Release | tail -n 1 | tr -d '\r' || true)
case "$VERSION" in
[0-9]*.[0-9]*.[0-9]*) VERSION=${VERSION%%-*} ;;
*) VERSION=$(git -C "$ROOT" describe --tags 2>/dev/null | sed 's/^v//; s/-.*//' || echo 0.0.0) ;;
esac
echo "== monoSettlers $VERSION for $RID -> $OUT"

echo "== publish (self-contained)"
dotnet publish "$ROOT/src/Homestead.Client/Homestead.Client.csproj" -c Release -r "$RID" --self-contained \
    -o "$STAGE/publish"

echo "== bundle"
APP="$STAGE/monoSettlers.app/Contents"
mkdir -p "$APP/MacOS" "$APP/Resources"
cp -R "$STAGE/publish/." "$APP/MacOS/"
sed "s/VERSION/$VERSION/g" "$ROOT/packaging/macos/Info.plist" >"$APP/Info.plist"
mkdir "$STAGE/icon.iconset"
sips -s format png "$ROOT/src/Homestead.Client/Icon.bmp" --out "$STAGE/icon.iconset/icon_256x256.png" >/dev/null
iconutil -c icns "$STAGE/icon.iconset" -o "$APP/Resources/monoSettlers.icns"

echo "== codesign (ad-hoc; fails hard - an unverified bundle must never ship)"
codesign --force --deep --sign - "$STAGE/monoSettlers.app"
codesign --verify --deep --strict "$STAGE/monoSettlers.app"

if [ "$SMOKE" = 1 ]; then
    echo "== smoke: 30-frame headless capture from the bundle"
    mkdir -p "$STAGE/smoke"
    "$APP/MacOS/Homestead.Client" --map random:2:repeat:2303 --capture "$STAGE/smoke" >/dev/null 2>&1
    FRAMES=$(ls "$STAGE/smoke" | wc -l | tr -d ' ')
    [ "$FRAMES" -gt 0 ] || {
        echo "smoke produced no frames" >&2
        exit 1
    }
    echo "   $FRAMES frames rendered"
fi

echo "== deliver"
rm -rf "$OUT/monoSettlers.app"
cp -R "$STAGE/monoSettlers.app" "$OUT/monoSettlers.app"
# Locally built bundles carry no quarantine flag; this makes sure of it even if staged oddly.
xattr -dr com.apple.quarantine "$OUT/monoSettlers.app" 2>/dev/null || true
du -sh "$OUT/monoSettlers.app"
if [ "$SMOKE" = 1 ]; then
    echo "OK: $OUT/monoSettlers.app (ad-hoc signed, verified, $FRAMES-frame smoke passed)"
else
    echo "OK: $OUT/monoSettlers.app (ad-hoc signed, verified, smoke skipped)"
fi
echo "If you share it: recipients once run  xattr -dr com.apple.quarantine monoSettlers.app"
