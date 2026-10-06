using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>One entry of <c>git stash list</c>.</summary>
public sealed record StashEntry(int Index, string Ref, string Branch, string Message);

/// <summary>
/// The writes that act on the working tree itself rather than on history: throwing changes
/// away, amending the last commit, and the stash stack. Kept apart from GitWriteService so the
/// panel features built on it do not share a file with the rest of the git plumbing.
///
/// Everything here is destructive in one way or another, so none of it is called without the
/// panel having asked the user first.
/// </summary>
public static class GitWorkingTreeService
{
    private static bool Usable(string repoRoot)
        => !string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot);

    // ── Discarding changes ─────────────────────────────────────────────

    /// <summary>
    /// Throws the given changes away. Which command depends on what git knows about the file:
    /// an untracked path is deleted with <c>clean</c> (a directory row such as <c>dir/</c> with
    /// <c>-fd</c>), a file that is new in the index is removed with <c>rm -f</c> because there
    /// is no HEAD version to go back to, and everything else is restored from HEAD - the working
    /// tree only when nothing is staged, index and working tree together when something is.
    /// None of it can be undone.
    /// </summary>
    public static Task<GitResult> DiscardAsync(string repoRoot, IReadOnlyList<GitChange> changes)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (changes == null || changes.Count == 0) return GitResult.Failed("nothing selected");

            var restoreWorktree = new List<string>();
            var restoreBoth = new List<string>();
            var removeAdded = new List<string>();
            var cleanFiles = new List<string>();
            var cleanDirs = new List<string>();

            foreach (var c in changes)
            {
                if (c.Untracked)
                {
                    if (c.Path.EndsWith("/", StringComparison.Ordinal)) cleanDirs.Add(c.Path);
                    else cleanFiles.Add(c.Path);
                }
                else if (c.StatusCode.Length >= 1 && (c.StatusCode[0] == 'A' || c.StatusCode[0] == 'R'
                         || c.StatusCode[0] == 'C'))
                    removeAdded.Add(c.Path);
                else if (c.Staged) restoreBoth.Add(c.Path);
                else restoreWorktree.Add(c.Path);
            }

            GitResult last = new(0, "", "");
            GitResult? Step(string[] verb, List<string> paths)
            {
                for (int start = 0; start < paths.Count; start += 40)
                {
                    var args = new List<string>(verb);
                    foreach (var p in paths.Skip(start).Take(40)) args.Add(GitCli.Pathspec(p));
                    last = GitCli.Execute(repoRoot, null, args.ToArray());
                    if (!last.Ok) return last;
                }
                return null;
            }

            return Step(new[] { "restore", "--" }, restoreWorktree)
                ?? Step(new[] { "restore", "--staged", "--worktree", "--source=HEAD", "--" }, restoreBoth)
                ?? Step(new[] { "rm", "-f", "-q", "--" }, removeAdded)
                ?? Step(new[] { "clean", "-f", "-q", "--" }, cleanFiles)
                ?? Step(new[] { "clean", "-f", "-d", "-q", "--" }, cleanDirs)
                ?? last;
        });
    }

    // ── Amend ──────────────────────────────────────────────────────────

    /// <summary>True when HEAD resolves to a commit - a fresh repository has none to amend.</summary>
    public static Task<bool> HasCommitAsync(string repoRoot)
    {
        return Task.Run(() => Usable(repoRoot)
            && GitCli.Execute(repoRoot, null, "rev-parse", "--verify", "-q", "HEAD").Ok);
    }

    /// <summary>The full message of HEAD, without the trailing newline git adds.</summary>
    public static Task<string> GetHeadMessageAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return "";
            var r = GitCli.Execute(repoRoot, null, "log", "-1", "--format=%B");
            return r.Ok ? r.StdOut.Replace("\r\n", "\n").TrimEnd() : "";
        });
    }

    /// <summary>
    /// True when HEAD is already on some remote branch, so amending it makes the local branch
    /// diverge from what others may have pulled and the next push has to be forced.
    /// </summary>
    public static Task<bool> IsHeadPublishedAsync(string repoRoot, bool hasUpstream, int ahead)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return false;
            var r = GitCli.Execute(repoRoot, null, "branch", "-r", "--contains", "HEAD");
            if (r.Ok && r.StdOut.Trim().Length > 0) return true;
            return hasUpstream && ahead == 0;
        });
    }

    /// <summary>
    /// Replaces the last commit with one holding what is staged now and the given message. The
    /// message goes over stdin, as for an ordinary commit. Nothing needs to be staged: amending
    /// only the message is allowed.
    /// </summary>
    public static Task<GitResult> AmendAsync(string repoRoot, string message)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(message)) return GitResult.Failed("empty message");

            return GitCli.Execute(repoRoot, message, "commit", "--amend", "-F", "-");
        });
    }

    // ── Stash ──────────────────────────────────────────────────────────

    private static readonly Regex StashSubject =
        new(@"^(?:WIP on|On) ([^:]+): (.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex StashRef =
        new(@"^stash@\{(\d+)\}$", RegexOptions.Compiled);

    public static Task<List<StashEntry>> ListStashAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var list = new List<StashEntry>();
            if (!Usable(repoRoot)) return list;

            var r = GitCli.Execute(repoRoot, null, "stash", "list", "--format=%gd%x1f%gs");
            if (!r.Ok) return list;

            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var parts = line.Split('\x1f');
                if (parts.Length < 2) continue;

                var m = StashRef.Match(parts[0]);
                if (!m.Success) continue;

                string branch = "", message = parts[1];
                var s = StashSubject.Match(parts[1]);
                if (s.Success)
                {
                    branch = s.Groups[1].Value;
                    message = s.Groups[2].Value;
                }
                list.Add(new StashEntry(int.Parse(m.Groups[1].Value), parts[0], branch, message));
            }
            return list;
        });
    }

    /// <summary>
    /// Sets the working tree's changes aside, untracked files included. An empty message leaves
    /// git's own "WIP on branch" wording. When there is nothing to save git still exits 0, so the
    /// caller can read <see cref="NothingToStash"/> off the result.
    /// </summary>
    public static Task<GitResult> StashPushAsync(string repoRoot, string message)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");

            return string.IsNullOrWhiteSpace(message)
                ? GitCli.Execute(repoRoot, null, "stash", "push", "-u")
                : GitCli.Execute(repoRoot, null, "stash", "push", "-u", "-m", message.Trim());
        });
    }

    public static bool NothingToStash(GitResult result)
        => result.Ok && (result.StdOut + result.StdErr)
            .Contains("No local changes to save", StringComparison.OrdinalIgnoreCase);

    public static Task<GitResult> StashApplyAsync(string repoRoot, string stashRef)
        => StashVerb(repoRoot, "apply", stashRef);

    public static Task<GitResult> StashPopAsync(string repoRoot, string stashRef)
        => StashVerb(repoRoot, "pop", stashRef);

    public static Task<GitResult> StashDropAsync(string repoRoot, string stashRef)
        => StashVerb(repoRoot, "drop", stashRef);

    private static Task<GitResult> StashVerb(string repoRoot, string verb, string stashRef)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            // Only stash@{n}: the ref is passed to git as an argument and must not read as an option.
            if (!StashRef.IsMatch(stashRef ?? "")) return GitResult.Failed("invalid stash");

            return GitCli.Execute(repoRoot, null, "stash", verb, stashRef!);
        });
    }

    /// <summary>
    /// The files one stash holds, as "M  path" style lines (status letter, then the path). The
    /// untracked half of a <c>-u</c> stash is included where git is new enough to show it.
    /// </summary>
    public static Task<List<string>> GetStashFilesAsync(string repoRoot, string stashRef)
    {
        return Task.Run(() =>
        {
            var files = new List<string>();
            if (!Usable(repoRoot) || !StashRef.IsMatch(stashRef ?? "")) return files;

            var r = GitCli.Execute(repoRoot, null, "-c", "core.quotepath=false",
                "stash", "show", "--include-untracked", "--name-status", stashRef!);
            if (!r.Ok)
                r = GitCli.Execute(repoRoot, null, "-c", "core.quotepath=false",
                    "stash", "show", "--name-status", stashRef!);
            if (!r.Ok) return files;

            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0) files.Add(line.Replace('\t', ' '));
            }
            return files;
        });
    }
}
