using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>One open pull request, as much of it as the panel shows.</summary>
public sealed record PullRequestInfo(
    int Number,
    string Title,
    string Author,
    string HeadBranch,
    string BaseBranch,
    bool IsDraft,
    string ReviewDecision,
    string Url,
    ChecksState Checks = ChecksState.None,
    IReadOnlyList<string>? FailingChecks = null);

/// <summary>Where a pull request's CI stands, all of its checks taken together.</summary>
public enum ChecksState { None, Pending, Passing, Failing }

/// <summary>One piece of review feedback: a review summary, a conversation comment or a line comment.</summary>
public sealed record PrFeedback(string Author, string Body, string? Path, int? Line, string Kind);

/// <summary>
/// The pull-request half of the source-control panel, driven through GitHub's own `gh` CLI.
/// Going through gh rather than the REST API means the user's existing `gh auth login` is the
/// only credential involved: Snipyard never sees or stores a token.
///
/// Every method is safe to call when gh is missing or signed out - the panel asks
/// <see cref="IsReadyAsync"/> first and hides the whole section when the answer is no.
/// </summary>
public static class GitHubCli
{
    private const int TimeoutMs = 45_000;

    /// <summary>The allowance for a repo-create-and-push, which reaches the network like a push does.</summary>
    private const int NetworkTimeoutMs = 180_000;

    public const int ListLimit = 30;

    /// <summary>
    /// Whether gh is installed and signed in. Cached: `gh auth status` reaches the network, and
    /// the answer does not change while the app is open often enough to pay for that every
    /// refresh. Null means "not asked yet".
    /// </summary>
    private static bool? _ready;

    public static Task<bool> IsReadyAsync()
    {
        if (_ready.HasValue) return Task.FromResult(_ready.Value);

        return Task.Run(() =>
        {
            // gh prints the account summary on stderr and exits non-zero when signed out, so
            // the exit code alone is the whole answer.
            var result = ProcessRunner.Run("gh", Environment.CurrentDirectory, null, TimeoutMs, null,
                "auth", "status");
            _ready = result.Ok;
            return result.Ok;
        });
    }

    /// <summary>Forgets the cached answer, so a user who signs in mid-session is noticed.</summary>
    public static void Reset() => _ready = null;

