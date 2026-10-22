using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Snipyard.Services;

/// <summary>The permission/edit mode Claude Code is currently running in, as shown on its status line.</summary>
public enum AiMode { Unknown, Normal, AcceptEdits, Plan, BypassPermissions }

/// <summary>What the AI appears to be doing right now, inferred from the most recent tool-call or status line.</summary>
public enum AiActivity { None, Thinking, Reading, Writing, Editing, RunningCommand, Searching, Browsing, Waiting }

/// <summary>Category of a detected error/warning banner, used to pick a helpful, actionable message.</summary>
public enum DiagnosisKind { None, AuthExpired, RateLimited, UsageLimit, NetworkDown, OutdatedCli, DiskOrPermission, UnknownError }

/// <summary>A human-readable explanation of an error banner found on screen, with an optional suggested action.</summary>
public sealed class ErrorDiagnosis
{
    public DiagnosisKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? ActionLabel { get; init; }
    public string? ActionCommand { get; init; }
    public string MatchedLine { get; init; } = "";

    /// <summary>
    /// True when <see cref="Detail"/> already names when the condition lifts, taken from the
    /// error line itself. The banner uses this to decide whether a recovery time still has to be
    /// filled in from elsewhere - it must not append a second, possibly contradicting, one.
    /// </summary>
    public bool HasResetTime { get; init; }
}

/// <summary>A point-in-time reading of what the terminal screen shows about the AI's current state.</summary>
public sealed class TerminalSnapshot
{
    public AiMode Mode { get; init; }

    /// <summary>
    /// The CLI's own wording for <see cref="Mode"/>, taken verbatim from the status line
    /// ("accept edits", "plan mode", "bypass permissions"), with the trailing "on" dropped.
    /// Empty when the screen did not name a mode. The badge shows this so its label cannot
    /// drift from what the CLI prints.
    /// </summary>
    public string ModeText { get; init; } = "";

    public AiActivity Activity { get; init; }
    public string ActivityText { get; init; } = "";
    public string? ActivityTarget { get; init; }
    public int? ElapsedSeconds { get; init; }
    public bool IsWorking { get; init; }
    public int? ContextRemainingPercent { get; init; }
    public ErrorDiagnosis? Error { get; init; }

    /// <summary>
    /// The model named in the CLI's own "Set model to X" confirmation, if one is on screen right
    /// now. This fires for a switch made any way - typed at the prompt, picked from the CLI's own
    /// interactive picker, or sent by Snipyard's dropdown - so the status bar never depends on
    /// which of those triggered it.
    /// </summary>
    public string? ModelSwitchedTo { get; init; }
}

/// <summary>
/// Reads the plain (already ANSI-stripped) text of a terminal screen and infers what Claude Code
/// is currently doing, so the UI can show a mode badge, a "working" indicator, remaining context,
/// and a friendly explanation when something goes wrong. Pure logic - no Avalonia dependency, no
/// mutable static state, and no exceptions escape <see cref="Analyze"/>.
/// </summary>
public static class TerminalInsight
{
    /// <summary>Only the tail of the screen is scanned - the newest state is always at the bottom.</summary>
    private const int ScanWindowLines = 120;

    // ── Mode ─────────────────────────────────────────────────────────────

