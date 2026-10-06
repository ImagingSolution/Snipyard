using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Snipyard.Services;

namespace Snipyard.Controls;

// Branch operations beyond create / switch / delete: rename, forced delete, remote delete.
public sealed partial class SourceControlPanel
{
    /// <summary>Adds "Rename branch..." and "Delete remote branch..." to the branch menu.</summary>
    private async Task AddBranchExtraItemsAsync(MenuFlyout flyout, List<string> branches, string current)
    {
        var repo = _repo;

        if (branches.Count > 0)
        {
            // Every local branch can be renamed; the current one is only absent when HEAD is
            // detached, in which case it is not in the list at all.
            var renameItems = new List<MenuItem>();
            foreach (var branch in branches)
            {
                var name = branch;
                var item = new MenuItem { Header = name == current ? "✓ " + name : name };
                item.Click += (_, _) => RenameBranch(name);
                renameItems.Add(item);
            }
            flyout.Items.Add(new MenuItem
            {
                Header = Loc.Get("RenameBranch", "Rename branch..."),
                ItemsSource = renameItems,
            });
        }

        var remoteBranches = await GitBranchService.GetRemoteBranchesAsync(repo);
        if (remoteBranches.Count > 0)
        {
            var items = new List<MenuItem>();
            foreach (var (remote, branch) in remoteBranches)
            {
                var r = remote;
                var b = branch;
                var item = new MenuItem { Header = r + "/" + b };
                item.Click += (_, _) => DeleteRemoteBranch(r, b);
                items.Add(item);
            }
            flyout.Items.Add(new MenuItem
            {
                Header = Loc.Get("DeleteRemoteBranch", "Delete remote branch..."),
                ItemsSource = items,
            });
        }
    }

    private async void RenameBranch(string oldName)
    {
        if (_repo.Length == 0 || _busy) return;
        var repo = _repo;

        var input = await _host.TextInput(Loc.Get("RenameBranch", "Rename branch..."),
            string.Format(Loc.Get("RenameBranchPromptFmt", "New name for \"{0}\""), oldName), oldName);
        var newName = input?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == oldName) return;

        if (!await GitBranchService.IsValidBranchNameAsync(repo, newName))
        {
            _host.ShowMessage(Loc.Get("RenameBranch", "Rename branch..."),
                string.Format(Loc.Get("RenameBranchInvalidFmt", "\"{0}\" is not a valid branch name."), newName));
            return;
        }

        bool hadUpstream = await GitBranchService.HasUpstreamAsync(repo, oldName);

        bool ok = await RunAsync(Loc.Get("RenamingBranchStatus", "Renaming..."),
            () => GitBranchService.RenameBranchAsync(repo, oldName, newName));
        if (ok && hadUpstream)
            _host.ShowMessage(Loc.Get("RenameBranch", "Rename branch..."),
                string.Format(Loc.Get("RenameBranchDoneUpstreamFmt",
                    "Renamed \"{0}\" to \"{1}\". The branch on the remote keeps its old name; push the new name to publish it."),
                    oldName, newName));
    }

    /// <summary>
    /// The body of DeleteBranch once the user has agreed: plain -d first, and when git says the
    /// branch is not fully merged, say how many commits would be lost and offer -D.
    /// </summary>
    private async Task DeleteLocalBranchAsync(string branch)
    {
        var repo = _repo;
        GitResult? refused = null;

        await RunAsync(Loc.Get("DeletingBranchStatus", "Deleting..."), async () =>
        {
            var result = await GitWriteService.DeleteBranchAsync(repo, branch);
            if (GitBranchService.IsNotFullyMerged(result))
            {
                // Handled below with its own dialog, so RunAsync is told it went fine.
                refused = result;
                return new GitResult(0, "", "");
            }
            return result;
        });

        if (refused == null) return;

        var (_, current) = await GitWriteService.GetBranchesAsync(repo);
        int lost = await GitBranchService.CountUnmergedCommitsAsync(repo, branch, current);

        if (!await _host.Confirm(
                Loc.Get("ForceDeleteBranchTitle", "Force delete branch"),
                string.Format(Loc.Get("ForceDeleteBranchFmt",
                    "\"{0}\" has {1} commit(s) that are not merged into the current branch. Deleting it anyway loses them for good (they can only be recovered through the reflog for a while). Force delete?"),
                    branch, lost)))
            return;

        await RunAsync(Loc.Get("DeletingBranchStatus", "Deleting..."),
            () => GitBranchService.ForceDeleteBranchAsync(repo, branch));
    }

    private async void DeleteRemoteBranch(string remote, string branch)
    {
        if (_repo.Length == 0 || _busy) return;
        var repo = _repo;

        if (!await _host.Confirm(
                Loc.Get("DeleteRemoteBranchTitle", "Delete remote branch"),
                string.Format(Loc.Get("DeleteRemoteBranchFmt",
                    "Delete the branch \"{1}\" on the remote \"{0}\"? This changes the shared repository and affects everyone who uses it. It cannot be undone from here."),
                    remote, branch)))
            return;

        await RunAsync(Loc.Get("DeletingRemoteBranchStatus", "Deleting on the remote..."),
            () => GitBranchService.DeleteRemoteBranchAsync(repo, remote, branch));
    }
}
