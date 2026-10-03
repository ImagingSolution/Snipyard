using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>
/// Gets the plan's rate limits the way Anthropic documents it. Claude Code hands every status
/// line command a JSON snapshot on stdin, and for Pro/Max accounts that snapshot carries
/// rate_limits.five_hour / seven_day. Snipyard launches its Claude sessions with --settings
/// pointing the status line at its own executable ("Snipyard.exe --statusline"); that process
/// saves the rate_limits object for <see cref="RateLimitService"/> and then runs the status line
/// the user had configured, if any, so its output still shows.
///
/// With no status line of the user's own to chain to, the relay prints nothing. Claude Code
/// hides its footer key hints ("? for shortcuts" and the like) whenever any status line is
/// configured, which is why this sits behind a setting.
/// </summary>
public static class StatusLineRelay
{
    public const string Flag = "--statusline";

    private static readonly string SettingsDir = AppPaths.Roaming;

    /// <summary>The latest rate_limits object, exactly as Claude Code sent it.</summary>
    public static string CachePath => Path.Combine(AppPaths.Local, "rate-limits.json");

    /// <summary>The user's own status line gets this long before the relay gives up on it.</summary>
    private static readonly TimeSpan ChainTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Regex SettingsArgRegex = new(
        @"--settings\s+(?:""(?<path>[^""]*)""|(?<path>\S+))", RegexOptions.Compiled);

    // ── Launch side ──

