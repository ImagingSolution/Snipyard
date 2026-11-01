using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>Overall health of one diagnostic check.</summary>
public enum DiagnosticStatus
{
    Ok,
    Warning,
    Error,
}

/// <summary>
/// One row of setup-diagnostic output. TitleKey is a stable localization key the UI
/// layer looks up; Detail is the measured value shown underneath it.
/// </summary>
public record DiagnosticResult(
    string Id,
    string TitleKey,
    DiagnosticStatus Status,
    string Detail,
    string? FixHint,
    string? FixCommand);

/// <summary>
/// Runs a handful of environment checks (CLI present, Node/Git on PATH, config dir seen,
/// logged in, project under git) so a user can tell why a session won't start without
/// digging through logs. Every check is best-effort: a failure downgrades the row to a
/// Warning/Error result rather than throwing.
/// </summary>
public static class SetupDoctor
{
    private const int ProbeTimeoutMs = 3000;

    public static async Task<List<DiagnosticResult>> RunAsync(CliProviderService cli, string? projectFolder)
    {
        var results = new List<DiagnosticResult>();

        try
        {
            // Picks up any CLI that was installed after the app started.
            cli.ResolveExecutables();

            var cliTask = Task.Run(() => CheckCliInstalled(cli));
            var nodeTask = Task.Run(() => CheckToolAsync("node", "DoctorNode", "Node.js",
                "DoctorNodeHint",
                OperatingSystem.IsWindows() ? "winget install OpenJS.NodeJS.LTS" : "brew install node"));
            var gitTask = Task.Run(() => CheckToolAsync("git", "DoctorGit", "Git",
                "DoctorGitHint",
                OperatingSystem.IsWindows() ? "winget install Git.Git" : "xcode-select --install"));
            var configTask = Task.Run(() => CheckConfigDir(cli));
            var authTask = Task.Run(() => CheckAuth(cli));
            var projectGitTask = Task.Run(() => CheckProjectGit(projectFolder));

            // Non-generic overload: authTask/projectGitTask are Task<DiagnosticResult?>, which
            // Task.WhenAll<T> can't mix with the non-nullable tasks above without a warning.
            await Task.WhenAll(new Task[] { cliTask, nodeTask, gitTask, configTask, authTask, projectGitTask });

            // Fixed order regardless of which check finished first.
            results.Add(cliTask.Result);
            results.Add(nodeTask.Result);
            results.Add(gitTask.Result);
            results.Add(configTask.Result);

            var auth = authTask.Result;
            if (auth != null) results.Add(auth);

            var projectGit = projectGitTask.Result;
            if (projectGit != null) results.Add(projectGit);
        }
        catch
        {
            // Diagnostics must never crash the caller; a partial/empty list is acceptable.
        }

        return results;
    }

    // ── Individual checks ──

    private static DiagnosticResult CheckCliInstalled(CliProviderService cli)
    {
        var active = cli.Active;
        if (active.IsInstalled)
        {
            return new DiagnosticResult(
                "cli", "DoctorCliInstalled", DiagnosticStatus.Ok,
                $"{active.DisplayName} ({active.ResolvedPath})",
                null, null);
        }

        return new DiagnosticResult(
            "cli", "DoctorCliInstalled", DiagnosticStatus.Error,
            Loc.Get("NotInstalled"),
            string.Format(Loc.Get("DoctorCliNotOnPathFmt"), active.Name),
            active.InstallHint);
    }

    private static async Task<DiagnosticResult> CheckToolAsync(
        string exe, string titleKey, string label, string fixHintKey, string fixCommand)
    {
        var (found, output) = await TryRunVersionAsync(exe);
        if (found)
        {
            var version = CliProviderService.ParseVersion(output);
            var detail = string.IsNullOrEmpty(version)
                ? string.Format(Loc.Get("DoctorFoundFmt"), label)
                : $"{label} {version}";
            return new DiagnosticResult(exe, titleKey, DiagnosticStatus.Ok, detail, null, null);
        }

        return new DiagnosticResult(
            exe, titleKey, DiagnosticStatus.Warning,
            Loc.Get("NotInstalled"), Loc.Get(fixHintKey), fixCommand);
    }

    private static DiagnosticResult CheckConfigDir(CliProviderService cli)
    {
        var dir = cli.ActiveConfigDirPath;
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            return new DiagnosticResult("config", "DoctorConfigDir", DiagnosticStatus.Ok, dir, null, null);
        }

        return new DiagnosticResult(
            "config", "DoctorConfigDir", DiagnosticStatus.Warning,
            string.IsNullOrEmpty(dir)
                ? Loc.Get("DoctorNotConfigured")
                : string.Format(Loc.Get("DoctorPathNotFoundFmt"), dir),
            Loc.Get("DoctorConfigDirHint"),
            null);
    }

    /// <summary>Only meaningful for Claude Code; other providers don't share this login model.</summary>
    private static DiagnosticResult? CheckAuth(CliProviderService cli)
    {
        if (cli.Active.Id != CliProviderService.ClaudeId) return null;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var credentials = Path.Combine(profile, ".claude", ".credentials.json");
        var claudeJson = Path.Combine(profile, ".claude.json");

        if (File.Exists(credentials) || File.Exists(claudeJson))
        {
            return new DiagnosticResult(
                "auth", "DoctorAuth", DiagnosticStatus.Ok, Loc.Get("DoctorCredentialsFound"), null, null);
        }

        return new DiagnosticResult(
            "auth", "DoctorAuth", DiagnosticStatus.Warning, Loc.Get("NotInstalled"),
            Loc.Get("DoctorAuthHint"),
            "claude");
    }

    private static DiagnosticResult? CheckProjectGit(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return null;

        var root = FindGitRoot(projectFolder);
        if (root != null)
        {
            return new DiagnosticResult("project-git", "DoctorProjectGit", DiagnosticStatus.Ok, root, null, null);
        }

        return new DiagnosticResult(
            "project-git", "DoctorProjectGit", DiagnosticStatus.Warning, Loc.Get("DoctorNotGitRepo"),
            Loc.Get("DoctorProjectGitHint"),
            "git init");
    }

    private static string? FindGitRoot(string startPath)
    {
        try
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // Inaccessible/invalid path — treated the same as "not a git repo".
        }
        return null;
    }

    // ── Process probing ──

    /// <summary>
    /// Runs "{exe} --version" through cmd.exe so PATH-resolved .cmd/.bat shims work the same
    /// as .exe files, mirroring CliProviderService.ReadVersionAsync. Never throws.
    /// </summary>
    private static async Task<(bool Found, string Output)> TryRunVersionAsync(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : exe,
                Arguments = !OperatingSystem.IsWindows() ? "--version" : $"/s /c \"\"{exe}\" --version\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "");

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(ProbeTimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return (false, "");
            }

            var text = await stdout;
            if (string.IsNullOrWhiteSpace(text))
                text = await stderr;

            // cmd.exe writes "'node' is not recognized..." to stderr and exits 9009, so a
            // non-empty stream alone would report a missing tool as found. The exit code is what
            // separates the two; tools that print their version to stderr and exit 0 still count.
            return (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(text), text);
        }
        catch
        {
            return (false, "");
        }
    }
}
