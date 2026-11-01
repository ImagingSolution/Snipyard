# Publishes the macOS builds as Snipyard.app bundles, one tarball per architecture:
#   publish-mac\Snipyard-macos-arm64.tar.gz   (Apple Silicon)
#   publish-mac\Snipyard-macos-x64.tar.gz     (Intel)
# These are the release assets the macOS updater looks for (UpdateService.MacAssetName).
#
# Run it right after the Windows publish: every publish bumps build.number, so this script
# rewinds the counter before each architecture and all three builds carry the same version.
#
# A tarball rather than a zip because a zip written on Windows loses the executable bit;
# Windows' bsdtar takes the modes from an mtree spec instead.
param([string[]]$Arch = @('arm64', 'x64'))

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = [int](Get-Content .\build.number -TotalCount 1)
$outRoot = '.\publish-mac'
New-Item -ItemType Directory -Force $outRoot | Out-Null

# --- .icns from the PNG frames icon.ico already holds ---
function Write-Icns([string]$ico, [string]$target) {
    $d = [IO.File]::ReadAllBytes((Resolve-Path $ico))
    $count = [BitConverter]::ToUInt16($d, 4)
    $types = @{ 16 = 'icp4'; 32 = 'icp5'; 64 = 'icp6'; 128 = 'ic07'; 256 = 'ic08' }
    $body = New-Object IO.MemoryStream
    for ($i = 0; $i -lt $count; $i++) {
        $e = 6 + 16 * $i
        $w = [int]$d[$e]; if ($w -eq 0) { $w = 256 }
        $size = [BitConverter]::ToInt32($d, $e + 8)
        $off = [BitConverter]::ToInt32($d, $e + 12)
        if (-not $types.ContainsKey($w)) { continue }
        $len = [BitConverter]::GetBytes([int]($size + 8)); [Array]::Reverse($len)
        $tag = [Text.Encoding]::ASCII.GetBytes($types[$w])
        $body.Write($tag, 0, 4); $body.Write($len, 0, 4); $body.Write($d, $off, $size)
    }
    $total = [BitConverter]::GetBytes([int]($body.Length + 8)); [Array]::Reverse($total)
    $fs = [IO.File]::Create($target)
    $fs.Write([Text.Encoding]::ASCII.GetBytes('icns'), 0, 4); $fs.Write($total, 0, 4); $body.WriteTo($fs)
    $fs.Close()
}

$icns = Join-Path $outRoot 'Snipyard.icns'
Write-Icns '.\icon.ico' $icns

foreach ($a in $Arch) {
    $rid = "osx-$a"
    $bin = Join-Path $outRoot $a
    if (Test-Path $bin) { Remove-Item -Recurse -Force $bin }

    # The build target adds one before compiling; this makes it land on $version.
    Set-Content .\build.number ($version - 1) -Encoding ascii

    Write-Host "Publishing $rid (1.0.$version)"
    dotnet publish -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -o $bin
    if ($LASTEXITCODE -ne 0) { throw "Publish $rid failed." }

    $plist = Join-Path $bin 'Info.plist'
    @"
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
"@ | Set-Content $plist -Encoding ascii

    $spec = Join-Path $outRoot "$a.mtree"
    @(
        '#mtree'
        'Snipyard.app type=dir mode=0755'
        'Snipyard.app/Contents type=dir mode=0755'
        'Snipyard.app/Contents/MacOS type=dir mode=0755'
        'Snipyard.app/Contents/Resources type=dir mode=0755'
        "Snipyard.app/Contents/MacOS/Snipyard type=file mode=0755 contents=publish-mac/$a/Snipyard"
        "Snipyard.app/Contents/Info.plist type=file mode=0644 contents=publish-mac/$a/Info.plist"
        'Snipyard.app/Contents/Resources/Snipyard.icns type=file mode=0644 contents=publish-mac/Snipyard.icns'
    ) | Set-Content $spec -Encoding ascii

    $tarball = Join-Path $outRoot "Snipyard-macos-$a.tar.gz"
    & "$env:SystemRoot\System32\tar.exe" -czf $tarball "@$spec"
    if ($LASTEXITCODE -ne 0) { throw "Packing $tarball failed." }
    Remove-Item $spec

    $f = Get-Item $tarball
    Write-Host ('  {0}  {1:N1} MB' -f $f.FullName, ($f.Length / 1MB))
}

# Leave the counter where a single publish would have.
Set-Content .\build.number $version -Encoding ascii
Write-Host "Done: version 1.0.$version"
