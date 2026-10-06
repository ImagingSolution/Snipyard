using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>
/// Branch operations beyond create / switch / delete -d: renaming, forced deletion of an
/// unmerged branch, and deleting a branch on a remote. Kept apart from GitWriteService so the
/// two can change independently.
/// </summary>
public static class GitBranchService
{
    private static bool Usable(string repoRoot)
        => !string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot);

    /// <summary>True when git accepts <paramref name="name"/> as a branch name.</summary>
    public static Task<bool> IsValidBranchNameAsync(string repoRoot, string name)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot) || string.IsNullOrWhiteSpace(name) || name.StartsWith("-", StringComparison.Ordinal))
                return false;
            return GitCli.Execute(repoRoot, null, "check-ref-format", "--branch", name).Ok;
        });
    }

    /// <summary>True when the local branch tracks an upstream.</summary>
    public static Task<bool> HasUpstreamAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return false;
            return GitCli.Execute(repoRoot, null, "rev-parse", "--abbrev-ref", "--symbolic-full-name",
                $"{branch}@{{upstream}}").Ok;
        });
    }

    /// <summary>
    /// Renames a local branch. The remote is untouched. The note CreateBranchAsync keeps of
    /// where a branch started (branch.&lt;name&gt;.snipyard-base) travels with the branch, because
    /// `git branch -m` renames the whole config section; other branches that were started
    /// from the old name are repointed at the new one.
    /// </summary>
    public static Task<GitResult> RenameBranchAsync(string repoRoot, string oldName, string newName)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                return GitResult.Failed("no branch given");
            if (newName.StartsWith("-", StringComparison.Ordinal)) return GitResult.Failed("invalid branch name");

            var result = GitCli.Execute(repoRoot, null, "branch", "-m", "--", oldName, newName);
            if (!result.Ok) return result;

            var key = GitWriteService.BaseBranchConfigKey;

            // git normally moves branch.<old>.* itself; copy the note over if it did not.
            var own = GitCli.Execute(repoRoot, null, "config", "--local", "--get", $"branch.{newName}.{key}");
            if (!own.Ok)
            {
                var oldNote = GitCli.Execute(repoRoot, null, "config", "--local", "--get", $"branch.{oldName}.{key}");
                if (oldNote.Ok && oldNote.StdOut.Trim().Length > 0)
                {
                    GitCli.Execute(repoRoot, null, "config", "--local", $"branch.{newName}.{key}", oldNote.StdOut.Trim());
                    GitCli.Execute(repoRoot, null, "config", "--local", "--unset", $"branch.{oldName}.{key}");
                }
            }

            // Branches that were started from the old name now start from the new one.
            var all = GitCli.Execute(repoRoot, null, "config", "--local", "--get-regexp", @"^branch\..*\." + key + "$");
            if (all.Ok)
            {
                foreach (var line in all.StdOut.Split('\n'))
                {
                    var t = line.Trim();
                    int sp = t.IndexOf(' ');
                    if (sp <= 0) continue;
                    if (t[(sp + 1)..].Trim() != oldName) continue;
                    GitCli.Execute(repoRoot, null, "config", "--local", t[..sp], newName);
                }
            }
            return result;
        });
    }

    /// <summary>
    /// How many commits on <paramref name="branch"/> are not reachable from
    /// <paramref name="against"/> (usually the current branch) - what a forced delete would
    /// leave unreferenced, give or take commits other branches also hold.
    /// </summary>
    public static Task<int> CountUnmergedCommitsAsync(string repoRoot, string branch, string against)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return 0;
            var args = string.IsNullOrEmpty(against)
                ? new[] { "rev-list", "--count", branch, "--" }
                : new[] { "rev-list", "--count", branch, "--not", against, "--" };
            return int.TryParse(GitCli.Run(repoRoot, args).Trim(), out var n) ? n : 0;
        });
    }

    /// <summary>Deletes a local branch with -D, discarding whatever is not merged.</summary>
    public static Task<GitResult> ForceDeleteBranchAsync(string repoRoot, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(branch)) return GitResult.Failed("no branch given");
            return GitCli.Execute(repoRoot, null, "branch", "-D", "--", branch);
        });
    }

    /// <summary>True when git's refusal to delete is the "not fully merged" one.</summary>
    public static bool IsNotFullyMerged(GitResult result)
        => !result.Ok && result.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remote-tracking branches as (remote, branch) pairs, e.g. ("origin", "feature/x").
    /// The symbolic HEAD of each remote is left out.
    /// </summary>
    public static Task<List<(string Remote, string Branch)>> GetRemoteBranchesAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            var list = new List<(string, string)>();
            if (!Usable(repoRoot)) return list;

            var remotes = GitCli.Run(repoRoot, "remote").Split('\n')
                .Select(r => r.Trim()).Where(r => r.Length > 0)
                // Longest first so a remote named "a/b" wins over "a".
                .OrderByDescending(r => r.Length).ToList();
            var refs = GitCli.Run(repoRoot, "for-each-ref", "--format=%(refname)", "refs/remotes/");

            foreach (var line in refs.Split('\n'))
            {
                var r = line.Trim();
                const string prefix = "refs/remotes/";
                if (!r.StartsWith(prefix, StringComparison.Ordinal)) continue;
                r = r[prefix.Length..];
                if (r.EndsWith("/HEAD", StringComparison.Ordinal)) continue;

                var remote = remotes.FirstOrDefault(x => r.StartsWith(x + "/", StringComparison.Ordinal));
                if (remote == null) continue;
                var branch = r[(remote.Length + 1)..];
                if (branch.Length > 0) list.Add((remote, branch));
            }
            return list;
        });
    }

    /// <summary>
    /// Deletes a branch on the remote (`git push &lt;remote&gt; --delete &lt;branch&gt;`). Git also
    /// drops the local remote-tracking ref when the push succeeds.
    /// </summary>
    public static Task<GitResult> DeleteRemoteBranchAsync(string repoRoot, string remote, string branch)
    {
        return Task.Run(() =>
        {
            if (!Usable(repoRoot)) return GitResult.Failed("not a repository");
            if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(branch))
                return GitResult.Failed("no branch given");
            if (remote.StartsWith("-", StringComparison.Ordinal) || branch.StartsWith("-", StringComparison.Ordinal))
                return GitResult.Failed("invalid name");
            return GitCli.ExecuteRemote(repoRoot, null, "push", remote, "--delete", "--", branch);
        });
    }
}
