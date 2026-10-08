using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snipyard.Services;

/// <summary>One subagent the CLI spawned, as the windows panel shows it.</summary>
public sealed record SubagentRun
{
    /// <summary>The CLI's task id - the same string its completion notice names.</summary>
    public string Id { get; init; } = "";
    /// <summary>The short description the parent gave the task.</summary>
    public string Label { get; init; } = "";
    public string? AgentType { get; init; }
    public string? Model { get; init; }
    public DateTime Started { get; init; }
    /// <summary>Agents an agent spawned sit deeper than 1.</summary>
    public int Depth { get; init; }
}

/// <summary>
/// Tracks what a session still has running behind the prompt: the subagents it spawned, and the
/// commands it launched with <c>run_in_background</c>.
///
/// The CLI writes one <c>agent-&lt;id&gt;.meta.json</c> per task beside the transcript, which is
/// where the description, type and model come from. Finishing is not recorded there, and it
/// cannot be read from the tool result either - a backgrounded task is answered the moment it
/// launches - so the end is taken from the completion notice the CLI posts back into the parent
/// transcript. That transcript is only ever read forward from where the last read stopped.
///
/// A backgrounded command has no meta file at all, so both ends of it come from the transcript:
/// its launch is the <c>backgroundTaskId</c> the tool result carries, and its end is the same
/// <c>task-notification</c> that closes a subagent - the CLI gives both kinds the one id space.
/// </summary>
public static class SubagentMonitor
{
    private static readonly Regex TaskId =
        new(@"<task-id>([A-Za-z0-9_-]{1,64})</task-id>", RegexOptions.Compiled);

    /// <summary>
    /// Matches the id the CLI stamps on a tool result it answered by backgrounding:
    /// <c>"toolUseResult":{...,"backgroundTaskId":"b036nfy2h"}</c>.
    /// </summary>
    private static readonly Regex BackgroundTaskId =
        new(@"""backgroundTaskId""\s*:\s*""([A-Za-z0-9_-]{1,64})""", RegexOptions.Compiled);

    /// <summary>
    /// Matches the task a stop call names: <c>"name":"TaskStop","input":{"task_id":"b7j0a1pvp"}</c>,
    /// or <c>shell_id</c> on the older KillShell.
    /// </summary>
    private static readonly Regex StoppedTaskId =
        new(@"""(?:task_id|shell_id)""\s*:\s*""([A-Za-z0-9_-]{1,64})""", RegexOptions.Compiled);

    /// <summary>Matches the record's own ISO timestamp, which dates the launch above.</summary>
    private static readonly Regex RecordTimestamp =
        new(@"""timestamp""\s*:\s*""([^""]{10,40})""", RegexOptions.Compiled);

    /// <summary>Where a transcript's forward scan stopped, and what it had seen start and finish.</summary>
    private sealed class Progress
    {
        public long Offset;

        /// <summary>
        /// File length as of the last scan, so a poll over an unchanged transcript costs one
        /// metadata call instead of opening it. -1 until the first scan, which a real length
        /// can never be, so the first call always reads.
        /// </summary>
        public long LastLength = -1;

        public readonly HashSet<string> Finished = new(StringComparer.Ordinal);

        /// <summary>Backgrounded commands this transcript launched, by id, dated by the record that launched them.</summary>
        public readonly Dictionary<string, DateTime> Background = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Progress> Scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a subagent may write nothing before this stops calling it running.
    ///
    /// A session that is killed mid-task leaves its agents with no completion notice at all -
    /// two of the twenty-six on this machine - and without a cutoff those would be reported as
    /// running forever the next time the session was resumed. Long enough that a real agent
    /// sitting inside one slow tool call is not written off.
    ///
    /// The same cutoff caps a backgrounded command, where it does a second job: a dev server
    /// launched with <c>run_in_background</c> is meant to outlive the turn and never posts a
    /// completion notice, so without a cap it would hold every window it touched at "still
    /// working" for the rest of the session. Builds and test runs - the backgrounded commands a
    /// turn is actually waiting on - are long finished by then.
    /// </summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The subagents of <paramref name="transcriptPath"/> that have not reported back yet,
    /// oldest first. An empty list is the normal answer.
    /// </summary>
    public static List<SubagentRun> ReadRunning(string? transcriptPath)
    {
        var runs = new List<SubagentRun>();
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) return runs;

