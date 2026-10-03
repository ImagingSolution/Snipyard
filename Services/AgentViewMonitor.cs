using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Snipyard.Services;

/// <summary>One background session, as agent view lists it.</summary>
public sealed record BackgroundAgent
{
    /// <summary>Short id the CLI prints and <c>claude stop</c> / <c>claude rm</c> take.</summary>
    public string Id { get; init; } = "";

    /// <summary>Name the CLI gave the session, or the opening prompt when it has none yet.</summary>
    public string Name { get; init; } = "";

    /// <summary>The one-line summary agent view puts beside the name.</summary>
    public string? Detail { get; init; }

    /// <summary>The prompt the session was dispatched with.</summary>
    public string? Intent { get; init; }

    /// <summary><c>working</c> or <c>blocked</c>; nothing else is listed.</summary>
    public string State { get; init; } = "";

    /// <summary>What the session is waiting on, when it is blocked.</summary>
    public string? WaitingFor { get; init; }

    /// <summary>Folder the session runs in - what ties it to a window.</summary>
    public string? Cwd { get; init; }

    public DateTime Started { get; init; }

    /// <summary>
    /// Whether the supervisor still lists a process for this session. A session whose process
    /// has exited is still real - its conversation is kept and attaching wakes it - so it is
    /// listed either way, and this only changes how the row is drawn.
    /// </summary>
    public bool ProcessAlive { get; init; }
}

/// <summary>
/// The background sessions <c>claude agents</c> lists, read the way the rest of the app reads the
/// CLI's state: straight off the files, never by running the CLI. Agent view's own data is one
/// <c>state.json</c> per session under <c>~/.claude/jobs</c>, plus the supervisor's
/// <c>roster.json</c> naming the ones that still have a process.
///
/// <c>claude agents --json</c> is the documented way to read this, and it is the wrong one here:
/// the windows panel redraws every 700ms, and starting a Node CLI on that cadence would cost more
/// than everything else the app does put together. Reading the files is what the CLI itself does.
///
/// Every call is served from one snapshot taken at most <see cref="Interval"/> apart, so a panel
/// with ten windows in it costs one scan rather than ten.
/// </summary>
public static class AgentViewMonitor
{
    /// <summary>The states agent view calls live. Everything else is history and is not listed.</summary>
    private static readonly HashSet<string> Listed =
        new(StringComparer.OrdinalIgnoreCase) { "working", "blocked" };

    /// <summary>
    /// How long a snapshot is reused. Comfortably shorter than the 700ms redraw, so a state
    /// change is never more than one frame late, and long enough that the redraw itself - which
    /// asks once per window - only ever reads the files once.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How stale a session may be before a <c>working</c> state stops being believed. A
    /// supervisor that is killed mid-run never gets to write a terminal state, leaving a session
    /// that claims to be working with no process behind it - there is one such leftover on this
    /// machine from September 5th. Only applied to sessions the roster has no process for, so a
    /// genuinely long-running session is never written off.
    /// </summary>
    private static readonly TimeSpan Stale = TimeSpan.FromHours(24);

    /// <summary>What one <c>state.json</c> said, and the stamp that says it can be reused.</summary>
    private sealed class Cached
    {
        public long Length;
        public DateTime WriteUtc;

        /// <summary>Null when the file parsed but the session is not one to list.</summary>
        public BackgroundAgent? Agent;
    }

    private static readonly Dictionary<string, Cached> Jobs = new(StringComparer.OrdinalIgnoreCase);

    private static List<BackgroundAgent> _snapshot = new();
    private static DateTime _taken = DateTime.MinValue;

    private static HashSet<string> _alive = new(StringComparer.OrdinalIgnoreCase);
    private static long _rosterLength = -1;
    private static DateTime _rosterWriteUtc;

    /// <summary>
    /// The live background sessions running under <paramref name="projectFolder"/>, oldest first.
    /// An empty list is the normal answer. A window with no project folder gets nothing: there is
    /// no folder to match sessions against, and matching them all would put every session under
    /// every such window.
    /// </summary>
    public static List<BackgroundAgent> ReadActive(string? projectFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder)) return new List<BackgroundAgent>();

        var wanted = Normalize(projectFolder);
        var matches = new List<BackgroundAgent>();
        foreach (var agent in Snapshot())
            if (Normalize(agent.Cwd) == wanted)
                matches.Add(agent);

        return matches;
    }

    /// <summary>
    /// Drops the snapshot so the next read goes back to the files. Called after this app has
    /// stopped or deleted a session, where waiting out the interval would leave the row up.
    /// </summary>
    public static void Invalidate()
    {
        _taken = DateTime.MinValue;
        _rosterLength = -1;
        Jobs.Clear();
    }

    /// <summary>
    /// Whether a session directory still exists under <c>~/.claude/jobs</c>. Used to retire the
    /// display names this app keeps for sessions that have since been deleted.
    /// </summary>
    public static bool JobExists(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { return Directory.Exists(Path.Combine(JobsRoot, id)); }
        catch { return false; }
    }

    private static string JobsRoot =>
        Path.Combine(ClaudeHome, "jobs");

    private static string RosterFile =>
        Path.Combine(ClaudeHome, "daemon", "roster.json");

    // A debug build can be pointed at a made-up ~/.claude to show sample sessions: faking them
    // in the real jobs folder would put them in front of the CLI's own supervisor
    private static string ClaudeHome =>
