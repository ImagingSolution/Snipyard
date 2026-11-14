#!/bin/bash
# Publishes the macOS builds as Snipyard.app bundles, one tarball per architecture:
#   publish-mac/Snipyard-macos-arm64.tar.gz   (Apple Silicon)
#   publish-mac/Snipyard-macos-x64.tar.gz     (Intel)
# These are the release assets the macOS updater looks for (UpdateService.MacAssetName).
#
# Run it on a Mac. The bundle has to be signed as a whole (ad hoc, there is no Developer ID):
# with only the executable signed, a downloaded copy is reported as "damaged" by Gatekeeper
# and cannot be opened at all. codesign exists only on macOS, which is why publish-mac.ps1
# on Windows is no longer enough.
#
# Usage: ./publish-mac.sh [version]
#   version  the build number to stamp (e.g. 459). Defaults to the number in build.number,
#            i.e. the version the Windows publish of the same release produced.
set -euo pipefail
cd "$(dirname "$0")"

DOTNET=dotnet
command -v dotnet >/dev/null 2>&1 || DOTNET="$HOME/.dotnet/dotnet"

version="${1:-$(head -1 build.number | tr -d '\r ')}"
saved="$(head -1 build.number | tr -d '\r ')"
out=publish-mac
mkdir -p "$out"

# .icns from the PNG frames icon.ico already holds.
python3 -I - icon.ico "$out/Snipyard.icns" <<'PY'
import sys, struct
d = open(sys.argv[1], 'rb').read()
count = struct.unpack_from('<H', d, 4)[0]
types = {16: b'icp4', 32: b'icp5', 64: b'icp6', 128: b'ic07', 256: b'ic08'}
body = b''
for i in range(count):
    e = 6 + 16 * i
    w = d[e] or 256
    size, off = struct.unpack_from('<II', d, e + 8)
    if w in types:
        body += types[w] + struct.pack('>I', size + 8) + d[off:off + size]
open(sys.argv[2], 'wb').write(b'icns' + struct.pack('>I', len(body) + 8) + body)
PY

for a in arm64 x64; do
    bin="$out/$a"
    stage="$out/stage-$a"
    rm -rf "$bin" "$stage"

    # The build target adds one before compiling; this makes it land on $version.
    echo $((version - 1)) > build.number

    echo "Publishing osx-$a (1.0.$version)"
    "$DOTNET" publish -c Release -r "osx-$a" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none -o "$bin"

    app="$stage/Snipyard.app/Contents"
    mkdir -p "$app/MacOS" "$app/Resources"
    cp "$bin/Snipyard" "$app/MacOS/Snipyard"
    chmod 755 "$app/MacOS/Snipyard"
    cp "$out/Snipyard.icns" "$app/Resources/"
    cat > "$app/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Snipyard</string>
    <key>CFBundleDisplayName</key><string>Snipyard</string>
    <key>CFBundleIdentifier</key><string>net.imagingsolution.snipyard</string>
    <key>CFBundleExecutable</key><string>Snipyard</string>
    <key>CFBundleIconFile</key><string>Snipyard.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0.$version</string>
    <key>CFBundleVersion</key><string>1.0.$version</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

    codesign --force --deep --sign - --identifier net.imagingsolution.snipyard "$stage/Snipyard.app"
    codesign --verify --deep --strict "$stage/Snipyard.app"

    tarball="$out/Snipyard-macos-$a.tar.gz"
    COPYFILE_DISABLE=1 tar -C "$stage" --no-xattrs -czf "$tarball" Snipyard.app
    rm -rf "$stage"
    ls -lh "$tarball"
done

# Leave the counter as it was.
echo "$saved" > build.number
echo "Done: version 1.0.$version"