        var dir = SubagentDirectory(transcriptPath);
        if (dir == null || !Directory.Exists(dir)) return runs;

        var finished = Scan(transcriptPath).Finished;

        foreach (var meta in SafeFiles(dir, "agent-*.meta.json"))
        {
            var id = IdFromMetaPath(meta);
            if (id == null || finished.Contains(id)) continue;

            if (DateTime.Now - LastActivity(dir, id, meta) > Abandoned) continue;

            var run = ReadMeta(meta, id);
            if (run != null) runs.Add(run);
        }

        runs.Sort((a, b) => a.Started.CompareTo(b.Started));
        return runs;
    }

    /// <summary>
    /// Whether this session still has anything running behind the prompt - a subagent that has
    /// not reported back, or a backgrounded command that has not been notified as finished.
    ///
    /// This is what says a turn is really over. The CLI hands the prompt back the moment it has
    /// nothing left to say, which is not the same as having nothing left running: it answers a
    /// backgrounded tool call immediately and picks the result up later. The answer is a bare
    /// bool because that is all the indicator needs - <see cref="ReadRunning"/> is what builds
    /// the list the panel shows.
    ///
    /// Called for every window on every poll, so it is built to be cheap: the transcript scan
    /// is incremental and no metadata is parsed.
    /// </summary>
    public static bool AnyWorkPending(string? transcriptPath)
    {
        if (string.IsNullOrEmpty(transcriptPath) || !File.Exists(transcriptPath)) return false;

        var scan = Scan(transcriptPath);
        var now = DateTime.Now;

        foreach (var launched in scan.Background)
        {
            if (scan.Finished.Contains(launched.Key)) continue;
            if (now - launched.Value > Abandoned) continue;
            return true;
        }

        var dir = SubagentDirectory(transcriptPath);
        if (dir == null || !Directory.Exists(dir)) return false;

        foreach (var meta in SafeFiles(dir, "agent-*.meta.json"))
        {
            var id = IdFromMetaPath(meta);
            if (id == null || scan.Finished.Contains(id)) continue;
            if (now - LastActivity(dir, id, meta) > Abandoned) continue;
            return true;
        }
        return false;
    }

    /// <summary>Frees the scan state for a transcript nothing is watching any more.</summary>
    public static void Forget(string? transcriptPath)
    {
        if (transcriptPath != null) Scans.Remove(transcriptPath);
    }

    /// <summary>
    /// <c>.../projects/&lt;project&gt;/&lt;session&gt;.jsonl</c> keeps its subagents in
    /// <c>.../projects/&lt;project&gt;/&lt;session&gt;/subagents</c>.
    /// </summary>
    private static string? SubagentDirectory(string transcriptPath)
    {
        var parent = Path.GetDirectoryName(transcriptPath);
        var session = Path.GetFileNameWithoutExtension(transcriptPath);
        if (parent == null || session.Length == 0) return null;
        return Path.Combine(parent, session, "subagents");
    }

