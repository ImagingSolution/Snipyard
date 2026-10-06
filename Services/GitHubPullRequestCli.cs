using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Snipyard.Services;

/// <summary>How `gh pr merge` should fold a pull request in.</summary>
public enum PrMergeMethod { Merge, Squash, Rebase }

/// <summary>
/// The write half of the pull-request actions (merge, request changes, comment, check out),
/// driven through gh like <see cref="GitHubCli"/>. Each command's argument list is built by a
/// separate pure function so it can be checked without touching GitHub.
/// </summary>
public static class GitHubPullRequestCli
{
    private const int TimeoutMs = 45_000;

    // ── Argument builders ──────────────────────────────────────────────

    public static string[] MergeArgs(int number, PrMergeMethod method, bool deleteBranch)
    {
        var args = new List<string> { "pr", "merge", number.ToString(),
            method switch
            {
                PrMergeMethod.Squash => "--squash",
                PrMergeMethod.Rebase => "--rebase",
                _ => "--merge",
            } };
        if (deleteBranch) args.Add("--delete-branch");
        return args.ToArray();
    }

    public static string[] RequestChangesArgs(int number, string body) =>
        new[] { "pr", "review", number.ToString(), "--request-changes", "--body", body };

    public static string[] CommentArgs(int number, string body) =>
        new[] { "pr", "comment", number.ToString(), "--body", body };

    public static string[] CheckoutArgs(int number) =>
        new[] { "pr", "checkout", number.ToString() };

    // ── Runners ────────────────────────────────────────────────────────

    public static Task<GitResult> MergeAsync(string repoRoot, int number, PrMergeMethod method, bool deleteBranch) =>
        RunAsync(repoRoot, MergeArgs(number, method, deleteBranch));

    public static Task<GitResult> RequestChangesAsync(string repoRoot, int number, string body) =>
        RunAsync(repoRoot, RequestChangesArgs(number, body));

    public static Task<GitResult> CommentAsync(string repoRoot, int number, string body) =>
        RunAsync(repoRoot, CommentArgs(number, body));

    public static Task<GitResult> CheckoutAsync(string repoRoot, int number) =>
        RunAsync(repoRoot, CheckoutArgs(number));

    private static Task<GitResult> RunAsync(string repoRoot, string[] args) =>
        Task.Run(() => ProcessRunner.Run("gh", repoRoot, null, TimeoutMs, null, args));

    // ── Repository page ────────────────────────────────────────────────

    /// <summary>
    /// The https page for a git remote URL (https, scp-style ssh or ssh://), or "" when it is
    /// not a recognisable host/owner/repo address.
    /// </summary>
    public static string RepoWebUrl(string remoteUrl)
    {
        var url = (remoteUrl ?? "").Trim();
        if (url.Length == 0) return "";

        var m = Regex.Match(url, @"^(?:[a-z+]+://)?(?:[^@/]+@)?(?<host>[^/:]+)(?::\d+)?[:/](?<path>[^/].*?)(?:\.git)?/?$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return "";

        var path = m.Groups["path"].Value;
        if (!path.Contains('/')) return "";
        return "https://" + m.Groups["host"].Value + "/" + path;
    }

    /// <summary>The repository's GitHub page, built from the origin (or first) remote. "" if none.</summary>
    public static Task<string> GetRepoWebUrlAsync(string repoRoot)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrEmpty(repoRoot)) return "";
            var origin = GitCli.Run(repoRoot, "remote", "get-url", "origin").Trim();
            if (origin.Length > 0) return RepoWebUrl(origin);

            var remotes = GitCli.Run(repoRoot, "remote").Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (remotes.Length == 0) return "";
            return RepoWebUrl(GitCli.Run(repoRoot, "remote", "get-url", remotes[0]).Trim());
        });
    }
}
