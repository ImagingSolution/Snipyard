using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// The confirm-then-run sequence behind the commit graph's history operations, shared by the
/// window and the sidebar so both ask the same questions in the same words. What differs between
/// them - how a git call is run and reported, and who may ask - is handed in.
/// </summary>
internal sealed class CommitGraphOperationFlow
{
    private readonly Func<string> _repo;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string, string> _showMessage;

    /// <summary>Runs one git call with the host's busy state, error dialog and reload. (status, work, quietOnConflict)</summary>
    private readonly Func<string, Func<Task<GitResult>>, bool, Task> _run;

    private readonly Func<bool> _isBusy;

    public CommitGraphOperationFlow(Func<string> repo, Func<string, string, Task<bool>> confirm,
        Action<string, string> showMessage, Func<string, Func<Task<GitResult>>, bool, Task> run, Func<bool> isBusy)
    {
        _repo = repo;
        _confirm = confirm;
        _showMessage = showMessage;
        _run = run;
        _isBusy = isBusy;
    }

    private static string Fmt(string key, string fallback, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Loc.Get(key, fallback), args);

    public async Task HandleAsync(GraphOperationRequest request)
    {
        var repo = _repo();
        if (repo.Length == 0 || _isBusy()) return;

        var commit = request.Commit;
        var what = commit.ShortHash + " " + commit.Subject;

        switch (request.Operation)
        {
            case GraphOperation.Revert:
            {
                var text = Fmt("GraphOpRevertConfirmFmt",
                    "Create a new commit that undoes {0}?", what);
                if (commit.IsMerge) text += Environment.NewLine + Environment.NewLine +
                    Loc.Get("GraphOpRevertMergeNote", "This is a merge commit; it is reverted against its first parent.");
                if (!await _confirm(Loc.Get("GraphOpRevertConfirmTitle", "Revert"), text)) return;

                await _run(Loc.Get("GraphOpRevertingStatus", "Reverting..."),
                    () => GitHistoryService.RevertAsync(repo, commit.Hash, commit.IsMerge), true);
                break;
            }

            case GraphOperation.CherryPick:
            {
                if (!await _confirm(Loc.Get("GraphOpCherryPickConfirmTitle", "Cherry-pick"),
                        Fmt("GraphOpCherryPickConfirmFmt", "Apply {0} on top of the current branch?", what)))
                    return;

                await _run(Loc.Get("GraphOpCherryPickingStatus", "Cherry-picking..."),
                    () => GitHistoryService.CherryPickAsync(repo, commit.Hash, commit.IsMerge), true);
                break;
            }

            case GraphOperation.ResetSoft:
            case GraphOperation.ResetMixed:
            case GraphOperation.ResetHard:
                await ResetAsync(repo, request);
                break;

            case GraphOperation.DeleteTag:
                await DeleteTagAsync(repo, request.Name);
                break;

            case GraphOperation.PushTag:
                await PushTagAsync(repo, request.Name);
                break;

            case GraphOperation.MergeBranch:
            {
                var (_, current) = await GitWriteService.GetBranchesAsync(repo);
                if (string.IsNullOrEmpty(current))
                {
                    _showMessage(Loc.Get("MergeAction", "Merge"),
                        Loc.Get("GraphOpNeedsBranch", "HEAD is detached. Check out a branch first."));
                    return;
                }
                if (!await _confirm(Loc.Get("MergeConfirmTitle", "Merge"),
                        Fmt("MergeConfirmFmt", "Bring \"{0}\" into \"{1}\"?", request.Name, current)))
                    return;

                await _run(Loc.Get("MergingStatus", "Merging..."),
                    () => GitWriteService.MergeAsync(repo, request.Name), true);
                break;
            }

            case GraphOperation.RebaseOnto:
            {
                var (_, current) = await GitWriteService.GetBranchesAsync(repo);
                if (string.IsNullOrEmpty(current))
                {
                    _showMessage(Loc.Get("GraphOpRebaseConfirmTitle", "Rebase"),
                        Loc.Get("GraphOpNeedsBranch", "HEAD is detached. Check out a branch first."));
                    return;
                }
                if (!await _confirm(Loc.Get("GraphOpRebaseConfirmTitle", "Rebase"),
                        Fmt("GraphOpRebaseConfirmFmt",
                            "Rebase \"{0}\" onto \"{1}\"? Its commits are rewritten, so pushing it afterwards needs a force push.",
                            current, request.Name)))
                    return;

                await _run(Loc.Get("GraphOpRebasingStatus", "Rebasing..."),
                    () => GitHistoryService.RebaseAsync(repo, request.Name), true);
                break;
            }
        }
    }

