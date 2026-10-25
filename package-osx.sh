#!/bin/bash

set -euo pipefail

Arch="$1"
OutputPath="$2"
Version="$3"
CoreBinRef="${CORE_BIN_REF:-master}"
ScriptDir="$(cd "$(dirname "$0")" && pwd)"
SevenZip="$(command -v 7z || command -v 7zz)"

FileName="v2rayN-${Arch}.zip"
PinnedCore=""
# Preserve the core built with this app without executing a different architecture.
if [[ -s "$OutputPath/bin/sing_box/sing-box" ]]; then
    PinnedCore="$(mktemp)"
    cp "$OutputPath/bin/sing_box/sing-box" "$PinnedCore"
fi
curl --fail --location --retry 3 -o "$FileName" "https://raw.githubusercontent.com/2dust/v2rayN-core-bin/${CoreBinRef}/$FileName"
"$SevenZip" x -y "$FileName"
if [[ ! "v2rayN-${Arch}" -ef "$OutputPath" ]]; then
    cp -R "v2rayN-${Arch}/." "$OutputPath/"
fi
if [[ -n "$PinnedCore" ]]; then
    cp "$PinnedCore" "$OutputPath/bin/sing_box/sing-box"
    chmod +x "$OutputPath/bin/sing_box/sing-box"
    rm "$PinnedCore"
fi

PackagePath="v2rayN-Package-${Arch}"
mkdir -p "$PackagePath/v2rayN.app/Contents/Resources"
cp -rf "$OutputPath" "$PackagePath/v2rayN.app/Contents/MacOS"
cp -f "$PackagePath/v2rayN.app/Contents/MacOS/v2rayN.icns" "$PackagePath/v2rayN.app/Contents/Resources/AppIcon.icns"
echo "When this file exists, app will not store configs under this folder" > "$PackagePath/v2rayN.app/Contents/MacOS/NotStoreConfigHere.txt"
chmod +x "$PackagePath/v2rayN.app/Contents/MacOS/v2rayN"
chmod +x "$PackagePath/v2rayN.app/Contents/MacOS/AmazTool"
chmod +x "$PackagePath/v2rayN.app/Contents/MacOS/proxy-check"
chmod -R +x "$PackagePath/v2rayN.app/Contents/MacOS/bin"

cat >"$PackagePath/v2rayN.app/Contents/Info.plist" <<-EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleLocalizations</key>
  <array>
    <string>zh-Hans</string>
    <string>zh-Hant</string>
    <string>en</string>
    <string>fa</string>
    <string>fr</string>
    <string>ru</string>
    <string>hu</string>
  </array>
  <key>CFBundleDisplayName</key>
  <string>v2rayN</string>
  <key>CFBundleExecutable</key>
  <string>v2rayN</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIconName</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>2dust.v2rayN</string>
  <key>CFBundleName</key>
  <string>v2rayN</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${Version}</string>
  <key>CFBundleVersion</key>
  <string>${Version}</string>
  <key>CSResourcesFileMapped</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>13.6</string>
</dict>
</plist>
EOF

AppPath="$PackagePath/v2rayN.app"
codesign --force --deep --sign - "$AppPath"
python3 "$ScriptDir/packaging/write-ssh-core-manifest.py" "$AppPath/Contents/MacOS/bin/sing_box/sing-box"
codesign --force --deep --sign - "$AppPath"
codesign --verify --deep --strict "$AppPath"
python3 "$ScriptDir/packaging/verify-ssh-core-manifest.py" "$AppPath/Contents/MacOS/bin/sing_box/sing-box"

mkdir -p "$PackagePath/.background"
swift "$ScriptDir/packaging/macos/dmg-background.swift" "$PackagePath/.background/background.png"

create-dmg \
    --volname "v2rayN Installer" \
    --window-size 900 620 \
    --background "$PackagePath/.background/background.png" \
    --icon-size 78 \
    --text-size 14 \
    --icon "v2rayN.app" 200 100 \
    --hide-extension "v2rayN.app" \
    --app-drop-link 650 100 \
    --add-file "安装说明.txt" "$ScriptDir/packaging/macos/安装说明.txt" 795 100 \
    "v2rayN-${Arch}.dmg" \
    "$PackagePath/v2rayN.app"
