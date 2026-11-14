# Snipyard

*Formerly Claucraft.*

An MDI (Multiple Document Interface) terminal application for AI coding CLIs on Windows and macOS, built with Avalonia UI.

[Claude Code](https://docs.anthropic.com/en/docs/claude-code) is the default, and Codex CLI, GitHub Copilot CLI, Antigravity CLI and Grok CLI can be picked per session. Run several sessions side by side in one window - or dragged out into windows of their own - each viewable as a raw terminal or as a desktop-app-style Chat View, with a project explorer, a git/GitHub panel, snippets, cost and usage readouts, and a dark/light theme.

![MDI Windows](screenshots/mdi-windows.png)

![Explorer](screenshots/explorer.png)

![Snippets](screenshots/snippets.gif)

## Features

### Windows and layout

- **Welcome Page** - VS Code-style startup page with new project, previous project, and recent projects list. Previous/recent projects automatically resume with `-c`
- **MDI Terminal Windows** - Open multiple sessions in child windows arranged as Tile / Tile Horizontally / Tile Vertically / Full View
- **Docking Layout** - Window positions are a dock tree, not coordinates: panes split horizontally or vertically and the splitters between them are draggable
- **Tab Drag** - Drag a window by its tab to reorder it along the strip, dock it into another pane, move it across to another window, or drop it clear of everything to give it an OS window of its own. The session keeps running through the move
- **Windows Panel** - Side panel listing every open window with a status dot and the conversation summary (preferred over the terminal title). Terminal output preview on hover. Click to switch, × to close
- **Agent View** - Background sessions (`claude agents`) are listed under the window they were dispatched from, with their state, what they are waiting on, and whether a process is still alive. Read straight off `~/.claude/jobs` rather than by running the CLI, so the 700 ms panel refresh stays free
- **Subagent Monitor** - The same panel shows a session's in-flight subagents (label, type, model, depth) while they run
- **Tab Management** - Right-click context menu (Close / Close Others / Close to Right / Duplicate / Export Output). Double-click to rename. Auto-names from the first user input or the session summary
- **Named Workspaces** - Save and restore any number of layouts. Restoring reopens the same transcripts with `-r`, so the conversations come back instead of blank sessions

### Sessions and AI CLIs

- **CLI Provider Support** - Claude Code, Codex CLI, GitHub Copilot CLI, Antigravity CLI and Grok CLI, each with its own executable (`claude`, `codex`, `copilot`, `agy`, `grok`), command-line shape, one-shot invocation, config folder and launch profiles. Switchable per session from the toolbar; the executable and the new/continue/resume argument templates are editable in Settings
- **Host Shell** - On Windows, launch the CLI under `cmd.exe` or PowerShell. Argument quoting follows whichever shell is chosen, and a PowerShell that is not installed falls back to `cmd.exe` instead of leaving a dead tab. On macOS the CLI always runs in your login shell (`$SHELL -l -c`), so the `PATH` from your shell profile applies
- **Session Management** - Resume previous sessions with AI-generated titles (from `sessions-index.json`) and timestamps. The picker is searchable, and a session can be thrown away from it
- **Session Index Auto-Creation** - Creates and updates `sessions-index.json` per project so the CLI can fill in AI-generated conversation summaries
- **Running Session Guard** - A session only runs in one place at a time, so launching one another process already holds is blocked before it can die at the prompt
- **Project Context Switching** - Switching MDI windows switches the project folder, explorer, session list and git status with it
- **Execution Targets (WSL / SSH)** - The New Session menu lists installed WSL distros (Windows only), recent and `~/.ssh/config` SSH hosts, and a `host:/path` prompt. The CLI runs through `wsl.exe` / `ssh` with each argument escaped for the remote side. WSL windows read their transcript through `\\wsl.localhost`, so Chat View works; SSH windows stay in the terminal view. Remote windows are left out of saved workspaces
- **Scheduled Tasks** - Open a new session in a chosen project with a prompt - once, daily or every N minutes - while the app is running. A slot missed by more than 30 minutes is skipped rather than caught up
- **Remote Control** - A session tab's menu sends `/remote-control` to continue the session from another device
- **Launch Profiles** - Light / Standard / Deep from the toolbar, applied on every launch path - new, `-c` continue and `-r` resume alike - so a resumed session cannot silently balloon back to an unbounded context. Light ships its own `--settings` file that trims MCP servers, tool descriptions and auto-memory out of the prompt prefix; Deep switches to `--model opus --effort high`
- **Launch Model & Effort** - Pin a model and effort level when a session starts (Claude only) instead of switching mid-conversation and throwing away the prompt cache. The choice carries across new windows, restores and launches
- **Git Worktree Isolation** - Give a session its own `git worktree` checkout (under a Snipyard-managed folder, on `snipyard/`-prefixed branches) so several windows on one repo can work in parallel without clobbering each other
- **Session Hand-off** - When context runs low (an absolute token threshold, so it still fires on 1M-context models), hand off to a fresh session with a brief extracted from the transcript locally. It costs no tokens, unlike `/compact`, and the brief lands in the new session's input box to be edited before anything is sent
- **Resume Cost & Token Badge** - The session picker shows each session's last known context size as a badge, with a tooltip estimate of what resuming it will cost. Right-click for "Start fresh from brief" to hand off without reopening the old session

### Terminal

- **VT100/ANSI Terminal** - Custom parser over Windows ConPTY (a forkpty pseudo-terminal on macOS): SGR with 256-colour and truecolour, alternate buffer, scroll regions, wide characters, Unicode code points and zero-width combining marks, and a 10,000-line scrollback
- **Caret and Line Editing** - A blinking insert-bar caret, click to move it, Shift+arrows to select, and undo/redo of the CLI's input line
- **Terminal Search (Ctrl+F)** - Full-text search across output and scrollback with match highlighting, navigation, regex mode and a case-sensitive toggle
- **Prompt Navigation (Ctrl+↑/↓)** - Move between user questions with a position counter (Q 2/5). Tracks input positions live and scans buffer separators for past conversations
- **Font Zoom (Ctrl+Scroll)** - Change font size with the wheel; Ctrl+0 resets
- **Expanded Input Panel** - Multi-line input in a drag-resizable panel. Enter for newline, Ctrl+Enter to send, Escape to collapse
- **@-Mention Completion** - Typing `@` completes file paths from a project file index, and files can be picked from the input row
- **Shift+Enter Line Break** - Insert a newline without submitting
- **File Drag & Drop** - Drop files onto the terminal to insert their paths, or drag them in from the Explorer panel
- **Image Attachments** - Clipboard images (Ctrl+V / Cmd+V on macOS, or Alt+V) and pasted or dropped image files go to a thumbnail strip above the input instead of being inserted as text. Click a thumbnail to view it, × to remove it; the images are sent with the prompt
- **Bracketed Paste Mode** - Wraps pasted text in bracket sequences for modern shells
- **Terminal Output Export** - Save output as a text file from the tab context menu

### Project and files

- **Project Explorer** - File tree with syntax-aware icons and colour-coded file types (40+ extensions), Windows shell icons (on Windows), auto-refresh on file system changes, and file preview on selection. Ctrl/Shift+click selects several items, and dragging them drops every path into the terminal as `@`-references
- **In-App Text Editor** - Edit text files straight from the explorer (Ctrl+S to save), preserving the original encoding, line endings and BOM. Binary, oversized or non-UTF8 files stay read-only instead of getting corrupted
- **Open With** - Right-click a file to hand it to the Windows "Open with" picker, or the macOS application chooser
- **Safe Delete** - Deleting from the explorer sends to the Windows Recycle Bin (the Trash on macOS) rather than removing permanently

### Source control

- **Source Control Panel (Ctrl+Shift+G)** - Unified git + GitHub sidebar: Fetch / Pull (rebase) / Push with a quiet 5-minute background auto-fetch, branch switch/create/delete/merge, staging per file or all, and banners for conflicts or an interrupted rebase/merge with an "Ask the AI" button that drafts a JA/EN prompt naming the affected files. Every operation is non-destructive by design - no force-push, `reset --hard` or `branch -D`
- **AI Commit Messages** - Draft a commit message in English or Japanese from the staged diff, generated locally through the active CLI's one-shot mode without joining the live session
- **Secret Scan** - Before a commit, the staged diff is scanned file by file for credentials and keys, and flagged files are called out rather than quietly committed
- **Risky File Warnings** - Staging policy flags files that were probably not meant to be committed - credential files, build output - and a per-clone ignore list keeps the ones you have decided about out of the way
- **GitHub Pull Requests** - List open PRs, approve, create and open in the browser via `gh`, right inside the panel. Hides itself if `gh` isn't installed/authenticated or the remote isn't GitHub
- **CI Status & Review Comments** - Each open PR shows a coloured dot for its combined check status, polled every minute while the panel is open. A fold-out lists failing checks and every review, conversation and line comment, with buttons that hand them to the AI to fix
- **Repository Creation** - Pushing a repo with no remote opens an owner / name / description / private dialog and creates the GitHub repository with `gh repo create`
- **Git Error Hints** - Common git failures get a localized explanation above git's own output, and branches checked out in another worktree are greyed out in the branch menus with the reason
- **Commit Graph & Diff Viewer** - Full commit history as a lane graph (as an MDI child or its own window, with Fetch / Pull / Push of its own) with lines coloured per branch, tag creation, plus a virtualized diff viewer. Select a diff range or right-click a changed file to send `@path:12-20 <comment>` straight to the terminal

### Knowing what the AI is doing

- **Mode Badge** - Shows the session's mode (manual / auto-accept / plan / bypass) in the status bar, painted the colour the CLI paints it. Click to cycle. If a CLI update renames the modes, it falls back to "Switch Mode" and still sends Shift+Tab
- **Model & Effort** - The active model and reasoning effort are shown in the status bar and switchable from it, and a model or effort switch made inside the CLI is followed (effort is read off the transcript once a reply carries it). The model list learns the newest version of each model line from recent transcripts, so it does not go stale when a new model ships
- **Activity Indicator** - Says what the AI is doing while it is quiet - reading a file, running a command, searching - with elapsed time, plus a sweep bar under the input row while a turn is in flight
- **Context Meter** - Reads the context left out of the CLI's own output and shows it as a meter, with one-click `/compact` when it runs low
- **Cache Expiry Warning** - An idle session close to its prompt-cache expiry gets a "compact now" banner, and switching model or effort mid-session compacts first when there is enough context worth saving
- **Marginal Cost Readout** - What the last turn cost, and what the next one costs just to re-read the conversation, so a session that has grown expensive says so instead of being discovered on the invoice
- **Usage in Plain Words** - "about 840 left, resets in 5h" instead of a bare message count, measured against the plan (Pro / Max 5x / Max 20x) chosen in Settings. Aggregated from the session transcripts themselves
- **Rate Limit Readout** - Utilization and reset countdown for both the 5-hour and 7-day plan windows. Claude sessions started from Snipyard get a status line that saves the `rate_limits` Claude Code itself reports (Pro/Max, after the first reply); a status line of your own keeps showing. It can be turned off in Settings > Live status, at the cost of the readout, and while it is on Claude Code hides its footer key hints. A `claude_usage_cache.json` left in the temp folder by your own script is read as a fallback. Snipyard never uses your Claude sign-in itself
- **Permission Prompts, Explained** - The approval overlay says what the command actually does and rates it read-only / changes files / deletes or reaches the network
- **Error Diagnosis Banner** - Known failures (signed out, rate limited, usage limit, network down, outdated CLI) surface as a banner with the fix one click away, and advisory banners hide themselves again
- **Stop Button** - A stop control on the input row of whichever window is working, for anyone who does not know Escape interrupts the AI
- **Turn-End Frame Blink** - The active window's blue frame blinks twice when the AI hands the turn back, so a finished session is visible at a glance across tiled windows
- **Task Completion Notification** - A tray toast, a sound and a taskbar flash when a session finishes in the background, including when a turn finishes generating. On macOS it is a Notification Center banner and a system sound. All can be turned off in Settings
- **Status Bar** - Repository name, branch, changed file count, terminal status, mode badge, model, current activity, context left, rate limits, and daily usage with a colour-gradient progress bar

### Chat View

- **Conversation View** - Toggle any window between the raw terminal and a Chat View that renders the session's JSONL transcript as desktop-app-style bubbles, Markdown, code blocks and collapsible tool-call summaries, in matching dark and light palettes. It spans the full window width and the choice is kept per window across restores
- **Tool Call Details** - Expand a tool group to see each call: commands with their output, Edit/Write as a coloured diff, Read/Grep/Glob targets with result excerpts, and anything else as input JSON and result. Thinking and the narration between tool calls are shown too. A file a tool worked on opens in the editor window with one click
- **Answering the CLI** - Permission prompts, plan approval, menus such as `/model`, and AskUserQuestion forms appear as interactive cards; the answer is sent to the CLI's selector as key presses
- **Composer** - Send prompts from a card at the bottom with image attachments, `@` file completion and `/` command completion (↑↓ / Tab / Enter / Esc). The CLI's suggested next prompt is shown as the placeholder: Tab pulls it in, Enter on an empty box sends it
- **Stop & Queue** - A spark shows while Claude is working, next to a Stop button. A prompt sent mid-turn waits as a removable chip and goes out once the turn ends; Stop puts queued prompts back in the box
- **Edit & Rewind** - Hover a prompt for Edit and Rewind. Both drive the CLI's `/rewind` to that prompt; Edit also puts the text back into the box
- **Tasks & Subagents** - A strip under the title rebuilds Claude's task checklist from TodoWrite / TaskCreate / TaskUpdate and lists every Agent call. A subagent with its own transcript opens in place of the session, with a bar to go back
- **Find (Ctrl+F)** - Highlights every message holding the query; Enter / Shift+Enter or F3 step through the matches
- **Side Pane** - A resizable pane next to the conversation with two tabs: **Terminal** (the same session's live terminal) and **Preview** (an embedded WebView2 browser with an address bar, back, reload and open-in-browser; a localhost address seen in the terminal output is offered as a one-click suggestion). On macOS the page opens in the system browser instead

### Panels, commands and tools

- **Command Palette (Ctrl+Shift+P)** - VS Code-style searchable action menu covering every command
- **Slash Commands Panel (Ctrl+/)** - Every slash command the CLI understands, listed with a description. Custom commands from `.claude/commands/*.md` are picked up automatically
- **Extensions Panel** - Configured MCP servers, skills and plugins, with enable/disable toggles where the CLI supports it. MCP servers can be added (stdio / http, with a scope), removed and checked through `claude mcp`. Double-click a skill to run it
- **Memory Panel** - Reads Claude Code's auto-memory notes for a project (`~/.claude/projects/<project>/memory/`), grouped by type, searchable, with the `MEMORY.md` index, backlinks, and links to notes not written yet
- **Diagram Viewer** - Renders Excalidraw diagrams drawn via MCP tool calls, and mermaid/chart code blocks from the output, in a zoomable, pannable window with PNG export/copy. Cached per project so they survive a resume or restart
- **Snippets Panel** - Store and send code snippets to the active console (`\r` in text sends Enter). Drag-and-drop reordering, and it starts pre-filled with common prompts in English or Japanese rather than empty
- **Tokens & Cost Dashboard** - A window reading `message.usage` out of session transcripts: 7/30/90-day range, a "this project only" scope toggle, a daily cost bar chart, and top-50 breakdowns by model, project and session, with CSV export
- **Usage Chart** - 14-day chart of messages, tool calls and sessions
- **Automatic Checkpoints** - Snapshots the project before each prompt so the work can be rolled back. Git repos use `git stash create`, which builds a commit object without touching the working tree; other folders get a file snapshot
- **Setup Check** - One-click diagnosis of the CLI, Node.js, Git, the config folder and sign-in state, with a copyable fix command for anything missing (`winget` on Windows, `brew` / `xcode-select` on macOS). Runs itself on first launch and stays quiet when everything is fine
- **Keyboard Shortcut Sheet (F1)** - Every shortcut on one screen, grouped by what it acts on
- **Built-in Help** - The "?" button opens illustrated English/Japanese help pages in the browser, including a comparison with Claude Desktop, the Claude Code CLI and the VS Code extension
- **Settings Panel** - Grouped into categories: language, font family and size, host shell, initial prompt, theme, notifications, checkpoints, live status, commit message language, plan, launch model and effort, AI provider executable and argument templates, and a shortcut to the `.claude` folder
- **Self-Update** - Checks the project's GitHub releases for a newer version, downloads it next to the running executable and verifies it before committing to anything, then steps the old executable aside by renaming it and restarts into the new one - undoing the rename if the swap fails, so there is no state with the application removed but not put back. On macOS it takes the release tarball for its architecture, replaces the executable inside `Snipyard.app` and relaunches the bundle. Only the published build can replace itself; a development build points at the releases page instead

### Everything else

- **Dark / Light Theme** - Toggle in Settings, carried through every panel, the terminal palette and the MDI chrome
- **Localization** - English and Japanese (日本語) throughout

## Keyboard Shortcuts

On macOS the app's shortcuts use Cmd, and Ctrl+letter always goes to the CLI (Ctrl+C interrupts, Ctrl+W deletes a word, and so on). The F1 sheet and tooltips show the keys for the platform you are on.

| Windows | macOS | |
|---|---|---|
| **Windows and tabs** | | |
| Ctrl+N | Cmd+N | New session |
| Ctrl+W | Cmd+W | Close |
| Ctrl+Tab / Ctrl+Shift+Tab | Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| **Panels** | | |
| Ctrl+Shift+E | Cmd+Shift+E | Explorer |
| Ctrl+Shift+G | Cmd+Shift+G | Source control |
| Ctrl+Shift+M | Cmd+Shift+M | Memory |
| Ctrl+Shift+P | Cmd+Shift+P | Command palette |
| Ctrl+/ | Cmd+/ | Slash commands |
| F1 | F1 | Keyboard shortcuts |
| **Terminal** | | |
| Ctrl+F | Cmd+F | Search |
| Ctrl+↑ / Ctrl+↓ | Cmd+↑ / Cmd+↓ | Jump between prompts |
| Ctrl+Scroll | Cmd+Scroll | Zoom font |
| Ctrl+0 | Cmd+0 | Reset font size |
| Shift+Enter | Shift+Enter | New line without sending |
| Ctrl+Enter | Cmd+Enter | Send from the expanded input |
| Esc | Esc | Stop the task |
| Ctrl+C | Cmd+C | Copy (on Windows: interrupt when nothing is selected) |
| | Ctrl+C | Interrupt, even with a selection |
| Ctrl+V | Cmd+V | Paste, images included |
| Ctrl+X | Cmd+X | Cut the selection |
| Ctrl+Z / Ctrl+Shift+Z | Cmd+Z / Cmd+Shift+Z | Undo / redo on the CLI's input line |
| Alt+V | Option+V | Paste a clipboard image |
| Shift+Tab | Shift+Tab | Cycle the AI mode |
| Ctrl+S | Cmd+S | Save the file open in the editor |
| **Chat View** | | |
| Ctrl+F | Cmd+F | Find in the conversation |
| Enter / Shift+Enter, F3 | Enter / Shift+Enter, F3 | Next / previous match |
| Tab | Tab | Take the suggested prompt into the box |

## Tech Stack

| Component | Technology |
|---|---|
| Framework | .NET 8.0 / C# |
| UI | Avalonia 12.1.1 + Fluent Theme + Inter fonts |
| Terminal | Custom VT100/ANSI parser over PseudoConsole (ConPTY) on Windows, forkpty on macOS |
| Browser preview | Microsoft Edge WebView2 (Windows; the system browser on macOS) |
| Serialization | System.Text.Json |
| External tools | The AI CLI itself, `git`, `gh` for pull requests, `wsl.exe` / `ssh` for remote sessions |

## Project Structure

```
Snipyard/
├── Program.cs                      # Application entry point
├── App.axaml / .cs                 # Application root and theme resources
├── MainWindow.axaml / .cs          # The application window - hosts one AppShell
├── AppShell.axaml / .cs            # The shell: toolbar, activity bar, panels, MDI area, status bar
├── AppShell.Update.cs              # Self-update flow (check, download, stage, restart)
├── AppShell.Schedule.cs            # Scheduled tasks dialog and timer
├── AppShell.Mcp.cs                 # MCP server add/remove/check through `claude mcp`
├── MdiLayoutItem.cs                # Interface every MDI child implements
├── FileTreeNode.cs                 # Explorer tree node model
├── UsageChartWindow.axaml / .cs    # 14-day usage chart dialog
├── Terminal/
│   ├── TerminalControl.cs          # Terminal control: render, select, caret, IME, search, drag & drop
│   ├── TerminalControl.SidePane.cs # Chat View side pane (Terminal / Preview)
│   ├── TerminalBuffer.cs           # Cell grid + scrollback, alternate buffer, bracketed paste
│   ├── TerminalCell.cs             # Cell model (Unicode code point, colours, attributes)
│   ├── VtParser.cs                 # ANSI/VT state machine, SGR 256-colour and truecolour
│   ├── PseudoConsole.cs            # Windows ConPTY P/Invoke wrapper
│   ├── UnixPseudoConsole.cs        # macOS forkpty pseudo-terminal, run through the login shell
│   ├── CodeBlockDetector.cs        # Finds mermaid/chart blocks and Excalidraw MCP calls
│   └── DiagramWindow.cs            # Zoomable, pannable diagram viewer
├── Controls/
│   ├── ShellWindow.cs              # A torn-off window holding its own shell
│   ├── DetachedWindow.axaml / .cs  # Window host for a dragged-out pane
│   ├── DockTree.cs                 # Split/leaf tree that says where each MDI child sits
│   ├── DockHost.cs                 # Renders the dock tree with draggable splitters
│   ├── TabDrag.cs                  # Tab drag: reorder, dock, move across windows, tear off
│   ├── SourceControlPanel.cs       # Unified git + GitHub sidebar panel
│   ├── CommitGraphPanel.cs         # Commit graph as an MDI child, with fetch/pull/push
│   ├── CommitGraphView.cs          # Lane-graph drawing control
│   ├── DiffWindow.cs               # Virtualized diff viewer with line-range comments
│   ├── CostDashboardWindow.cs      # Tokens & cost dashboard
│   ├── DocumentViewPanel.cs        # Chat View: transcript bubbles, tool details, cards, find
│   ├── ChatSidePane.cs             # Chat View side pane tabs
│   ├── BrowserPreviewPanel.cs      # WebView2 preview of local web servers
│   ├── ImageAttachmentStrip.cs     # Thumbnail strip for attached images
│   ├── MemoryPanel.cs              # Auto-memory notes panel
│   ├── MemoryNoteView.cs           # One memory note rendered with its links
│   └── MarqueeBar.cs               # Turn-in-progress sweep bar
├── Services/
│   ├── AppSettings.cs              # Configuration persistence
│   ├── AppPaths.cs                 # App data folders, and the one-time copy from Claucraft
│   ├── PlatformShell.cs            # Explorer on Windows, Finder / "Open with" on macOS
│   ├── Shortcut.cs                 # Ctrl on Windows, Cmd on macOS for app shortcuts
│   ├── Localization.cs             # EN/JP string localization
│   ├── SnippetStore.cs             # Snippet storage
│   ├── WorkspaceService.cs         # Named workspace save/restore
│   ├── CliProvider.cs              # CLI provider / launch-profile data model
│   ├── CliProviderService.cs       # Provider registry, command building, argument quoting
│   ├── CliOneShotRunner.cs         # One throwaway answer from the CLI, with no session state
│   ├── ShellHost.cs                # Which shell (cmd.exe / PowerShell / login shell) a session launches inside
│   ├── ProcessRunner.cs            # Shared process-launch plumbing
│   ├── ProcessTree.cs              # Walks child processes to find a window's CLI process
│   ├── SessionService.cs           # Session list and sessions-index management
│   ├── SessionMessageReader.cs     # JSONL transcript parser
│   ├── ChatTaskTracker.cs          # Task checklist and subagents rebuilt from the transcript
│   ├── ClaudeProjectPaths.cs       # Maps a project folder to its ~/.claude/projects folder
│   ├── ExecutionTarget.cs          # Local / WSL / SSH launch targets
│   ├── ScheduleService.cs          # Scheduled task storage
│   ├── McpService.cs               # `claude mcp` wrapper
│   ├── MemoryStore.cs              # Auto-memory note reader
│   ├── ModelCatalog.cs             # Newest model versions learned from transcripts
│   ├── SessionCostMonitor.cs       # Live per-session cost and context tracking
│   ├── RunningSessionService.cs    # Detects a session already held by another process
│   ├── SubagentMonitor.cs          # In-flight subagent tracking
│   ├── AgentViewMonitor.cs         # Background agent sessions, read from ~/.claude/jobs
│   ├── HandoffBuilder.cs           # Session hand-off brief builder
│   ├── WorktreeService.cs          # Per-session git worktree isolation
│   ├── CheckpointService.cs        # Pre-prompt snapshots (stash object / file copy)
│   ├── UsageTracker.cs             # Today's usage, aggregated from transcripts
│   ├── RateLimitService.cs         # 5h/7d plan rate-limit windows
│   ├── CostAnalytics.cs            # Cost/token aggregation for the dashboard
│   ├── TerminalInsight.cs          # Mode/activity inference and error diagnosis
│   ├── CommandExplainer.cs         # Plain-language permission-prompt explanations
│   ├── SetupDoctor.cs              # Environment diagnostics (Setup Check)
│   ├── SlashCommandCatalog.cs      # Slash command listing
│   ├── ExtensionCatalog.cs         # MCP server / skill / plugin listing
│   ├── ProjectFileIndex.cs         # @-mention file path index
│   ├── TextFileEditor.cs           # Explorer's in-app text file read/write
│   ├── RecycleBin.cs               # Safe delete via the Windows Recycle Bin / macOS Trash
│   ├── ShellIconProvider.cs        # Windows Explorer icons, cached, as Avalonia images
│   ├── MarkdownParser.cs           # Lightweight Markdown renderer
│   ├── DiagramCache.cs             # Persists Excalidraw diagrams per project
│   ├── NotificationService.cs      # Tray notifications and sound (Notification Center on macOS)
│   ├── UpdateService.cs            # GitHub release check, download and staging
│   ├── CommitMessageService.cs     # AI-drafted commit messages (EN/JP)
│   ├── SecretScanService.cs        # Per-file secret scan of the staged diff
│   ├── StagingPolicy.cs            # Flags files that probably should not be committed
│   ├── GitIgnoreService.cs         # Per-clone ignore list for those warnings
│   ├── GitHubCli.cs                # `gh` wrapper for pull requests, CI status and repo creation
│   ├── GitErrorHints.cs            # Localized explanations of common git failures
│   ├── UnifiedDiffParser.cs        # Unified diff parser
│   └── GitCli.cs / GitWriteService.cs / GitChangeService.cs / GitLogService.cs / GitPath.cs / CommitGraphLayout.cs
│                                   # Git process wrapper, write ops, status/log readers, path decoding, graph layout
├── Help/                           # Embedded EN/JP help pages and screenshots
├── icon.ico / icon.png             # Application icon
├── app.manifest                    # Application manifest (Windows)
├── publish.bat                     # One-click self-contained Windows release build
├── publish-mac.sh                  # macOS Snipyard.app tarballs (arm64 / x64), signed; run on a Mac
├── publish-mac.ps1                 # Windows-side check that the macOS builds compile and package (unsigned)
├── run-mac.command                 # Double-click in Finder to build and run on macOS
└── build.number                    # Auto-incrementing build number
```

## Requirements

- Windows 10 or later, or macOS 11 (Big Sur) or later on Apple Silicon or Intel
- Node.js - the supported CLIs are npm packages
- At least one AI CLI: [Claude Code](https://docs.anthropic.com/en/docs/claude-code) (default), Codex CLI, GitHub Copilot CLI, Antigravity CLI or Grok CLI
- `git` (optional) - for the source control panel, commit graph and worktree isolation
- `gh` (optional) - for pull requests, CI status and repository creation
- Microsoft Edge WebView2 Runtime (optional, Windows only, preinstalled on Windows 11) - for the Chat View browser preview
- WSL (optional, Windows only) or an OpenSSH client - for WSL / SSH sessions

## Installing on macOS

The quickest way is one command in Terminal. It downloads the latest release for your Mac (Apple Silicon or Intel) and puts `Snipyard.app` in Applications. A file fetched this way is not marked as downloaded, so it opens without the Gatekeeper warning; running it again replaces an existing copy.

```bash
curl -fsSL "https://github.com/ImagingSolution/Snipyard/releases/latest/download/Snipyard-macos-$([ "$(uname -m)" = arm64 ] && echo arm64 || echo x64).tar.gz" | tar -xz -C /Applications
```

Or download `Snipyard-macos-arm64.tar.gz` (Apple Silicon) or `Snipyard-macos-x64.tar.gz` (Intel) from the releases page, extract it and move `Snipyard.app` to Applications. The app is not notarized by Apple, so a copy downloaded with a browser is blocked on first launch ("Apple could not verify..."): allow it under System Settings > Privacy & Security, or run `xattr -dr com.apple.quarantine /Applications/Snipyard.app`. Later updates come through the in-app updater.

The CLI is started through your login shell, so a `claude` installed with npm, Homebrew or the native installer is found as long as your shell profile puts it on `PATH`.

## Build

```bash
# Build (auto-increments build.number)
dotnet build

# Run
dotnet run

# Publish single-file executable (or just run publish.bat)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single

# macOS Snipyard.app tarballs for arm64 and x64, ad-hoc signed (run on a Mac)
./publish-mac.sh
```

On a Mac, install the .NET SDK (8.0 or later) and run `dotnet run`, or double-click `run-mac.command` in Finder, which expects the SDK in `~/.dotnet`.

## Data Locations

On macOS, `%APPDATA%\Snipyard` and `%LOCALAPPDATA%\Snipyard` are both `~/Library/Application Support/Snipyard/`, and the paths under `~/.claude` are the same as on Windows.

| Data | Path |
|---|---|
| Settings | `%APPDATA%\Snipyard\appsettings.json` |
| Snippets | `%APPDATA%\Snipyard\snippets.json` |
| Workspaces | `%APPDATA%\Snipyard\workspace.json` |
| CLI providers | `%APPDATA%\Snipyard\providers.json` |
| Light profile settings | `%APPDATA%\Snipyard\light-settings.json` |
| Slash command overrides | `%APPDATA%\Snipyard\slashcommands.json` (optional) |
| Scheduled tasks | `%APPDATA%\Snipyard\schedules.json` |
| Help pages | `%APPDATA%\Snipyard\Help\` |
| Checkpoints | `%APPDATA%\Snipyard\checkpoints\` |
| Session worktrees | `%LOCALAPPDATA%\Snipyard\worktrees\` |
| Rate limits (from the status line) | `%LOCALAPPDATA%\Snipyard\rate-limits.json` |
| Session index (read/write) | `~/.claude/projects/*/sessions-index.json` |
| Session transcripts (read-only) | `~/.claude/projects/*/*.jsonl` |
| Cached diagrams | `~/.claude/projects/*/diagrams/` |
| Auto-memory notes (read-only) | `~/.claude/projects/*/memory/` |
| Background agents (read-only) | `~/.claude/jobs/` |
| CLI settings and plugins (read-only) | `~/.claude/settings.json`, `~/.claude.json`, `~/.claude/plugins/installed_plugins.json` |
| Project MCP servers and skills (read-only) | `<project>/.mcp.json`, `<project>/.claude/settings.local.json`, `<project>/.claude/commands/*.md` |

**Upgrading from Claucraft:** the first start copies `%APPDATA%\Claucraft` to `%APPDATA%\Snipyard`, so settings, snippets, providers, schedules and checkpoints carry over. The old folder is left in place and can be deleted once you no longer need to go back. Worktrees made before the rename stay under `%LOCALAPPDATA%\Claucraft\worktrees\` and are still recognised. An install updated in place keeps its `Claucraft.exe` file name; rename it to `Snipyard.exe` whenever convenient.

## Disclaimer

Snipyard is an unofficial, independent project. It is not affiliated with, endorsed by, or sponsored by Anthropic. Claude and Claude Code are trademarks of Anthropic, PBC; other product names mentioned here are trademarks of their respective owners.

Snipyard does not bundle Claude Code. It runs the copy you installed, signed in with your own account.

## License

MIT

---

# Snipyard (日本語)

*旧名 Claucraft。*

Avalonia UI で構築された、Windows と macOS で動く AI コーディング CLI 用の MDI（マルチドキュメントインターフェース）ターミナルアプリケーションです。

既定は [Claude Code](https://docs.anthropic.com/en/docs/claude-code) で、Codex CLI・GitHub Copilot CLI・Antigravity CLI・Grok CLI をセッションごとに選択できます。複数のセッションを1つのウィンドウに並べて（あるいは別ウィンドウに切り離して）実行でき、各セッションは生のターミナルとデスクトップアプリ風のチャットビューを切り替えて表示できます。プロジェクトエクスプローラー、git / GitHub パネル、スニペット、コストと使用量の表示、ダーク/ライトテーマを備えています。

## 機能

### ウィンドウとレイアウト

- **ウェルカムページ** - VS Code 風の起動画面。新規プロジェクト、前回のプロジェクト、最近使用したプロジェクト一覧を表示。前回/最近のプロジェクトは `-c` で自動継続
- **MDI ターミナルウィンドウ** - 複数セッションを子ウィンドウで表示。タイル / 横並べ / 縦並べ / 最大表示に対応
- **ドッキングレイアウト** - ウィンドウ配置は座標ではなくドックツリーで管理。ペインを水平・垂直に分割でき、境界はドラッグでリサイズ可能
- **タブドラッグ** - タブを掴んでストリップ内で並べ替え、別ペインへドッキング、別ウィンドウへ移動、どこにも重ならない場所へ落とせば独立した OS ウィンドウとして切り離し。移動中もセッションは動き続ける
- **ウィンドウパネル** - 開いている全ウィンドウを状態ドットと会話要約（ターミナルタイトルより優先）付きで一覧表示。ホバーでターミナル出力プレビュー。クリックで切替、×で閉じる
- **Agent View** - バックグラウンドセッション（`claude agents`）を、起動元のウィンドウの下に状態・待機理由・プロセスの生死とともに表示。CLI を実行せず `~/.claude/jobs` から直接読むため、700ms ごとのパネル更新でもコストがかからない
- **サブエージェントモニター** - 同じパネルに実行中のサブエージェント（ラベル・種類・モデル・深さ）を表示
- **タブ管理** - 右クリックメニュー（閉じる / 他を閉じる / 右側を閉じる / 複製 / エクスポート）。ダブルクリックで名前変更。最初のユーザー入力またはセッション要約から自動命名
- **名前付きワークスペース** - レイアウトを任意の数だけ保存・復元。復元時は `-r` で同じセッションを再開するため、新規セッションではなく会話が戻る

### セッションと AI CLI

- **CLI プロバイダー対応** - Claude Code / Codex CLI / GitHub Copilot CLI / Antigravity CLI / Grok CLI。それぞれ独自の実行ファイル（`claude` / `codex` / `copilot` / `agy` / `grok`）・コマンドライン形式・ワンショット実行・設定フォルダ・起動プロファイルを持つ。ツールバーからセッションごとに切替でき、実行ファイルと新規/継続/再開の引数テンプレートは設定パネルで編集可能
- **ホストシェル** - Windows では CLI を `cmd.exe` と PowerShell のどちらで起動するかを選択。引数のクォートは選んだシェルに追従し、PowerShell が未インストールの場合はタブが死なないよう `cmd.exe` にフォールバック。macOS では常にログインシェル（`$SHELL -l -c`）で起動するため、シェルのプロファイルで設定した `PATH` がそのまま使われる
- **セッション管理** - 過去のセッションを AI 生成タイトル（`sessions-index.json` より）とタイムスタンプ付きで再開。一覧は検索可能で、不要なセッションは削除できる
- **セッションインデックス自動作成** - プロジェクトごとに `sessions-index.json` を自動作成・更新し、CLI が AI 生成の会話要約を追加できるようにする
- **多重起動ガード** - 1つのセッションは同時に1か所でしか動かないため、他プロセスが保持中のセッションの起動をプロンプト表示前にブロック
- **プロジェクトコンテキスト切替** - MDI ウィンドウの切替に合わせて、プロジェクトフォルダ・エクスプローラー・セッション一覧・git 状態を自動切替
- **実行環境（WSL / SSH）** - 新規セッションのメニューに、インストール済みの WSL ディストリビューション（Windows のみ）、最近使った SSH ホストと `~/.ssh/config` のホスト、`host:/path` の入力欄を表示。CLI は `wsl.exe` / `ssh` 経由で起動し、引数はリモート側向けにエスケープする。WSL のウィンドウは `\\wsl.localhost` 経由で記録を読むためチャットビューも使える。SSH のウィンドウはターミナル表示のみ。リモートのウィンドウはワークスペースに保存しない
- **スケジュールタスク** - 指定したプロジェクトで新規セッションを開いてプロンプトを送信。1回・毎日・N分ごとを選べ、アプリの起動中に実行する。30分以上過ぎた実行枠は後から追いかけずスキップ
- **リモート操作** - セッションタブのメニューから `/remote-control` を送り、別の端末からセッションを続けられる
- **起動プロファイル** - ツールバーで Light / Standard / Deep を選択。新規・`-c` 継続・`-r` 再開のすべての起動経路に適用されるため、再開時にコンテキスト上限が外れて膨らむことがない。Light は独自の `--settings` ファイルで MCP サーバー・ツール説明・自動メモリをプロンプト冒頭から削減、Deep は `--model opus --effort high` に切替
- **起動時のモデルと effort** - 会話の途中で切り替えてプロンプトキャッシュを捨てる代わりに、セッション開始時にモデルと effort を固定できる（Claude のみ）。選択は新規ウィンドウ・復元・起動をまたいで引き継がれる
- **Git Worktree 分離** - セッションごとに専用の `git worktree` チェックアウト（Snipyard 管理フォルダ配下、`snipyard/` 接頭辞のブランチ）を持たせ、同一リポジトリの複数ウィンドウが互いのファイルを壊さず並行作業可能に
- **セッション引き継ぎ** - コンテキストが少なくなったら（割合ではなく絶対トークン数が閾値なので 1M コンテキストのモデルでも発火する）、記録からローカルで抽出したブリーフを持って新規セッションへ引き継ぐ。`/compact` と違いトークン費用はかからず、ブリーフは新規セッションの入力欄に置かれるので送信前に編集できる
- **再開コスト＆トークンバッジ** - セッション選択画面に直近のコンテキストサイズをバッジ表示し、ツールチップで再開コストを見積もり。右クリックの「ブリーフから新規開始」で、元のセッションを開かずに引き継げる

### ターミナル

- **VT100/ANSI ターミナル** - Windows ConPTY（macOS では forkpty の疑似端末）上の自前パーサー。SGR の 256 色・トゥルーカラー、代替バッファ、スクロール領域、全角文字、Unicode コードポイントとゼロ幅結合文字に対応。スクロールバックは 10,000 行
- **キャレットと行編集** - 点滅する挿入バー型キャレット、クリックでの移動、Shift+矢印での選択、CLI 入力行の Undo/Redo
- **ターミナル検索 (Ctrl+F)** - 出力とスクロールバック履歴の全文検索。マッチハイライト、ナビゲーション、正規表現モード、大文字小文字区別トグル
- **プロンプトナビゲーション (Ctrl+↑/↓)** - 質問単位で移動し、現在位置を表示（Q 2/5）。セッション中の入力位置を追跡し、過去の会話はバッファ内のセパレータを検出して移動
- **フォントズーム (Ctrl+スクロール)** - ホイールでフォントサイズを変更。Ctrl+0 でリセット
- **拡張入力パネル** - ドラッグでサイズ変更できる複数行入力。Enter で改行、Ctrl+Enter で送信、Escape で縮小
- **@メンション補完** - `@` を入力するとプロジェクトのファイルインデックスからパスを補完。入力行からファイルを選ぶこともできる
- **Shift+Enter 改行** - 送信せずに改行を挿入
- **ファイルドラッグ＆ドロップ** - ターミナルにファイルをドロップしてパスを入力。エクスプローラーパネルからのドラッグにも対応
- **画像の添付** - クリップボードの画像（Ctrl+V / macOS は Cmd+V、または Alt+V）や、貼り付け・ドロップした画像ファイルは、テキストとして挿入せず入力欄の上のサムネイル列に追加。クリックで拡大表示、×で削除でき、送信時にプロンプトと一緒に渡される
- **ブラケットペーストモード** - モダンシェル向けにペーストテキストをブラケットシーケンスでラップ
- **ターミナル出力のエクスポート** - タブの右クリックメニューから出力をテキストファイルに保存

### プロジェクトとファイル

- **プロジェクトエクスプローラー** - 構文対応アイコンと色分け（40種類以上の拡張子）、Windows のシェルアイコン（Windows のみ）、ファイルシステム変更時の自動リフレッシュ、選択時のプレビュー表示。Ctrl/Shift+クリックで複数選択でき、まとめてドラッグすると全パスを `@` 参照としてターミナルへ入力
- **アプリ内テキストエディタ** - エクスプローラーからテキストファイルを直接編集（Ctrl+S で保存）。元のエンコーディング・改行コード・BOM を保持。バイナリ・大きすぎる・非 UTF-8 のファイルは壊さないよう読み取り専用のまま
- **プログラムから開く** - ファイルを右クリックして Windows の「プログラムから開く」、または macOS のアプリケーション選択に渡す
- **安全な削除** - エクスプローラーからの削除は完全削除ではなく Windows のごみ箱（macOS ではゴミ箱）へ

### ソースコントロール

- **ソースコントロールパネル (Ctrl+Shift+G)** - git + GitHub 統合サイドパネル。取得 / 取込（rebase）/ 送信と5分間隔の静かなバックグラウンド自動取得、ブランチの切替・作成・削除・マージ、ファイル単位または一括のステージ、コンフリクトや中断したリベース/マージのバナー表示（対象ファイル名入りの日英プロンプトを作成する「AI に聞く」ボタン付き）。強制プッシュ・`reset --hard`・`branch -D` は設計上存在せず、常に非破壊的
- **AI コミットメッセージ** - ステージ済み差分から日本語または英語のコミットメッセージを生成。現在のセッションに参加せず、アクティブな CLI のワンショットモードでローカルに生成
- **シークレットスキャン** - コミット前にステージ済み差分をファイル単位で走査し、認証情報や鍵を検出。該当ファイルを黙ってコミットせず明示的に警告
- **危険ファイルの警告** - コミットするつもりがなかったと思われるファイル（認証情報ファイル、ビルド生成物など）をステージングポリシーが検出。判断済みのものはクローンごとの無視リストで以後表示されなくなる
- **GitHub プルリクエスト** - `gh` を介して、パネル内でオープンな PR の一覧表示・承認・作成・ブラウザで開くが可能。`gh` が未インストール/未認証、またはリモートが GitHub でない場合は自動的に非表示
- **CI 状態とレビューコメント** - オープンな PR ごとにチェック結果をまとめた色付きドットを表示（パネル表示中は1分ごとに更新）。展開すると失敗したチェックと、レビュー・会話・行コメントを一覧でき、AI に修正を依頼するボタンが付く
- **リポジトリ作成** - リモートのないリポジトリで送信すると、オーナー / 名前 / 説明 / 非公開を指定するダイアログを開き、`gh repo create` で GitHub リポジトリを作成
- **git エラーのヒント** - よくある git の失敗には、git の出力の上に日英の説明を表示。別の worktree でチェックアウト中のブランチはブランチメニューでグレー表示し、理由を示す
- **コミットグラフ＆差分ビューア** - コミット履歴をレーングラフで表示（MDI 子ウィンドウ or 専用ウィンドウ。取得/取込/送信ボタンを内蔵。線はブランチごとに色分けし、タグ作成も可能）し、仮想化された差分ビューアを提供。差分範囲を選択、または変更ファイルを右クリックして `@path:12-20 <コメント>` をターミナルへ直接送信

### AI の状態を知る

- **モードバッジ** - セッションのモード（手動 / 自動承認 / プラン / 権限スキップ）を、CLI が使う色そのままでステータスバーに表示。クリックで切替。CLI の仕様変更でモード名を読めなくなった場合は「Switch Mode」表示に戻り、Shift+Tab の送信は継続
- **モデルと effort** - 使用中のモデルと推論 effort をステータスバーに表示し、そこから切替可能。CLI 内部で行われたモデルや effort の切替にも追従（effort は応答の記録から読み取る）。モデル一覧は直近の記録から各系統の最新バージョンを学習するため、新しいモデルが出ても古いままにならない
- **実行中インジケータ** - AI が黙っている間に何をしているか（ファイル読み込み・コマンド実行・検索）を経過時間付きで表示。ターン実行中は入力行の下にスイープバーを表示
- **コンテキスト残量メーター** - CLI の出力から残量を読み取ってメーター表示。少なくなったらワンクリックで `/compact`
- **キャッシュ期限の警告** - アイドル中のセッションがプロンプトキャッシュの期限に近づくと「今すぐ compact」バナーを表示。会話途中でモデルや effort を切り替えるときは、残す価値のあるコンテキストがあれば先に compact する
- **限界コスト表示** - 直前のターンにかかった額と、次のターンが会話を読み直すだけでかかる額を表示。高くなったセッションが自分から知らせる
- **使用量の人間語表示** - 単なるメッセージ数ではなく「残り約 840 回・リセットまで 5 時間」と表示。基準は設定で選んだプラン（Pro / Max 5x / Max 20x）で、集計元はセッション記録そのもの
- **レート制限表示** - プランの5時間枠・7日枠それぞれの使用率とリセットまでの時間を表示。Snipyard から起動した Claude セッションに status line を追加し、Claude Code 自身が報告する `rate_limits` を保存して表示します（Pro/Max、最初の応答以降）。ご自身の status line はそのまま表示されます。設定 > ライブステータスでオフにできます（表示も消えます）。オンの間、Claude Code はフッターのキー操作ヒントを表示しません。ご自身のスクリプトが一時フォルダに残す `claude_usage_cache.json` も予備として読みます（Snipyard 自身が Claude のサインイン情報を使うことはありません）
- **権限プロンプトの解説** - 承認オーバーレイに、そのコマンドが何をするかの平易な説明と危険度（読み取りのみ / ファイルを変更 / 削除・ネットワーク）を表示
- **エラー診断バナー** - 既知の失敗（サインアウト・レート制限・使用量上限・ネットワーク断・CLI が古い）を検出し、対処をワンクリックで実行できるバナーを表示。お知らせ系のバナーは自動で消える
- **停止ボタン** - 作業中のウィンドウの入力行に停止ボタンを表示。Escape で中断できることを知らなくても止められる
- **ターン終了時の枠点滅** - AI が応答を終えて入力待ちに戻ると、アクティブウィンドウの青い枠が2回点滅。タイル表示でもどのセッションが終わったか一目で分かる
- **タスク完了通知** - バックグラウンドでセッションが終了したとき、およびターンの生成が完了したときに、トースト通知・効果音・タスクバー点滅で知らせる（macOS では通知センターとシステムサウンド）。いずれも設定でオフにできる
- **ステータスバー** - リポジトリ名、ブランチ、変更ファイル数、ターミナル状態、モードバッジ、モデル、実行中の作業、コンテキスト残量、レート制限、日次使用量（緑→黄→赤のプログレスバー）を表示

### チャットビュー

- **会話ビュー** - 各ウィンドウを生のターミナルとチャットビューで切替。チャットビューはセッションの JSONL 記録を、デスクトップアプリ風の吹き出し・Markdown・コードブロック・折りたたみ式のツール呼び出し要約で表示し、ダーク/ライトの配色も合わせる。ウィンドウ幅いっぱいに広がり、表示の選択はウィンドウごとに復元後も保持
- **ツール呼び出しの詳細** - ツールのグループを展開すると各呼び出しを表示。コマンドとその出力、Edit/Write は色分けした差分、Read/Grep/Glob は対象と結果の抜粋、それ以外は入力 JSON と結果。思考とツール呼び出しの間の説明文も表示。ツールが扱ったファイルはワンクリックでエディタウィンドウに開く
- **CLI への応答** - 権限プロンプト、プランの承認、`/model` などのメニュー、AskUserQuestion のフォームを操作できるカードで表示し、回答は CLI の選択肢へキー操作として送る
- **入力欄** - 下部のカードからプロンプトを送信。画像添付、`@` によるファイル補完、`/` によるコマンド補完（↑↓ / Tab / Enter / Esc）に対応。CLI が提案する次のプロンプトをプレースホルダーに表示し、Tab で取り込み、空欄で Enter を押すとそのまま送信
- **停止と送信キュー** - Claude の作業中はスパークと停止ボタンを表示。作業中に送ったプロンプトは取り消し可能なチップとして待機し、ターン終了後に送信。停止するとキューのプロンプトは入力欄に戻る
- **編集と巻き戻し** - プロンプトにホバーすると「編集」「巻き戻し」を表示。どちらも CLI の `/rewind` でそのプロンプトまで戻り、編集では元の文を入力欄に戻す
- **タスクとサブエージェント** - タイトル下の帯に、TodoWrite / TaskCreate / TaskUpdate から組み立てた Claude のタスク一覧と、すべての Agent 呼び出しを表示。記録のあるサブエージェントはセッションの代わりに開き、戻るバーで元に戻れる
- **会話内検索 (Ctrl+F)** - 検索語を含むメッセージを強調表示し、Enter / Shift+Enter または F3 で移動
- **サイドペイン** - 会話の横にサイズ変更できるペインを開く。タブは **Terminal**（同じセッションのライブターミナル）と **Preview**（アドレスバー・戻る・再読み込み・ブラウザで開くを備えた WebView2 ブラウザ。ターミナル出力に出た localhost のアドレスをワンクリックで開ける候補として表示）の2つ。macOS ではページをシステムのブラウザで開く

### パネル・コマンド・ツール

- **コマンドパレット (Ctrl+Shift+P)** - VS Code 風の検索可能なアクションメニュー。全コマンドを網羅
- **スラッシュコマンドパネル (Ctrl+/)** - CLI が解釈するスラッシュコマンドを説明付きで一覧表示。`.claude/commands/*.md` のカスタムコマンドも自動取り込み
- **拡張機能パネル** - 設定済みの MCP サーバー・スキル・プラグインを一覧表示。CLI が対応していれば有効/無効を切替可能。MCP サーバーは `claude mcp` を使って追加（stdio / http、スコープ指定）・削除・状態確認ができる。スキルはダブルクリックで実行
- **メモリパネル** - Claude Code がプロジェクトについて書き残した自動メモリのノート（`~/.claude/projects/<project>/memory/`）を種類別に表示。検索、`MEMORY.md` の目次、被リンク、未作成のリンクの一覧に対応
- **ダイアグラムビューア** - MCP ツール呼び出しで描画された Excalidraw 図と、出力中の mermaid / chart コードブロックを、ズーム・パン可能なウィンドウで表示。PNG エクスポート/コピーに対応し、プロジェクトごとにキャッシュされるため再開・再起動後も残る
- **スニペットパネル** - コードスニペットを保存してアクティブなコンソールへ送信（テキスト中の `\r` で Enter を送信）。ドラッグ＆ドロップで並べ替え可能。初期状態でよく使うプロンプト（日本語／英語）が入っている
- **トークン／コストダッシュボード** - セッション記録の `message.usage` を集計する専用ウィンドウ。7/30/90日の期間切替、「このプロジェクトのみ」表示、日別コストの棒グラフ、モデル別・プロジェクト別・セッション別の上位50件内訳、CSV エクスポート
- **使用量チャート** - 過去14日のメッセージ数・ツール呼び出し数・セッション数をグラフ表示
- **自動チェックポイント** - プロンプト送信前にプロジェクトのスナップショットを取り、巻き戻し可能にする。Git リポジトリでは作業ツリーに触れない `git stash create` を使い、Git 管理外のフォルダはファイルコピーで保存
- **セットアップ診断** - CLI・Node.js・Git・設定フォルダ・サインイン状態をワンクリックで診断し、足りないものにはコピーできる対処コマンド（Windows は `winget`、macOS は `brew` / `xcode-select`）を表示。初回起動時に自動実行され、問題がなければ何も出さない
- **ショートカット一覧 (F1)** - 全ショートカットを対象ごとにグループ分けして1画面で表示
- **ヘルプ** - 「?」ボタンで、スクリーンショット付きの日英ヘルプページをブラウザで開く。Claude Desktop・Claude Code CLI・VS Code 拡張との比較も収録
- **設定パネル** - カテゴリごとにグループ分け。言語、フォントとサイズ、ホストシェル、初期プロンプト、テーマ、通知、チェックポイント、ライブステータス、コミットメッセージ言語、プラン、起動時のモデルと effort、AI プロバイダーの実行ファイルと引数テンプレート、`.claude` フォルダへのショートカット
- **自動アップデート** - GitHub リリースを確認し、新しいバージョンがあれば実行中の exe の隣にダウンロードして検証したうえで適用。古い exe はリネームで退避してから新しい exe を配置し、失敗した場合はリネームを戻すため「アプリが消えたまま」の状態にはならない。macOS ではアーキテクチャに合ったリリースの tar.gz を取得し、`Snipyard.app` 内の実行ファイルを置き換えてアプリを再起動する。自己置換できるのは発行済みのビルドのみで、開発ビルドはリリースページを案内する

### その他

- **ダーク/ライトテーマ** - 設定パネルから切替。全パネル・ターミナルのカラーパレット・MDI の枠まで対応
- **多言語対応** - 英語・日本語

## キーボードショートカット

macOS ではアプリのショートカットに Cmd を使い、Ctrl+文字は常に CLI へ送られます（Ctrl+C は中断、Ctrl+W は単語削除など）。F1 の一覧とツールチップには、使用中の OS に合わせたキーが表示されます。

| Windows | macOS | |
|---|---|---|
| **ウィンドウとタブ** | | |
| Ctrl+N | Cmd+N | 新規セッション |
| Ctrl+W | Cmd+W | 閉じる |
| Ctrl+Tab / Ctrl+Shift+Tab | Ctrl+Tab / Ctrl+Shift+Tab | 次 / 前のタブ |
| **パネル** | | |
| Ctrl+Shift+E | Cmd+Shift+E | エクスプローラー |
| Ctrl+Shift+G | Cmd+Shift+G | ソースコントロール |
| Ctrl+Shift+M | Cmd+Shift+M | メモリ |
| Ctrl+Shift+P | Cmd+Shift+P | コマンドパレット |
| Ctrl+/ | Cmd+/ | スラッシュコマンド |
| F1 | F1 | ショートカット一覧 |
| **ターミナル** | | |
| Ctrl+F | Cmd+F | 検索 |
| Ctrl+↑ / Ctrl+↓ | Cmd+↑ / Cmd+↓ | プロンプト間を移動 |
| Ctrl+スクロール | Cmd+スクロール | フォントズーム |
| Ctrl+0 | Cmd+0 | フォントサイズをリセット |
| Shift+Enter | Shift+Enter | 送信せずに改行 |
| Ctrl+Enter | Cmd+Enter | 拡張入力から送信 |
| Esc | Esc | タスクを停止 |
| Ctrl+C | Cmd+C | コピー（Windows では選択がなければ中断） |
| | Ctrl+C | 中断（選択中でも） |
| Ctrl+V | Cmd+V | 貼り付け（画像も可） |
| Ctrl+X | Cmd+X | 選択範囲を切り取り |
| Ctrl+Z / Ctrl+Shift+Z | Cmd+Z / Cmd+Shift+Z | CLI 入力行の元に戻す / やり直し |
| Alt+V | Option+V | クリップボードの画像を貼り付け |
| Shift+Tab | Shift+Tab | AI モードを切替 |
| Ctrl+S | Cmd+S | エディタで開いているファイルを保存 |
| **チャットビュー** | | |
| Ctrl+F | Cmd+F | 会話内を検索 |
| Enter / Shift+Enter、F3 | Enter / Shift+Enter、F3 | 次 / 前の一致へ移動 |
| Tab | Tab | 提案されたプロンプトを入力欄に取り込む |

## 技術スタック

| コンポーネント | 技術 |
|---|---|
| フレームワーク | .NET 8.0 / C# |
| UI | Avalonia 12.1.1 + Fluent テーマ + Inter フォント |
| ターミナル | Windows は PseudoConsole (ConPTY)、macOS は forkpty 上の自前 VT100/ANSI パーサー |
| ブラウザプレビュー | Microsoft Edge WebView2（Windows。macOS はシステムのブラウザ） |
| シリアライズ | System.Text.Json |
| 外部ツール | AI CLI 本体、`git`、プルリクエスト用の `gh`、リモートセッション用の `wsl.exe` / `ssh` |

## プロジェクト構成

```
Snipyard/
├── Program.cs                      # アプリケーションのエントリポイント
├── App.axaml / .cs                 # アプリケーションルートとテーマリソース
├── MainWindow.axaml / .cs          # アプリ本体のウィンドウ。中身は AppShell ひとつ
├── AppShell.axaml / .cs            # シェル本体：ツールバー、アクティビティバー、パネル、MDI 領域、ステータスバー
├── AppShell.Update.cs              # 自動アップデートの一連の流れ（確認・DL・配置・再起動）
├── AppShell.Schedule.cs            # スケジュールタスクのダイアログとタイマー
├── AppShell.Mcp.cs                 # `claude mcp` による MCP サーバーの追加・削除・確認
├── MdiLayoutItem.cs                # MDI 子ウィンドウが実装するインターフェース
├── FileTreeNode.cs                 # エクスプローラーのツリーノードモデル
├── UsageChartWindow.axaml / .cs    # 14日間の使用量チャートダイアログ
├── Terminal/
│   ├── TerminalControl.cs          # ターミナルコントロール：描画・選択・キャレット・IME・検索・D&D
│   ├── TerminalControl.SidePane.cs # チャットビューのサイドペイン（Terminal / Preview）
│   ├── TerminalBuffer.cs           # セルグリッドとスクロールバック、代替バッファ、ブラケットペースト
│   ├── TerminalCell.cs             # セルモデル（Unicode コードポイント、色、属性）
│   ├── VtParser.cs                 # ANSI/VT 状態機械。SGR の 256 色・トゥルーカラー対応
│   ├── PseudoConsole.cs            # Windows ConPTY の P/Invoke ラッパー
│   ├── UnixPseudoConsole.cs        # macOS の forkpty 疑似端末（ログインシェル経由で起動）
│   ├── CodeBlockDetector.cs        # mermaid/chart ブロックと Excalidraw MCP 呼び出しの検出
│   └── DiagramWindow.cs            # ズーム・パン可能なダイアグラムビューア
├── Controls/
│   ├── ShellWindow.cs              # 切り離された、独自のシェルを持つウィンドウ
│   ├── DetachedWindow.axaml / .cs  # ドラッグで切り離されたペインのウィンドウホスト
│   ├── DockTree.cs                 # 各 MDI 子ウィンドウの位置を表す分割/葉ノードのツリー
│   ├── DockHost.cs                 # ドックツリーをドラッグ可能なスプリッタ付きで描画
│   ├── TabDrag.cs                  # タブドラッグ：並べ替え・ドッキング・ウィンドウ間移動・切り離し
│   ├── SourceControlPanel.cs       # git + GitHub 統合サイドパネル
│   ├── CommitGraphPanel.cs         # MDI 子ウィンドウとしてのコミットグラフ（取得/取込/送信付き）
│   ├── CommitGraphView.cs          # レーングラフの描画コントロール
│   ├── DiffWindow.cs               # 仮想化された差分ビューア（行範囲コメント対応）
│   ├── CostDashboardWindow.cs      # トークン／コストダッシュボード
│   ├── DocumentViewPanel.cs        # チャットビュー：吹き出し・ツール詳細・カード・検索
│   ├── ChatSidePane.cs             # チャットビューのサイドペインのタブ
│   ├── BrowserPreviewPanel.cs      # ローカル Web サーバーの WebView2 プレビュー
│   ├── ImageAttachmentStrip.cs     # 添付画像のサムネイル列
│   ├── MemoryPanel.cs              # 自動メモリのノートパネル
│   ├── MemoryNoteView.cs           # リンク付きでノート1件を表示
│   └── MarqueeBar.cs               # ターン実行中のスイープバー
├── Services/
│   ├── AppSettings.cs              # 設定の永続化
│   ├── AppPaths.cs                 # アプリのデータフォルダと Claucraft からの初回コピー
│   ├── PlatformShell.cs            # Windows はエクスプローラー、macOS は Finder／アプリ選択
│   ├── Shortcut.cs                 # アプリのショートカット修飾キー（Windows は Ctrl、macOS は Cmd）
│   ├── Localization.cs             # EN/JP 文字列のローカライズ
│   ├── SnippetStore.cs             # スニペットの保存
│   ├── WorkspaceService.cs         # 名前付きワークスペースの保存・復元
│   ├── CliProvider.cs              # CLI プロバイダー／起動プロファイルのデータモデル
│   ├── CliProviderService.cs       # プロバイダー登録、コマンド構築、引数クォート
│   ├── CliOneShotRunner.cs         # セッション状態を持たない CLI 単発実行
│   ├── ShellHost.cs                # セッションを起動するシェル（cmd.exe / PowerShell / ログインシェル）の決定
│   ├── ProcessRunner.cs            # プロセス起動の共通処理
│   ├── ProcessTree.cs              # 子プロセスを辿ってウィンドウの CLI プロセスを特定
│   ├── SessionService.cs           # セッション一覧と sessions-index の管理
│   ├── SessionMessageReader.cs     # JSONL 記録のパーサー
│   ├── ChatTaskTracker.cs          # 記録から組み立てるタスク一覧とサブエージェント
│   ├── ClaudeProjectPaths.cs       # プロジェクトフォルダと ~/.claude/projects のフォルダの対応付け
│   ├── ExecutionTarget.cs          # ローカル / WSL / SSH の起動先
│   ├── ScheduleService.cs          # スケジュールタスクの保存
│   ├── McpService.cs               # `claude mcp` のラッパー
│   ├── MemoryStore.cs              # 自動メモリのノートの読み取り
│   ├── ModelCatalog.cs             # 記録から学習する最新のモデルバージョン
│   ├── SessionCostMonitor.cs       # セッションごとのコスト・コンテキストのライブ追跡
│   ├── RunningSessionService.cs    # 他プロセスが保持中のセッションの検出
│   ├── SubagentMonitor.cs          # 実行中サブエージェントの追跡
│   ├── AgentViewMonitor.cs         # ~/.claude/jobs から読むバックグラウンドセッション
│   ├── HandoffBuilder.cs           # セッション引き継ぎブリーフの生成
│   ├── WorktreeService.cs          # セッションごとの git worktree 分離
│   ├── CheckpointService.cs        # プロンプト前スナップショット（stash オブジェクト／ファイルコピー）
│   ├── UsageTracker.cs             # セッション記録から集計する当日の使用量
│   ├── RateLimitService.cs         # プランの5時間枠・7日枠
│   ├── CostAnalytics.cs            # ダッシュボード用のコスト／トークン集計
│   ├── TerminalInsight.cs          # モード・作業内容の推定とエラー診断
│   ├── CommandExplainer.cs         # 権限プロンプトの平易な説明
│   ├── SetupDoctor.cs              # 環境診断（セットアップ診断）
│   ├── SlashCommandCatalog.cs      # スラッシュコマンドの一覧取得
│   ├── ExtensionCatalog.cs         # MCP サーバー／スキル／プラグインの一覧取得
│   ├── ProjectFileIndex.cs         # @メンション用のファイルパスインデックス
│   ├── TextFileEditor.cs           # エクスプローラーのアプリ内テキスト読み書き
│   ├── RecycleBin.cs               # Windows のごみ箱／macOS のゴミ箱経由の安全な削除
│   ├── ShellIconProvider.cs        # Windows のシェルアイコンをキャッシュして Avalonia 画像に
│   ├── MarkdownParser.cs           # 軽量 Markdown レンダラー
│   ├── DiagramCache.cs             # Excalidraw 図のプロジェクト単位の永続化
│   ├── NotificationService.cs      # トレイ通知と効果音（macOS は通知センター）
│   ├── UpdateService.cs            # GitHub リリースの確認・ダウンロード・配置
│   ├── CommitMessageService.cs     # AI によるコミットメッセージ生成（EN/JP）
│   ├── SecretScanService.cs        # ステージ済み差分のファイル単位シークレットスキャン
│   ├── StagingPolicy.cs            # コミットすべきでない可能性が高いファイルの検出
│   ├── GitIgnoreService.cs         # 上記警告のクローン単位の無視リスト
│   ├── GitHubCli.cs                # プルリクエスト・CI 状態・リポジトリ作成用の `gh` ラッパー
│   ├── GitErrorHints.cs            # よくある git の失敗の日英説明
│   ├── UnifiedDiffParser.cs        # unified diff のパーサー
│   └── GitCli.cs / GitWriteService.cs / GitChangeService.cs / GitLogService.cs / GitPath.cs / CommitGraphLayout.cs
│                                   # git プロセスラッパー、書き込み操作、status/log 読み取り、パス復号、グラフレイアウト
├── Help/                           # 埋め込みの日英ヘルプページとスクリーンショット
├── icon.ico / icon.png             # アプリケーションアイコン
├── app.manifest                    # アプリケーションマニフェスト（Windows）
├── publish.bat                     # Windows 用の自己完結型リリースビルドのワンクリック発行
├── publish-mac.sh                  # macOS 用 Snipyard.app の tar.gz（arm64 / x64）。署名付き。Mac で実行
├── publish-mac.ps1                 # macOS ビルドがコンパイル・パッケージできるかの Windows 側での確認（署名なし）
├── run-mac.command                 # macOS の Finder からダブルクリックでビルド・実行
└── build.number                    # 自動インクリメントされるビルド番号
```

## 動作要件

- Windows 10 以降、または macOS 11 (Big Sur) 以降（Apple Silicon / Intel）
- Node.js - 対応 CLI はいずれも npm パッケージ
- AI CLI をいずれか1つ以上: [Claude Code](https://docs.anthropic.com/en/docs/claude-code)（既定）、Codex CLI、GitHub Copilot CLI、Antigravity CLI、Grok CLI
- `git`（任意）- ソースコントロールパネル、コミットグラフ、worktree 分離に必要
- `gh`（任意）- プルリクエスト、CI 状態、リポジトリ作成に必要
- Microsoft Edge WebView2 ランタイム（任意。Windows のみ。Windows 11 には標準搭載）- チャットビューのブラウザプレビューに必要
- WSL（任意。Windows のみ）または OpenSSH クライアント - WSL / SSH セッションに必要

## macOS へのインストール

いちばん簡単なのは、ターミナルで次のコマンドを1行実行する方法です。お使いの Mac（Apple Silicon / Intel）に合った最新リリースをダウンロードし、`Snipyard.app` をアプリケーションフォルダに置きます。この方法で取得したファイルにはダウンロードの印が付かないため、Gatekeeper の警告なしで起動できます。もう一度実行すると、既存のアプリを上書きします。

```bash
curl -fsSL "https://github.com/ImagingSolution/Snipyard/releases/latest/download/Snipyard-macos-$([ "$(uname -m)" = arm64 ] && echo arm64 || echo x64).tar.gz" | tar -xz -C /Applications
```

ブラウザで入手する場合は、リリースページから `Snipyard-macos-arm64.tar.gz`（Apple Silicon）または `Snipyard-macos-x64.tar.gz`（Intel）をダウンロードして展開し、`Snipyard.app` をアプリケーションフォルダへ移動してください。Apple の公証を受けていないため、ブラウザでダウンロードしたものは初回起動時に「Apple は…検証できませんでした」と表示されてブロックされます。システム設定 > プライバシーとセキュリティで許可するか、`xattr -dr com.apple.quarantine /Applications/Snipyard.app` を実行してください。以降の更新はアプリ内のアップデートで行えます。

CLI はログインシェル経由で起動するため、npm・Homebrew・ネイティブインストーラーのどれで入れた `claude` でも、シェルのプロファイルで `PATH` が通っていれば見つかります。

## ビルド

```bash
# ビルド（build.number を自動インクリメント）
dotnet build

# 実行
dotnet run

# 単一ファイル実行可能ファイルを発行（publish.bat でも可）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ./publish-single

# macOS 用 Snipyard.app の tar.gz を arm64 / x64 で発行（アドホック署名付き。Mac で実行）
./publish-mac.sh
```

Mac では .NET SDK（8.0 以降）をインストールして `dotnet run` を実行するか、Finder で `run-mac.command` をダブルクリックしてください（SDK が `~/.dotnet` にある前提です）。

## データ保存先

macOS では `%APPDATA%\Snipyard` と `%LOCALAPPDATA%\Snipyard` はどちらも `~/Library/Application Support/Snipyard/` になります。`~/.claude` 以下のパスは Windows と同じです。

| データ | パス |
|---|---|
| 設定 | `%APPDATA%\Snipyard\appsettings.json` |
| スニペット | `%APPDATA%\Snipyard\snippets.json` |
| ワークスペース | `%APPDATA%\Snipyard\workspace.json` |
| CLI プロバイダー | `%APPDATA%\Snipyard\providers.json` |
| Light プロファイル設定 | `%APPDATA%\Snipyard\light-settings.json` |
| スラッシュコマンド上書き | `%APPDATA%\Snipyard\slashcommands.json`（任意） |
| スケジュールタスク | `%APPDATA%\Snipyard\schedules.json` |
| ヘルプページ | `%APPDATA%\Snipyard\Help\` |
| チェックポイント | `%APPDATA%\Snipyard\checkpoints\` |
| セッション用 worktree | `%LOCALAPPDATA%\Snipyard\worktrees\` |
| レート制限（status line 経由） | `%LOCALAPPDATA%\Snipyard\rate-limits.json` |
| セッションインデックス（読み書き） | `~/.claude/projects/*/sessions-index.json` |
| セッション記録（読み取り専用） | `~/.claude/projects/*/*.jsonl` |
| ダイアグラムのキャッシュ | `~/.claude/projects/*/diagrams/` |
| 自動メモリのノート（読み取り専用） | `~/.claude/projects/*/memory/` |
| バックグラウンドセッション（読み取り専用） | `~/.claude/jobs/` |
| CLI 設定・プラグイン（読み取り専用） | `~/.claude/settings.json`, `~/.claude.json`, `~/.claude/plugins/installed_plugins.json` |
| プロジェクトの MCP・スキル（読み取り専用） | `<project>/.mcp.json`, `<project>/.claude/settings.local.json`, `<project>/.claude/commands/*.md` |

**Claucraft からの移行:** 初回起動時に `%APPDATA%\Claucraft` を `%APPDATA%\Snipyard` にコピーするので、設定・スニペット・プロバイダー・スケジュール・チェックポイントはそのまま引き継がれます。旧フォルダは残るので、以前の版に戻す必要がなくなったら削除してかまいません。改名前に作った worktree は `%LOCALAPPDATA%\Claucraft\worktrees\` に残ったまま認識されます。アプリ内の更新で上書きした場合、ファイル名は `Claucraft.exe` のままなので、都合のよいときに `Snipyard.exe` へ変更してください。

## 免責事項

Snipyard は非公式の個人プロジェクトであり、Anthropic とは提携・承認・後援のいずれの関係もありません。Claude および Claude Code は Anthropic, PBC の商標です。その他の製品名は各社の商標です。

Snipyard は Claude Code を同梱していません。利用者がインストールした Claude Code を、利用者自身のアカウントで起動します。

## ライセンス

MIT
