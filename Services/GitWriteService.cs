using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>Where the current branch stands against the remote it tracks.</summary>
public sealed record BranchState(string Current, int Ahead, int Behind, bool HasUpstream)
{
    public static readonly BranchState None = new("", 0, 0, false);

    /// <summary>
    /// The short hash HEAD points at when it is detached (on no branch), otherwise empty. Lets
    /// the panel say "detached" instead of showing a bare dash that reads like "no repository".
    /// </summary>
    public string DetachedAt { get; init; } = "";

    /// <summary>
    /// The remote branch this one was started from, such as "origin/main", or empty when it
    /// cannot be told or is the branch's own upstream.
    /// </summary>
    public string BaseRef { get; init; } = "";

    /// <summary>Commits on <see cref="BaseRef"/> that HEAD does not contain yet.</summary>
    public int BaseBehind { get; init; }

    /// <summary>
    /// Commits on the upstream or on the base that HEAD does not contain, each counted once -
    /// everything a pull or a merge from the base might still bring in.
    /// </summary>
    public int Incoming { get; init; }
}

/// <summary>
/// A multi-step operation git has started and not finished. It changes what "continue" and
/// "abort" have to run, and it is the reason the panel refuses to commit while one is open.
/// </summary>
public enum RepoOperation
{
    None,
    Rebase,
    Merge,
    CherryPick,
    Revert,
}

/// <summary>
/// The write half of the git integration: staging, committing, branching and pushing. Split from
/// <see cref="GitChangeService"/> so the read path stays plainly read-only, and every call here
/// reports what git said rather than swallowing it - a refused push is only useful with its
/// reason attached.
///
/// Nothing in here rewrites history or forces anything: the destructive verbs are deliberately
/// absent rather than merely unused. The two that sound destructive are not - `branch -d`
/// refuses a branch whose work is not already merged, and `--abort` puts back exactly what the
/// merge or rebase started from. `-D`, `push --force` and `reset --hard` have no entry point here.
/// </summary>
public static class GitWriteService
{
    /// <summary>Environment for a fetch nobody asked for: fail rather than block on a prompt.</summary>
    private static readonly Dictionary<string, string> NoPromptEnv = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>
    /// Where a repository name typed at "Create Repository..." is remembered when it differs
    /// from the folder name. Git has no such concept on its own; this is local config only, so
    /// it never leaks into a push and disappears if the repository is ever re-cloned.
    /// </summary>
    public const string RepoNameConfigKey = "snipyard.repo-name";

    /// <summary>The same setting as written before the app was renamed from Claucraft.</summary>
    public const string LegacyRepoNameConfigKey = "claucraft.repo-name";

    /// <summary>Turns an existing, ordinary folder into a new git repository.</summary>
    /// <param name="repoName">
    /// The name the user chose in the dialog. When it differs from the folder's own name, it is
    /// saved to local git config so the status bar (and later, GitHub repo creation) can show it
    /// instead of the folder name - the folder itself is never renamed.
    /// </param>
    public static Task<GitResult> InitAsync(string folder, string? repoName = null)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return GitResult.Failed("folder not found");
            // Without -b, the initial branch name follows the user's global git config (or
            // git's own default, "master", on older installs) - pin it to "main" instead.
            var result = GitCli.Execute(folder, null, "init", "-b", "main");
            if (!result.Ok) return result;

