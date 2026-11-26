#!/bin/sh
# Packs a published macOS build into ThixGate.app, a zip (for the update of installed versions) and a disk image
# (for the first installation: drag ThixGate into "Programme").
#
#   scripts/package-macos.sh <publish folder> <rid: osx-arm64|osx-x64> <version> <build number> <output folder> [<cli publish folder> (unused by ThixGate)]
#
# Out: ThixGate-macOS-<arch>.zip and ThixGate-macOS-<arch>.dmg.
#
# Runs on macOS (iconutil, codesign, ditto, hdiutil). The app has a window and a Dock icon and keeps a menu bar icon.
# Without an Apple Developer ID the app is signed ad hoc (needed on Apple Silicon); Gatekeeper then asks once
# (right click > Öffnen, or System Settings > Privacy & Security). Catching system keys in a session (Cmd+Tab and the like) may ask for "Bedienungshilfen" access.
#
# The command line (src/ThixGate.Cli, published self-contained for the same rid) goes into ThixGate.app/Contents/MacOS/thixgate-cli
# and shares the runtime files of the app there (only its own files are added, the app's files are never replaced). A terminal
# finds it after a link on the PATH, which needs administrator rights and is therefore left to the user (not done by the app):
#   sudo ln -sf /Applications/ThixGate.app/Contents/MacOS/thixgate-cli /usr/local/bin/thixgate-cli
# (the program follows the link to its folder). Started from a terminal, macOS asks for folder access on behalf of the terminal.
set -eu

publish="$1"
rid="$2"
version="$3"
build="$4"
out="$5"
cli="${6:-}"

root="$(cd "$(dirname "$0")/.." && pwd)"
arch="${rid#osx-}"
major=$(sed -n 's:.*<ThixGateMajor>\(.*\)</ThixGateMajor>.*:\1:p' "$root/Directory.Build.props")
minor=$(sed -n 's:.*<ThixGateMinor>\(.*\)</ThixGateMinor>.*:\1:p' "$root/Directory.Build.props")
work="$(mktemp -d)"
app="$work/ThixGate.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/ThixGate"

# The command line next to the app: its own files (thixgate-cli, thixgate-cli.dll, .deps.json, .runtimeconfig.json) are added,
# what the app has already (runtime, ThixGate.Core, SQLite) stays the app's – same build, same versions.
if [ -n "$cli" ]; then
  (cd "$cli" && find . -type f) | while IFS= read -r file; do
    target="$app/Contents/MacOS/${file#./}"
    if [ ! -e "$target" ]; then
      mkdir -p "$(dirname "$target")"
      cp -p "$cli/${file#./}" "$target"
    fi
  done
  chmod +x "$app/Contents/MacOS/thixgate-cli"
fi

# App icon from the rendered branding (tools/render-icons.js): the macOS variant keeps Apple's margin around the tile.
iconset="$work/ThixGate.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  cp "$root/branding/png/thixgate-mac-${size}.png" "$iconset/icon_${size}x${size}.png"
  double=$((size * 2))
  cp "$root/branding/png/thixgate-mac-${double}.png" "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/ThixGate.icns"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>info.schmoldt.thixgate</string>
  <key>CFBundleName</key>
  <string>ThixGate</string>
  <key>CFBundleDisplayName</key>
  <string>ThixGate</string>
  <key>CFBundleExecutable</key>
  <string>ThixGate</string>
  <key>CFBundleIconFile</key>
  <string>ThixGate</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${major}.${minor}.${build}</string>
  <key>CFBundleVersion</key>
  <string>${build}</string>
  <key>CFBundleGetInfoString</key>
  <string>ThixGate ${version}</string>
  <key>NSHumanReadableCopyright</key>
  <string>© Matthias Schmoldt</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>CFBundleDevelopmentRegion</key>
  <string>de</string>
  <key>NSLocalNetworkUsageDescription</key>
  <string>ThixGate verbindet sich mit Rechnern in deinem lokalen Netzwerk (Remote Desktop, VNC, SSH).</string>
  <key>NSAppleEventsUsageDescription</key>
  <string>ThixGate öffnet SSH-Verbindungen im Terminal.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: Apple Silicon runs only signed code. A Developer ID signature + notarization can replace it later.
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# The command line inside the bundle must find the app's runtime: try it where this machine can run it.
host_arch="$(uname -m)"
if [ -n "$cli" ] && { [ "$host_arch" = "$arch" ] || { [ "$host_arch" = "x86_64" ] && [ "$arch" = "x64" ]; }; }; then
  "$app/Contents/MacOS/thixgate-cli" --version
fi

# Update package (MacAppBundle unpacks it with ditto) and disk image for the first installation.
ditto -c -k --keepParent "$app" "$out/ThixGate-macOS-${arch}.zip"

dmg_root="$work/dmg"
mkdir -p "$dmg_root"
cp -R "$app" "$dmg_root/"
ln -s /Applications "$dmg_root/Programme"
hdiutil create -volname "ThixGate" -srcfolder "$dmg_root" -ov -format UDZO "$out/ThixGate-macOS-${arch}.dmg" >/dev/null

# The command line alone (servers, scripts): a folder thixgate-cli/ with the program and its runtime.
if [ -n "$cli" ]; then
  mkdir -p "$work/cli/thixgate-cli"
  cp -R "$cli/." "$work/cli/thixgate-cli/"
  chmod +x "$work/cli/thixgate-cli/thixgate-cli"
  tar -czf "$out/ThixGate-CLI-macOS-${arch}.tar.gz" -C "$work/cli" thixgate-cli
fi

rm -rf "$work"
ls -l "$out"
