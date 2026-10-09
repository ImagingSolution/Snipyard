using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Snipyard.Services;

public class AppSettings
{
    public string ProjectFolder { get; set; } = "";
    public string FontFamily { get; set; } = "Cascadia Mono";
    public double FontSize { get; set; } = 14;

    /// <summary>
    /// Text size in the file editor windows, set by Ctrl+wheel. Separate from the terminal's
    /// FontSize: the terminal is sized to a readable session, an editor to a readable diff.
    /// </summary>
    public double EditorFontSize { get; set; } = 12;
    public bool IsDark { get; set; } = true;
    public string Language { get; set; } = "English";
    public string InitialPrompt { get; set; } = "";
    public bool ShowWelcomePage { get; set; } = true;
    public bool EnableChartRendering { get; set; } = true;

    /// <summary>
    /// Draw the explorer tree with the icons Windows Explorer shows, instead of the built-in
    /// extension-coloured glyphs. On by default; the switch that turns it off is hidden in the
    /// settings panel (ChkUseShellIcons), so this is what the tree actually uses.
    /// </summary>
    public bool UseShellIcons { get; set; } = true;

    public string CliProviderId { get; set; } = CliProviderService.ClaudeId;

    /// <summary>
    /// Which shell a new session's CLI is launched inside: "cmd" or "powershell". Open tabs keep
    /// the shell they were started in, so a change here reaches the next new session only.
    /// PowerShell is the default because it is what every CLI here is documented and installed
    /// against; a CLI that cannot survive in it pins "cmd" in providers.json and overrides this,
    /// and a machine without PowerShell falls back to cmd.exe in <see cref="ShellHost.For"/>.
    /// </summary>
    public string TerminalShell { get; set; } = ShellHost.PowerShellId;

    /// <summary>
    /// True once <see cref="TerminalShell"/> has been through the move to the PowerShell default.
    /// cmd.exe was the original default, so an install written before the change holds "cmd"
    /// whether or not anyone picked it; the flag is absent from those files and present in every
    /// one written since, which is what keeps the move to a single occasion.
    /// </summary>
    public bool TerminalShellDefaulted { get; set; }

    // ── Task completion notification ──

    /// <summary>Raise a tray toast when a terminal finishes while the window is in the background.</summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>Play a system sound alongside the notification.</summary>
    public bool NotifySound { get; set; } = true;

    // ── Safety net ──

    /// <summary>Snapshot the working tree before each prompt so it can be rolled back.</summary>
    public bool EnableCheckpoints { get; set; } = true;

    /// <summary>Set once the setup diagnostics have been shown, so they only auto-open on first run.</summary>
    public bool SetupDoctorShown { get; set; }

    // ── Live status readouts ──

    /// <summary>Read the terminal screen to show mode, activity and context left in the status bar.</summary>
    public bool EnableLiveStatus { get; set; } = true;

    /// <summary>Surface a banner with a suggested fix when a known CLI error shows up in the output.</summary>
    public bool EnableErrorBanner { get; set; } = true;

    /// <summary>
    /// Ask before sending into a session idle past the prompt cache's lifetime, when the next
    /// turn would re-read a large conversation at the uncached rate.
    /// </summary>
    public bool EnableCacheExpiryGuard { get; set; } = true;

    /// <summary>
    /// Launch Claude sessions with a status line that hands the plan's rate limits to the status
    /// bar. Costs Claude Code's footer key hints, hence the switch.
    /// </summary>
    public bool RateLimitStatusLine { get; set; } = true;

    /// <summary>Plan the usage readout is measured against: Pro, Max5x or Max20x.</summary>
    public string PlanTier { get; set; } = "Pro";

    /// <summary>Launch profile applied to new sessions, matched against CliProvider.Profiles.</summary>
    public string ActiveProfileId { get; set; } = CliProviderService.StandardProfileId;

    /// <summary>
    /// Model/effort pinned at launch for a CLI that supports it. Null means unset - the
    /// profile's own flags decide. Fixing these at the start of a session and never
    /// switching mid-conversation keeps the prompt cache from being invalidated.
    /// </summary>
    public string? PreferredModel { get; set; }
    public string? PreferredEffort { get; set; }

    /// <summary>
    /// Effort the window in front was last at, so the next launch of the app starts there
    /// instead of wherever the CLI's own default happens to sit.
    /// </summary>
    public string? LastEffort { get; set; }

    /// <summary>
    /// Whether the window in front was last showing Chat View, so a session opened with no
    /// window to take after - from the welcome page's Recent list - still opens the same way.
    /// </summary>
    public bool LastChatView { get; set; }

    /// <summary>SSH targets ("host:/remote/folder") sessions were last opened on, newest first.</summary>
    public List<string> RecentSshTargets { get; set; } = new();

    // ── Source control ──

    /// <summary>
    /// Language the AI writes commit messages in: "auto" follows the UI language, "ja" and "en"
    /// pin it. Separate from the UI setting because a team often works in one language and
    /// writes its history in another.
    /// </summary>
    public string CommitMessageLanguage { get; set; } = "auto";

    /// <summary>Fetch from the remote every few minutes while the source-control panel is open.</summary>
    public bool GitAutoFetch { get; set; } = true;

    /// <summary>
    /// Conversation prefix size, in tokens, at or above which the hand-off banner appears.
    /// An absolute count rather than a share of the window: on a 1M-token model "20% left"
    /// is 800k tokens, long after every turn has become several times dearer than a fresh
    /// start from a brief. (Replaces the percentage-based HandoffBannerThreshold; an old
    /// value in appsettings.json is ignored and this default applies.)
    /// </summary>
    public long HandoffBannerTokens { get; set; } = 150_000;

    /// <summary>
    /// Names given to background sessions in the windows panel, keyed by the short id of
    /// ~/.claude/jobs/&lt;id&gt;. Local to this app on purpose: a running session's state.json
    /// belongs to its supervisor, which rewrites it continuously.
    /// </summary>
    public Dictionary<string, string> AgentDisplayNames { get; set; } = new();

    // ── Updates ──

    /// <summary>Ask GitHub for a newer release at startup and offer to install it.</summary>
    public bool CheckUpdateOnStartup { get; set; } = true;

    private static readonly string SettingsDir = AppPaths.Roaming;

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "appsettings.json");

    /// <summary>
    /// The one settings object in the process. Every shell reads and writes this one: a second
    /// shell loading its own copy would mean the last window to close decides what was saved.
    /// </summary>
    public static AppSettings Shared { get; } = Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    // A file written before PowerShell became the default carries the old one,
                    // and nobody chose it - it was simply what shipped. Move it once and record
                    // that, so a later switch back to cmd.exe is a preference and stays put.
                    if (!loaded.TerminalShellDefaulted)
                    {
                        loaded.TerminalShell = ShellHost.PowerShellId;
                        loaded.TerminalShellDefaulted = true;
                        loaded.Save();
                    }
                    return loaded;
                }
            }
        }
        catch { }
        // A fresh install starts on the current default, so there is nothing to move later.
        return new AppSettings { TerminalShellDefaulted = true };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}