#if DEBUG
        Environment.GetEnvironmentVariable("SNIPYARD_TEST_CLAUDE_HOME") is { Length: > 0 } fake ? fake :
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static List<BackgroundAgent> Snapshot()
    {
        var now = DateTime.Now;
        if (now - _taken < Interval) return _snapshot;
        _taken = now;

        RefreshRoster();

        var agents = new List<BackgroundAgent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in SafeDirectories(JobsRoot))
        {
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id)) continue;
            seen.Add(id);

            var agent = Read(dir, id, now);
            if (agent != null) agents.Add(agent);
        }

        // A session that was deleted leaves its cache entry behind, which would otherwise hold
        // its parsed state for the life of the process.
        if (Jobs.Count != seen.Count)
        {
            var gone = new List<string>();
            foreach (var key in Jobs.Keys)
                if (!seen.Contains(key)) gone.Add(key);
            foreach (var key in gone) Jobs.Remove(key);
        }

        agents.Sort((a, b) => a.Started.CompareTo(b.Started));
        _snapshot = agents;
        return _snapshot;
    }

    /// <summary>
    /// One session's state, from the cache when its file has not moved. The liveness flag is
    /// applied after the cache, since the roster changes without the state file changing.
    /// </summary>
    private static BackgroundAgent? Read(string dir, string id, DateTime now)
    {
        var path = Path.Combine(dir, "state.json");

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch { return null; }

        if (!Jobs.TryGetValue(id, out var cached))
        {
            cached = new Cached { Length = -1 };
            Jobs[id] = cached;
        }

        if (cached.Length != info.Length || cached.WriteUtc != info.LastWriteTimeUtc)
        {
            cached.Length = info.Length;
            cached.WriteUtc = info.LastWriteTimeUtc;
            cached.Agent = Parse(path, id);
        }

        if (cached.Agent == null) return null;

        bool alive = _alive.Contains(id);

        // A state the supervisor never got to close out. Believed while the process is there,
        // and while it is recent enough that the session could still be one nobody has looked
        // at yet.
        if (!alive && now - cached.WriteUtc.ToLocalTime() > Stale) return null;

        return cached.Agent with { ProcessAlive = alive };
    }

    private static BackgroundAgent? Parse(string path, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(path));
            var root = doc.RootElement;

            var state = Text(root, "state") ?? "";
            if (!Listed.Contains(state)) return null;

            var intent = Text(root, "intent");
            var name = Text(root, "name");
            if (string.IsNullOrWhiteSpace(name)) name = FirstLine(intent);
            if (string.IsNullOrWhiteSpace(name)) name = id;

            return new BackgroundAgent
            {
                Id = id,
                Name = name!,
                Detail = FirstLine(Text(root, "detail")),
                Intent = intent,
                State = state,
                WaitingFor = Text(root, "waitingFor"),
                Cwd = Text(root, "cwd"),
                Started = Moment(Text(root, "createdAt")) ?? File.GetCreationTime(path),
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads a file the CLI may be writing. Opened with the widest sharing rather than
    /// File.ReadAllText, which refuses while another process holds the file for writing.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The sessions the supervisor still has a process for.</summary>
    private static void RefreshRoster()
    {
        try
        {
            var info = new FileInfo(RosterFile);
            if (!info.Exists)
            {
                _alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _rosterLength = -1;
                return;
            }
            if (_rosterLength == info.Length && _rosterWriteUtc == info.LastWriteTimeUtc) return;

            _rosterLength = info.Length;
            _rosterWriteUtc = info.LastWriteTimeUtc;

            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(ReadShared(RosterFile));
            if (doc.RootElement.TryGetProperty("workers", out var workers)
                && workers.ValueKind == JsonValueKind.Object)
                foreach (var worker in workers.EnumerateObject())
                    alive.Add(worker.Name);

            _alive = alive;
        }
        catch
        {
            // A half-written roster leaves the previous answer standing rather than reporting
            // every session as dead for one frame.
            _rosterLength = -1;
        }
    }

    /// <summary>
    /// A folder path in the one form two of them can be compared in: no trailing separator, and
    /// case-insensitive, since Windows does not distinguish them and the CLI and this app can
    /// spell the same folder differently.
    /// </summary>
    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try { return Path.GetFullPath(trimmed).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant(); }
        catch { return trimmed.ToUpperInvariant(); }
    }

    /// <summary>The opening line of a prompt or summary, short enough for one row.</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var line = text.Trim();
        int cut = line.IndexOfAny(new[] { '\r', '\n' });
        if (cut >= 0) line = line[..cut].TrimEnd();

        const int limit = 80;
        return line.Length > limit ? line[..limit].TrimEnd() + "…" : line;
    }

    private static DateTime? Moment(string? iso) =>
        DateTimeOffset.TryParse(
            iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.LocalDateTime
            : null;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }
}
