using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>
/// Owns the list of AI CLIs Snipyard can drive, which one is active, and all
/// command-line construction. Definitions live in %AppData%\Snipyard\providers.json
/// so argument changes in a CLI can be fixed without rebuilding.
/// </summary>
public class CliProviderService
{
    public const string ClaudeId = "claude";
    public const string GrokId = "grok";

    public const string LightProfileId = "light";
    public const string StandardProfileId = "standard";
    public const string DeepProfileId = "deep";

    private const int VersionTimeoutMs = 3000;

    private static readonly string SettingsDir = AppPaths.Roaming;

    private static readonly string ProvidersFile = Path.Combine(SettingsDir, "providers.json");

    /// <summary>
    /// Settings the Light profile hands the CLI with --settings. A file rather than inline JSON
    /// because the launch line goes through cmd.exe, where quoted braces do not survive intact.
    /// </summary>
    private static readonly string LightSettingsFile = Path.Combine(SettingsDir, "light-settings.json");

    /// <summary>
    /// Trims the fixed prefix and per-turn tool output the Light profile carries: shorter tool
    /// results, one-line skill descriptions in the system prompt, and no auto-memory block.
    /// </summary>
    private const string LightSettingsJson =
        "{\n  \"bashOutputMaxChars\": 20000,\n  \"skillListingMaxDescChars\": 60,\n  \"autoMemoryEnabled\": false\n}\n";

    /// <summary>The Light profile's flags before --settings was added, for the backfill.</summary>
    private const string LightExtraArgsV1 =
        "--model sonnet --effort low --autocompact 100k --strict-mcp-config --disable-slash-commands";

    /// <summary>
    /// Plain double quotes rather than QuoteArg, because this result is persisted into
    /// providers.json, where it outlives the launch that wrote it and has to read the same in
    /// either shell. A path under %APPDATA% holds no quote, no $ and no backtick, so the two
    /// shells agree on it; the shell-specific escapes QuoteArg emits would only agree with the
    /// shell that happened to be active when the file was written.
    /// </summary>
    private static string LightExtraArgs() => $"{LightExtraArgsV1} --settings \"{LightSettingsFile}\"";

    private static readonly Regex VersionRegex = new(@"\d+\.\d+[\w.\-]*", RegexOptions.Compiled);

    /// <summary>
    /// CLIs that were replaced by a successor. An existing providers.json is rewritten so the
    /// dead entry disappears instead of lingering as an unusable radio button.
    /// </summary>
    private static readonly Dictionary<string, string> RetiredIds = new()
    {
        // Google folded Gemini CLI into Antigravity CLI in May 2026; the gemini binary is gone.
        ["gemini"] = "antigravity",
    };

    private readonly List<CliProvider> _providers;
    private string _activeId = ClaudeId;

    public CliProviderService()
    {
        _providers = Load();
        ResolveExecutables();
    }

    public IReadOnlyList<CliProvider> Providers => _providers;

    /// <summary>Providers whose executable was found on this machine.</summary>
    public IEnumerable<CliProvider> InstalledProviders => _providers.Where(p => p.IsInstalled);

    public string ActiveId
    {
        get => _activeId;
        set
        {
            var id = RetiredIds.TryGetValue(value ?? "", out var successor) ? successor : value ?? "";
            _activeId = _providers.Any(p => p.Id == id) ? id : ClaudeId;
        }
    }

    public CliProvider Active =>
        _providers.FirstOrDefault(p => p.Id == _activeId)
        ?? _providers.FirstOrDefault(p => p.Id == ClaudeId)
        ?? _providers[0];

    public CliFeatures Features => Active.Features;

    /// <summary>
    /// Model/effort to pin on every launch of a CLI that supports it, chosen once at session
    /// start rather than switched mid-conversation - a mid-session switch invalidates the
    /// prompt cache for everything said before it. Null means the profile's own flags decide.
    /// </summary>
    public string? PreferredModel { get; set; }
    public string? PreferredEffort { get; set; }

    /// <summary>
    /// Launch Claude sessions with the status line relay, which is how the rate-limit readout
    /// gets its figures. See <see cref="StatusLineRelay"/>.
    /// </summary>
    public bool RateLimitStatusLine { get; set; }

    public CliProvider? Find(string id) => _providers.FirstOrDefault(p => p.Id == id);

