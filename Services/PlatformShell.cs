using System;
using System.Diagnostics;

namespace Snipyard.Services;

/// <summary>The file manager: Explorer on Windows, Finder on macOS, xdg-open elsewhere.</summary>
public static class PlatformShell
{
    /// <summary>Opens the containing folder with <paramref name="path"/> selected.</summary>
    public static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows()) Start("explorer.exe", $"/select,\"{path}\"", shell: true);
        else if (OperatingSystem.IsMacOS()) Start("open", "-R", path);
        else Start("xdg-open", System.IO.Path.GetDirectoryName(path) ?? path);
    }

    /// <summary>Shows a folder in the file manager.</summary>
    public static void OpenFolder(string folder)
    {
        if (OperatingSystem.IsWindows()) Start("explorer.exe", $"\"{folder}\"", shell: true);
        else Start(OperatingSystem.IsMacOS() ? "open" : "xdg-open", folder);
    }

    /// <summary>
    /// macOS has no "Open with" dialog to call, but AppleScript's application chooser is the same
    /// question; the pick then goes to `open -a`. Runs off the UI thread because the chooser blocks.
    /// </summary>
    public static void OpenWithMac(string filePath)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("POSIX path of (choose application)");
                using var p = Process.Start(psi);
                if (p == null) return;
                var app = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                // A cancelled chooser exits non-zero with nothing on stdout.
                if (p.ExitCode == 0 && app.Length > 0) Start("open", "-a", app, filePath);
            }
            catch { }
        });
    }

    private static void Start(string exe, string args, bool shell)
    {
        try { Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = shell }); }
        catch { }
    }

    private static void Start(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch { }
    }
}
