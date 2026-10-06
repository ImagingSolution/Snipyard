using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>One GitHub Actions workflow run, as much of it as the panel shows.</summary>
public sealed record WorkflowRunInfo(
    long Id,
    string Name,
    string Status,
    string Conclusion,
    string CreatedAt,
    string Url,
    string HeadSha,
    string Event)
{
    /// <summary>Still queued or running, so worth polling again.</summary>
    public bool IsActive => !string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase)
        && Conclusion.ToLowerInvariant() is "failure" or "timed_out" or "startup_failure";
}

/// <summary>One open GitHub issue.</summary>
public sealed record IssueInfo(
    int Number,
    string Title,
    string Author,
    IReadOnlyList<string> Labels,
    string Url,
    string UpdatedAt);

/// <summary>A `gh` invocation ready to run: its arguments and what goes to stdin (null for none).</summary>
public sealed record GhCommand(string[] Args, string? Stdin);

/// <summary>
/// The Actions / Issues / Release half of the GitHub integration, driven through `gh` like
/// <see cref="GitHubCli"/>. Writes are split into a pure Build... function (the exact argv) and
/// a thin runner, so the argument shapes can be checked without touching GitHub.
/// </summary>
public static class GitHubWorkflowCli
{
    private const int TimeoutMs = 45_000;
    private const int UploadTimeoutMs = 600_000;
    private const int RunLimit = 10;
    private const int IssueLimit = 30;

    /// <summary>How many lines of a failed-job log the viewer keeps (from the end).</summary>
    public const int MaxLogLines = 400;

    // ── Actions ────────────────────────────────────────────────────────

    public static string[] BuildRunListArgs(string branch) => new[]
    {
        "run", "list", "--branch", branch, "--limit", RunLimit.ToString(),
        "--json", "databaseId,name,status,conclusion,createdAt,url,headSha,event",
    };