            var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(repoName) &&
                !string.Equals(repoName, folderName, StringComparison.Ordinal) &&
                !repoName.StartsWith("-", StringComparison.Ordinal))
            {
                // "--" stops option parsing so a value git config would otherwise read as a
                // flag (even past the leading-'-' check above, e.g. embedded via some other
                // path) is always taken as the literal value.
                GitCli.Execute(folder, null, "config", "--local", "--", RepoNameConfigKey, repoName);
            }
            return result;
        });
    }

    /// <summary>Clones <paramref name="url"/> into a new subfolder of <paramref name="parentFolder"/>.</summary>
    public static Task<GitResult> CloneAsync(string parentFolder, string url, string folderName)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(parentFolder) || !Directory.Exists(parentFolder))
                return GitResult.Failed("folder not found");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(folderName))
                return GitResult.Failed("missing url or folder name");
            // A leading '-' would let the url or folder name be parsed as a git option instead
            // of a positional argument; "--" below stops option parsing as a second guard.
            if (url.StartsWith("-", StringComparison.Ordinal) || folderName.StartsWith("-", StringComparison.Ordinal))
                return GitResult.Failed("invalid url or folder name");

            // null env, not NoPromptEnv: this is a user-initiated clone and should be allowed
            // to prompt for credentials via the credential manager, same as an interactive push.
            return GitCli.ExecuteRemote(parentFolder, null, "clone", "--", url, folderName);
        });
    }

    /// <summary>Whether the repository has any remote configured at all, not just a tracked upstream.</summary>
    public static Task<bool> HasAnyRemoteAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return false;
            return GitCli.Run(repoRoot, "remote").Trim().Length > 0;
        });
    }

    /// <summary>
    /// Whether HEAD points at a real commit. False right after `git init`, before the first
    /// commit - there is nothing to push yet, and `gh repo create --push` fails with a cryptic
    /// "no commits found" rather than explaining that.
    /// </summary>
    public static Task<bool> HasAnyCommitAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return false;
            return GitCli.Execute(repoRoot, null, "rev-parse", "--verify", "-q", "HEAD").Ok;
        });
    }

    /// <summary>
    /// Creates a lightweight tag on the given commit. The name is free text - it can be Japanese,
    /// unlike the GitHub-facing identifiers elsewhere in this class - so it is checked against
    /// git's own ref-name rules (a blocklist) rather than an allowlist regex.
    /// </summary>
    public static Task<GitResult> CreateTagAsync(string repoRoot, string tagName, string commitHash)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!IsValidTagName(tagName)) return GitResult.Failed("invalid tag name");
            if (string.IsNullOrWhiteSpace(commitHash) || commitHash.StartsWith("-", StringComparison.Ordinal))
                return GitResult.Failed("invalid commit");

            return GitCli.Execute(repoRoot, null, "tag", "--", tagName, commitHash);
        });
    }

    /// <summary>Mirrors git's check-ref-format rules for a single-component ref name.</summary>
    private static bool IsValidTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return false;
        if (name.StartsWith("-", StringComparison.Ordinal)) return false;
        if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal)) return false;
        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("..") || name.Contains("//") || name.Contains("@{") || name == "@") return false;

        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
            if ("~^:?*[\\".IndexOf(c) >= 0) return false;
        }
        return true;
    }

    /// <summary>Stages the given repository-relative paths. Untracked files included.</summary>
    public static Task<GitResult> StageAsync(string repoRoot, IReadOnlyList<string> paths)
        => RunOnPaths(repoRoot, paths, new[] { "add", "--" });

    /// <summary>
    /// Takes the given paths back out of the index, leaving the working tree alone.
    /// `restore --staged` is the safe half of the old `reset` - it cannot touch the file itself.
    /// </summary>
    public static Task<GitResult> UnstageAsync(string repoRoot, IReadOnlyList<string> paths)
        => RunOnPaths(repoRoot, paths, new[] { "restore", "--staged", "--" });

    /// <summary>
    /// Commits what is staged. The message goes in over stdin, so newlines, quotes and Japanese
    /// all survive - a message on the command line would have to get past both the shell and the
    /// console code page.
    /// </summary>
    public static Task<GitResult> CommitAsync(string repoRoot, string message)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(message)) return GitResult.Failed("empty message");

            return GitCli.Execute(repoRoot, message, "commit", "-F", "-");
        });
    }

    /// <summary>Local branch names, newest activity first, with the current one named separately.</summary>
    public static Task<(List<string> Branches, string Current)> GetBranchesAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var branches = new List<string>();
            string current = "";
            if (!Usable(repoRoot)) return (branches, current);

            // %(refname:short) rather than `git branch`, whose output carries the "* " marker and
            // is decorated for a terminal.
            var output = GitCli.Run(repoRoot,
                "for-each-ref", "--sort=-committerdate", "--format=%(refname:short)", "refs/heads/");

            foreach (var line in output.Split('\n'))
            {
                var name = line.Trim();
                if (name.Length > 0) branches.Add(name);
            }

            current = GitCli.Run(repoRoot, "branch", "--show-current").Trim();
            return (branches, current);
        });
    }

    /// <summary>Switches branches. Fails rather than discarding anything if the tree is dirty.</summary>
    public static Task<GitResult> CheckoutBranchAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");

            // `switch` refuses on conflicting local changes; `checkout` would silently carry
            // them across. Refusing is the right default when the user has not asked to move
            // work between branches.
            return GitCli.Execute(repoRoot, null, "switch", "--", branch);
        });
    }

    /// <summary>
    /// Checks out one commit as a detached HEAD. Like <see cref="CheckoutBranchAsync"/> it uses
    /// `switch`, so conflicting local changes make it refuse instead of being carried along.
    /// </summary>
    public static Task<GitResult> CheckoutCommitAsync(string repoRoot, string commitHash)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(commitHash) || commitHash.StartsWith("-", StringComparison.Ordinal))
                return GitResult.Failed("invalid commit");

            return GitCli.Execute(repoRoot, null, "switch", "--detach", commitHash);
        });
    }

    /// <summary>
    /// Creates a branch at HEAD and switches to it, noting which branch it was started from so
    /// the badge can count what lands there later (see <see cref="FindBaseRef"/>).
    /// </summary>
    public static Task<GitResult> CreateBranchAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");

            var from = GitCli.Run(repoRoot, "branch", "--show-current").Trim();
            var result = GitCli.Execute(repoRoot, null, "switch", "-c", branch);
            if (result.Ok && from.Length > 0)
                GitCli.Execute(repoRoot, null, "config", "--local", $"branch.{branch}.{BaseBranchConfigKey}", from);
            return result;
        });
    }

    /// <summary>
    /// Pushes the current branch. A branch with no upstream gets one; nothing is ever forced.
    /// </summary>
    public static Task<GitResult> PushAsync(string repoRoot, BranchState state)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrEmpty(state.Current)) return GitResult.Failed("no branch");

            return state.HasUpstream
                ? GitCli.ExecuteRemote(repoRoot, null, "push")
                : GitCli.ExecuteRemote(repoRoot, null, "push", "--set-upstream", "origin", state.Current);
        });
    }

    /// <summary>
    /// The current branch and how far it has drifted from its upstream. Reads only what is
    /// already local - no fetch, so this cannot stall on the network.
    /// </summary>
    public static Task<BranchState> GetBranchStateAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return BranchState.None;

            var current = GitCli.Run(repoRoot, "branch", "--show-current").Trim();
            if (current.Length == 0)
            {
                // No branch name: either a detached HEAD or a repository with no commit yet.
                // Only the former resolves HEAD to a commit.
                var head = GitCli.Execute(repoRoot, null, "rev-parse", "--short", "--verify", "-q", "HEAD");
                return head.Ok && head.StdOut.Trim().Length > 0
                    ? BranchState.None with { DetachedAt = head.StdOut.Trim() }
                    : BranchState.None;
            }

            var upstream = GitCli.Execute(repoRoot, null,
                "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
            var upstreamName = upstream.Ok ? upstream.StdOut.Trim() : "";

            var state = new BranchState(current, 0, 0, false);
            if (upstream.Ok)
            {
                // "<behind>\t<ahead>" - left is the upstream side, right is ours.
                var counts = GitCli.Run(repoRoot, "rev-list", "--left-right", "--count", "@{upstream}...HEAD");
                var parts = counts.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int behind = parts.Length > 0 && int.TryParse(parts[0], out var b) ? b : 0;
                int ahead = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 0;
                state = new BranchState(current, ahead, behind, true);
            }

            var baseRef = FindBaseRef(repoRoot, current, upstreamName);
            if (baseRef.Length == 0) return state with { Incoming = state.Behind };

            // The union, not the sum: a commit merged into both the base and the upstream is
            // one thing to bring in, not two.
            int baseBehind = CountRevs(repoRoot, "rev-list", "--count", baseRef, "^HEAD", "--");
            int incoming = upstream.Ok
                ? CountRevs(repoRoot, "rev-list", "--count", "@{upstream}", baseRef, "^HEAD", "--")
                : baseBehind;
            return state with { BaseRef = baseRef, BaseBehind = baseBehind, Incoming = incoming };
        });
    }

    /// <summary>
    /// Where <see cref="CreateBranchAsync"/> notes the branch a new one was started from. Git
    /// itself keeps no such record past the reflog, which expires.
    /// </summary>
    public const string BaseBranchConfigKey = "snipyard-base";

    private static int CountRevs(string repoRoot, params string[] args) =>
        int.TryParse(GitCli.Run(repoRoot, args).Trim(), out var n) ? n : 0;

    /// <summary>
    /// The remote branch <paramref name="current"/> was started from, or "" when that cannot be
    /// told. Git does not record a branch's parent, so this asks, in order: the note this app
    /// writes on "New branch...", the branch's own reflog ("branch: Created from main"), and the
    /// HEAD reflog's oldest "checkout: moving from main to feature" - which is what both
    /// `switch -c` and `checkout -b` write. There is deliberately no guess at the default branch:
    /// a release branch that never takes main would wear a badge it can never clear.
    /// </summary>
    private static string FindBaseRef(string repoRoot, string current, string upstreamName)
    {
        var candidates = new List<string>();

        var noted = GitCli.Run(repoRoot, "config", "--local", "--get", $"branch.{current}.{BaseBranchConfigKey}").Trim();
        if (noted.Length > 0) candidates.Add(noted);

        const string createdFrom = "branch: Created from ";
        var branchLog = GitCli.Run(repoRoot, "reflog", "show", "--format=%gs", "refs/heads/" + current, "--")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (branchLog.Length > 0 && branchLog[^1].TrimEnd().StartsWith(createdFrom, StringComparison.Ordinal))
            candidates.Add(branchLog[^1].TrimEnd()[createdFrom.Length..].Trim());

        var movedInto = " to " + current;
        const string moving = "checkout: moving from ";
        var headLog = GitCli.Run(repoRoot, "reflog", "show", "--format=%gs", "HEAD", "--")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = headLog.Length - 1; i >= 0; i--)
        {
            var line = headLog[i].TrimEnd();
            if (!line.StartsWith(moving, StringComparison.Ordinal) || !line.EndsWith(movedInto, StringComparison.Ordinal)
                || line.Length <= moving.Length + movedInto.Length)
                continue;
            candidates.Add(line[moving.Length..^movedInto.Length]);
            break;
        }

        foreach (var name in candidates)
        {
            var remote = ResolveRemoteRef(repoRoot, name, current);
            if (remote.Length > 0 && !string.Equals(remote, upstreamName, StringComparison.Ordinal))
                return remote;
        }
        return "";
    }

    /// <summary>
    /// The remote-tracking ref that stands for <paramref name="name"/>: itself when it already is
    /// one, else that local branch's upstream, else origin/&lt;name&gt;. A local branch alone is no
    /// use - it never moves when someone else pushes.
    /// </summary>
    private static string ResolveRemoteRef(string repoRoot, string name, string current)
    {
        if (name.Length == 0 || name == "HEAD" || name == current || name.StartsWith("-", StringComparison.Ordinal))
            return "";

        bool IsRemote(string n) =>
            GitCli.Execute(repoRoot, null, "show-ref", "--verify", "-q", "refs/remotes/" + n).Ok;

        if (IsRemote(name)) return name;

        if (GitCli.Execute(repoRoot, null, "show-ref", "--verify", "-q", "refs/heads/" + name).Ok)
        {
            var up = GitCli.Execute(repoRoot, null,
                "rev-parse", "--abbrev-ref", "--symbolic-full-name", name + "@{upstream}");
            if (up.Ok && up.StdOut.Trim().Length > 0) return up.StdOut.Trim();
        }

        return IsRemote("origin/" + name) ? "origin/" + name : "";
    }

    // ── Remote traffic ─────────────────────────────────────────────────

    /// <summary>
    /// Brings the remote's refs up to date without touching the working tree - the one network
    /// call that cannot lose anything, which is why the panel offers it as the safe first move.
    /// <paramref name="quiet"/> marks the timer's own fetch: it must fail rather than sit on a
    /// credential prompt no one is watching.
    /// </summary>
    public static Task<GitResult> FetchAsync(string repoRoot, bool quiet)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");

            return GitCli.ExecuteRemote(repoRoot, quiet ? NoPromptEnv : null, "fetch", "--prune");
        });
    }

    /// <summary>
    /// Takes the remote's commits and replays the local ones on top. Rebasing keeps the history
    /// a single line, which is what makes the graph readable for people who are not going to
    /// untangle a merge bubble.
    /// </summary>
    public static Task<GitResult> PullRebaseAsync(string repoRoot, bool autostash = false)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");

            return autostash
                ? GitCli.ExecuteRemote(repoRoot, null, "pull", "--rebase", "--autostash")
                : GitCli.ExecuteRemote(repoRoot, null, "pull", "--rebase");
        });
    }

    /// <summary>
    /// Pulls, and when git refuses because uncommitted edits would be overwritten, offers to
    /// set them aside for the pull and put them back afterwards (--autostash) instead of sending
    /// the user to a terminal. A declined offer returns success so no error dialog follows.
    /// When the edits cannot be put back cleanly git keeps them in the stash and says so; that
    /// is surfaced through <paramref name="showMessage"/> because the pull itself succeeded.
    /// </summary>
    public static async Task<GitResult> PullOfferingStashAsync(string repoRoot,
        Func<string, string, Task<bool>>? confirm, Action<string, string>? showMessage)
    {
        var result = await PullRebaseAsync(repoRoot);
        if (result.Ok || confirm == null || !GitErrorHints.IsLocalChanges(result.Message)) return result;

        if (!await ConfirmStashAsync(repoRoot, result.Message, "PullStashText", confirm))
            return new GitResult(0, "", "");

        var retry = await PullRebaseAsync(repoRoot, autostash: true);
        if (retry.Ok && (retry.StdErr + retry.StdOut).Contains("autostash resulted in conflicts", StringComparison.OrdinalIgnoreCase))
            showMessage?.Invoke(Loc.Get("PullStashTitle"), Loc.Get("PullStashKept"));
        return retry;
    }

    /// <summary>
    /// Runs a branch or commit switch, and when git refuses because uncommitted edits would be
    /// overwritten, offers to carry them across: stash, switch, then pop on the new HEAD.
    /// `git switch` has no --autostash, so the three steps are spelled out. A failed switch pops
    /// the stash straight back onto the HEAD it came from, leaving the tree as it was.
    /// </summary>
    public static async Task<GitResult> SwitchOfferingStashAsync(string repoRoot, Func<Task<GitResult>> switchWork,
        Func<string, string, Task<bool>>? confirm, Action<string, string>? showMessage)
    {
        var result = await switchWork();
        if (result.Ok || confirm == null || !GitErrorHints.IsLocalChanges(result.Message)) return result;

        if (!await ConfirmStashAsync(repoRoot, result.Message, "SwitchStashText", confirm))
            return new GitResult(0, "", "");

        var stash = await Task.Run(() =>
            GitCli.Execute(repoRoot, null, "stash", "push", "-m", "Snipyard: carried across a switch"));
        if (!stash.Ok) return stash;
        // Nothing was stashed: popping now would apply some older, unrelated entry.
        if ((stash.StdOut + stash.StdErr).Contains("No local changes to save", StringComparison.OrdinalIgnoreCase))
            return result;

        var retry = await switchWork();
        var pop = await Task.Run(() => GitCli.Execute(repoRoot, null, "stash", "pop"));
        if (!retry.Ok) return retry;

        // A pop that cannot apply cleanly leaves conflict markers and keeps the stash entry.
        if (!pop.Ok)
            showMessage?.Invoke(Loc.Get("PullStashTitle"), Loc.Get("SwitchStashKept"));
        return retry;
    }

    /// <summary>The "set your edits aside?" question, listing the files git named.</summary>
    private static async Task<bool> ConfirmStashAsync(string repoRoot, string gitMessage, string textKey,
        Func<string, string, Task<bool>> confirm)
    {
        // "cannot pull with rebase: You have unstaged changes" names no files, so ask git.
        var files = GitErrorHints.LocalChangeFiles(gitMessage);
        if (files.Count == 0)
            files = (await Task.Run(() => GitCli.Run(repoRoot, "diff", "--name-only", "HEAD")))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var list = files.Count == 0 ? "" : Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, files.Take(15).Select(f => "  " + f))
            + (files.Count > 15 ? Environment.NewLine + "  ..." : "");
        return await confirm(Loc.Get("PullStashTitle"), Loc.Get(textKey) + list);
    }

    // ── Branch work ────────────────────────────────────────────────────

    /// <summary>Brings <paramref name="branch"/> into the current one. --no-edit keeps git from opening an editor.</summary>
    public static Task<GitResult> MergeAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");

            return GitCli.Execute(repoRoot, null, "merge", "--no-edit", "--", branch);
        });
    }

    /// <summary>
    /// Deletes a local branch. Lower-case -d only: git refuses a branch holding work that is not
    /// merged anywhere else, so the button cannot throw away commits. Its refusal is the answer
    /// the user is shown, and -D is never offered as the way past it.
    /// </summary>
    public static Task<GitResult> DeleteBranchAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");

            return GitCli.Execute(repoRoot, null, "branch", "-d", "--", branch);
        });
    }

    // ── Interrupted merges and rebases ─────────────────────────────────

    /// <summary>Files git has left with conflict markers in them.</summary>
    public static Task<List<string>> GetConflictsAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var files = new List<string>();
            if (!Usable(repoRoot)) return files;

            var output = GitCli.Run(repoRoot, "-c", "core.quotepath=false",
                "diff", "--name-only", "--diff-filter=U");

            foreach (var line in output.Split('\n'))
            {
                var path = GitPath.Unquote(line.Trim()).Replace('\\', '/').Trim();
                if (path.Length > 0) files.Add(path);
            }
            return files;
        });
    }

    /// <summary>
    /// Whether the repository is part-way through something. Read from the git directory rather
    /// than from a status message, so it is the same answer in any locale - and via
    /// --absolute-git-dir, so it is still right inside a worktree, where .git is a file.
    /// </summary>
    public static Task<RepoOperation> GetRepoOperationAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return RepoOperation.None;

            var gitDir = GitCli.Run(repoRoot, "rev-parse", "--absolute-git-dir").Trim();
            if (gitDir.Length == 0) return RepoOperation.None;

            gitDir = gitDir.Replace('/', Path.DirectorySeparatorChar);

            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
                Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
                return RepoOperation.Rebase;

            // A stopped cherry-pick or revert also leaves no MERGE_HEAD, so each is read by its
            // own marker; the commit graph starts both.
            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
                return RepoOperation.CherryPick;

            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                return RepoOperation.Revert;

            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
                return RepoOperation.Merge;

            return RepoOperation.None;
        });
    }

    /// <summary>Puts the tree back the way it was before the merge or rebase started.</summary>
    public static Task<GitResult> AbortAsync(string repoRoot, RepoOperation operation)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");

            return operation switch
            {
                RepoOperation.Rebase => GitCli.Execute(repoRoot, null, "rebase", "--abort"),
                RepoOperation.Merge => GitCli.Execute(repoRoot, null, "merge", "--abort"),
                RepoOperation.CherryPick => GitCli.Execute(repoRoot, null, "cherry-pick", "--abort"),
                RepoOperation.Revert => GitCli.Execute(repoRoot, null, "revert", "--abort"),
                _ => GitResult.Failed("nothing in progress"),
            };
        });
    }

    /// <summary>
    /// Carries on once the conflicts are resolved and staged. core.editor=true stands in for the
    /// editor git would otherwise open and wait on forever, there being no terminal to open it in.
    /// </summary>
    public static Task<GitResult> ContinueAsync(string repoRoot, RepoOperation operation)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");

            return operation switch
            {
                RepoOperation.Rebase => GitCli.Execute(repoRoot, null, "-c", "core.editor=true", "rebase", "--continue"),
                RepoOperation.Merge => GitCli.Execute(repoRoot, null, "-c", "core.editor=true", "merge", "--continue"),
                RepoOperation.CherryPick => GitCli.Execute(repoRoot, null, "-c", "core.editor=true", "cherry-pick", "--continue"),
                RepoOperation.Revert => GitCli.Execute(repoRoot, null, "-c", "core.editor=true", "revert", "--continue"),
                _ => GitResult.Failed("nothing in progress"),
            };
        });
    }

    // ── Reading, for the commit-message draft ──────────────────────────

    /// <summary>
    /// What a commit right now would record. Falls back to the unstaged diff so pressing the
    /// draft button before staging anything still describes something rather than nothing.
    /// </summary>
    public static Task<string> GetStagedDiffAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return "";

            var staged = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--cached");
            if (string.IsNullOrWhiteSpace(staged))
                staged = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff");

            return GitCli.TruncateDiff(staged ?? "");
        });
    }

    /// <summary>
    /// One line per staged file. The diff itself is cut down before it reaches an AI, so this is
    /// what keeps a draft describing the whole change rather than only the files that fit.
    /// </summary>
    public static Task<string> GetStagedStatAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return "";

            var stat = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--cached", "--stat");
            if (string.IsNullOrWhiteSpace(stat))
                stat = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--stat");

            return stat ?? "";
        });
    }

    /// <summary>
    /// The path of each staged file, one per entry. Lets the secret scan check every file's own
    /// diff separately, so one large file elsewhere in the stage can never crowd a small one out
    /// of another file's truncation window.
    /// </summary>
    public static Task<string[]> GetStagedFilePathsAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return Array.Empty<string>();

            var names = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--cached", "--name-only");
            if (string.IsNullOrWhiteSpace(names))
                names = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--name-only");

            return (names ?? "").Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        });
    }

    /// <summary>The staged diff for one file only, truncated the same way the whole-repo diff is.</summary>
    public static Task<string> GetStagedFileDiffAsync(string repoRoot, string path)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return "";

            var diff = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--cached", "--", path);
            if (string.IsNullOrWhiteSpace(diff))
                diff = GitCli.Run(repoRoot, "-c", "core.quotepath=false", "diff", "--", path);

            return GitCli.TruncateDiff(diff ?? "");
        });
    }

    /// <summary>The subject line of the newest commit, used as the default pull-request title.</summary>
    public static Task<string> GetLastSubjectAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return "";
            return GitCli.Run(repoRoot, "log", "-1", "--pretty=%s").Trim();
        });
    }

    // ── Internals ──────────────────────────────────────────────────────

    private static bool Usable(string repoRoot)
        => !string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot);

    /// <summary>
    /// Runs one git command over a list of paths. Each goes over as a literal pathspec anchored
    /// to the top of the working tree, so a name holding '[', '*' or '?' is a name and not a
    /// pattern, and stops at the first failure so a half-applied batch is not reported as fine.
    /// </summary>
    private static Task<GitResult> RunOnPaths(string repoRoot, IReadOnlyList<string> paths, string[] verb)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (paths == null || paths.Count == 0) return GitResult.Failed("nothing selected");

            // Batched so a large selection cannot overrun the command line length limit.
            const int batchSize = 40;
            for (int start = 0; start < paths.Count; start += batchSize)
            {
                var args = new List<string>(verb);
                for (int i = start; i < Math.Min(start + batchSize, paths.Count); i++)
                    args.Add(GitCli.Pathspec(paths[i]));

                var result = GitCli.Execute(repoRoot, null, args.ToArray());
                if (!result.Ok) return result;
            }

            return new GitResult(0, "", "");
        });
    }
}
