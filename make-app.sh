#!/bin/sh
# Builds TylerCast.app (menu bar only) from the Swift package.
#
# Why a bundle at all: `swift build` makes a plain executable, which works from
# Terminal (Terminal holds the Screen Recording permission for it). Launched on
# its own, macOS attributes Screen Recording and Local Network permission to an
# app by bundle identifier, and an LSUIElement Info.plist keeps it out of the
# Dock. No Xcode project is needed: this copies the binary into a bundle and
# signs it ad hoc.
set -eu
cd "$(dirname "$0")"
swift build -c release
APP=build/TylerCast.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/TylerCast "$APP/Contents/MacOS/TylerCast"
# SwiftPM records the deployment target (14.0) as the binary's SDK version.
# macOS picks an app's look from that number, so without this the menu bar
# panel gets the pre-Tahoe flat window instead of Control Center's glass.
SDK_VERSION=$(xcrun --show-sdk-version)
vtool -set-build-version macos 14.0 "$SDK_VERSION" -replace -output "$APP/Contents/MacOS/TylerCast" "$APP/Contents/MacOS/TylerCast"
# App icon: a screen with a T, drawn by the app itself (Sources/TylerCast/Icon.swift)
mkdir -p "$APP/Contents/Resources"
ICONSET=$(mktemp -d)/AppIcon.iconset
.build/release/TylerCast make-icon "$ICONSET"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>local.tylercast.TylerCast</string>
  <key>CFBundleName</key><string>TylerCast</string>
  <key>CFBundleDisplayName</key><string>TylerCast</string>
  <key>CFBundleExecutable</key><string>TylerCast</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundleShortVersionString</key><string>0.1</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>TylerCast finds your TV on the local network and streams the screen to it.</string>
  <key>NSAudioCaptureUsageDescription</key>
  <string>TylerCast sends your Mac's sound to the TV, and mutes the Mac while mirroring.</string>
</dict>
</plist>
PLIST
# Signing. With a code-signing certificate named "TylerCast Signing" in the
# login keychain (self-signed is fine; README has the steps), every build has
# the same identity, so the Screen Recording grant survives rebuilds. Without
# it the signature is ad hoc and changes every build: macOS then keeps showing
# TylerCast as allowed while refusing the new build, until the old grant is
# cleared with `tccutil reset ScreenCapture local.tylercast.TylerCast`.
if security find-identity -p codesigning 2>/dev/null | grep -q '"TylerCast Signing"'; then
  codesign --force --sign "TylerCast Signing" --identifier local.tylercast.TylerCast "$APP"
  echo "Signed with the TylerCast Signing certificate"
else
  codesign --force --sign - --identifier local.tylercast.TylerCast "$APP"
  echo "Signed ad hoc: after this rebuild, Screen Recording may need resetting (see README)"
fi
echo "Built $(pwd)/$APP"
