using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// History operations (revert, cherry-pick, reset, tags, merge, rebase) picked from a row's
/// menu, and the toolbar's commit search. The search is a client-side filter over the commits
/// already loaded; "Load more" is what widens it.
/// </summary>
public partial class CommitGraphPanel
{
    private CommitGraphOperationFlow? _operationFlow;
    private TextBox? _filterBox;
    private List<GitCommit> _allCommits = new();
    private string _filterText = "";

    /// <summary>Wires the row menu's operations to the shared confirm-and-run flow.</summary>
    private void InitGraphOperations()
    {
        if (_confirm != null)
        {
            _operationFlow = new CommitGraphOperationFlow(
                () => _repoRoot,
                _confirm,
                (title, text) => _showMessage?.Invoke(title, text),
                (status, work, quiet) => RunGitAsync(status, work, quiet),
                () => _gitBusy);
            _view.OperationRequested += (_, request) => _ = _operationFlow.HandleAsync(request);
        }

        // Whether the repository is on GitHub decides if "Open Commit on GitHub" is offered.
        Loaded += async (_, _) => _view.GitHubSlug = await GitHistoryService.GetGitHubSlugAsync(_repoRoot);
    }

    private Control CreateSearchBox()
    {
        _filterBox = new TextBox
        {
            Width = 200,
            MinHeight = 0,
            Height = 26,
            FontSize = 12,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText =Loc.Get("GraphSearchWatermark", "Search message, author, hash"),
        };

        _filterBox.TextChanged += (_, _) =>
        {
            _filterText = _filterBox.Text ?? "";
            ApplyGraph(_allCommits, keepSelection: true);
            UpdateFilterStatus();
        };

        // Esc clears the search first; with nothing typed it falls through and closes the window.
        _filterBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(_filterBox.Text)) return;
            _filterBox.Text = "";
            e.Handled = true;
        };

        return _filterBox;
    }

    private bool Matches(GitCommit commit, string needle)
    {
        return commit.Subject.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
            || commit.Author.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
            || commit.Hash.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Puts <paramref name="commits"/> in the view, narrowed by the search text. A filtered list
    /// is no longer a connected history, so lanes and lines are dropped and the working-tree row
    /// is left out while it is active.
    /// </summary>
    private void ApplyGraph(List<GitCommit> commits, bool keepSelection)
    {
        _allCommits = commits;
        var needle = _filterText.Trim();

        if (needle.Length == 0)
        {
            _view.HideLines = false;
            _view.SetGraph(CommitGraphLayout.Build(commits), _workingTree.Count > 0, keepSelection);
        }
        else
        {
            var shown = commits.Where(c => Matches(c, needle)).ToList();
            _view.HideLines = true;
            _view.SetGraph(CommitGraphLayout.Build(shown), false, keepSelection);
        }

        _header.Margin = new Thickness(_view.GraphWidth, 0, 0, 0);
    }

    private void UpdateFilterStatus()
    {
        var needle = _filterText.Trim();
        if (needle.Length == 0)
        {
            _statusText.Text = _allCommits.Count == 0
                ? Loc.Get("GraphNoCommits", "No commits to show")
                : string.Format(CultureInfo.CurrentCulture, Loc.Get("GraphCommitCountFmt", "{0} commits"), _allCommits.Count);
            return;
        }

        int matches = _allCommits.Count(c => Matches(c, needle));
        _statusText.Text = string.Format(CultureInfo.CurrentCulture,
            Loc.Get("GraphSearchCountFmt", "{0} of {1} commits match"), matches, _allCommits.Count);
    }
}
