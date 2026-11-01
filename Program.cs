using Avalonia;
using System;

namespace Snipyard;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Claude Code runs this on every status line refresh. It has to answer before the debug
        // banner below could reach stdout, and without paying for the UI it never shows.
        if (args.Length > 0 && args[0] == Services.StatusLineRelay.Flag)
        {
            Environment.Exit(Services.StatusLineRelay.Run());
            return;
        }

        if (OperatingSystem.IsMacOS()) ImportLoginShellPath();

        RunApp(args);
    }

    /// <summary>
    /// An app started from Finder or the Dock gets launchd's PATH (/usr/bin:/bin:...), which has
    /// neither Homebrew nor npm's global bin, so no CLI would be found. The login shell's PATH is
    /// the one the user's Terminal sees; adopt it once, before anything resolves an executable.
    /// </summary>
    private static void ImportLoginShellPath()
    {
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !System.IO.File.Exists(shell)) shell = "/bin/zsh";
            var psi = new System.Diagnostics.ProcessStartInfo(shell)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            // -i as well as -l: nvm and most PATH edits live in .zshrc, which only interactive shells read.
            foreach (var a in new[] { "-l", "-i", "-c", "printf '\\n__SNIPYARD_PATH__%s' \"$PATH\"" })
                psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return; }
            const string marker = "__SNIPYARD_PATH__";
            var text = stdout.Result;
            int at = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return;
            var path = text[(at + marker.Length)..].Trim();
            if (path.Length > 0) Environment.SetEnvironmentVariable("PATH", path);
        }
        catch { }
    }

    /// <summary>Kept out of Main so a status line run never loads the Avalonia assemblies.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunApp(string[] args)
    {
#if DEBUG
        // Debugビルド時はコンソールウィンドウにDebug出力を表示
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        Console.WriteLine("=== Debug Console ===");
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
