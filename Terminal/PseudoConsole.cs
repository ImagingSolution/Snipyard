using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Snipyard.Terminal;

/// <summary>A child process attached to a pseudo terminal: ConPTY on Windows, forkpty elsewhere.</summary>
public interface IPseudoConsole : IDisposable
{
    event Action<byte[]>? OutputReceived;
    event Action? ProcessExited;
    int ProcessId { get; }
    bool IsRunning { get; }
    void Start(string command, string? workingDirectory, int cols, int rows);
    void WriteInput(byte[] data);
    void WriteInput(string text);
    void Resize(int cols, int rows);
    bool WaitForExitTimeout(int milliseconds);
    void Kill();

    static IPseudoConsole Create() => OperatingSystem.IsWindows()
        ? new PseudoConsole()
        : new UnixPseudoConsole();
}

public class PseudoConsole : IPseudoConsole
{
    private IntPtr _hPC;
    private SafeFileHandle? _pipeIn;   // PTY reads from this (we write to this)
    private SafeFileHandle? _pipeOut;  // PTY writes to this (we read from this)
    private SafeFileHandle? _processHandle;
    private SafeFileHandle? _threadHandle;
    private FileStream? _writer;
    private FileStream? _reader;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event Action<byte[]>? OutputReceived;
    public event Action? ProcessExited;
    public int ProcessId { get; private set; }

    // P/Invoke declarations
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle hProcess, uint uExitCode);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
    private const uint WAIT_OBJECT_0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    public void Start(string command, string? workingDirectory, int cols, int rows)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        // Create pipes for PTY
        // Pipe for PTY input: we write → PTY reads
        if (!CreatePipe(out var ptyInputRead, out var ptyInputWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Pipe for PTY output: PTY writes → we read
        if (!CreatePipe(out var ptyOutputRead, out var ptyOutputWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        // Create pseudo console
        var size = new COORD { X = (short)cols, Y = (short)rows };
        int hr = CreatePseudoConsole(size, ptyInputRead, ptyOutputWrite, 0, out _hPC);
        if (hr != 0)
            throw new Win32Exception(hr, $"CreatePseudoConsole failed: 0x{hr:X8}");

        // Close handles that the PTY now owns
        ptyInputRead.Dispose();
        ptyOutputWrite.Dispose();

        _pipeIn = ptyInputWrite;
        _pipeOut = ptyOutputRead;

        // Create process with pseudo console
        IntPtr attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var si = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
            lpAttributeList = attrList
        };

        if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            IntPtr.Zero, workingDirectory, ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);

        _processHandle = new SafeFileHandle(pi.hProcess, true);
        _threadHandle = new SafeFileHandle(pi.hThread, true);
        ProcessId = pi.dwProcessId;

        _writer = new FileStream(_pipeIn, FileAccess.Write);
        _reader = new FileStream(_pipeOut, FileAccess.Read);

        _cts = new CancellationTokenSource();
        Task.Run(() => ReadLoop(_cts.Token));
        Task.Run(() => WaitForExit());
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _reader!.ReadAsync(buf, 0, buf.Length, ct);
                if (read <= 0) break;
                var data = new byte[read];
                Buffer.BlockCopy(buf, 0, data, 0, read);
                OutputReceived?.Invoke(data);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitForExit()
    {
        if (_processHandle != null && !_processHandle.IsInvalid)
        {
            WaitForSingleObject(_processHandle, 0xFFFFFFFF);
            ProcessExited?.Invoke();
        }
    }

    public void WriteInput(byte[] data)
    {
        if (_writer == null || data.Length == 0) return;

        const int chunkSize = 512;
        if (data.Length <= chunkSize)
        {
            try
            {
                _writer.Write(data, 0, data.Length);
                _writer.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            return;
        }

        // Large data: write in chunks on a background thread to avoid blocking UI
        var writer = _writer;
        Task.Run(async () =>
        {
            try
            {
                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - offset);
                    writer.Write(data, offset, len);
                    writer.Flush();
                    await Task.Delay(5);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public void WriteInput(string text)
    {
        WriteInput(System.Text.Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int cols, int rows)
    {
        if (_hPC != IntPtr.Zero)
        {
            ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
        }
    }

    public bool IsRunning =>
        _processHandle != null && !_processHandle.IsInvalid && !_disposed
        && WaitForSingleObject(_processHandle, 0) != 0; // WAIT_OBJECT_0 = 0

    public bool WaitForExitTimeout(int milliseconds)
    {
        if (_processHandle == null || _processHandle.IsInvalid) return true;
        return WaitForSingleObject(_processHandle, (uint)milliseconds) == 0;
    }

    public void Kill()
    {
        if (_processHandle != null && !_processHandle.IsInvalid)
        {
            try { TerminateProcess(_processHandle, 1); } catch { }
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
        _pipeIn?.Dispose();
        _pipeOut?.Dispose();
        _processHandle?.Dispose();
        _threadHandle?.Dispose();

        if (_hPC != IntPtr.Zero)
        {
            ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        _cts?.Dispose();
    }
}
