using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Snipyard.Services;

/// <summary>
/// Where Claude Code keeps a project's own state on disk, and the one name mangling that gets you
/// there. The CLI does not store a project under its real path: it flattens the path into a single
/// directory name under <c>~/.claude/projects</c>, replacing every run of non-ASCII-alphanumeric
/// characters with a single dash. A project at <c>D:\a\b</c> becomes <c>D--a-b</c>.
///
/// That mangling is reproduced in four other places already (<see cref="SessionService"/> is the
/// original, with <c>SessionMessageReader</c>, <c>CostAnalytics</c> and <c>DiagramCache</c> each
/// carrying a private copy commented "must match SessionService exactly"). This class exists so a
/// fifth copy is not written. It does not replace the four - rewriting working code to route
/// through here would risk them for nothing - it is simply where new code goes.
/// </summary>
public static class ClaudeProjectPaths
{
    /// <summary><c>~/.claude/projects</c>, whether or not it exists.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// The directory name Claude Code itself writes: every character that is not ASCII
    /// alphanumeric becomes one dash, one for one. A folder with ten Japanese characters in its
    /// path leaves ten dashes behind, which is why this is not the same thing as
    /// <see cref="NormalizeFolderName"/>.
    /// </summary>
    public static string LiteralFolderName(string path)
    {
        path = (path ?? "").Replace('/', '\\').TrimEnd('\\');

        var sb = new StringBuilder(path.Length);
        foreach (char c in path)
            sb.Append(char.IsLetterOrDigit(c) && c <= 127 ? c : '-');
        // "/Users/me/app" is written "-Users-me-app": the leading dash is the root, not padding.
        return OperatingSystem.IsWindows() ? sb.ToString().Trim('-') : sb.ToString().TrimEnd('-');
    }

    /// <summary>
    /// A comparison key, not a directory name: runs of separators collapse to a single dash, so
    /// two spellings of the same path match. Must stay byte-identical to
    /// <c>SessionService.NormalizeFolderName</c>, which applies it to both sides of every
    /// comparison for exactly this reason.
    /// </summary>
    public static string NormalizeFolderName(string path)
    {
        path = (path ?? "").Replace('/', '\\').TrimEnd('\\');

        var sb = new StringBuilder(path.Length);
        bool lastWasDash = false;
        foreach (char c in path)
        {
            if (char.IsLetterOrDigit(c) && c <= 127)
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else
            {
                if (!lastWasDash)
                    sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Claude Code's directory for one project folder. The literal name is tried first; failing
    /// that, every existing directory is compared on the collapsed key, which is how
    /// <see cref="SessionService"/> finds a project whose path is spelled differently from the one
    /// the CLI recorded. Returns the literal name when nothing matches - the caller wants a path
    /// to report as empty, not a null.
    /// </summary>
    public static string ProjectDir(string projectFolder)
    {
        var literal = Path.Combine(Root, LiteralFolderName(projectFolder));
        if (Directory.Exists(literal)) return literal;

        var key = NormalizeFolderName(projectFolder);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (NormalizeFolderName(Path.GetFileName(dir)).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return literal;
    }

    /// <summary>Where that project's persistent memory notes live. May not exist.</summary>
    public static string MemoryDir(string projectFolder) =>
        Path.Combine(ProjectDir(projectFolder), "memory");

    /// <summary>
    /// Every project directory Claude Code has created, newest first by write time. Empty when the
    /// CLI has never run, or when the directory cannot be read.
    /// </summary>
    public static List<string> AllProjectDirs()
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(Root)) return result;
            var dirs = new List<DirectoryInfo>();
            foreach (var d in Directory.EnumerateDirectories(Root))
                dirs.Add(new DirectoryInfo(d));
            dirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (var d in dirs) result.Add(d.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>
    /// The real folder a project directory stands for, recovered from a session transcript. The
    /// mangled directory name cannot be reversed - every run of separators collapses to one dash -
    /// so the original path is read out of the <c>cwd</c> the CLI records on the first line of a
    /// session. Falls back to the directory name when there is no transcript to read.
    /// </summary>
    public static string DisplayPath(string projectDir)
    {
        var name = Path.GetFileName(projectDir.TrimEnd('\\', '/'));
        if (_displayPaths.TryGetValue(projectDir, out var cached)) return cached;

        var result = name;
        try
        {
            FileInfo? newest = null;
            foreach (var f in Directory.EnumerateFiles(projectDir, "*.jsonl"))
            {
                var info = new FileInfo(f);
                if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = info;
            }

            if (newest != null)
            {
                using var reader = new StreamReader(newest.FullName);
                var line = reader.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, "\"cwd\"\\s*:\\s*\"(.*?)(?<!\\\\)\"");
                    if (m.Success)
                    {
                        var cwd = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (cwd.Length > 0) result = cwd;
                    }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (ReferenceEquals(result, name)) result = TrailingSegment(name);

        _displayPaths[projectDir] = result;
        return result;
    }

    /// <summary>
    /// The real path could not be recovered, so <paramref name="mangled"/> is the literal
    /// dash-for-every-character directory name - mostly noise (drive letter, home directory,
    /// OneDrive's own dashes) with the one useful part, the leaf folder name, at the very end.
    /// Trims everything up to the last run of dashes and keeps that tail; falls back to the full
    /// mangled name if the tail is empty (a leaf name that was itself all non-ASCII).
    /// </summary>
    private static string TrailingSegment(string mangled)
    {
        int i = mangled.Length;
        while (i > 0 && char.IsLetterOrDigit(mangled[i - 1])) i--;
        var tail = mangled.Substring(i);
        return tail.Length > 0 ? tail : mangled;
    }

    /// <summary>Recovered paths, kept for the session: a transcript's first line never changes.</summary>
    private static readonly Dictionary<string, string> _displayPaths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every project directory that actually holds memory notes, newest first. This is what the
    /// memory panel offers to switch between - a project with no <c>memory/</c> has nothing to show.
    /// </summary>
    public static List<string> ProjectsWithMemory()
    {
        var result = new List<string>();
        foreach (var dir in AllProjectDirs())
        {
            var memory = Path.Combine(dir, "memory");
            try
            {
                if (!Directory.Exists(memory)) continue;
                using var e = Directory.EnumerateFiles(memory, "*.md").GetEnumerator();
                if (e.MoveNext()) result.Add(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}
