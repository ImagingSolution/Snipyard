using System;
using System.Collections.Generic;

namespace Snipyard.Services;

/// <summary>
/// Turns git's raw refusal into something a user can act on: recognises the common failures
/// by the words git prints and puts a localized "what to do" above git's own output.
/// </summary>
public static class GitErrorHints
{
    // Order matters: the first match wins, so the more specific phrases come first.
    private static readonly (string[] Needles, string Key)[] Rules =
    {
        (new[] { "non-fast-forward", "fetch first", "tip of your current branch is behind" }, "GitHintPushBehind"),
        (new[] { "has no upstream branch", "no upstream configured" }, "GitHintNoUpstream"),
        (new[] { "Authentication failed", "could not read Username", "Permission denied (publickey)", "error: 403" }, "GitHintAuth"),
        (new[] { "Could not resolve host", "unable to access", "Connection timed out", "Failed to connect" }, "GitHintNetwork"),
        (new[] { "would be overwritten by merge", "would be overwritten by checkout", "Please commit your changes or stash them" }, "GitHintLocalChanges"),
        (new[] { "CONFLICT", "Merge conflict", "fix conflicts", "unmerged files" }, "GitHintConflict"),
        (new[] { "Not possible to fast-forward", "divergent branches", "Need to specify how to reconcile" }, "GitHintDiverged"),
        (new[] { "index.lock" }, "GitHintIndexLock"),
        (new[] { "nothing to commit" }, "GitHintNothingToCommit"),
        (new[] { "Please tell me who you are", "unable to auto-detect email" }, "GitHintIdentity"),
        (new[] { "does not appear to be a git repository", "Repository not found" }, "GitHintNoRemote"),
    };

    /// <summary>The hint for this git output, or null when it is not one we recognise.</summary>
    public static string? Find(string gitMessage)
    {
        if (string.IsNullOrWhiteSpace(gitMessage)) return null;
        foreach (var (needles, key) in Rules)
            foreach (var needle in needles)
                if (gitMessage.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return string.Format(Loc.Get(key), Loc.Get("PullAction"), Loc.Get("PushAction"));
        return null;
    }

    /// <summary>Whether git refused because uncommitted edits would be overwritten.</summary>
    public static bool IsLocalChanges(string gitMessage) =>
        !string.IsNullOrEmpty(gitMessage)
        && (gitMessage.Contains("Your local changes to the following files would be overwritten", StringComparison.OrdinalIgnoreCase)
            || gitMessage.Contains("cannot pull with rebase: You have unstaged changes", StringComparison.OrdinalIgnoreCase)
            || gitMessage.Contains("Your index contains uncommitted changes", StringComparison.OrdinalIgnoreCase));

    /// <summary>The indented file list git prints under "would be overwritten by ...:".</summary>
    public static List<string> LocalChangeFiles(string gitMessage)
    {
        var files = new List<string>();
        bool inList = false;
        foreach (var raw in gitMessage.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("would be overwritten by", StringComparison.OrdinalIgnoreCase)) { inList = true; continue; }
            if (!inList) continue;
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) break;
            files.Add(line.Trim());
        }
        return files;
    }

    /// <summary>
    /// The dialog body for a failed git call: the hint (when there is one) followed by git's
    /// own words, so nothing git said is lost.
    /// </summary>
    public static string Describe(string gitMessage)
    {
        var detail = gitMessage?.Trim() ?? "";
        if (detail.Length == 0) return Loc.Get("GitFailedTitle");

        var hint = Find(detail);
        return hint == null
            ? detail
            : hint + Environment.NewLine + Environment.NewLine
                   + Loc.Get("GitHintRawOutput") + Environment.NewLine + detail;
    }
}