    /// <summary>Launch profiles the active CLI offers. Empty means the picker stays hidden.</summary>
    public IReadOnlyList<LaunchProfile> ActiveProfiles => Active.Profiles;

    /// <summary>
    /// Resolves a stored profile id against the active CLI. Falls back to the standard
    /// profile, then the first one, so a stale id from another CLI never launches unflagged.
    /// </summary>
    public LaunchProfile? FindProfile(string? id)
    {
        var list = Active.Profiles;
        if (list.Count == 0) return null;
        return list.FirstOrDefault(p => p.Id == id)
            ?? list.FirstOrDefault(p => p.Id == StandardProfileId)
            ?? list[0];
    }

    public static string ConfigFolderPath => SettingsDir;

    /// <summary>Absolute path of the active provider's config directory, e.g. C:\Users\me\.claude.</summary>
    public string ActiveConfigDirPath
    {
        get
        {
            var dir = Active.ConfigDir;
            if (string.IsNullOrWhiteSpace(dir)) return "";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                dir.Replace('\\', Path.DirectorySeparatorChar));
        }
    }

    // ── Command construction ──

    /// <summary>
    /// Command for a brand-new session. NewArgs is only applied when an initial prompt
    /// is set; without one the CLI is launched with just the profile flags.
    /// </summary>
    public string BuildNewCommand(string? initialPrompt, LaunchProfile? profile = null)
    {
        var p = Active;
        var shell = ShellHost.For(p);
        var exe = QuoteExe(p.Exe, shell);
        var prompt = SanitizePrompt(initialPrompt);
        var extra = AppendStatusLine(AppendModelEffortOverride(SanitizePrompt(profile?.ExtraArgs)));

        if (string.IsNullOrEmpty(prompt))
            return string.IsNullOrEmpty(extra) ? exe : $"{exe} {extra}";

        var args = p.NewArgs;
        args = string.IsNullOrWhiteSpace(args)
            ? QuoteArg(prompt, shell)
            : args.Contains("{prompt}")
                ? args.Replace("{prompt}", QuoteArg(prompt, shell))
                : $"{args} {QuoteArg(prompt, shell)}";

        // Profile flags lead so a prompt beginning with "-" still lands as the trailing
        // positional argument rather than being eaten as a flag value.
        if (!string.IsNullOrEmpty(extra))
            args = $"{extra} {args}";

        return $"{exe} {args}".Trim();
    }

    /// <summary>
    /// The same launch as BuildNewCommand, as the bare executable name and an unquoted argument
    /// list, for a target (WSL, SSH) whose own shell does the quoting. The name drops any local
    /// path and extension: on the other side the CLI is whatever PATH finds under that name.
    /// </summary>
    public (string Exe, List<string> Args) BuildNewArgv(string? initialPrompt, LaunchProfile? profile = null)
    {
        var p = Active;
        var exe = Path.GetFileNameWithoutExtension((p.Exe ?? "").Trim().Trim('"'));
        var args = SplitArgs(AppendModelEffortOverride(SanitizePrompt(profile?.ExtraArgs)));
        var prompt = SanitizePrompt(initialPrompt);
        if (string.IsNullOrEmpty(prompt)) return (exe, args);

        var template = SplitArgs(p.NewArgs ?? "");
        if (!template.Any(t => t.Contains("{prompt}"))) template.Add("{prompt}");
        args.AddRange(template.Select(t => t.Replace("{prompt}", prompt)));
        return (exe, args);
    }

    /// <summary>Splits flags on whitespace, keeping a double-quoted run as one argument.</summary>
    private static List<string> SplitArgs(string text)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(sb.ToString());
                sb.Clear();
                any = false;
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any) result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// Command that continues the most recent session. The profile flags ride along here too:
    /// a resumed session is the one most likely to be sitting on a large prefix, so a cap such
    /// as --autocompact matters more on this path than on a fresh launch.
    /// </summary>
    public string BuildContinueCommand(LaunchProfile? profile = null)
    {
        var p = Active;
        var exe = QuoteExe(p.Exe, ShellHost.For(p));
        var extra = AppendStatusLine(AppendModelEffortOverride(SanitizePrompt(profile?.ExtraArgs)));
        var args = string.IsNullOrWhiteSpace(p.ContinueArgs) ? "" : p.ContinueArgs.Trim();
        return JoinCommand(exe, extra, args);
    }

    /// <summary>Command that resumes a specific session id. Falls back to continue when unsupported.</summary>
    public string BuildResumeCommand(string sessionId, LaunchProfile? profile = null)
    {
        var p = Active;
        if (string.IsNullOrWhiteSpace(p.ResumeArgs))
            return BuildContinueCommand(profile);

        var exe = QuoteExe(p.Exe, ShellHost.For(p));
        var extra = AppendStatusLine(AppendModelEffortOverride(SanitizePrompt(profile?.ExtraArgs)));
        var args = p.ResumeArgs.Replace("{sessionId}", sessionId).Trim();
        return JoinCommand(exe, extra, args);
    }

    /// <summary>
    /// Appends the user's pinned --model/--effort after the profile flags, so it is the last
    /// word the CLI reads and wins over whatever the profile itself set (e.g. Deep's own
    /// --model opus --effort high). No-op for a CLI that does not understand these flags.
    /// </summary>
    private string AppendModelEffortOverride(string extra)
    {
        if (!Active.Features.SupportsModelEffortOverride) return extra;

        var sb = new System.Text.StringBuilder(extra);
        if (!string.IsNullOrWhiteSpace(PreferredModel))
            sb.Append(sb.Length > 0 ? " " : "").Append("--model ").Append(PreferredModel);
        if (!string.IsNullOrWhiteSpace(PreferredEffort))
            sb.Append(sb.Length > 0 ? " " : "").Append("--effort ").Append(PreferredEffort);
        return sb.ToString();
    }

    /// <summary>
    /// Adds the status line relay for a local Claude launch. Not applied to BuildNewArgv: a WSL
    /// or SSH session runs its CLI on another machine, where this executable's path means nothing.
    /// </summary>
    private string AppendStatusLine(string extra)
        => RateLimitStatusLine && Active.Id == ClaudeId ? StatusLineRelay.ApplyToArgs(extra) : extra;

    /// <summary>Profile flags lead, provider args follow - the same order BuildNewCommand uses.</summary>
    private static string JoinCommand(string exe, string extra, string args)
    {
        var sb = new System.Text.StringBuilder(exe);
        if (!string.IsNullOrEmpty(extra)) sb.Append(' ').Append(extra);
        if (!string.IsNullOrEmpty(args)) sb.Append(' ').Append(args);
        return sb.ToString();
    }

    /// <summary>
    /// Names the executable for the shell that will run it. cmd needs quotes only when the path
    /// holds a space; PowerShell treats a quoted string in command position as a value rather
    /// than a command, so it needs the call operator to run one either way.
    /// </summary>
    private static string QuoteExe(string exe, ShellKind shell)
    {
        exe = (exe ?? "").Trim();
        if (exe.Length == 0) return "";
        if (shell == ShellKind.Posix) return ShellHost.PosixArg(exe.Trim('"'));
        if (shell == ShellKind.PowerShell)
            return "& " + ShellHost.SingleQuote(exe.Trim('"'));
        if (exe.StartsWith("\"")) return exe;
        return exe.Contains(' ') ? $"\"{exe}\"" : exe;
    }

    /// <summary>
    /// Wraps a value so the shell hands it on as one literal argument. Both shells need the
    /// same thing said in their own escapes, and ShellHost is where each one is written down.
    /// </summary>
    private static string QuoteArg(string value, ShellKind shell) => shell switch
    {
        ShellKind.Posix => ShellHost.PosixArg(value),
        ShellKind.PowerShell => ShellHost.PowerShellArg(value),
        _ => ShellHost.CmdArg(value),
    };

    private static string SanitizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "";
        // Newlines would terminate the command line mid-way.
        return prompt.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    // ── Executable detection ──

    /// <summary>Re-resolves every provider's executable against the current PATH.</summary>
    public void ResolveExecutables()
    {
        foreach (var p in _providers)
        {
            var resolved = ResolveExecutable(p.Exe);
            if (resolved != p.ResolvedPath)
            {
                p.ResolvedPath = resolved;
                p.Version = "";
            }
        }
    }

    /// <summary>
    /// Reads `--version` from every installed provider in parallel. Runs off the UI thread;
    /// <paramref name="onProviderUpdated"/> fires once per provider as results arrive.
    /// </summary>
    public async Task DetectVersionsAsync(Action<CliProvider>? onProviderUpdated = null)
    {
        var targets = _providers.Where(p => p.IsInstalled && string.IsNullOrEmpty(p.Version)).ToList();
        if (targets.Count == 0) return;

        await Task.WhenAll(targets.Select(async p =>
        {
            var version = await ReadVersionAsync(p.ResolvedPath!);
            p.Version = version;
            onProviderUpdated?.Invoke(p);
        }));
    }

    private static async Task<string> ReadVersionAsync(string resolvedPath)
    {
        try
        {
            // Routed through cmd.exe so .cmd/.bat shims (npm installs) work the same as .exe.
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : resolvedPath,
                Arguments = OperatingSystem.IsWindows() ? $"/s /c \"\"{resolvedPath}\" --version\"" : "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(VersionTimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return "";
            }

            var text = await stdout;
            if (string.IsNullOrWhiteSpace(text))
                text = await stderr;

            return ParseVersion(text);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Pulls the version number out of a --version line.
    /// "2.1.232 (Claude Code)" → "2.1.232", "codex-cli 0.144.5" → "0.144.5".
    /// </summary>
    internal static string ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";

        var line = output
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (line == null) return "";

        var match = VersionRegex.Match(line);
        // Copilot prints "GitHub Copilot CLI 1.0.80." — drop the sentence-ending punctuation
        return match.Success ? match.Value.TrimEnd('.', '-') : "";
    }

    /// <summary>Mirrors how cmd.exe resolves a bare command name against PATH + PATHEXT.</summary>
    internal static string? ResolveExecutable(string exe)
    {
        exe = (exe ?? "").Trim().Trim('"');
        if (exe.Length == 0) return null;

        var extensions = CandidateExtensions(exe);

        try
        {
            if (Path.IsPathRooted(exe) ||
                exe.Contains(Path.DirectorySeparatorChar) ||
                exe.Contains(Path.AltDirectorySeparatorChar))
            {
                var full = Path.GetFullPath(exe);
                foreach (var ext in extensions)
                {
                    if (File.Exists(full + ext)) return full + ext;
                }
                return null;
            }
        }
        catch
        {
            return null;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;

            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, exe + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    private static List<string> CandidateExtensions(string exe)
    {
        var list = new List<string>();

        // Elsewhere a command is its file name, extension or not.
        if (!OperatingSystem.IsWindows())
        {
            list.Add("");
            return list;
        }

        // An explicit extension is tried as-is first.
        if (Path.HasExtension(exe))
            list.Add("");

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
            pathext = ".COM;.EXE;.BAT;.CMD";

        foreach (var ext in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = ext.Trim();
            if (e.Length == 0) continue;
            if (!e.StartsWith(".")) e = "." + e;
            list.Add(e);
        }

        return list;
    }

    // ── Persistence ──

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var file = new CliProviderFile { Providers = _providers };
            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProvidersFile, json);
        }
        catch { }
    }

    /// <summary>Resets one provider back to its built-in preset. Returns false for unknown ids.</summary>
    public bool RestoreDefaults(string id)
    {
        var preset = BuildPresets().FirstOrDefault(p => p.Id == id);
        var index = _providers.FindIndex(p => p.Id == id);
        if (preset == null || index < 0) return false;

        _providers[index] = preset;
        ResolveExecutables();
        Save();
        return true;
    }

    private static List<CliProvider> Load()
    {
        EnsureLightSettingsFile();
        try
        {
            if (File.Exists(ProvidersFile))
            {
                var json = File.ReadAllText(ProvidersFile);
                var file = JsonSerializer.Deserialize<CliProviderFile>(json);
                if (file?.Providers is { Count: > 0 })
                {
                    var merged = MergeWithPresets(file.Providers, out var changed);
                    // Without writing back, a retired entry would be re-dropped on every launch.
                    if (changed) Write(merged);
                    return merged;
                }
            }
        }
        catch { }

        var presets = BuildPresets();
        Write(presets);
        return presets;
    }

    /// <summary>
    /// Writes the Light profile's settings file when it is missing. Never overwrites: the file
    /// is the user's to tune once it exists.
    /// </summary>
    private static void EnsureLightSettingsFile()
    {
        try
        {
            if (File.Exists(LightSettingsFile)) return;
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(LightSettingsFile, LightSettingsJson);
        }
        catch { }
    }

    private static void Write(List<CliProvider> providers)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(new CliProviderFile { Providers = providers },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ProvidersFile, json);
        }
        catch { }
    }

    /// <summary>
    /// Keeps user edits but adds presets introduced by newer app versions, so an existing
    /// providers.json never hides a newly supported CLI.
    /// </summary>
    private static List<CliProvider> MergeWithPresets(List<CliProvider> stored, out bool changed)
    {
        var presets = BuildPresets();
        var result = new List<CliProvider>();
        changed = false;

        foreach (var stale in stored)
        {
            if (string.IsNullOrWhiteSpace(stale.Id))
            {
                changed = true;
                continue;
            }

            // A retired CLI is swapped for its successor in place, so the list keeps its order.
            if (RetiredIds.TryGetValue(stale.Id, out var successorId))
            {
                changed = true;
                var successor = presets.FirstOrDefault(p => p.Id == successorId);
                if (successor != null && !result.Any(p => p.Id == successorId))
                    result.Add(successor);
                continue;
            }

            // Launch profiles arrived after providers.json first shipped. Backfill them so an
            // existing install gets the presets rather than an empty picker; a user who has
            // already customised or removed them keeps their list untouched.
            if (stale.Profiles is not { Count: > 0 })
            {
                var match = presets.FirstOrDefault(p => p.Id == stale.Id);
                if (match != null && match.Profiles.Count > 0)
                {
                    stale.Profiles = match.Profiles.Select(x => x.Clone()).ToList();
                    changed = true;
                }
            }

            // The Light profile gained a --settings file after it first shipped. A profile still on
            // the original flag set moves to the new one; one the user has edited is left alone.
            foreach (var profile in stale.Profiles ?? new List<LaunchProfile>())
            {
                if (profile.Id == LightProfileId && profile.ExtraArgs == LightExtraArgsV1)
                {
                    profile.ExtraArgs = LightExtraArgs();
                    changed = true;
                }
            }

            // A CLI found to work in only one shell gets pinned in its preset. Backfilling that
            // onto an entry written before the field existed is what keeps an upgrade from
            // launching it into the shell it cannot survive; an entry the user has pinned
            // themselves already holds a value and is left alone.
            if (string.IsNullOrWhiteSpace(stale.Shell))
            {
                var match = presets.FirstOrDefault(p => p.Id == stale.Id);
                if (match != null && !string.IsNullOrWhiteSpace(match.Shell))
                {
                    stale.Shell = match.Shell;
                    changed = true;
                }
            }

            // Same story for the one-shot arguments the commit-message draft needs: an install
            // that predates them would otherwise show the button permanently greyed out.
            if (string.IsNullOrWhiteSpace(stale.OneShotArgs))
            {
                var match = presets.FirstOrDefault(p => p.Id == stale.Id);
                if (match != null && !string.IsNullOrWhiteSpace(match.OneShotArgs))
                {
                    stale.OneShotArgs = match.OneShotArgs;
                    changed = true;
                }
            }

            // Same story for the model/effort override flag: an entry written before it existed
            // deserializes with the field at its bool default (false), permanently hiding the
            // Settings picker even though the preset now supports it.
            if (!stale.Features.SupportsModelEffortOverride)
            {
                var match = presets.FirstOrDefault(p => p.Id == stale.Id);
                if (match != null && match.Features.SupportsModelEffortOverride)
                {
                    stale.Features.SupportsModelEffortOverride = true;
                    changed = true;
                }
            }

            // Same story for the PermissionOverlay flag: an entry written before it was added to
            // BuildPresets() will deserialize false and leave Chat View disabled, even though the
            // preset now enables it. Backfill from the preset if it's switched on there.
            if (!stale.Features.PermissionOverlay)
            {
                var match = presets.FirstOrDefault(p => p.Id == stale.Id);
                if (match != null && match.Features.PermissionOverlay)
                {
                    stale.Features.PermissionOverlay = true;
                    changed = true;
                }
            }

            result.Add(stale);
        }

        foreach (var preset in presets)
        {
            if (!result.Any(p => p.Id == preset.Id))
            {
                result.Add(preset);
                changed = true;
            }
        }
        return result;
    }

    private static List<CliProvider> BuildPresets() => new()
    {
        new CliProvider
        {
            Id = ClaudeId,
            Name = "Claude Code",
            Exe = "claude",
            NewArgs = "{prompt}",
            ContinueArgs = "-c",
            ResumeArgs = "-r {sessionId}",
            // Drafting a commit message is summarisation and runs on every commit, so everything
            // here exists to keep the fixed prefix off the bill: the cheapest model, no MCP
            // servers, no tool definitions, no settings sources (which is what drops CLAUDE.md,
            // skills and plugins) and a one-line system prompt in place of the built-in one.
            // Measured on this repo, that takes a draft from ~67k input tokens to ~4k - about
            // 6.6 yen down to 0.7. Anyone who wants a better writer edits providers.json to
            // --model sonnet; no rebuild needed.
            OneShotArgs = "-p --model haiku --strict-mcp-config --tools \"\" --setting-sources \"\" "
                          + "--system-prompt \"You write git commit messages.\"",
            ConfigDir = ".claude",
            InstallHint = "npm i -g @anthropic-ai/claude-code",
            Features = new CliFeatures
            {
                SessionList = true,
                ChatView = true,
                UsageTracker = true,
                PermissionOverlay = true,
                CompactButton = true,
                ModeSwitchButton = true,
                DiagramViewer = true,
                SupportsModelEffortOverride = true,
                ExitCommand = "/exit\r",
            },
            // Re-reading the conversation prefix (cache_read) is the bulk of a session's cost,
            // so these presets exist to bound context length and trim the fixed prefix.
            // Deliberately not using --bare: it reads auth only from ANTHROPIC_API_KEY or an
            // apiKeyHelper, never OAuth or the keychain, so it breaks Pro/Max sign-in.
            Profiles = new List<LaunchProfile>
            {
                new()
                {
                    Id = LightProfileId,
                    Name = "Light",
                    ExtraArgs = LightExtraArgs(),
                    Description = "ProfileLightDesc",
                },
                new()
                {
                    Id = StandardProfileId,
                    Name = "Standard",
                    ExtraArgs = "--autocompact 200k --exclude-dynamic-system-prompt-sections",
                    Description = "ProfileStandardDesc",
                },
                new()
                {
                    Id = DeepProfileId,
                    Name = "Deep",
                    ExtraArgs = "--model opus --effort high",
                    Description = "ProfileDeepDesc",
                },
            },
        },
        new CliProvider
        {
            Id = "antigravity",
            Name = "Antigravity CLI",
            Exe = "agy",
            // Successor to Gemini CLI. -i runs the prompt then stays interactive (-p would
            // print once and exit); -c picks up the most recent conversation.
            NewArgs = "-i {prompt}",
            ContinueArgs = "-c",
            ResumeArgs = "--conversation {sessionId}",
            OneShotArgs = "-p {prompt}",
            ConfigDir = @".gemini\antigravity-cli",
            InstallHint = "irm https://antigravity.google/cli/install.ps1 | iex",
            Features = new CliFeatures(),
        },
        new CliProvider
        {
            Id = "codex",
            Name = "Codex CLI",
            Exe = "codex",
            NewArgs = "{prompt}",
            ContinueArgs = "resume --last",
            ResumeArgs = "resume {sessionId}",
            // `codex exec` with no PROMPT argument reads the instructions from stdin, which
            // spares the prompt a trip through the .cmd shim's quoting.
            OneShotArgs = "exec",
            ConfigDir = ".codex",
            InstallHint = "npm i -g @openai/codex",
            Features = new CliFeatures(),
        },
        new CliProvider
        {
            Id = "copilot",
            Name = "GitHub Copilot CLI",
            Exe = "copilot",
            NewArgs = "-i {prompt}",
            ContinueArgs = "--continue",
            ResumeArgs = "--resume={sessionId}",
            OneShotArgs = "-p {prompt}",
            ConfigDir = ".copilot",
            InstallHint = "npm i -g @github/copilot",
            Features = new CliFeatures(),
        },
        new CliProvider
        {
            Id = GrokId,
            Name = "Grok CLI",
            Exe = "grok",
            // Bare `grok` opens the TUI; the prompt rides along as the trailing positional
            // argument, the same shape Claude Code and Codex use.
            NewArgs = "{prompt}",
            ContinueArgs = "-c",
            // -r takes the id, or resumes the most recent one when omitted.
            ResumeArgs = "-r {sessionId}",
            // -p is xAI's headless flag (--single); it takes the prompt as its value.
            OneShotArgs = "-p {prompt}",
            ConfigDir = ".grok",
            InstallHint = "npm i -g @xai-official/grok",
            Features = new CliFeatures
            {
                // Grok's TUI quits on /quit, with /exit as an alias.
                ExitCommand = "/exit\r",
            },
        },
    };
}
