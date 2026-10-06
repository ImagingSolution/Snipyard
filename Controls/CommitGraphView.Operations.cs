using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>The history operations the commit row's right-click menu can ask for.</summary>
public enum GraphOperation
{
    Revert,
    CherryPick,
    ResetSoft,
    ResetMixed,
    ResetHard,
    DeleteTag,
    PushTag,
    MergeBranch,
    RebaseOnto,
}

/// <summary>One menu pick: what to do, to which commit, and the tag or branch name it concerns.</summary>
public sealed record GraphOperationRequest(GraphOperation Operation, GitCommit Commit, string Name = "");

/// <summary>
/// The history-editing half of the row menu. The view only reports the pick; the host that owns
/// the repository confirms and runs it. Opening on GitHub is the one thing done here, since it
/// touches no repository.
/// </summary>
public sealed partial class CommitGraphView
{
    /// <summary>Raised when a revert, cherry-pick, reset, tag or branch operation is picked from the row menu.</summary>
    public event EventHandler<GraphOperationRequest>? OperationRequested;

    /// <summary>
    /// "owner/repo" of the GitHub remote, set by the host once known. Null hides "Open on GitHub".
    /// </summary>
    public string? GitHubSlug { get; set; }

    /// <summary>When true, no lanes or edges are drawn: a filtered list is not a connected history.</summary>
    public bool HideLines
    {
        get => _hideLines;
        set
        {
            if (_hideLines == value) return;
            _hideLines = value;
            InvalidateVisual();
        }
    }

    private bool _hideLines;

    /// <summary>The checked-out local branch, or null when HEAD is detached or unknown.</summary>
    private string? CurrentBranchName => _graph.Rows
        .Select(r => r.Node).OfType<GitCommit>()
        .SelectMany(c => c.Refs)
        .FirstOrDefault(r => r.IsHead && r.Kind == GitRefKind.LocalBranch)?.Name;

    /// <summary>Makes the row menu rebuild itself each time it opens, so it reflects the row's own refs.</summary>
    private void InitOperations()
    {
        var baseItems = ((IEnumerable<object>)(_rowContextMenu.ItemsSource ?? Array.Empty<object>())).ToList();
        _rowContextMenu.Opening += (_, _) =>
            _rowContextMenu.ItemsSource = baseItems.Concat(BuildOperationItems()).ToList();
    }

    private IEnumerable<object> BuildOperationItems()
    {
        var items = new List<object>();
        if (SelectedCommit is not { } commit) return items;

        MenuItem Item(string header, GraphOperation op, string name = "", bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => OperationRequested?.Invoke(this, new GraphOperationRequest(op, commit, name));
            return item;
        }

        items.Add(new Separator());
        items.Add(Item(Loc.Get("GraphOpRevert", "Revert Commit..."), GraphOperation.Revert));
        items.Add(Item(Loc.Get("GraphOpCherryPick", "Cherry-pick..."), GraphOperation.CherryPick));

        var reset = new MenuItem { Header = Loc.Get("GraphOpReset", "Reset Current Branch to Here") };
        reset.ItemsSource = new object[]
        {
            Item(Loc.Get("GraphOpResetSoft", "Soft (keep changes staged)"), GraphOperation.ResetSoft),
            Item(Loc.Get("GraphOpResetMixed", "Mixed (keep changes unstaged)"), GraphOperation.ResetMixed),
            Item(Loc.Get("GraphOpResetHard", "Hard (discard all changes)"), GraphOperation.ResetHard),
        };
        items.Add(reset);

        var tags = commit.Refs.Where(r => r.Kind == GitRefKind.Tag).Select(r => r.Name).ToList();
        if (tags.Count > 0)
        {
            items.Add(new Separator());
            AddTagItems(items, tags, GraphOperation.DeleteTag,
                "GraphOpDeleteTag", "Delete Tag", "GraphOpDeleteTagFmt", "Delete Tag \"{0}\"...", Item);
            AddTagItems(items, tags, GraphOperation.PushTag,
                "GraphOpPushTag", "Push Tag", "GraphOpPushTagFmt", "Push Tag \"{0}\"...", Item);
        }

        var current = CurrentBranchName;
        var branches = commit.Refs
            .Where(r => r.Kind == GitRefKind.LocalBranch && !r.IsHead && r.Name != current)
            .Select(r => r.Name).ToList();
        if (branches.Count > 0)
        {
            items.Add(new Separator());
            bool canMove = current != null;
            foreach (var branch in branches)
            {
                items.Add(Item(string.Format(Loc.Get("GraphOpMergeFmt", "Merge \"{0}\" into the Current Branch"), branch),
                    GraphOperation.MergeBranch, branch, canMove));
                items.Add(Item(string.Format(Loc.Get("GraphOpRebaseFmt", "Rebase the Current Branch onto \"{0}\"..."), branch),
                    GraphOperation.RebaseOnto, branch, canMove));
            }
        }

        if (!string.IsNullOrEmpty(GitHubSlug))
        {
            items.Add(new Separator());
            var open = new MenuItem { Header = Loc.Get("GraphOpOpenOnGitHub", "Open Commit on GitHub") };
            var slug = GitHubSlug;
            open.Click += (_, _) => OpenInBrowser(GitHistoryService.GitHubCommitUrl(slug!, commit.Hash));
            items.Add(open);
        }

        return items;
    }

    private static void AddTagItems(List<object> items, List<string> tags, GraphOperation op,
        string groupKey, string groupFallback, string itemKey, string itemFallback,
        Func<string, GraphOperation, string, bool, MenuItem> make)
    {
        if (tags.Count == 1)
        {
            items.Add(make(string.Format(Loc.Get(itemKey, itemFallback), tags[0]), op, tags[0], true));
            return;
        }

        var group = new MenuItem { Header = Loc.Get(groupKey, groupFallback) };
        group.ItemsSource = tags.Select(t => (object)make(t, op, t, true)).ToList();
        items.Add(group);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No browser association, or the user cancelled the shell prompt.
        }
    }
}