    private static string? IdFromMetaPath(string path)
    {
        var name = Path.GetFileName(path);
        const string prefix = "agent-";
        const string suffix = ".meta.json";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal)) return null;

        var id = name[prefix.Length..^suffix.Length];
        return id.Length == 0 ? null : id;
    }

    /// <summary>When the agent last wrote to its own transcript, or was handed the task.</summary>
    private static DateTime LastActivity(string dir, string id, string metaPath)
    {
        try
        {
            var transcript = Path.Combine(dir, "agent-" + id + ".jsonl");
            if (File.Exists(transcript)) return File.GetLastWriteTime(transcript);
            return File.GetLastWriteTime(metaPath);
        }
        catch { return DateTime.Now; }
    }

    private static SubagentRun? ReadMeta(string path, string id)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            // Creation time is when the task was handed out; the file is written once.
            var started = File.GetCreationTime(path);
            if (started == default) started = File.GetLastWriteTime(path);

            return new SubagentRun
            {
                Id = id,
                Label = Text(root, "description") ?? id,
                AgentType = Text(root, "agentType"),
                Model = Text(root, "model"),
                Started = started,
                Depth = root.TryGetProperty("spawnDepth", out var depth)
                        && depth.ValueKind == JsonValueKind.Number
                    ? depth.GetInt32()
                    : 1,
            };
        }
        catch { return null; }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// What the parent transcript has reported started and finished. Only the bytes appended
    /// since the last call are read; a transcript that shrank - a rewrite, or a different
    /// session at the same path - is read again from the start.
    /// </summary>
    private static Progress Scan(string transcriptPath)
    {
        if (!Scans.TryGetValue(transcriptPath, out var scan))
        {
            scan = new Progress();
            Scans[transcriptPath] = scan;
        }

        try
        {
            // Nothing appended since the last look, so there is nothing new to read. Checked
            // before opening the file because this now runs for every window on every poll,
            // not only while the windows panel is up.
            if (new FileInfo(transcriptPath).Length == scan.LastLength) return scan;

            using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length < scan.Offset)
            {
                scan.Offset = 0;
                scan.Finished.Clear();
                scan.Background.Clear();
            }
            stream.Position = scan.Offset;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // Cheap reject first: the notice is a fraction of a percent of the lines.
                if (line.Contains("task-notification", StringComparison.Ordinal))
                    foreach (Match match in TaskId.Matches(line))
                        scan.Finished.Add(match.Groups[1].Value);

                // A task stopped by hand never posts that notice, so the stop call is its end.
                // Without this the window stayed on "still working" until the cutoff below.
                if (line.Contains(@"""name"":""TaskStop""", StringComparison.Ordinal) ||
                    line.Contains(@"""name"":""KillShell""", StringComparison.Ordinal))
                    foreach (Match match in StoppedTaskId.Matches(line))
                        scan.Finished.Add(match.Groups[1].Value);

                if (line.Contains("backgroundTaskId", StringComparison.Ordinal))
                {
                    var launch = BackgroundTaskId.Match(line);
                    // Dated from the record rather than from now: the first pass over a resumed
                    // session reads its whole history at once, and stamping those launches with
                    // the wall clock would revive every command it ever backgrounded.
                    if (launch.Success)
                        scan.Background[launch.Groups[1].Value] = RecordTime(line);
                }
            }

            // ReadLine can stop mid-line if the CLI is still writing; resuming from the
            // stream's own position would then split a record. Committing the whole length
            // is wrong for the same reason, so the tail is re-read next time instead.
            scan.Offset = LastCompleteLineEnd(transcriptPath, stream.Length);
            scan.LastLength = stream.Length;
        }
        catch { }

        return scan;
    }

    /// <summary>
    /// The local time a transcript record was written. A record with no readable timestamp is
    /// dated now - the launch is real either way, and the abandonment cutoff will retire it.
    /// </summary>
    private static DateTime RecordTime(string line)
    {
        var m = RecordTimestamp.Match(line);
        // Read as an offset rather than a DateTime: the CLI writes UTC with a trailing Z, and
        // DateTimeOffset is the parse that keeps that zone instead of guessing at one.
        return m.Success && DateTimeOffset.TryParse(
                   m.Groups[1].Value,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind,
                   out var parsed)
            ? parsed.LocalDateTime
            : DateTime.Now;
    }

    /// <summary>
    /// The offset just past the file's last newline, so a record the CLI is still writing is
    /// left for the next pass rather than being consumed in halves.
    /// </summary>
    private static long LastCompleteLineEnd(string path, long length)
    {
        if (length == 0) return 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var window = (int)Math.Min(8192, length);
            stream.Position = length - window;

            var buffer = new byte[window];
            int read = stream.Read(buffer, 0, window);
            for (int i = read - 1; i >= 0; i--)
                if (buffer[i] == (byte)'\n') return length - window + i + 1;

            return length - window;
        }
        catch { return 0; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }
}
