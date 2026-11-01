using System;
using System.Runtime.InteropServices;

namespace Snipyard.Services;

/// <summary>What kind of event a notification is reporting, used to pick icon/sound.</summary>
public enum NotifyKind
{
    TaskComplete,
    PermissionWaiting,
    Error,
}

/// <summary>
/// Fires a Windows tray balloon and/or a system sound when a background session needs
/// attention. Uses only shell32/user32/winmm P/Invokes so no extra NuGet dependency or
/// WinRT/Windows-only TFM is needed. All failures are swallowed — a missed notification
/// must never take the app down.
/// </summary>
public class NotificationService : IDisposable
{
    private const string ClassName = "SnipyardNotifyWnd";

    public bool EnableToast { get; set; } = true;
    public bool EnableSound { get; set; } = true;

    private readonly object _lock = new();
    private bool _initialized;
    private bool _iconAdded;
    private IntPtr _hwnd;
    private IntPtr _hIcon;

    // Kept as a field so the delegate isn't garbage-collected while the native window
    // class still holds an unmanaged pointer to it.
    private static readonly WndProc s_wndProc = (hWnd, msg, wParam, lParam) => DefWindowProcW(hWnd, msg, wParam, lParam);

    public void Notify(NotifyKind kind, string title, string message)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                MacNotify(kind, title, message);
                return;
            }

            if (EnableToast)
            {
                ShowToast(kind, title, message);
            }

            if (EnableSound)
            {
                PlayNotifySound(kind);
            }
        }
        catch
        {
            // Notification failures must never surface to the caller.
        }
    }

    /// <summary>
    /// macOS: Notification Center through osascript, and the system sounds through afplay.
    /// Both are on every Mac, so this needs no entitlement or extra package either.
    /// </summary>
    private void MacNotify(NotifyKind kind, string title, string message)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (EnableToast)
        {
            var script = $"display notification {AppleString(message)} with title {AppleString(title)}";
            Run("osascript", "-e", script);
        }
        if (EnableSound)
        {
            var sound = kind switch
            {
                NotifyKind.PermissionWaiting => "Funk",
                NotifyKind.Error => "Basso",
                _ => "Glass",
            };
            Run("afplay", $"/System/Library/Sounds/{sound}.aiff");
        }
    }

    private static string AppleString(string s)
        => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(psi)?.Dispose();
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_lock)
        {
            try
            {
                if (_iconAdded)
                {
                    var nid = NewNotifyIconData();
                    Shell_NotifyIconW(NIM_DELETE, ref nid);
                    _iconAdded = false;
                }

                if (_hwnd != IntPtr.Zero)
                {
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }

                UnregisterClassW(ClassName, GetModuleHandleW(null));
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    // ── Toast (tray balloon) ──

    private void ShowToast(NotifyKind kind, string title, string message)
    {
        EnsureInitialized();

        lock (_lock)
        {
            if (!_iconAdded) return;

            var nid = NewNotifyIconData();
            nid.uFlags = NIF_INFO;
            nid.szInfo = Truncate(message, 255);
            nid.szInfoTitle = Truncate(title, 63);
            nid.dwInfoFlags = InfoFlagsFor(kind);

            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }
    }

    /// <summary>Registers a message-only window and adds the tray icon on first use.</summary>
    private void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var hInstance = GetModuleHandleW(null);

                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = s_wndProc,
                    hInstance = hInstance,
                    lpszClassName = ClassName,
                };
                RegisterClassExW(ref wc);

                _hwnd = CreateWindowExW(0, ClassName, null, 0, 0, 0, 0, 0,
                    HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) return;

                _hIcon = LoadIconW(IntPtr.Zero, IdiApplication);

                var nid = NewNotifyIconData();
                nid.uFlags = NIF_ICON | NIF_TIP;
                nid.hIcon = _hIcon;
                nid.szTip = "Snipyard";

                _iconAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
            }
            catch
            {
                // Toasts stay disabled for this run; sound notifications still work.
            }
        }
    }

    private NOTIFYICONDATAW NewNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private static uint InfoFlagsFor(NotifyKind kind) => kind switch
    {
        NotifyKind.PermissionWaiting => NIIF_WARNING,
        NotifyKind.Error => NIIF_ERROR,
        _ => NIIF_INFO,
    };

    private static string Truncate(string? value, int maxLength)
    {
        value ??= "";
        return value.Length > maxLength ? value.Substring(0, maxLength) : value;
    }

    // ── Sound ──

    private static void PlayNotifySound(NotifyKind kind)
    {
        try
        {
            var alias = kind switch
            {
                NotifyKind.PermissionWaiting => "SystemExclamation",
                NotifyKind.Error => "SystemHand",
                _ => "SystemAsterisk",
            };

            var played = PlaySoundW(alias, IntPtr.Zero, SND_ALIAS | SND_ASYNC);
            if (!played)
            {
                MessageBeep(BeepTypeFor(kind));
            }
        }
        catch
        {
            try { MessageBeep(BeepTypeFor(kind)); } catch { }
        }
    }

    private static uint BeepTypeFor(NotifyKind kind) => kind switch
    {
        NotifyKind.PermissionWaiting => MB_ICONEXCLAMATION,
        NotifyKind.Error => MB_ICONHAND,
        _ => MB_ICONASTERISK,
    };

    // ── Win32 interop ──

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiApplication = new(32512);

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_ERROR = 0x00000003;

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_ALIAS = 0x00010000;

    private const uint MB_ICONHAND = 0x00000010;
    private const uint MB_ICONEXCLAMATION = 0x00000030;
    private const uint MB_ICONASTERISK = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);
}