    private async Task ResetAsync(string repo, GraphOperationRequest request)
    {
        var commit = request.Commit;
        var mode = request.Operation switch
        {
            GraphOperation.ResetSoft => ResetMode.Soft,
            GraphOperation.ResetHard => ResetMode.Hard,
            _ => ResetMode.Mixed,
        };

        var impact = await GitHistoryService.GetResetImpactAsync(repo, commit.Hash);
        var lines = new List<string>
        {
            Fmt("GraphOpResetConfirmFmt", "Move the current branch to {0} ({1} reset)?",
                commit.ShortHash + " " + commit.Subject, mode.ToString().ToLowerInvariant()),
        };

        if (impact.CommitsDropped > 0)
            lines.Add(Fmt("GraphOpResetDropsFmt",
                "{0} commit(s) will no longer be on this branch.", impact.CommitsDropped));

        if (mode == ResetMode.Hard)
        {
            lines.Add(impact.UncommittedFiles > 0
                ? Fmt("GraphOpResetHardLossFmt",
                    "WARNING: {0} file(s) with uncommitted changes will be overwritten and the changes lost permanently.",
                    impact.UncommittedFiles)
                : Loc.Get("GraphOpResetHardNoChanges", "Any uncommitted changes would be lost permanently."));
            lines.Add(Loc.Get("GraphOpResetHardUndone", "This cannot be undone."));
        }

        if (impact.NeedsForcePush)
            lines.Add(Loc.Get("GraphOpResetForcePush",
                "The branch will fall behind its upstream, so pushing it afterwards needs a force push."));

        var title = mode == ResetMode.Hard
            ? Loc.Get("GraphOpResetHardTitle", "Hard Reset")
            : Loc.Get("GraphOpResetTitle", "Reset");
        if (!await _confirm(title, string.Join(Environment.NewLine + Environment.NewLine, lines))) return;

        await _run(Loc.Get("GraphOpResettingStatus", "Resetting..."),
            () => GitHistoryService.ResetAsync(repo, commit.Hash, mode), false);
    }

    private async Task DeleteTagAsync(string repo, string tag)
    {
        if (!await _confirm(Loc.Get("GraphOpDeleteTagConfirmTitle", "Delete Tag"),
                Fmt("GraphOpDeleteTagConfirmFmt", "Delete the local tag \"{0}\"? The commit itself is not touched.", tag)))
            return;

        // Only asked about when the remote really has it; a tag that was never pushed has
        // nothing to remove there.
        string? remote = await GitHistoryService.GetDefaultRemoteAsync(repo);
        bool alsoRemote = false;
        if (remote != null && await GitHistoryService.RemoteHasTagAsync(repo, remote, tag))
            alsoRemote = await _confirm(Loc.Get("GraphOpDeleteRemoteTagTitle", "Delete Tag from Remote"),
                Fmt("GraphOpDeleteRemoteTagFmt",
                    "\"{0}\" also exists on \"{1}\". Delete it there too? Anyone who has fetched it keeps their copy.",
                    tag, remote));

        await _run(Loc.Get("GraphOpDeletingTagStatus", "Deleting tag..."), async () =>
        {
            var local = await GitHistoryService.DeleteLocalTagAsync(repo, tag);
            if (!local.Ok || !alsoRemote) return local;
            return await GitHistoryService.DeleteRemoteTagAsync(repo, remote!, tag);
        }, false);
    }

    private async Task PushTagAsync(string repo, string tag)
    {
        var remote = await GitHistoryService.GetDefaultRemoteAsync(repo);
        if (remote == null)
        {
            _showMessage(Loc.Get("GraphOpPushTag", "Push Tag"),
                Loc.Get("GraphOpNoRemote", "This repository has no remote to push to."));
            return;
        }

        if (!await _confirm(Loc.Get("GraphOpPushTagConfirmTitle", "Push Tag"),
                Fmt("GraphOpPushTagConfirmFmt", "Publish the tag \"{0}\" to \"{1}\"?", tag, remote)))
            return;

        await _run(Loc.Get("GraphOpPushingTagStatus", "Pushing tag..."),
            () => GitHistoryService.PushTagAsync(repo, remote, tag), false);
    }
}
