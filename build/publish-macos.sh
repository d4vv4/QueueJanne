#!/usr/bin/env bash
# Builds QueueJanne.app with the MCP server inside it. Run it on a Mac.
# Usage: ./build/publish-macos.sh [--install] [--rid osx-arm64|osx-x64]
#   --install copies QueueJanne.app to ~/Applications.
#   --rid builds for Apple silicon (osx-arm64) or Intel (osx-x64); the default is this Mac's kind.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
install=false
rid=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --install) install=true ;;
    --rid) rid="$2"; shift ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done
if [[ -z "$rid" ]]; then
  case "$(uname -m)" in
    arm64) rid=osx-arm64 ;;
    *) rid=osx-x64 ;;
  esac
fi
out="$root/artifacts/$rid"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")"

dotnet publish "$root/src/QueueJanne.App" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -o "$out/app"
dotnet publish "$root/src/QueueJanne.Mcp" -c Release -r "$rid" --self-contained \
  -p:PublishSingleFile=true -o "$out/mcp"

app="$out/QueueJanne.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$out/app/QueueJanne" "$app/Contents/MacOS/QueueJanne"
cp "$out/mcp/queuejanne-mcp" "$app/Contents/MacOS/queuejanne-mcp"

# Icon: the 256 px PNG scaled into an icon set.
iconset="$(mktemp -d)/QueueJanne.iconset"
mkdir -p "$iconset"
png="$root/src/QueueJanne.App/Assets/queuejanne.png"
for size in 16 32 128; do
  sips -z "$size" "$size" "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
sips -z 256 256 "$png" --out "$iconset/icon_256x256.png" >/dev/null
iconutil -c icns "$iconset" -o "$app/Contents/Resources/QueueJanne.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>QueueJanne</string>
  <key>CFBundleDisplayName</key><string>QueueJanne</string>
  <key>CFBundleIdentifier</key><string>io.github.d4vv4.queuejanne</string>
  <key>CFBundleExecutable</key><string>QueueJanne</string>
  <key>CFBundleIconFile</key><string>QueueJanne</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# Ad-hoc signature: Apple silicon only runs signed code, and this build isn't signed with a developer certificate.
codesign --force --deep --sign - "$app"
xattr -dr com.apple.quarantine "$app" 2>/dev/null || true

if [[ "$install" == true ]]; then
  mkdir -p "$HOME/Applications"
  rm -rf "$HOME/Applications/QueueJanne.app"
  cp -R "$app" "$HOME/Applications/"
  app="$HOME/Applications/QueueJanne.app"
  echo "Installed in ~/Applications."
fi

echo
echo "App:        $app"
echo "MCP server: $app/Contents/MacOS/queuejanne-mcp"
echo "Register the MCP server with Claude Code:  claude mcp add --scope user queuejanne -- \"$app/Contents/MacOS/queuejanne-mcp\""
