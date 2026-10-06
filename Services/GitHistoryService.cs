using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>How far a reset moves the index and the working tree along with HEAD.</summary>
public enum ResetMode
{
    Soft,
    Mixed,
    Hard,
}

/// <summary>What a reset to a given commit would cost, for the confirmation dialog.</summary>
/// <param name="UncommittedFiles">Files with uncommitted changes a hard reset would discard.</param>
/// <param name="CommitsDropped">Commits on HEAD that the target does not contain.</param>
/// <param name="NeedsForcePush">True when the branch would no longer be a fast-forward of its upstream.</param>
public sealed record ResetImpact(int UncommittedFiles, int CommitsDropped, bool NeedsForcePush);

/// <summary>
/// History-editing git calls behind the commit graph's right-click menu: revert, cherry-pick,
/// reset, tag deletion and publishing, and rebase. Every call reports what git said as a
/// <see cref="GitResult"/>; confirmation is the caller's job and has already happened by the
/// time anything here runs. Hashes and names are validated so none can be read as an option.
/// </summary>
public static class GitHistoryService
{
    private static readonly Dictionary<string, string> NoPromptEnv = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    private static bool Usable(string repoRoot)
        => !string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot);

    private static bool SafeArg(string value)
        => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal);

    /// <summary>
    /// Reverts one commit with a new commit. A merge commit is reverted against its first parent
    /// (-m 1), the mainline of the branch it was merged into.
    /// </summary>
    public static Task<GitResult> RevertAsync(string repoRoot, string hash, bool isMerge)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(hash)) return GitResult.Failed("invalid commit");

            return isMerge
                ? GitCli.Execute(repoRoot, null, "revert", "--no-edit", "-m", "1", hash)
                : GitCli.Execute(repoRoot, null, "revert", "--no-edit", hash);
        });
    }

    /// <summary>Applies one commit on top of HEAD. A conflict leaves git's cherry-pick in progress.</summary>
    public static Task<GitResult> CherryPickAsync(string repoRoot, string hash, bool isMerge = false)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(hash)) return GitResult.Failed("invalid commit");

            return isMerge
                ? GitCli.Execute(repoRoot, null, "cherry-pick", "-m", "1", hash)
                : GitCli.Execute(repoRoot, null, "cherry-pick", hash);
        });
    }

    /// <summary>Moves the current branch to <paramref name="hash"/>.</summary>
    public static Task<GitResult> ResetAsync(string repoRoot, string hash, ResetMode mode)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(hash)) return GitResult.Failed("invalid commit");

            var flag = mode switch
            {
                ResetMode.Soft => "--soft",
                ResetMode.Hard => "--hard",
                _ => "--mixed",
            };
            return GitCli.Execute(repoRoot, null, "reset", flag, hash);
        });
    }

    /// <summary>
    /// Measures what resetting to <paramref name="hash"/> would throw away, so the dialog can
    /// name it: uncommitted files, commits that fall off the branch, and whether the result
    /// could no longer be pushed without force.
    /// </summary>
    public static Task<ResetImpact> GetResetImpactAsync(string repoRoot, string hash)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot) || !SafeArg(hash)) return new ResetImpact(0, 0, false);

            var status = GitCli.Run(repoRoot, "status", "--porcelain");
            int files = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            int dropped = 0;
            var count = GitCli.Execute(repoRoot, null, "rev-list", "--count", hash + "..HEAD");
            if (count.Ok) int.TryParse(count.StdOut.Trim(), out dropped);

            bool force = false;
            var upstream = GitCli.Execute(repoRoot, null, "rev-parse", "--verify", "--quiet", "@{u}");
            if (upstream.Ok && upstream.StdOut.Trim().Length > 0)
            {
                // The branch can still fast-forward only if the target contains everything upstream has.
                var contains = GitCli.Execute(repoRoot, null, "merge-base", "--is-ancestor", upstream.StdOut.Trim(), hash);
                force = contains.ExitCode == 1;
            }

            return new ResetImpact(files, dropped, force);
        });
    }

    /// <summary>The remote tags go to: origin when there is one, otherwise the first listed. Null with none.</summary>
    public static Task<string?> GetDefaultRemoteAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return null;
            var remotes = GitCli.Run(repoRoot, "remote")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (remotes.Length == 0) return null;
            return remotes.Contains("origin") ? "origin" : remotes[0];
        });
    }

    /// <summary>Whether <paramref name="remote"/> already has the tag. Any failure reads as "no".</summary>
    public static Task<bool> RemoteHasTagAsync(string repoRoot, string remote, string tag)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot) || !SafeArg(remote) || !SafeArg(tag)) return false;
            var result = GitCli.ExecuteRemote(repoRoot, NoPromptEnv, "ls-remote", "--tags", remote, "refs/tags/" + tag);
            return result.Ok && result.StdOut.Trim().Length > 0;
        });
    }

    /// <summary>Deletes a local tag. The commit it pointed at is untouched.</summary>
    public static Task<GitResult> DeleteLocalTagAsync(string repoRoot, string tag)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(tag)) return GitResult.Failed("invalid tag");
            return GitCli.Execute(repoRoot, null, "tag", "-d", tag);
        });
    }

    /// <summary>Removes a tag from the remote. Spelled with the full ref so a branch of the same name is safe.</summary>
    public static Task<GitResult> DeleteRemoteTagAsync(string repoRoot, string remote, string tag)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(remote) || !SafeArg(tag)) return GitResult.Failed("invalid tag");
            return GitCli.ExecuteRemote(repoRoot, null, "push", remote, "--delete", "refs/tags/" + tag);
        });
    }

    /// <summary>Publishes one tag.</summary>
    public static Task<GitResult> PushTagAsync(string repoRoot, string remote, string tag)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(remote) || !SafeArg(tag)) return GitResult.Failed("invalid tag");
            return GitCli.ExecuteRemote(repoRoot, null, "push", remote, "refs/tags/" + tag);
        });
    }

    /// <summary>Replays the current branch on top of <paramref name="branch"/>. A stop leaves a rebase in progress.</summary>
    public static Task<GitResult> RebaseAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (!SafeArg(branch)) return GitResult.Failed("invalid branch");
            return GitCli.Execute(repoRoot, null, "rebase", branch);
        });
    }

    private static readonly Regex GitHubRemote = new(
        @"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// "owner/repo" of the repository's GitHub remote, or null when it has none. Read from the
    /// local remote list, so it works offline.
    /// </summary>
    public static Task<string?> GetGitHubSlugAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return null;

            var urls = GitCli.Run(repoRoot, "remote", "-v")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.EndsWith("(fetch)", StringComparison.Ordinal))
                .Select(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2)
                .OrderBy(p => p[0] == "origin" ? 0 : 1)
                .Select(p => p[1]);

            foreach (var url in urls)
            {
                var m = GitHubRemote.Match(url);
                if (m.Success) return m.Groups["owner"].Value + "/" + m.Groups["repo"].Value;
            }
            return null;
        });
    }

    /// <summary>The page for one commit on GitHub.</summary>
    public static string GitHubCommitUrl(string slug, string hash)
        => "https://github.com/" + slug + "/commit/" + hash;
}
