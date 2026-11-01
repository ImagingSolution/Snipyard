using System;
using System.IO;
using System.Text;

namespace Snipyard.Services;

/// <summary>Which shell a terminal session's CLI is launched inside.</summary>
public enum ShellKind
{
    Cmd,
    PowerShell,
    /// <summary>macOS/Linux: the user's login shell reading a POSIX command line.</summary>
    Posix,
}

/// <summary>
/// Wraps a CLI command in the shell that hosts it, and is the single place that knows how.
/// The shell decides three things a session depends on: the console code page, whether the
/// user's profile and aliases are in scope, and which of the shims an npm install leaves on
/// PATH gets picked - claude.cmd under cmd.exe, claude.ps1 under PowerShell.
///
/// The argument quoting in <see cref="CliProviderService"/> has to match whichever shell this
/// picks, which is why both read <see cref="For"/> with the same provider.
/// </summary>
public static class ShellHost
{
    public const string CmdId = "cmd";
    public const string PowerShellId = "powershell";

    /// <summary>Ids in the order the settings combo lists them.</summary>
    public static readonly string[] Ids = { CmdId, PowerShellId };

    public static ShellKind Parse(string? id) =>
        string.Equals(id, PowerShellId, StringComparison.OrdinalIgnoreCase)
            ? ShellKind.PowerShell
            : ShellKind.Cmd;

    /// <summary>
    /// The shell a CLI started right now would run in: what the CLI pins, else the setting,
    /// and cmd.exe when that asks for a PowerShell this machine does not have. Read per launch
    /// rather than cached, so a change reaches the next new tab without disturbing the open ones.
    /// </summary>
    public static ShellKind For(CliProvider? provider)
    {
        if (!OperatingSystem.IsWindows()) return ShellKind.Posix;
        var kind = Pinned(provider) ?? Parse(AppSettings.Shared.TerminalShell);
        // A PowerShell that is not installed would leave the tab dead on arrival, with
        // nothing on screen to say why. cmd.exe is always there, so fall back to it.
        return kind == ShellKind.PowerShell && ResolvePowerShell() == null ? ShellKind.Cmd : kind;
    }