    /// <summary>
    /// Adds the relay to a Claude launch's flags. A --settings file already in the flags (the
    /// Light profile's, or one the user added) is merged into a copy rather than joined by a
    /// second --settings, since only one of the two would be read. Anything unexpected - an
    /// inline JSON value, a missing file, a file that sets its own status line - leaves the
    /// flags exactly as they were.
    /// </summary>
    public static string ApplyToArgs(string extra)
    {
        try
        {
            var match = SettingsArgRegex.Match(extra);
            if (!match.Success)
            {
                var path = WriteSettings(null);
                if (path == null) return extra;
                var flag = $"--settings \"{path}\"";
                return string.IsNullOrEmpty(extra) ? flag : $"{extra} {flag}";
            }

            // cmd.exe expands %VAR% in the launch line, so a profile may well spell the path that way.
            var original = Environment.ExpandEnvironmentVariables(match.Groups["path"].Value);
            if (!File.Exists(original)) return extra;

            var merged = WriteSettings(original);
            if (merged == null) return extra;
            return extra[..match.Index] + $"--settings \"{merged}\"" + extra[(match.Index + match.Length)..];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatusLineRelay] Left the launch flags unchanged: {ex.Message}");
            return extra;
        }
    }

    /// <summary>
    /// Writes the settings file that names the relay as the status line, on top of
    /// <paramref name="basePath"/>'s settings when given. Null when the base already chose a
    /// status line of its own - that is an explicit choice for this launch, and not ours to wrap.
    /// </summary>
    private static string? WriteSettings(string? basePath)
    {
        var settings = basePath == null
            ? new JsonObject()
            : JsonNode.Parse(File.ReadAllText(basePath), documentOptions: LenientJson) as JsonObject;
        if (settings == null || settings.ContainsKey("statusLine")) return null;

        var statusLine = new JsonObject
        {
            ["type"] = "command",
            ["command"] = RelayCommand(),
        };
        CopyDisplayOptions(statusLine);
        settings["statusLine"] = statusLine;

        var path = basePath == null
            ? Path.Combine(SettingsDir, "statusline-settings.json")
            : Path.Combine(SettingsDir,
                $"statusline-{Path.GetFileNameWithoutExtension(basePath)}-{ShortHash(Path.GetFullPath(basePath))}.json");

        var json = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(SettingsDir);
        // Every launch comes through here; rewriting an unchanged file would only churn the disk.
        if (!File.Exists(path) || File.ReadAllText(path) != json)
            File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// The relay replaces the user's statusLine object wholesale, so its padding and refresh
    /// timer would be lost. They are carried over from the user-level settings, which is where
    /// a status line is almost always configured.
    /// </summary>
    private static void CopyDisplayOptions(JsonObject statusLine)
    {
        try
        {
            var userSettings = Path.Combine(ClaudeConfigDir(), "settings.json");
            if (!File.Exists(userSettings)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(userSettings), LenientJson);
            if (!doc.RootElement.TryGetProperty("statusLine", out var own) || own.ValueKind != JsonValueKind.Object)
                return;
            foreach (var name in new[] { "padding", "refreshInterval" })
            {
                if (own.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                    statusLine[name] = JsonNode.Parse(value.GetRawText());
            }
        }
        catch
        {
            // Cosmetic; the relay works the same without them.
        }
    }

    /// <summary>
    /// The command Claude Code runs. It goes through Git Bash when that is installed and through
    /// PowerShell otherwise, so it has to be written for whichever of the two Claude Code will
    /// pick. The path uses forward slashes either way (Git Bash would eat backslashes).
    /// </summary>
    private static string RelayCommand()
    {
        var exe = (Environment.ProcessPath ?? "").Replace('\\', '/');

        if (FindGitBash() != null)
        {
            return IsBareSafe(exe)
                ? $"{exe} {Flag}"
                : $"\"{exe.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`")}\" {Flag}";
        }

        // PowerShell does not wait for a windowed exe - which the Release build is - and would
        // return before the relay had read its input. Piping the output makes it wait.
        return $"& '{exe.Replace("'", "''")}' {Flag} | Write-Output";
    }

    /// <summary>Letters, digits and the path punctuation both shells read literally.</summary>
    private static bool IsBareSafe(string path)
    {
        foreach (var c in path)
        {
            if (!char.IsLetterOrDigit(c) && "/._-:".IndexOf(c) < 0) return false;
        }
        return path.Length > 0;
    }

    private static string ShortHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..8].ToLowerInvariant();

    // ── Relay side: "Snipyard.exe --statusline" ──

    /// <summary>
    /// The whole of a relay run. Called from Program.Main before anything of the UI loads,
    /// because Claude Code starts this process on every status line refresh.
    /// </summary>
    public static int Run()
    {
        byte[] input;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            stdin.CopyTo(buffer);
            input = buffer.ToArray();
        }
        catch
        {
            return 0;
        }

        string? chained = null;
        try
        {
            using var doc = JsonDocument.Parse(input);
            SaveRateLimits(doc.RootElement);
            chained = FindUserStatusLine(doc.RootElement);
        }
        catch
        {
            // Not the JSON we expected: nothing to save, and nothing to hand on either.
        }

        if (chained != null) RunChained(chained, input);
        return 0;
    }

    /// <summary>
    /// A snapshot without rate_limits (an API-key session, or one that has not had a reply yet)
    /// leaves the file alone, so it cannot wipe what another session just reported.
    /// </summary>
    private static void SaveRateLimits(JsonElement root)
    {
        if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var merged = KeepHigherSaved(limits, path);
            // Written aside and moved into place, so a reader never sees half a file.
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, merged);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Several sessions may refresh at once; losing one write just means the next wins.
        }
    }

    private static readonly string[] WindowNames = { "five_hour", "seven_day" };

    /// <summary>
    /// The snapshot to save, with any window the saved file already shows as further spent put
    /// back. Every session redraws its status line with the rate_limits of its own last reply, so
    /// an idle tab would otherwise roll the readout back to an hour-old figure. Within one window
    /// (same reset time) usage only climbs, so the higher figure is the newer one.
    /// </summary>
    private static string KeepHigherSaved(JsonElement limits, string path)
    {
        var fresh = limits.GetRawText();
        try
        {
            if (!File.Exists(path)) return fresh;
            if (JsonNode.Parse(fresh) is not JsonObject merged) return fresh;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject saved) return fresh;

            bool changed = false;
            foreach (var name in WindowNames)
            {
                if (merged[name] is not JsonObject now || saved[name] is not JsonObject before) continue;
                if (ResetsAt(now) is not { } nowReset || ResetsAt(before) is not { } beforeReset) continue;
                // Sessions may round the reset differently; a new window moves it by hours.
                if (Math.Abs(nowReset - beforeReset) > 600) continue;
                if (UsedPercent(before) <= UsedPercent(now)) continue;

                merged[name] = before.DeepClone();
                changed = true;
            }
            return changed ? merged.ToJsonString() : fresh;
        }
        catch
        {
            // An unreadable saved file is simply replaced.
            return fresh;
        }
    }

    private static long? ResetsAt(JsonObject window) =>
        window["resets_at"] is JsonValue v && v.TryGetValue<long>(out var epoch) ? epoch
        : window["resets_at"] is JsonValue d && d.TryGetValue<double>(out var real) ? (long)real
        : null;

    private static double UsedPercent(JsonObject window) =>
        window["used_percentage"] is JsonValue v && v.TryGetValue<double>(out var pct) ? pct : 0;

    /// <summary>
    /// The status line the user configured, in Claude Code's own order of precedence: the
    /// project's local settings, then the project's shared settings, then the user's. The first
    /// file that sets one decides, even when it turns out to be this relay.
    /// </summary>
    private static string? FindUserStatusLine(JsonElement root)
    {
        var project = ReadString(root, "workspace", "project_dir")
                      ?? ReadString(root, "cwd")
                      ?? Environment.CurrentDirectory;

        var candidates = new[]
        {
            Path.Combine(project, ".claude", "settings.local.json"),
            Path.Combine(project, ".claude", "settings.json"),
            Path.Combine(ClaudeConfigDir(), "settings.json"),
        };

        foreach (var file in candidates)
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), LenientJson);
                if (!doc.RootElement.TryGetProperty("statusLine", out var statusLine)
                    || statusLine.ValueKind != JsonValueKind.Object)
                    continue;

                var command = ReadString(statusLine, "command");
                if (string.IsNullOrWhiteSpace(command) || IsRelay(command)) return null;
                return command;
            }
            catch
            {
                // An unreadable file does not set a status line.
            }
        }
        return null;
    }

    /// <summary>
    /// The exe names this relay runs under: whatever this copy is called, plus both names the app
    /// has had, since an install updated in place from before the rename is still Claucraft.exe.
    /// </summary>
    private static readonly string[] RelayExeNames =
    {
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? AppPaths.AppName),
        AppPaths.AppName,
        "Claucraft",
    };

    private static bool IsRelay(string command)
        => command.Contains(Flag, StringComparison.Ordinal)
           && Array.Exists(RelayExeNames, name => command.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the user's status line the way Claude Code would have - Git Bash when it is there,
    /// PowerShell otherwise - on the same stdin, and passes its output through untouched.
    /// </summary>
    private static void RunChained(string command, byte[] input)
    {
        var bash = FindGitBash();
        var psi = new ProcessStartInfo(bash ?? ShellHost.ResolvePowerShell() ?? "powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        if (bash != null)
        {
            psi.ArgumentList.Add("-c");
        }
        else
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
        }
        psi.ArgumentList.Add(command);

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return;

            // Fed from another thread: a script that prints before reading would otherwise
            // stall against a full stdin pipe while this thread waits on its stdout.
            var feed = Task.Run(() =>
            {
                try
                {
                    process.StandardInput.BaseStream.Write(input);
                    process.StandardInput.Close();
                }
                catch
                {
                    // The script exited without reading everything; that is its business.
                }
            });

            var copy = Task.Run(() =>
            {
                using var stdout = Console.OpenStandardOutput();
                process.StandardOutput.BaseStream.CopyTo(stdout);
            });

            if (!process.WaitForExit(ChainTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            Task.WaitAll(new[] { feed, copy }, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatusLineRelay] Chained status line failed: {ex.Message}");
        }
    }

    // ── Shared ──

    private static string ClaudeConfigDir()
    {
        var overridden = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : overridden;
    }

    /// <summary>
    /// Git for Windows' bash, found the way Claude Code finds it: CLAUDE_CODE_GIT_BASH_PATH, else
    /// next to the git on PATH. Never System32's bash.exe, which is WSL.
    /// </summary>
    internal static string? FindGitBash()
    {
        var pinned = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH")?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(pinned) && File.Exists(pinned)) return pinned;

        var git = CliProviderService.ResolveExecutable("git");
        if (git != null)
        {
            // git.exe sits in <Git>\cmd, <Git>\bin or <Git>\mingw64\bin; bash is in <Git>\bin.
            var dir = Path.GetDirectoryName(git)!;
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, "..", "bin", "bash.exe"),
                         Path.Combine(dir, "bash.exe"),
                         Path.Combine(dir, "..", "..", "bin", "bash.exe"),
                     })
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
        }

        // A package-manager shim on PATH says nothing about where Git itself lives.
        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(standard) ? standard : null;
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