    /// <summary>Matches "⏵⏵ accept edits on (shift+tab to cycle)" and its 2.x wording "▶▶ auto mode on (shift+tab to cycle)".</summary>
    private static readonly Regex ModeAcceptEditsRegex = new(
        @"(?:auto[-\s]?accept\s+edits|accept\s+edits|auto\s+mode)\s+on",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "⏸ manual mode on", the 2.x wording for the plain, approve-everything mode.</summary>
    private static readonly Regex ModeManualRegex = new(@"manual\s+mode\s+on", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "⏸ plan mode on (shift+tab to cycle)".</summary>
    private static readonly Regex ModePlanRegex = new(@"plan\s+mode\s+on", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "⏵⏵ bypass permissions on".</summary>
    private static readonly Regex ModeBypassRegex = new(@"bypass\s+permissions", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the input-box prompt line, e.g. "│ &gt;                                   │".</summary>
    private static readonly Regex InputPromptRegex = new(@"^\s*[│|]\s*>", RegexOptions.Compiled);

    // ── Activity / working state ────────────────────────────────────────

    /// <summary>
    /// Matches the interrupt hint where the CLI actually prints it - inside the spinner's
    /// bracketed status group: "✻ Cerebrating… (12s · ↓ 1.2k tokens · esc to interrupt)" or
    /// "(esc to interrupt · ctrl+t to show todos)".
    /// The leading "(" or "·" is the whole point. Matching the bare phrase made every mention of
    /// it in the transcript read as a live spinner, so a session that had merely talked about
    /// interrupting showed a progress bar for as long as the words stayed on screen. Prose writes
    /// the phrase bare; the CLI never does.
    /// </summary>
    private static readonly Regex WorkingLineRegex = new(@"[(·]\s*esc\s+to\s+interrupt", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches the spinner's own progress readout, e.g. "✻ Deciphering… (23m 3s · ↓ 103.1k tokens)"
    /// or "✳ Concocting…(1m 58s · ↓7.5k tokens)". This is the detector that carries the load: the
    /// CLI hides the interrupt hint behind ctrl+t, so the readout is often the only thing on the
    /// line. The whole shape is required, not just the bracketed figures - spinner glyph, one
    /// word, ellipsis, readout - because the figures on their own turn up in the transcript
    /// whenever the session talks about them, and a line of prose is not a running spinner.
    /// The token count is not required: it only turns up once output streams, and a turn that
    /// streams late (a /compact, a long think) reads "(16s)" or "(16s · thinking)" until then.
    /// Some verbs are hyphenated ("Sock-hopping…"), so the word allows - and '.
    /// </summary>
    private static readonly Regex WorkingSpinnerRegex = new(
        @"^\s{0,6}[^\s\p{L}\p{N}]\s*\p{L}[\p{L} '\-]*(?:…|\.\.\.)\s*\(\s*(?:\d+m\s*)?\d+s\s*(?:\)|[·.])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the elapsed-time part of a working line: "(12s" or "(1m 5s".</summary>
    private static readonly Regex ElapsedRegex = new(@"\((?:(?<min>\d+)\s*m\s*)?(?<sec>\d+)\s*s\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches a tool-call line, e.g. "● Read(Services/AppSettings.cs)" or "● Web Search("…")".</summary>
    private static readonly Regex ToolLineRegex = new(@"[●•⏺✳]\s*([A-Za-z][A-Za-z ]*?)\(([^()]*)\)", RegexOptions.Compiled);

    /// <summary>Matches a permission-prompt question, e.g. "Do you want to proceed?" or "Do you want to make this edit?".</summary>
    private static readonly Regex WaitingRegex = new(@"do you want to", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Context remaining ────────────────────────────────────────────────

    /// <summary>Matches "23% until auto-compact", the countdown the CLI prints once the session nears the limit.</summary>
    private static readonly Regex ContextUntilCompactRegex = new(@"(\d{1,3})\s*%\s*until\s+auto-?compact", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "87% context used", printed in place of the countdown when auto-compact will not fire.</summary>
    private static readonly Regex ContextUsedRegex = new(@"(\d{1,3})\s*%\s*context\s+used", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Context left until auto-compact: 23%", the wording older builds used.</summary>
    private static readonly Regex ContextAutoCompactRegex = new(@"context\s+left\s+until\s+auto-?compact:?\s*(\d{1,3})\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Context low (12% remaining)".</summary>
    private static readonly Regex ContextLowRegex = new(@"context\s+low\s*\(\s*(\d{1,3})\s*%\s*remaining\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "23% context left".</summary>
    private static readonly Regex ContextLeftRegex = new(@"(\d{1,3})\s*%\s*context\s+left", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches the /context readout, e.g. "Free space: 956.1k (95.6%)".</summary>
    private static readonly Regex ContextFreeSpaceRegex = new(
        @"free\s+space:\s*[\d.,]+\s*[km]?\s*\(\s*(\d{1,3})(?:\.\d+)?\s*%\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Error diagnosis ─────────────────────────────────────────────────

    /// <summary>Matches "Invalid API key", "Please run /login", "OAuth token has expired", "authentication_error", or a bare "401".</summary>
    private static readonly Regex DiagAuthExpiredRegex = new(@"invalid api key|please run /login|oauth token has expired|authentication_error|\b401\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Rate limit exceeded, please try again", "rate_limit_error", or a bare "429".</summary>
    private static readonly Regex DiagRateLimitedRegex = new(@"rate limit|rate_limit_error|\b429\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Usage limit reached" or "You've reached your usage limit".</summary>
    private static readonly Regex DiagUsageLimitRegex = new(@"usage limit reached|you.?ve reached your usage limit", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "resets at 3pm" so the reset time can be surfaced in the detail text.</summary>
    private static readonly Regex UsageResetRegex = new(@"resets?\s+at\s+([^\n\r.,;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches the wait a 429 names: "try again in 30 seconds", "retry after 1m 30s",
    /// "please try again in 2 minutes". Deliberately narrow - only digit+unit runs are taken,
    /// so the box-drawing characters that share the line cannot be swept into the message.
    /// </summary>
    private static readonly Regex RetryAfterRegex = new(
        @"(?:try\s+again|retry)\s+(?:in|after)\s+((?:\d+\s*(?:h(?:ours?|rs?)?|m(?:in(?:ute)?s?)?|s(?:ec(?:ond)?s?)?)\s*){1,3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "ENOTFOUND", "ETIMEDOUT", "ECONNREFUSED", "network error", "fetch failed", or "getaddrinfo".</summary>
    private static readonly Regex DiagNetworkDownRegex = new(@"enotfound|etimedout|econnrefused|network error|fetch failed|getaddrinfo", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "Update available" or "A new version of Claude Code is available".</summary>
    private static readonly Regex DiagOutdatedCliRegex = new(@"update available|a new version of claude code", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches "EACCES", "EPERM", "ENOSPC", or "permission denied".</summary>
    private static readonly Regex DiagDiskOrPermissionRegex = new(@"eacces|eperm|enospc|permission denied", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches a generic "Error: something broke" or "Failed to do X" line not caught by a more specific pattern.</summary>
    private static readonly Regex DiagGenericErrorRegex = new(@"error:|failed to", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Model switch ─────────────────────────────────────────────────────

    /// <summary>Matches "Set model to Fable 5.1 and saved as your default for new sessions" and
    /// the plain "Set model to Opus 5" the CLI prints for a session-only switch.</summary>
    private static readonly Regex ModelSetRegex = new(
        @"set model to\s+([A-Za-z][A-Za-z0-9 ./+-]*?)(?:\s+and saved\b|\s+for this session\b|\s*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Analyzes the visible terminal text and returns everything the UI needs to render badges/indicators. Never throws.</summary>
    /// <param name="screenText">The terminal's visible screen (plus a few dozen lines of scrollback), newline-separated with \n.</param>
    public static TerminalSnapshot Analyze(string screenText)
    {
        try
        {
            var lines = SplitWindow(screenText);

            var (mode, modeText) = DetectMode(lines);
            var (isWorking, elapsed) = DetectWorking(lines);
            var (activity, target) = DetectActivity(lines, isWorking);
            var contextPct = DetectContextRemaining(lines);
            var error = DetectError(lines);
            var modelSwitchedTo = DetectModelSwitch(lines);

            return new TerminalSnapshot
            {
                Mode = mode,
                ModeText = modeText,
                Activity = activity,
                ActivityText = ActivityLabel(activity),
                ActivityTarget = target,
                ElapsedSeconds = elapsed,
                IsWorking = isWorking,
                ContextRemainingPercent = contextPct,
                Error = error,
                ModelSwitchedTo = modelSwitchedTo
            };
        }
        catch
        {
            return new TerminalSnapshot { Mode = AiMode.Unknown, Activity = AiActivity.None };
        }
    }

    /// <summary>
    /// Whether the CLI is mid-turn. Cheaper than a full <see cref="Analyze"/> when the spinner
    /// is all that matters - a background window's progress line, say. Never throws.
    /// </summary>
    public static bool IsWorking(string screenText)
    {
        try { return DetectWorking(SplitWindow(screenText)).IsWorking; }
        catch { return false; }
    }

    /// <summary>Localized long-form display name for a mode, e.g. for a tooltip.</summary>
    public static string ModeLabel(AiMode mode) => mode switch
    {
        AiMode.Normal => Loc.Get("ModeNormalLabel", "Normal mode"),
        AiMode.AcceptEdits => Loc.Get("ModeAcceptEditsLabel", "Auto-accept edits"),
        AiMode.Plan => Loc.Get("ModePlanLabel", "Plan mode"),
        AiMode.BypassPermissions => Loc.Get("ModeBypassPermissionsLabel", "Bypass permissions"),
        _ => Loc.Get("ModeUnknownLabel", "Unknown mode")
    };

    /// <summary>Localized short display name for a mode, sized for a small status badge.</summary>
    public static string ModeShortLabel(AiMode mode) => mode switch
    {
        AiMode.Normal => Loc.Get("ModeNormalShort", "normal"),
        AiMode.AcceptEdits => Loc.Get("ModeAcceptEditsShort", "auto-accept"),
        AiMode.Plan => Loc.Get("ModePlanShort", "plan"),
        AiMode.BypassPermissions => Loc.Get("ModeBypassPermissionsShort", "bypass"),
        _ => Loc.Get("ModeUnknownShort", "?")
    };

    // ── Internals ────────────────────────────────────────────────────────

    private static string[] SplitWindow(string screenText)
    {
        if (string.IsNullOrEmpty(screenText)) return Array.Empty<string>();
        var all = screenText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return all.Length <= ScanWindowLines ? all : all[^ScanWindowLines..];
    }

    private static (AiMode Mode, string Text) DetectMode(string[] lines)
    {
        var sawInputPrompt = false;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            Match m;
            if ((m = ModeBypassRegex.Match(line)).Success) return (AiMode.BypassPermissions, ModePhrase(m.Value));
            if ((m = ModePlanRegex.Match(line)).Success) return (AiMode.Plan, ModePhrase(m.Value));
            if ((m = ModeManualRegex.Match(line)).Success) return (AiMode.Normal, ModePhrase(m.Value));
            if ((m = ModeAcceptEditsRegex.Match(line)).Success) return (AiMode.AcceptEdits, ModePhrase(m.Value));
            if (!sawInputPrompt && InputPromptRegex.IsMatch(line)) sawInputPrompt = true;
        }
        return sawInputPrompt ? (AiMode.Normal, "") : (AiMode.Unknown, "");
    }

    /// <summary>Matches the trailing " on" of "accept edits on", which reads as noise on a badge.</summary>
    private static readonly Regex ModePhraseTailRegex = new(@"\s+on$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Tidies a matched mode phrase into a badge label: collapsed spaces, lower case, no trailing "on".</summary>
    private static string ModePhrase(string matched)
    {
        var phrase = Regex.Replace(matched.Trim(), @"\s+", " ");
        return ModePhraseTailRegex.Replace(phrase, "").ToLowerInvariant();
    }

    /// <summary>
    /// How far above the last written line the spinner can sit. The CLI redraws it directly above
    /// the composer, so this has to clear the spinner's todo hint, the composer box and the status
    /// rows beneath it - but no more. Everything further up is transcript: text the CLI printed
    /// once and will not update, which says nothing about what it is doing now.
    /// </summary>
    private const int WorkingTailLines = 16;

    private static (bool IsWorking, int? ElapsedSeconds) DetectWorking(string[] lines)
    {
        var i = FindWorkingLine(lines);
        if (i < 0) return (false, null);

        var m = ElapsedRegex.Match(lines[i]);
        if (!m.Success) return (true, null);

        var seconds = int.TryParse(m.Groups["sec"].Value, out var s) ? s : 0;
        if (m.Groups["min"].Success && int.TryParse(m.Groups["min"].Value, out var min))
            seconds += min * 60;
        return (true, seconds);
    }

    private static int FindWorkingLine(string[] lines)
    {
        // A screen is padded with blank rows to its full height, so the tail has to be measured
        // from the last row that has anything on it rather than from the end of the array.
        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
        var first = Math.Max(0, last - WorkingTailLines + 1);

        for (var i = last; i >= first; i--)
            if (WorkingLineRegex.IsMatch(lines[i]) || WorkingSpinnerRegex.IsMatch(lines[i])) return i;
        return -1;
    }

    /// <summary>
    /// The spinner line as the terminal shows it, e.g. "Compacting conversation… (22s · ↑ 1.4k tokens)",
    /// without its animated glyph; null when the CLI is not working.
    /// </summary>
    public static string? WorkingStatus(string screenText)
    {
        try
        {
            var lines = SplitWindow(screenText);
            var i = FindWorkingLine(lines);
            if (i < 0) return null;
            var line = lines[i].Trim();
            // The glyph cycles (✻ ✳ · …); the chat view runs its own
            if (line.Length > 1 && !char.IsLetterOrDigit(line[0]) && line[0] != '(') line = line[1..].TrimStart();
            return line;
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, AiActivity> ToolVerbMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = AiActivity.Reading,
        ["cat"] = AiActivity.Reading,
        ["write"] = AiActivity.Writing,
        ["create"] = AiActivity.Writing,
        ["edit"] = AiActivity.Editing,
        ["update"] = AiActivity.Editing,
        ["multiedit"] = AiActivity.Editing,
        ["bash"] = AiActivity.RunningCommand,
        ["run"] = AiActivity.RunningCommand,
        ["search"] = AiActivity.Searching,
        ["grep"] = AiActivity.Searching,
        ["glob"] = AiActivity.Searching,
        ["find"] = AiActivity.Searching,
        ["webfetch"] = AiActivity.Browsing,
        ["websearch"] = AiActivity.Browsing,
        ["fetch"] = AiActivity.Browsing,
    };

    private static (AiActivity Activity, string? Target) DetectActivity(string[] lines, bool isWorking)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var m = ToolLineRegex.Match(lines[i]);
            if (!m.Success) continue;

            var verbKey = Regex.Replace(m.Groups[1].Value, @"\s+", "");
            if (!ToolVerbMap.TryGetValue(verbKey, out var activity)) continue;

            var target = m.Groups[2].Value.Trim();
            if (target.Length > 60) target = target[..60];
            return (activity, target.Length == 0 ? null : target);
        }

        if (isWorking) return (AiActivity.Thinking, null);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (WaitingRegex.IsMatch(lines[i])) return (AiActivity.Waiting, null);
        }

        return (AiActivity.None, null);
    }

    private static string ActivityLabel(AiActivity activity) => activity switch
    {
        AiActivity.Thinking => Loc.Get("ActivityThinking", "Thinking…"),
        AiActivity.Reading => Loc.Get("ActivityReading", "Reading files…"),
        AiActivity.Writing => Loc.Get("ActivityWriting", "Writing a file…"),
        AiActivity.Editing => Loc.Get("ActivityEditing", "Editing a file…"),
        AiActivity.RunningCommand => Loc.Get("ActivityRunning", "Running a command…"),
        AiActivity.Searching => Loc.Get("ActivitySearching", "Searching…"),
        AiActivity.Browsing => Loc.Get("ActivityBrowsing", "Fetching from the web…"),
        AiActivity.Waiting => Loc.Get("ActivityWaiting", "Waiting for your answer…"),
        _ => ""
    };

    private static int? DetectContextRemaining(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];

            // "87% context used" counts the other way, so it is turned around here and the
            // caller only ever sees one meaning: how much room is left.
            var used = ContextUsedRegex.Match(line);
            if (used.Success && int.TryParse(used.Groups[1].Value, out var usedPct))
                return Math.Clamp(100 - usedPct, 0, 100);

            var m = ContextUntilCompactRegex.Match(line);
            if (!m.Success) m = ContextAutoCompactRegex.Match(line);
            if (!m.Success) m = ContextLowRegex.Match(line);
            if (!m.Success) m = ContextLeftRegex.Match(line);
            if (!m.Success) m = ContextFreeSpaceRegex.Match(line);
            if (!m.Success) continue;

            if (int.TryParse(m.Groups[1].Value, out var pct))
                return Math.Clamp(pct, 0, 100);
        }
        return null;
    }

    private static string? DetectModelSwitch(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var m = ModelSetRegex.Match(lines[i]);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private static ErrorDiagnosis? DetectError(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (DiagAuthExpiredRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.AuthExpired, trimmed,
                    "DiagAuthExpiredTitle", "Your session has expired",
                    "DiagAuthExpiredDetail", "Sign in again to continue.",
                    "DiagAuthExpiredAction", "Sign in", "/login");

            if (DiagRateLimitedRegex.IsMatch(line))
            {
                // A 429 is short-term throttling, not the plan's usage window, so the only time
                // worth showing is the one the CLI names on the line - it knows what it was
                // throttled on. Naming nothing is honest; the 5-hour window belongs to
                // UsageLimit and would overstate this wait by hours.
                var detail = Loc.Get("DiagRateLimitedDetail", "Too many requests were sent in a short time. Wait a moment and try again.");
                string? resetSuffix = null;

                var resetsAt = UsageResetRegex.Match(line);
                if (resetsAt.Success)
                    resetSuffix = string.Format(Loc.Get("DiagRateLimitedResetAt", "It clears at {0}."),
                        resetsAt.Groups[1].Value.Trim());
                else
                {
                    var retryIn = RetryAfterRegex.Match(line);
                    if (retryIn.Success)
                        resetSuffix = string.Format(Loc.Get("DiagRateLimitedRetryIn", "Try again in {0}."),
                            retryIn.Groups[1].Value.Trim());
                }

                return new ErrorDiagnosis
                {
                    Kind = DiagnosisKind.RateLimited,
                    Title = Loc.Get("DiagRateLimitedTitle", "Rate limit reached"),
                    Detail = resetSuffix == null ? detail : detail + " " + resetSuffix,
                    ActionLabel = null,
                    ActionCommand = null,
                    MatchedLine = trimmed,
                    HasResetTime = resetSuffix != null
                };
            }

            if (DiagUsageLimitRegex.IsMatch(line))
            {
                var detail = Loc.Get("DiagUsageLimitDetail", "You've used up your available usage for this period.");
                var resetMatch = UsageResetRegex.Match(line);
                if (resetMatch.Success)
                {
                    var suffix = string.Format(Loc.Get("DiagUsageLimitResetSuffix", "It resets at {0}."), resetMatch.Groups[1].Value.Trim());
                    detail = detail + " " + suffix;
                }

                return new ErrorDiagnosis
                {
                    Kind = DiagnosisKind.UsageLimit,
                    Title = Loc.Get("DiagUsageLimitTitle", "Usage limit reached"),
                    Detail = detail,
                    ActionLabel = null,
                    ActionCommand = null,
                    MatchedLine = trimmed,
                    HasResetTime = resetMatch.Success
                };
            }

            if (DiagNetworkDownRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.NetworkDown, trimmed,
                    "DiagNetworkDownTitle", "Network connection problem",
                    "DiagNetworkDownDetail", "Claude Code couldn't reach the server. Check your internet connection and try again.",
                    null, null, null);

            if (DiagOutdatedCliRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.OutdatedCli, trimmed,
                    "DiagOutdatedCliTitle", "A newer version of Claude Code is available",
                    "DiagOutdatedCliDetail", "Update to get the latest fixes and features.",
                    "DiagOutdatedCliAction", "Copy update command", "npm install -g @anthropic-ai/claude-code");

            if (DiagDiskOrPermissionRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.DiskOrPermission, trimmed,
                    "DiagDiskOrPermissionTitle", "Disk or permission error",
                    "DiagDiskOrPermissionDetail", "Claude Code couldn't read or write a file. Check disk space and file permissions.",
                    null, null, null);

            if (DiagGenericErrorRegex.IsMatch(line))
                return BuildDiagnosis(DiagnosisKind.UnknownError, trimmed,
                    "DiagUnknownErrorTitle", "Something went wrong",
                    "DiagUnknownErrorDetail", "An error occurred. Check the terminal output above for details.",
                    null, null, null);
        }
        return null;
    }

    private static ErrorDiagnosis BuildDiagnosis(
        DiagnosisKind kind, string matchedLine,
        string titleKey, string titleDefault,
        string detailKey, string detailDefault,
        string? actionKey, string? actionDefault, string? actionCommand)
    {
        return new ErrorDiagnosis
        {
            Kind = kind,
            Title = Loc.Get(titleKey, titleDefault),
            Detail = Loc.Get(detailKey, detailDefault),
            ActionLabel = actionKey != null ? Loc.Get(actionKey, actionDefault ?? "") : null,
            ActionCommand = actionCommand,
            MatchedLine = matchedLine
        };
    }
}
