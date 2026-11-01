using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Snipyard.Services;

/// <summary>
/// The processes running under one of ours. ConPTY hands back the pid of the shell it started -
/// cmd.exe or PowerShell - and the CLI we actually want to identify is a child of that, so the
/// only way from a window to its CLI process is down the parent links.
///
/// Toolhelp rather than WMI: this runs on the poll that follows a launch, and a
/// Win32_Process query costs tens of milliseconds per process where a snapshot costs one for
/// the whole table.
/// </summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Every process below <paramref name="rootPid"/>, the root itself first.
    ///
    /// Windows keeps the recorded parent pid after the parent dies and hands the number out
    /// again, so the raw parent links alone are not a family tree: a freshly started shell
    /// inherits the orphans of whatever last held its pid, and a caller matching pids against a
    /// per-process record could be handed one belonging to a stranger. A process cannot be
    /// created before its parent, so a candidate that started earlier than the root is one of
    /// those leftovers and is dropped, along with everything below it.
    /// </summary>
    public static List<int> Descendants(int rootPid)
    {
        var found = new List<int> { rootPid };
        if (rootPid <= 0) return found;

        DateTime rootStart;
        try
        {
            using var root = Process.GetProcessById(rootPid);
            rootStart = root.StartTime;
        }
        catch { return found; }   // the root is already gone; it has no live descendants

        var children = OperatingSystem.IsWindows() ? new Dictionary<int, List<int>>() : UnixChildren();
        IntPtr snapshot = InvalidHandle;
        if (OperatingSystem.IsWindows())
        try
        {
            snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == InvalidHandle) return found;

            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return found;
            do
            {
                int pid = (int)entry.th32ProcessID;
                int parent = (int)entry.th32ParentProcessID;
                if (pid == parent) continue;    // the idle process names itself
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = new List<int>();
                list.Add(pid);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch { return found; }
        finally
        {
            if (snapshot != InvalidHandle) CloseHandle(snapshot);
        }

        // Breadth-first, with a visited set: a recycled pid can close a loop in the parent links.
        var seen = new HashSet<int> { rootPid };
        for (int i = 0; i < found.Count && found.Count < 256; i++)
        {
            if (!children.TryGetValue(found[i], out var kids)) continue;
            foreach (int kid in kids)
                if (seen.Add(kid) && StartedAfter(kid, rootStart)) found.Add(kid);
        }
        return found;
    }

    /// <summary>The parent links on macOS/Linux, read from ps since there is no Toolhelp.</summary>
    private static Dictionary<int, List<int>> UnixChildren()
    {
        var children = new Dictionary<int, List<int>>();
        try
        {
            var psi = new ProcessStartInfo("ps", "-A -o pid= -o ppid=")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var ps = Process.Start(psi);
            if (ps == null) return children;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(2000);
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[0], out int pid) || !int.TryParse(parts[1], out int parent)) continue;
                if (pid == parent) continue;
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = new List<int>();
                list.Add(pid);
            }
        }
        catch { }
        return children;
    }

    /// <summary>
    /// Whether this process is young enough to be descended from something that started at
    /// <paramref name="rootStart"/>. A process we cannot open is not one of ours either: the
    /// processes under our own shell are always readable, and the ones that refuse are system
    /// processes that only appear here through a recycled pid.
    /// </summary>
    private static bool StartedAfter(int pid, DateTime rootStart)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime >= rootStart;
        }
        catch { return false; }
    }
}
