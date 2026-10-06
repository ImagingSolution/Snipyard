using System;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// The sidebar graph's history operations (revert, cherry-pick, reset, tags, merge, rebase).
/// The confirm-and-run sequence is shared with the graph window; this only supplies the
/// sidebar's busy state, error reporting and reload.
/// </summary>
public sealed partial class SourceControlPanel
{
    private CommitGraphOperationFlow? _graphOperationFlow;
    private string _graphSlugRepo = "";

    private void InitGraphOperations()
    {
        _graphOperationFlow = new CommitGraphOperationFlow(
            () => _repo,
            _host.Confirm,
            _host.ShowMessage,
            async (status, work, quiet) => { await RunAsync(status, work, quiet); },
            () => _busy);
        _graph.OperationRequested += (_, request) => _ = _graphOperationFlow.HandleAsync(request);

        // "Open Commit on GitHub" is offered only when the repository has a GitHub remote; the
        // answer is read from the local remote list, once per repository.
        BranchStateRead += async (_, args) =>
        {
            if (string.Equals(args.Repo, _graphSlugRepo, StringComparison.OrdinalIgnoreCase)) return;
            _graphSlugRepo = args.Repo;
            var slug = await GitHistoryService.GetGitHubSlugAsync(args.Repo);
            if (string.Equals(args.Repo, _graphSlugRepo, StringComparison.OrdinalIgnoreCase))
                _graph.GitHubSlug = slug;
        };
    }
}
