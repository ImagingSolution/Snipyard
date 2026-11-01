using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Snipyard.Services;

/// <summary>
/// Sends files and folders to the Windows recycle bin.
///
/// Anything Snipyard deletes on the user's behalf goes through here rather than
/// <see cref="File.Delete(string)"/>: a transcript is the only copy of a conversation, and a
/// confirmation dialog the user clicked through by habit should still be recoverable.
/// </summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    /// <summary>
    /// Recycles every path that exists. Returns null on success, else the reason.
    /// Paths that are already gone are not an error - the point is that they end up absent.
    /// </summary>
    public static string? Send(params string[] paths)
    {
        try
        {
            // The API takes a double-null-terminated list, so the whole set goes in one call
            // and lands in the bin as one undoable operation.
            var present = Array.FindAll(paths, p =>
                !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)));
            if (present.Length == 0) return null;
            if (!OperatingSystem.IsWindows()) return MoveToTrash(present);

            var op = new ShFileOpStruct
            {
                wFunc = FO_DELETE,
                pFrom = string.Join('\0', present) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };

            int result = SHFileOperation(ref op);
            if (result != 0) return "SHFileOperation 0x" + result.ToString("X");
            return op.fAnyOperationsAborted ? "aborted" : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// macOS keeps the user's Trash at ~/.Trash, and a move there is what Finder's own delete
    /// does minus the "Put Back" record. Linux desktops use the freedesktop folder.
    /// </summary>
    private static string? MoveToTrash(string[] paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trash = OperatingSystem.IsMacOS()
            ? Path.Combine(home, ".Trash")
            : Path.Combine(home, ".local", "share", "Trash", "files");
        Directory.CreateDirectory(trash);

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd('/'));
            var target = Path.Combine(trash, name);
            // Same rule as Finder: a name already in the Trash gets a suffix rather than replaced.
            if (File.Exists(target) || Directory.Exists(target))
                target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {DateTime.Now:HH-mm-ss-fff}{Path.GetExtension(name)}");

            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
        }
        return null;
    }
}
