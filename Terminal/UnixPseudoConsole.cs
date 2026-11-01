using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Snipyard.Services;

namespace Snipyard.Terminal;

/// <summary>
/// The macOS/Linux side of <see cref="IPseudoConsole"/>: forkpty, then the user's login shell
/// runs <c>command</c> as a POSIX command line (see <see cref="ShellHost.Build"/>).
///
/// The child half of fork runs in a copy of this process with only one thread, so it must not
/// touch the managed runtime: everything it needs is marshalled to native memory before the
/// fork, and the calls it makes carry SuppressGCTransition so no GC poll can stall it waiting
/// for threads that no longer exist.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixPseudoConsole : IPseudoConsole
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Row, Col, XPixel, YPixel;
    }

    [DllImport("libc", EntryPoint = "forkpty"), SuppressGCTransition]
    private static extern unsafe int ForkPtyLibc(int* master, IntPtr name, IntPtr termios, WinSize* size);

    [DllImport("libutil.so.1", EntryPoint = "forkpty"), SuppressGCTransition]
    private static extern unsafe int ForkPtyLibutil(int* master, IntPtr name, IntPtr termios, WinSize* size);

    [DllImport("libc", EntryPoint = "execve"), SuppressGCTransition]
    private static extern int Execve(IntPtr path, IntPtr argv, IntPtr envp);

    [DllImport("libc", EntryPoint = "chdir"), SuppressGCTransition]
    private static extern int Chdir(IntPtr path);

    [DllImport("libc", EntryPoint = "signal"), SuppressGCTransition]
    private static extern IntPtr Signal(int signum, IntPtr handler);

    [DllImport("libc", EntryPoint = "_exit"), SuppressGCTransition]
    private static extern void Exit(int status);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern unsafe int WaitPid(int pid, int* status, int options);

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int KillProcess(int pid, int sig);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fd);

    // ioctl is variadic. Apple's arm64 ABI passes variadic arguments on the stack, so the
    // padded overload fills x2-x7 with dummies to push the real one there.
    [DllImport("libc", EntryPoint = "ioctl")]
    private static extern unsafe int Ioctl(int fd, nuint request, WinSize* arg);

    [DllImport("libc", EntryPoint = "ioctl")]
    private static extern unsafe int IoctlApple(int fd, nuint request,
        nint p2, nint p3, nint p4, nint p5, nint p6, nint p7, WinSize* arg);

    private const int SIGHUP = 1, SIGKILL = 9, SIGPIPE = 13, SIGINT = 2;
    private const int EINTR = 4;

    private static nuint TIOCSWINSZ => OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

    private int _master = -1;
    private int _pid;
    private FileStream? _reader;
    private FileStream? _writer;
    private readonly ManualResetEventSlim _exited = new(false);
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event Action<byte[]>? OutputReceived;
    public event Action? ProcessExited;
    public int ProcessId => _pid;

    /// <summary>$SHELL when it points at something, else zsh (the macOS default), else sh.</summary>
    public static string UserShell
    {
        get
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(shell) && File.Exists(shell)) return shell;
            return File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";
        }
    }

    public unsafe void Start(string command, string? workingDirectory, int cols, int rows)
    {
        var shell = UserShell;
        var argv = new[] { shell, "-l", "-c", command };
        var env = BuildEnvironment();
        var cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : null;

        var allocs = new List<IntPtr>();
        try
        {
            IntPtr pPath = Utf8(shell, allocs);
            IntPtr pArgv = StringArray(argv, allocs);
            IntPtr pEnv = StringArray(env, allocs);
            IntPtr pCwd = cwd == null ? IntPtr.Zero : Utf8(cwd, allocs);

            var size = new WinSize { Col = (ushort)cols, Row = (ushort)rows };
            int master;
            int pid = ForkPty(&master, &size);

            if (pid == 0)
            {
                // Child: async-signal-safe calls only, then become the shell.
                // .NET ignores SIGPIPE, and an ignored signal survives exec.
                Signal(SIGPIPE, IntPtr.Zero);
                if (pCwd != IntPtr.Zero) Chdir(pCwd);
                Execve(pPath, pArgv, pEnv);
                Exit(127);
            }
            // shortcut: a GC requested in the microseconds forkpty runs could still park the child
            // at a poll; move the fork into a native helper if that hang is ever seen.
            if (pid < 0)
                throw new IOException("forkpty failed");

            _pid = pid;
            _master = master;
        }
        finally
        {
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }

        // Two streams over two descriptors: FileStream is not safe for a read and a write
        // running at once on different threads.
        _reader = new FileStream(new SafeFileHandle((IntPtr)_master, ownsHandle: true), FileAccess.Read, 1, false);
        _writer = new FileStream(new SafeFileHandle((IntPtr)Dup(_master), ownsHandle: true), FileAccess.Write, 1, false);

        _cts = new CancellationTokenSource();
        var reader = new Thread(() => ReadLoop(_cts.Token)) { IsBackground = true, Name = "pty-read" };
        reader.Start();
        var waiter = new Thread(WaitForExit) { IsBackground = true, Name = "pty-wait" };
        waiter.Start();
    }

    private unsafe static int ForkPty(int* master, WinSize* size)
    {
        // glibc before 2.34 keeps forkpty in libutil; macOS and newer glibc have it in libc.
        try { return ForkPtyLibc(master, IntPtr.Zero, IntPtr.Zero, size); }
        catch (EntryPointNotFoundException) { return ForkPtyLibutil(master, IntPtr.Zero, IntPtr.Zero, size); }
    }

    /// <summary>
    /// This process's environment with what a terminal app owes its children. A Finder launch
    /// has no LANG, and without a UTF-8 locale the CLIs draw their box characters as '?'.
    /// </summary>
    private static string[] BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        if (!env.ContainsKey("LANG") && !env.ContainsKey("LC_ALL")) env["LANG"] = "en_US.UTF-8";

        var list = new string[env.Count];
        int i = 0;
        foreach (var pair in env) list[i++] = pair.Key + "=" + pair.Value;
        return list;
    }

    private static IntPtr Utf8(string s, List<IntPtr> allocs)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);
        allocs.Add(p);
        return p;
    }

    private static IntPtr StringArray(string[] items, List<IntPtr> allocs)
    {
        var array = Marshal.AllocHGlobal(IntPtr.Size * (items.Length + 1));
        allocs.Add(array);
        for (int i = 0; i < items.Length; i++)
            Marshal.WriteIntPtr(array, i * IntPtr.Size, Utf8(items[i], allocs));
        Marshal.WriteIntPtr(array, items.Length * IntPtr.Size, IntPtr.Zero);
        return array;
    }

    private void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A read on the master fails with EIO once the last slave holder has exited.
                int read = _reader!.Read(buf, 0, buf.Length);
                if (read <= 0) break;
                var data = new byte[read];
                Buffer.BlockCopy(buf, 0, data, 0, read);
                OutputReceived?.Invoke(data);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private unsafe void WaitForExit()
    {
        int status;
        while (WaitPid(_pid, &status, 0) < 0 && Marshal.GetLastPInvokeError() == EINTR) { }
        _exited.Set();
        ProcessExited?.Invoke();
    }

    public void WriteInput(byte[] data)
    {
        if (_writer == null || data.Length == 0) return;

        const int chunkSize = 512;
        if (data.Length <= chunkSize)
        {
            try { _writer.Write(data, 0, data.Length); _writer.Flush(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return;
        }

        // Same pacing as the ConPTY side: a big paste in one write overruns the tty's input queue.
        var writer = _writer;
        Task.Run(async () =>
        {
            try
            {
                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    writer.Write(data, offset, Math.Min(chunkSize, data.Length - offset));
                    writer.Flush();
                    await Task.Delay(5);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public void WriteInput(string text) => WriteInput(Encoding.UTF8.GetBytes(text));

    public unsafe void Resize(int cols, int rows)
    {
        if (_master < 0 || _disposed) return;
        var size = new WinSize { Col = (ushort)cols, Row = (ushort)rows };
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            IoctlApple(_master, TIOCSWINSZ, 0, 0, 0, 0, 0, 0, &size);
        else
            Ioctl(_master, TIOCSWINSZ, &size);
    }

    public bool IsRunning => _pid > 0 && !_disposed && !_exited.IsSet;

    public bool WaitForExitTimeout(int milliseconds) => _pid <= 0 || _exited.Wait(milliseconds);

    public void Kill()
    {
        // forkpty made the child a session leader, so its pid is also the process group:
        // the shell and anything it started go together, as TerminateProcess's tree would not.
        if (_pid > 0 && !_exited.IsSet)
        {
            KillProcess(-_pid, SIGHUP);
            KillProcess(-_pid, SIGKILL);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        Kill();
        _writer?.Dispose();
        _reader?.Dispose();
        _cts?.Dispose();
    }
}