    /// <summary>
    /// The shell this CLI insists on, or null when it runs in either and the setting decides.
    /// The settings combo reads this to show the pin instead of a choice that would not be kept.
    /// </summary>
    public static ShellKind? Pinned(CliProvider? provider)
    {
        var id = provider?.Shell;
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.Equals(id, CmdId, StringComparison.OrdinalIgnoreCase)) return ShellKind.Cmd;
        if (string.Equals(id, PowerShellId, StringComparison.OrdinalIgnoreCase)) return ShellKind.PowerShell;
        // An id nothing recognises is a typo in providers.json, and pinning a session to a shell
        // the user never named is worse than ignoring it.
        return null;
    }

    private static string? _psPath;
    private static bool _psResolved;

    /// <summary>
    /// PowerShell 7 when it is on PATH, Windows PowerShell 5.1 otherwise, null when neither is.
    /// 7 is preferred because it defaults to UTF-8, which is what a project path holding
    /// characters the machine's ANSI code page cannot represent needs.
    /// Cached: this is consulted once per launch and once per quoted argument.
    /// </summary>
    public static string? ResolvePowerShell()
    {
        if (_psResolved) return _psPath;
        _psPath = CliProviderService.ResolveExecutable("pwsh")
                  ?? CliProviderService.ResolveExecutable("powershell");
        _psResolved = true;
        return _psPath;
    }

    /// <summary>Re-runs the PATH lookup, for when PowerShell is installed while Snipyard is open.</summary>
    public static void InvalidateResolution() => _psResolved = false;

    /// <summary>
    /// The full command line handed to ConPTY: the shell, the encoding setup it needs, and the
    /// CLI command itself.
    /// </summary>
    public static string Build(string command, string? workingDirectory, CliProvider? provider)
        => For(provider) switch
        {
            ShellKind.Posix => BuildPosix(command, workingDirectory),
            ShellKind.PowerShell => BuildPowerShell(command, workingDirectory),
            _ => BuildCmd(command, workingDirectory),
        };

    /// <summary>The line the login shell runs with -c; exec so the CLI owns the terminal itself.</summary>
    private static string BuildPosix(string command, string? workingDirectory)
        => (HasFolder(workingDirectory) ? $"cd {PosixArg(workingDirectory!)} && " : "") + "exec " + command;

    /// <summary>A POSIX single-quoted word: nothing inside expands, and a quote is closed, escaped and reopened.</summary>
    internal static string PosixArg(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// /e:on and /v:off look redundant and are not: both are registry settings a machine can
    /// have flipped, and both decide what this line means. Without command extensions `cd /d`
    /// and `&amp;&amp;` are not commands and the session starts dead; with delayed expansion on, an
    /// `!` inside a prompt is read as a variable. Naming them leaves <see cref="CmdArg"/> one
    /// set of rules to escape against instead of four.
    /// </summary>
    private static string BuildCmd(string command, string? workingDirectory)
    {
        string cdPart = HasFolder(workingDirectory) ? $"cd /d \"{workingDirectory}\" && " : "";
        return $"cmd.exe /e:on /v:off /c chcp 65001 >nul && {cdPart}{command}";
    }

    /// <summary>
    /// PowerShell gets its script through -EncodedCommand rather than -Command. Everything after
    /// -Command is first split by the ordinary Windows argv rules, which eat the quotes an
    /// initial prompt or a path with spaces depends on; base64 UTF-16 gives that pass nothing to
    /// take apart, so the script PowerShell parses is exactly the script written here.
    ///
    /// -NoProfile is deliberately absent: someone who picks PowerShell is picking their profile,
    /// aliases and environment along with it.
    /// </summary>
    private static string BuildPowerShell(string command, string? workingDirectory)
    {
        var exe = ResolvePowerShell();
        if (exe == null) return BuildCmd(command, workingDirectory);

        var script = new StringBuilder();

        // These two code pages are what `chcp 65001` sets on the cmd side, and they have to be
        // in place before the CLI starts or its first output is decoded as ANSI. Guarded because
        // the setter throws on a console that does not own its input.
        script.Append("try { $OutputEncoding = [Console]::OutputEncoding = [Console]::InputEncoding = ")
              .Append("[System.Text.UTF8Encoding]::new($false) } catch { }; ");

        // PowerShell 7.3 changed how it builds the command line for a native process, and the two
        // modes need opposite escaping from the caller. Pinning the old one where the variable
        // exists means PowerShellArg has a single rule to write against instead of two.
        script.Append("if (Test-Path variable:PSNativeCommandArgumentPassing) ")
              .Append("{ $PSNativeCommandArgumentPassing = 'Legacy' }; ");

        // ConPTY already starts the process in this folder; this mirrors the cmd path's `cd /d`
        // so both shells behave the same when something upstream leaves the directory unset.
        if (HasFolder(workingDirectory))
            script.Append("Set-Location -LiteralPath ").Append(SingleQuote(workingDirectory!)).Append("; ");

        script.Append(command);

        var bytes = Encoding.Unicode.GetBytes(script.ToString());
        return $"\"{exe}\" -NoLogo -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(bytes)}";
    }

    /// <summary>A PowerShell literal string: nothing inside expands, and a quote is doubled.</summary>
    internal static string SingleQuote(string value) => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// An argument as PowerShell has to be handed it for the CLI to receive the original text.
    /// Two layers are in play and only one of them is PowerShell's: the single quotes stop the
    /// parser expanding $ and backticks, and the backslash escapes inside them survive the
    /// command line PowerShell then builds for the native process - a line it writes without
    /// escaping anything, so a quote of ours would otherwise arrive as a word boundary.
    /// </summary>
    internal static string PowerShellArg(string value)
        => SingleQuote(CrtEscape(value, WillBeQuoted(value)));

    /// <summary>
    /// An argument as cmd.exe has to be handed it for the CLI to receive the original text.
    /// Two layers again, and cmd's quotes can only serve one of them: inside them a % is still
    /// expanded, so a prompt mentioning %PATH% would arrive as the machine's path. Caret escapes
    /// work everywhere quotes do not, so the quotes the C runtime needs are themselves escaped
    /// and cmd never enters a quoted region - which is also what lets a literal quote through,
    /// since a real one would close the region and leave the rest of the prompt unprotected.
    /// </summary>
    internal static string CmdArg(string value)
    {
        var inner = CrtEscape(value, willBeQuoted: true);
        var sb = new StringBuilder(inner.Length * 2 + 4);

        sb.Append('^').Append('"');
        foreach (var c in inner)
        {
            // The backslashes CrtEscape just wrote are not special to cmd and stay as they are.
            if (c == '%' || c == '"' || CmdMeta.IndexOf(c) >= 0) sb.Append('^');
            sb.Append(c);
        }
        sb.Append('^').Append('"');

        return sb.ToString();
    }

    /// <summary>What cmd acts on outside quotes, beyond the quote and the percent themselves.</summary>
    private const string CmdMeta = "^&|<>()!";

    /// <summary>
    /// Whether PowerShell will wrap this argument in quotes on the command line it builds.
    /// It does that for whitespace and nothing else, and the answer changes how a trailing
    /// backslash has to be written - unquoted it is a backslash, quoted it escapes the quote.
    /// </summary>
    private static bool WillBeQuoted(string value)
    {
        if (value.Length == 0) return true;
        foreach (var c in value)
            if (char.IsWhiteSpace(c)) return true;
        return false;
    }

    /// <summary>
    /// Escapes for the rules the C runtime uses to split a command line back into arguments:
    /// a backslash is literal except in the run before a quote, where the run is halved and an
    /// odd one escapes the quote.
    /// </summary>
    private static string CrtEscape(string value, bool willBeQuoted)
    {
        var sb = new StringBuilder(value.Length + 8);
        int backslashes = 0;

        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }

            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);

            backslashes = 0;
        }

        // A run at the very end only needs doubling when a closing quote is going to follow it.
        sb.Append('\\', willBeQuoted ? backslashes * 2 : backslashes);
        return sb.ToString();
    }

    private static bool HasFolder(string? path)
        => !string.IsNullOrEmpty(path) && Directory.Exists(path);
}