    /// <summary>
    /// Whether this repository is even on GitHub. Signed in to gh is not enough on its own: a
    /// repository with no remote, or one hosted elsewhere, has no pull requests to show, and an
    /// empty "Pull requests (0)" there reads as "none open" rather than "not applicable".
    /// Answered from the local remote list, so it costs nothing and works offline.
    /// </summary>
    public static Task<bool> HasGitHubRemoteAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(repoRoot)) return false;
            var remotes = GitCli.Run(repoRoot, "remote", "-v");
            return remotes.Contains("github.com", StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// How many pull requests are open, for the badge on the Source Control button. Null when
    /// gh is not signed in, the repository is not on GitHub, or the call fails, so a passing
    /// network error does not read as "none open".
    /// </summary>
    public static async Task<int?> CountOpenAsync(string repoRoot)
    {
        if (!await IsReadyAsync() || !await HasGitHubRemoteAsync(repoRoot)) return null;

        return await Task.Run<int?>(() =>
        {
            try
            {
                var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "pr", "list", "--state", "open", "--limit", "1000", "--json", "number");
                if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut)) return null;
                using var doc = JsonDocument.Parse(result.StdOut);
                return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : null;
            }
            catch { return null; }
        });
    }

    /// <summary>Open pull requests on the repository, newest first. Empty on any failure.</summary>
    public static Task<List<PullRequestInfo>> ListAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var list = new List<PullRequestInfo>();
            try
            {
                var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "pr", "list",
                    "--state", "open",
                    "--limit", ListLimit.ToString(),
                    "--json", "number,title,author,headRefName,baseRefName,isDraft,reviewDecision,url,statusCheckRollup");
                if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut)) return list;

                var raw = JsonSerializer.Deserialize<List<PrDto>>(result.StdOut);
                if (raw == null) return list;

                foreach (var pr in raw)
                {
                    var (checks, failing) = SummarizeChecks(pr.StatusCheckRollup);
                    list.Add(new PullRequestInfo(
                        pr.Number,
                        pr.Title ?? "",
                        pr.Author?.Login ?? "",
                        pr.HeadRefName ?? "",
                        pr.BaseRefName ?? "",
                        pr.IsDraft,
                        pr.ReviewDecision ?? "",
                        pr.Url ?? "",
                        checks,
                        failing));
                }
            }
            catch
            {
                return new List<PullRequestInfo>();
            }
            return list;
        });
    }

    /// <summary>
    /// Folds a statusCheckRollup into one state. It mixes two shapes: CheckRun (Actions and
    /// apps: status + conclusion) and StatusContext (the older commit statuses: state).
    /// A single failure fails the whole; otherwise anything unfinished keeps it pending.
    /// </summary>
    public static (ChecksState State, List<string> Failing) SummarizeChecks(JsonElement? rollup)
    {
        var failing = new List<string>();
        if (rollup is not { ValueKind: JsonValueKind.Array } items || items.GetArrayLength() == 0)
            return (ChecksState.None, failing);

        bool pending = false;
        foreach (var c in items.EnumerateArray())
        {
            string Str(string name) => c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            var name = Str("name").Length > 0 ? Str("name") : Str("context");
            if (c.TryGetProperty("state", out _) && Str("state").Length > 0)
            {
                var state = Str("state").ToUpperInvariant();
                if (state is "FAILURE" or "ERROR") failing.Add(name);
                else if (state is "PENDING" or "EXPECTED") pending = true;
            }
            else
            {
                if (!string.Equals(Str("status"), "COMPLETED", StringComparison.OrdinalIgnoreCase)) pending = true;
                else if (Str("conclusion").ToUpperInvariant() is "FAILURE" or "TIMED_OUT" or "CANCELLED"
                         or "ACTION_REQUIRED" or "STARTUP_FAILURE")
                    failing.Add(name);
            }
        }
        var result = failing.Count > 0 ? ChecksState.Failing : pending ? ChecksState.Pending : ChecksState.Passing;
        return (result, failing);
    }

    /// <summary>
    /// Everything reviewers have said on a pull request: review summaries and conversation
    /// comments from `gh pr view`, and line comments from the REST endpoint, which gh pr view
    /// does not carry. Empty bodies (a bare approval) are left out. Empty on any failure.
    /// </summary>
    public static Task<List<PrFeedback>> GetFeedbackAsync(string repoRoot, int number)
    {
        return Task.Run(() =>
        {
            var list = new List<PrFeedback>();
            try
            {
                var view = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "pr", "view", number.ToString(), "--json", "reviews,comments");
                if (view.Ok && !string.IsNullOrWhiteSpace(view.StdOut))
                {
                    using var doc = JsonDocument.Parse(view.StdOut);
                    foreach (var (key, kind) in new[] { ("reviews", "review"), ("comments", "comment") })
                    {
                        if (!doc.RootElement.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                        foreach (var r in arr.EnumerateArray())
                        {
                            var body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(body)) continue;
                            list.Add(new PrFeedback(Login(r), body.Trim(), null, null, kind));
                        }
                    }
                }

                var inline = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "api", $"repos/{{owner}}/{{repo}}/pulls/{number}/comments");
                if (inline.Ok && !string.IsNullOrWhiteSpace(inline.StdOut))
                {
                    using var doc = JsonDocument.Parse(inline.StdOut);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var r in doc.RootElement.EnumerateArray())
                        {
                            var body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                            if (string.IsNullOrWhiteSpace(body)) continue;
                            var path = r.TryGetProperty("path", out var p) ? p.GetString() : null;
                            int? line = r.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number
                                ? l.GetInt32()
                                : r.TryGetProperty("original_line", out var ol) && ol.ValueKind == JsonValueKind.Number
                                    ? ol.GetInt32() : null;
                            var user = r.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var ul)
                                ? ul.GetString() ?? "" : "";
                            list.Add(new PrFeedback(user, body.Trim(), path, line, "line"));
                        }
                }
            }
            catch { }
            return list;
        });

        static string Login(JsonElement e) =>
            e.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
    }

    /// <summary>
    /// Opens a pull request from the current branch. The body goes over stdin, so newlines and
    /// Japanese survive intact; gh works out the head branch from the checkout.
    /// </summary>
    public static Task<GitResult> CreateAsync(string repoRoot, string title, string body, string baseBranch,
        IReadOnlyList<string>? reviewers = null, IReadOnlyList<string>? assignees = null,
        IReadOnlyList<string>? labels = null, bool draft = false)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(title)) return GitResult.Failed("no title");

            var args = new List<string> { "pr", "create", "--title", title, "--body-file", "-" };
            if (!string.IsNullOrWhiteSpace(baseBranch))
            {
                args.Add("--base");
                args.Add(baseBranch);
            }
            // "=" form so a value can never be read as another flag. gh splits each of these on
            // commas as CSV, so a label with a comma in its name has to arrive quoted.
            foreach (var r in reviewers ?? Array.Empty<string>()) args.Add("--reviewer=" + CsvItem(r));
            foreach (var a in assignees ?? Array.Empty<string>()) args.Add("--assignee=" + CsvItem(a));
            foreach (var l in labels ?? Array.Empty<string>()) args.Add("--label=" + CsvItem(l));
            if (draft) args.Add("--draft");

            return ProcessRunner.Run("gh", repoRoot, body ?? "", TimeoutMs, null, args.ToArray());
        });

        static string CsvItem(string value) =>
            value.IndexOfAny(new[] { ',', '"' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// What a new pull request can be tagged with: the people who can be assigned or asked for a
    /// review, the teams that can review it, and the repository's labels. <see cref="Me"/> is
    /// the signed-in login, which GitHub refuses as a reviewer of one's own pull request.
    /// </summary>
    public sealed record PrCandidates(string Me, List<string> Users, List<string> Teams, List<string> Labels);

    /// <summary>
    /// Reads <see cref="PrCandidates"/>. The four gh calls run side by side, and any one of them
    /// failing - no teams on a personal repository, say - only leaves its list empty.
    /// </summary>
    public static async Task<PrCandidates> GetPrCandidatesAsync(string repoRoot)
    {
        // {owner}/{repo} is gh's own placeholder for the repository in the working directory.
        // /assignees rather than /collaborators: it is the same set of people, and it does not
        // need push access to read.
        var me = Task.Run(() => Lines("api", "user", "--jq", ".login"));
        var users = Task.Run(() => Lines("api", "repos/{owner}/{repo}/assignees", "--paginate", "--jq", ".[].login"));
        var teams = Task.Run(() => Lines("api", "repos/{owner}/{repo}/teams", "--paginate", "--jq", ".[].html_url"));
        var labels = Task.Run(() => Lines("label", "list", "--limit", "500", "--json", "name", "--jq", ".[].name"));
        await Task.WhenAll(me, users, teams, labels);

        // --reviewer takes a team as org/slug, which the team's own URL spells out
        var teamNames = new List<string>();
        foreach (var url in teams.Result)
        {
            var m = Regex.Match(url, @"/orgs/([^/]+)/teams/([^/?#]+)");
            if (m.Success) teamNames.Add(m.Groups[1].Value + "/" + m.Groups[2].Value);
        }
        return new PrCandidates(me.Result.Count > 0 ? me.Result[0] : "", users.Result, teamNames, labels.Result);

        List<string> Lines(params string[] args)
        {
            var list = new List<string>();
            try
            {
                var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null, args);
                if (!result.Ok) return list;
                foreach (var line in result.StdOut.Split('\n'))
                    if (line.Trim() is { Length: > 0 } text) list.Add(text);
            }
            catch { }
            return list;
        }
    }

    /// <summary>
    /// Approves a pull request. GitHub refuses to let an author approve their own, and that
    /// refusal comes back as gh's message for the panel to show - it is the correct answer,
    /// not a bug to work around.
    /// </summary>
    public static Task<GitResult> ApproveAsync(string repoRoot, int number)
    {
        return Task.Run(() => ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
            "pr", "review", number.ToString(), "--approve"));
    }

    /// <summary>
    /// The accounts a new repo could be created under: the signed-in user first, then every
    /// organization they belong to. Either gh call failing just shrinks the list - never throws.
    /// </summary>
    public static Task<List<string>> GetOwnersAsync()
    {
        return Task.Run(() =>
        {
            var owners = new List<string>();

            var user = ProcessRunner.Run("gh", Environment.CurrentDirectory, null, TimeoutMs, null,
                "api", "user", "--jq", ".login");
            var login = user.StdOut.Trim();
            if (user.Ok && login.Length > 0) owners.Add(login);

            var orgs = ProcessRunner.Run("gh", Environment.CurrentDirectory, null, TimeoutMs, null,
                "api", "user/orgs", "--jq", ".[].login");
            if (orgs.Ok)
            {
                foreach (var line in orgs.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var name = line.Trim();
                    if (name.Length > 0) owners.Add(name);
                }
            }

            return owners;
        });
    }

    /// <summary>
    /// Creates a repository on GitHub from the current local one, wires it up as `origin`, and
    /// pushes the current branch - all in one gh invocation, so a failure cannot leave the
    /// remote half-configured.
    /// </summary>
    public static Task<GitResult> CreateAndPushAsync(
        string repoRoot, string? owner, string name, string? description, bool isPrivate)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(name)) return GitResult.Failed("no name");
            // GitHub repo names are letters, digits, '.', '-', '_' only; this also blocks a
            // leading '-' from being parsed as a gh option instead of the repo name.
            if (!Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,100}$"))
                return GitResult.Failed("invalid repository name");

            // "owner/name" is gh's own syntax for creating under an org instead of the
            // signed-in account - there is no separate --org flag. Owner comes from a
            // gh-populated dropdown, not free typing, but it's still validated: GitHub logins
            // are alnum/hyphen only.
            var target = name;
            if (!string.IsNullOrWhiteSpace(owner))
            {
                if (!Regex.IsMatch(owner, "^[A-Za-z0-9-]{1,100}$"))
                    return GitResult.Failed("invalid owner");
                target = owner + "/" + name;
            }

            var args = new List<string>
            {
                "repo", "create", target, isPrivate ? "--private" : "--public",
                "--source=.", "--remote=origin", "--push",
            };
            // "=" form, not a separate argv element: keeps a description that happens to
            // start with '-' from ever being re-parsed as another flag.
            if (!string.IsNullOrWhiteSpace(description))
                args.Add("--description=" + description);

            return ProcessRunner.Run("gh", repoRoot, null, NetworkTimeoutMs, null, args.ToArray());
        });
    }

    /// <summary>The branch a new pull request should merge into, as GitHub has it configured.</summary>
    public static Task<string> GetDefaultBranchAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            try
            {
                var result = ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null,
                    "repo", "view", "--json", "defaultBranchRef");
                if (!result.Ok || string.IsNullOrWhiteSpace(result.StdOut)) return "";

                using var doc = JsonDocument.Parse(result.StdOut);
                if (doc.RootElement.TryGetProperty("defaultBranchRef", out var refNode) &&
                    refNode.ValueKind == JsonValueKind.Object &&
                    refNode.TryGetProperty("name", out var name))
                    return name.GetString() ?? "";
            }
            catch
            {
                // A repository gh cannot see has no default branch to report.
            }
            return "";
        });
    }

    /// <summary>Pulls the pull-request URL out of whatever gh printed when it created one.</summary>
    public static string ExtractUrl(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";

        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return "";
    }

    // ── Wire shapes ────────────────────────────────────────────────────

    private sealed class PrDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("author")] public AuthorDto? Author { get; set; }
        [JsonPropertyName("headRefName")] public string? HeadRefName { get; set; }
        [JsonPropertyName("baseRefName")] public string? BaseRefName { get; set; }
        [JsonPropertyName("isDraft")] public bool IsDraft { get; set; }
        [JsonPropertyName("reviewDecision")] public string? ReviewDecision { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("statusCheckRollup")] public JsonElement? StatusCheckRollup { get; set; }
    }

    private sealed class AuthorDto
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
    }
}
