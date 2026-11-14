# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Language

Always respond to the user in Japanese, regardless of the language used elsewhere in this file or in the codebase.

## Build & Run

```bash
dotnet build                # Build (auto-increments build.number)
dotnet run                  # Run debug build (shows console window)

# Publish self-contained single-file executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single
```

No test framework is configured. Verify changes by building successfully (`dotnet build`).

## Release Process

Whenever a GitHub Release is made (tag `vX.Y.Z`), it MUST carry the published
single-file `Snipyard.exe` as an asset, plus the macOS bundles
`Snipyard-macos-arm64.tar.gz` and `Snipyard-macos-x64.tar.gz` (the macOS
updater picks the one matching its architecture) — the in-app updater (`UpdateService`)
reads `/releases/latest` and only recognizes a release as installable if it
finds that exact asset. A tag with no asset silently breaks update checks for
everyone, since GitHub always serves the newest release as "latest" regardless
of assets.

The app was called Claucraft until the rename, and builds from before it look
for an asset named `Claucraft.exe`. The transition period, when every release
also carried a `Claucraft.exe` copy, is over (ended 2026-10-08 at the user's
request): releases carry `Snipyard.exe` only. Pre-rename installs no longer
see new updates. The current updater still accepts `Claucraft.exe`, but do not
upload one.

Each half has to be built on its own OS:

- `Snipyard.exe` on **Windows**. Cross-built on a Mac it lacks the icon and the
  `FileVersion` resource.
- The macOS tarballs on a **Mac**, with `./publish-mac.sh`. The `.app` bundle
  has to be ad-hoc signed as a whole (`codesign`, macOS only); a bundle where
  only the executable is signed is reported as "damaged" once downloaded and
  cannot be opened at all. `publish-mac.ps1` (Windows) produces exactly that, so
  never upload its output. v1.0.459 first shipped such tarballs and had them
  replaced.

Steps, every time (Windows first, then the Mac):

```bash
# --- Windows ---
git commit ...                      # build.number bumps on the next build
dotnet publish -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o ./publish-single     # (or run publish.bat)
git add build.number && git commit -m "Bump build number to X.Y.Z"
git push
gh release create vX.Y.Z ./publish-single/Snipyard.exe --draft \
    --target master --title vX.Y.Z --notes "..."   # draft: not "latest" yet

# --- Mac ---
git pull
./publish-mac.sh                    # stamps the version in build.number, signs, packs
gh release upload vX.Y.Z ./publish-mac/Snipyard-macos-arm64.tar.gz \
    ./publish-mac/Snipyard-macos-x64.tar.gz
gh release edit vX.Y.Z --draft=false --latest
```

Keep the release a draft until all three assets are on it: a published release
missing the asset for some platform leaves that platform without updates.

The version in the tag must match the `FileVersion` baked into
`publish-single/Snipyard.exe` by that publish (check with
`(Get-Item .\publish-single\Snipyard.exe).VersionInfo.FileVersion`) and the
`CFBundleVersion` in the tarballs' `Info.plist`.

## Architecture

MDI terminal app for Claude Code on Windows and macOS, built with .NET 8.0 / Avalonia 12. All UI runs on a single STA thread.

Platform split: `IPseudoConsole.Create()` picks ConPTY (`PseudoConsole`) on Windows and forkpty (`UnixPseudoConsole`) elsewhere; off Windows `ShellHost` emits a POSIX command line that the login shell runs (`$SHELL -l -c`). Windows-only features check `OperatingSystem.IsWindows()`: the WebView2 preview falls back to the system browser, Explorer calls go through `PlatformShell`, notifications use osascript/afplay, and the recycle bin is `~/.Trash`. `publish-mac.ps1` on Windows only proves the macOS builds compile and package; release tarballs come from `publish-mac.sh` on a Mac.

### Terminal Pipeline

```
PseudoConsole (ConPTY P/Invoke)
  → OutputReceived event (raw bytes)
    → VtParser.Process() (ANSI/VT state machine, UTF-8 decode)
      → TerminalBuffer (cell grid + scrollback + SGR state)
        → TerminalControl.Render() (Avalonia DrawingContext)
```

- **PseudoConsole**: Windows ConPTY wrapper via P/Invoke. Manages process lifecycle, pipes, resize.
- **VtParser**: State machine (Normal/Escape/Csi/Osc/Dcs). Handles SGR with 256-color and truecolor.
- **TerminalBuffer**: 2D `TerminalCell[,]` grid + `List<TerminalCell[]>` scrollback (10K lines). Tracks wide-char pairs, line-wrap state, alternate buffer, and bracketed paste mode.
- **TerminalControl**: Custom Avalonia `Control`. Handles rendering, text selection, scrollbar, IME input, file drag-and-drop, clipboard image paste.

### MDI Window Management

`AppShell` (hosted by `MainWindow`) manages children via `List<MdiChildInfo>`. Each `MdiChildInfo` record holds the visual container (Border), title bar, TerminalControl, strip button (tab), and project folder context. Layout modes: Maximize, Tile, TileHorizontal, TileVertical, Cascade.

Switching active child triggers project context switching — the toolbar, explorer, session list, and git status all update to reflect that child's project folder.

### Services

- **AppSettings** / **SnippetStore**: JSON persistence to `%APPDATA%\Snipyard/` (`AppPaths` copies an old `%APPDATA%\Claucraft` across on first start)
- **SessionService**: Reads Claude Code JSONL session files from `~/.claude/projects/` (read-only)
- **UsageTracker**: Aggregates today's usage from the Claude Code session transcripts (via CostAnalytics) on a 30-second polling interval (read-only)
- **Localization**: Static dictionary-based EN/JP localization via `Loc.Get("key")`

### Key Conventions

- File-scoped namespaces throughout
- Nullable reference types enabled
- Unsafe blocks allowed (for P/Invoke in PseudoConsole)
- Version scheme: `1.0.{auto-increment}` from `build.number` file
- Debug builds output to console (`Exe`), Release builds are windowless (`WinExe`)
- All localized strings go in `Services/Localization.cs` with both EN and JP entries