    public static Task<List<WorkflowRunInfo>> ListRunsAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(branch)) return new List<WorkflowRunInfo>();
            var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null, BuildRunListArgs(branch));
            return result.Ok ? ParseRuns(result.StdOut) : new List<WorkflowRunInfo>();
        });
    }

    public static List<WorkflowRunInfo> ParseRuns(string json)
    {
        var list = new List<WorkflowRunInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                list.Add(new WorkflowRunInfo(
                    r.TryGetProperty("databaseId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                    Str(r, "name"), Str(r, "status"), Str(r, "conclusion"), Str(r, "createdAt"),
                    Str(r, "url"), Str(r, "headSha"), Str(r, "event")));
            }
        }
        catch (JsonException)
        {
            return new List<WorkflowRunInfo>();
        }
        return list;
    }

    public static string[] BuildLogFailedArgs(long runId) =>
        new[] { "run", "view", runId.ToString(), "--log-failed" };

    /// <summary>The failed steps' log for one run; use <see cref="TailLines"/> before showing it.</summary>
    public static Task<GitResult> GetFailedLogAsync(string repoRoot, long runId)
    {
        return Task.Run(() => ProcessRunner.Run("gh", repoRoot, null, TimeoutMs * 2, null, BuildLogFailedArgs(runId)));
    }

    /// <summary>Keeps the last <paramref name="max"/> lines, noting how many were cut from the top.</summary>
    public static string TailLines(string text, int max = MaxLogLines)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length <= max) return string.Join("\n", lines);
        var kept = new string[max];
        Array.Copy(lines, lines.Length - max, kept, 0, max);
        return $"... ({lines.Length - max} earlier lines omitted)\n" + string.Join("\n", kept);
    }

    public static string[] BuildRerunFailedArgs(long runId) =>
        new[] { "run", "rerun", runId.ToString(), "--failed" };

    public static Task<GitResult> RerunFailedAsync(string repoRoot, long runId)
    {
        return Task.Run(() => ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null, BuildRerunFailedArgs(runId)));
    }

    // ── Issues ─────────────────────────────────────────────────────────

    public static string[] BuildIssueListArgs() => new[]
    {
        "issue", "list", "--limit", IssueLimit.ToString(),
        "--json", "number,title,author,labels,url,updatedAt",
    };

    public static Task<List<IssueInfo>> ListIssuesAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null, BuildIssueListArgs());
            return result.Ok ? ParseIssues(result.StdOut) : new List<IssueInfo>();
        });
    }

    public static List<IssueInfo> ParseIssues(string json)
    {
        var list = new List<IssueInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var i in doc.RootElement.EnumerateArray())
            {
                var author = i.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
                    ? Str(a, "login") : "";
                var labels = new List<string>();
                if (i.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                        if (Str(l, "name") is { Length: > 0 } n) labels.Add(n);
                list.Add(new IssueInfo(
                    i.TryGetProperty("number", out var num) && num.ValueKind == JsonValueKind.Number ? num.GetInt32() : 0,
                    Str(i, "title"), author, labels, Str(i, "url"), Str(i, "updatedAt")));
            }
        }
        catch (JsonException)
        {
            return new List<IssueInfo>();
        }
        return list;
    }

    /// <summary>
    /// `gh issue create`. The body goes over stdin (--body-file -) so newlines and Japanese
    /// survive; the title uses the "=" form so a leading '-' is never read as a flag.
    /// </summary>
    public static GhCommand BuildCreateIssue(string title, string body) =>
        new(new[] { "issue", "create", "--title=" + title, "--body-file", "-" }, body ?? "");

    public static Task<GitResult> CreateIssueAsync(string repoRoot, string title, string body)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(title)) return GitResult.Failed("no title");
            var cmd = BuildCreateIssue(title.Trim(), body);
            return ProcessRunner.Run("gh", repoRoot, cmd.Stdin, TimeoutMs, null, cmd.Args);
        });
    }

    /// <summary>
    /// The suggested branch for working on an issue: issue-&lt;n&gt;-&lt;short slug&gt;. Only ASCII letters
    /// and digits survive into the slug; a title with none (all Japanese, say) yields plain issue-&lt;n&gt;.
    /// </summary>
    public static string BuildIssueBranchName(int number, string title, int maxSlug = 40)
    {
        var sb = new StringBuilder();
        bool dash = true;
        foreach (var ch in (title ?? "").ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
            if (sb.Length >= maxSlug) break;
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? $"issue-{number}" : $"issue-{number}-{slug}";
    }

    // ── Releases ───────────────────────────────────────────────────────

    /// <summary>Existing tags, newest first. Empty on any failure.</summary>
    public static Task<List<string>> ListTagsAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var list = new List<string>();
            try
            {
                var raw = GitCli.Run(repoRoot, "tag", "--sort=-creatordate");
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (line.Trim() is { Length: > 0 } t) list.Add(t);
            }
            catch { }
            return list;
        });
    }

    /// <summary>
    /// `gh release create`. Notes go over stdin (--notes-file -) unless auto-generated. The target
    /// is only passed for a tag that does not exist yet; an existing tag already points somewhere.
    /// </summary>
    public static GhCommand BuildCreateRelease(string tag, IReadOnlyList<string> files, string title,
        string notes, bool generateNotes, bool draft, bool prerelease, string? target)
    {
        var args = new List<string> { "release", "create", tag };
        foreach (var f in files) args.Add(f);
        args.Add("--title=" + title);
        string? stdin = null;
        if (generateNotes) args.Add("--generate-notes");
        else { args.Add("--notes-file"); args.Add("-"); stdin = notes ?? ""; }
        if (draft) args.Add("--draft");
        if (prerelease) args.Add("--prerelease");
        if (!string.IsNullOrWhiteSpace(target)) { args.Add("--target"); args.Add(target); }
        return new GhCommand(args.ToArray(), stdin);
    }

    public static Task<GitResult> CreateReleaseAsync(string repoRoot, GhCommand cmd)
    {
        return Task.Run(() => ProcessRunner.Run("gh", repoRoot, cmd.Stdin, UploadTimeoutMs, null, cmd.Args));
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
