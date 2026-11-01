using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Snipyard.Services;

public enum ExecutionKind { Wsl, Ssh }

/// <summary>A CLI launch bound for a target: the bare executable name and its unquoted arguments.</summary>
public sealed record RemoteLaunch(ExecutionTarget Target, string Exe, List<string> Args);

/// <summary>
/// Somewhere other than this machine's own shell that a session can run: a WSL distro, or a host
/// reached over SSH. The CLI then lives - and keeps its transcripts - on that side.
/// </summary>
/// <param name="Name">The distro name, or the SSH host as ssh itself would take it.</param>
/// <param name="RemoteFolder">SSH only: the folder on the host to start in; empty means home.</param>
/// <param name="ProjectsRoot">
/// WSL only: the distro's ~/.claude/projects as seen from Windows, which is what lets Chat View
/// read the session. Null when the distro's home could not be learned.
/// </param>
public sealed record ExecutionTarget(ExecutionKind Kind, string Name, string RemoteFolder = "",
    string? ProjectsRoot = null)
{
    public string Label => Kind == ExecutionKind.Wsl
        ? $"WSL: {Name}"
        : string.IsNullOrEmpty(RemoteFolder) ? $"SSH: {Name}" : $"SSH: {Name}:{RemoteFolder}";

    /// <summary>
    /// The ConPTY command line that starts <paramref name="exe"/> with <paramref name="args"/> on
    /// this target. It bypasses the local shell entirely: every argument goes to wsl.exe or
    /// ssh.exe as its own argv element, so nothing is quoted for cmd or PowerShell.
    /// </summary>
    public string BuildCommandLine(string exe, IReadOnlyList<string> args, string? localFolder)
    {
        var argv = new List<string>();
        if (Kind == ExecutionKind.Wsl)
        {
            // A login shell, because that is where ~/.local/bin and nvm put the CLI on PATH;
            // "$0" "$@" hands the arguments through without bash reading them a second time.
            argv.Add("wsl.exe");
            argv.Add("-d");
            argv.Add(Name);
            if (!string.IsNullOrEmpty(localFolder))
            {
                argv.Add("--cd");
                argv.Add(localFolder);
            }
            argv.Add("-e");
            argv.Add("bash");
            argv.Add("-lc");
            argv.Add("exec \"$0\" \"$@\"");
            argv.Add(exe);
            argv.AddRange(args);
        }
        else
        {
            // ssh sends a single string for the remote shell to read, so the quoting there is
            // bash's. The inner login shell finds the CLI the same way an interactive one would.
            var inner = new StringBuilder();
            if (!string.IsNullOrEmpty(RemoteFolder))
                inner.Append("cd ").Append(BashQuote(RemoteFolder)).Append(" && ");
            inner.Append("exec ").Append(BashQuote(exe));
            foreach (var a in args) inner.Append(' ').Append(BashQuote(a));

            argv.Add(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");
            argv.Add("-t");
            argv.Add(Name);
            argv.Add("exec bash -lc " + BashQuote(inner.ToString()));
        }
        // Off Windows the line is read by the local login shell (UnixPseudoConsole), not CreateProcess.
        return string.Join(" ", argv.Select(OperatingSystem.IsWindows() ? (Func<string, string>)CrtQuote : BashQuote));
    }

    /// <summary>
    /// Where this WSL session's transcript is, or null when it is not there (yet). The CLI names
    /// the folder after its Linux working directory, which for a Windows folder is /mnt/x/...
    /// </summary>
    public string? FindSessionFile(string? localFolder, string sessionId)
    {
        if (Kind != ExecutionKind.Wsl || string.IsNullOrEmpty(ProjectsRoot)) return null;
        var name = sessionId + ".jsonl";
        try
        {
            var dir = ProjectDir(localFolder);
            if (dir != null && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
            // A long path gets a shortened folder name, so fall back to looking everywhere
            foreach (var d in Directory.EnumerateDirectories(ProjectsRoot))
            {
                var p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    /// <summary>The newest session begun in this WSL folder since <paramref name="after"/>.</summary>
    public string? FindSessionIdCreatedAfter(string? localFolder, DateTime after, HashSet<string> taken)
    {
        var dir = ProjectDir(localFolder);
        if (dir == null) return null;
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*.jsonl")
                .Where(f => f.CreationTime >= after)
                .OrderByDescending(f => f.CreationTime)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .FirstOrDefault(id => !taken.Contains(id));
        }
        catch { return null; }
    }

    private string? ProjectDir(string? localFolder)
    {
        if (string.IsNullOrEmpty(ProjectsRoot) || string.IsNullOrEmpty(localFolder)) return null;
        var dir = Path.Combine(ProjectsRoot, Mangle(WslPath(localFolder)));
        return Directory.Exists(dir) ? dir : null;
    }

    // ── Helpers ──

    /// <summary>C:\x\y as WSL mounts it: /mnt/c/x/y. Anything else is passed through.</summary>
    public static string WslPath(string windowsPath)
    {
        var p = windowsPath.TrimEnd('\\', '/');
        if (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]))
            return "/mnt/" + char.ToLowerInvariant(p[0]) + p[2..].Replace('\\', '/');
        return p.Replace('\\', '/');
    }

    /// <summary>The CLI's project-folder name for a working directory.</summary>
    public static string Mangle(string path) => Regex.Replace(path, "[^A-Za-z0-9]", "-");

    /// <summary>One bash word holding exactly <paramref name="s"/>.</summary>
    public static string BashQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>One argv element as CommandLineToArgvW will split it back out.</summary>
    public static string CrtQuote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1);
            else sb.Append('\\', slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Parses "host" or "host:/folder" (user@ allowed). Rejects anything ssh would read as an
    /// option or split into more than one word.
    /// </summary>
    public static ExecutionTarget? ParseSsh(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        int colon = text.IndexOf(':');
        var host = colon < 0 ? text : text[..colon];
        var folder = colon < 0 ? "" : text[(colon + 1)..].Trim();
        // Handed to ssh.exe as its destination argument, so it must be a plain [user@]host or an
        // ssh_config alias: nothing that ssh could read as an option or a URI.
        if (!SshDestination.IsMatch(host)) return null;
        return new ExecutionTarget(ExecutionKind.Ssh, host, folder);
    }

    private static readonly Regex SshDestination =
        new(@"^([A-Za-z0-9_.][A-Za-z0-9_.-]*@)?[A-Za-z0-9_.][A-Za-z0-9_.-]*$");

    // ── Discovery ──

    /// <summary>Installed WSL distros; empty when WSL is missing or holds none.</summary>
    public static List<string> ListWslDistros()
    {
        var output = RunWsl(3000, "-l", "-q");
        if (output == null) return new();
        return output.Split('\n')
            .Select(l => l.Replace("\0", "").Trim())
            .Where(l => l.Length > 0 && !l.Contains(' '))
            .ToList();
    }

    /// <summary>A WSL target with the distro's projects folder filled in when it can be found.</summary>
    public static ExecutionTarget ResolveWsl(string distro)
    {
        string? root = null;
        var home = RunWsl(15000, "-d", distro, "-e", "sh", "-c", "printf %s \"$HOME\"")?.Replace("\0", "").Trim();
        if (!string.IsNullOrEmpty(home) && home.StartsWith('/'))
            root = $@"\\wsl.localhost\{distro}{home.Replace('/', '\\')}\.claude\projects";
        return new ExecutionTarget(ExecutionKind.Wsl, distro, "", root);
    }

    /// <summary>Hosts named in ~/.ssh/config, wildcards left out.</summary>
    public static List<string> ListSshConfigHosts()
    {
        var hosts = new List<string>();
        try
        {
            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            if (!File.Exists(config)) return hosts;
            foreach (var line in File.ReadLines(config))
            {
                var m = Regex.Match(line, @"^\s*Host\s+(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                hosts.AddRange(m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(h => h.IndexOfAny(new[] { '*', '?', '!' }) < 0));
            }
        }
        catch { }
        return hosts.Distinct().ToList();
    }

    private static string? RunWsl(int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // Without this wsl.exe's own messages come out as UTF-16
            psi.Environment["WSL_UTF8"] = "1";
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch { return null; }
    }
}
