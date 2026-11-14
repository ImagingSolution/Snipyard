# Release handoff: v1.0.459 (in progress)

Started on the Mac on 2026-11-14; finish it on the Windows PC. Delete this file (and
the pointer to it in CLAUDE.md) in the commit that completes the release.

## State

- A **draft** GitHub Release `v1.0.459` already exists, targeting `master`, with the
  release notes written and these assets uploaded from the Mac:
  - `Snipyard-macos-arm64.tar.gz`
  - `Snipyard-macos-x64.tar.gz`
  Both are `Snipyard.app` bundles built at version 1.0.459. Do **not** rebuild or
  re-upload them; they are final.
- Missing: `Snipyard.exe`. A Windows exe cross-built on the Mac lacks the icon and the
  `FileVersion` resource, so it has to be published on Windows.
- `build.number` is committed at `458`, so the next publish produces 1.0.459.

## Remaining steps (Windows)

```powershell
git pull
# 1. Publish the exe. build.number goes 458 -> 459.
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -o ./publish-single        # (or run publish.bat)

# 2. It must be 1.0.459. If not, stop: the Mac assets are 1.0.459.
(Get-Item .\publish-single\Snipyard.exe).VersionInfo.FileVersion

# 3. Commit the counter and remove this handoff, then push.
#    Delete RELEASE-HANDOFF.md and the "Release handoff" line in CLAUDE.md.
git add build.number RELEASE-HANDOFF.md CLAUDE.md
git commit -m "Bump build number to 1.0.459"
git push

# 4. Attach the exe and publish the draft (the tag is created on master now).
gh release upload v1.0.459 .\publish-single\Snipyard.exe
gh release edit v1.0.459 --draft=false --latest

# 5. Check: latest must be v1.0.459 with all three assets.
gh release view v1.0.459 --json isDraft,assets -q "{draft:.isDraft,assets:[.assets[].name]}"
```

Skip `publish-mac.ps1` this time (step list in CLAUDE.md), since the macOS assets are
already on the draft.
