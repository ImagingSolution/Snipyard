using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Snipyard.Services;

namespace Snipyard.Controls;

// Pull-request actions beyond approve: merge, request changes, comment, check out, and the
// links out to GitHub. The row and the section heading only call into the methods below.
public sealed partial class SourceControlPanel
{
    /// <summary>The "..." button on a pull request row: every action that has no room for its own button.</summary>
    private Button BuildPullRequestMenuButton(PullRequestInfo pr)
    {
        var button = GlyphButton("⋯", Loc.Get("PrMoreActions", "More"), () => { });
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        flyout.Items.Add(PrMenuItem(Loc.Get("PrMergeAction", "Merge..."), () => MergePullRequest(pr)));
        flyout.Items.Add(PrMenuItem(Loc.Get("PrRequestChangesAction", "Request changes..."), () => RequestChangesOnPullRequest(pr)));
        flyout.Items.Add(PrMenuItem(Loc.Get("PrCommentAction", "Comment..."), () => CommentOnPullRequest(pr)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(PrMenuItem(Loc.Get("PrCheckoutAction", "Check out this branch"), () => CheckoutPullRequest(pr)));
        button.Flyout = flyout;
        return button;
    }

    private static MenuItem PrMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>A link row at the top of the pull-request list that opens the repository on GitHub.</summary>
    private Control BuildRepoLinkRow()
    {
        var link = new Button
        {
            Content = "↗ " + Loc.Get("PrOpenRepoOnGitHub", "Open repository on GitHub"),
            FontSize = 11,
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        link.Click += async (_, _) =>
        {
            var url = await GitHubPullRequestCli.GetRepoWebUrlAsync(_repo);
            if (url.Length > 0) OpenUrl(url);
        };
        return link;
    }

    private async void MergePullRequest(PullRequestInfo pr)
    {
        if (_busy || _repo.Length == 0) return;

        var repo = _repo;
        var choice = await ShowMergeDialogAsync(pr);
        if (choice == null) return;

        await RunAsync(Loc.Get("PrMergingStatus", "Merging the pull request..."),
            () => GitHubPullRequestCli.MergeAsync(repo, pr.Number, choice.Value.Method, choice.Value.DeleteBranch));
    }

    private async void RequestChangesOnPullRequest(PullRequestInfo pr)
    {
        if (_busy || _repo.Length == 0) return;

        var repo = _repo;
        var body = await _host.TextInput(Loc.Get("PrRequestChangesAction", "Request changes"),
            Loc.Get("PrRequestChangesPrompt", "What needs to change? (required)"), "");
        if (string.IsNullOrWhiteSpace(body)) return;

        if (!await _host.Confirm(Loc.Get("PrRequestChangesAction", "Request changes"),
                string.Format(Loc.Get("PrRequestChangesConfirm", "Request changes on #{0}?"), pr.Number)))
            return;

        // Reviewing your own pull request is refused by GitHub; gh's message is shown as-is.
        await RunAsync(Loc.Get("PrReviewingStatus", "Sending the review..."),
            () => GitHubPullRequestCli.RequestChangesAsync(repo, pr.Number, body.Trim()));
    }

    private async void CommentOnPullRequest(PullRequestInfo pr)
    {
        if (_busy || _repo.Length == 0) return;

        var repo = _repo;
        var body = await _host.TextInput(Loc.Get("PrCommentAction", "Comment"),
            Loc.Get("PrCommentPrompt", "Comment on this pull request"), "");
        if (string.IsNullOrWhiteSpace(body)) return;

        await RunAsync(Loc.Get("PrCommentingStatus", "Posting the comment..."),
            () => GitHubPullRequestCli.CommentAsync(repo, pr.Number, body.Trim()));
    }

    private async void CheckoutPullRequest(PullRequestInfo pr)
    {
        if (_busy || _repo.Length == 0) return;

        var repo = _repo;
        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.SwitchOfferingStashAsync(repo,
                () => GitHubPullRequestCli.CheckoutAsync(repo, pr.Number), _host.Confirm, _host.ShowMessage));
    }

    /// <summary>Warnings worth reading before merging, from what the pull request list already knows.</summary>
    private static List<string> MergeWarnings(PullRequestInfo pr)
    {
        var warnings = new List<string>();
        if (pr.IsDraft) warnings.Add(Loc.Get("PrMergeWarnDraft", "This pull request is still a draft."));
        if (pr.Checks == ChecksState.Failing)
            warnings.Add(string.Format(Loc.Get("PrMergeWarnChecks", "Checks are failing: {0}"),
                string.Join(", ", pr.FailingChecks ?? Array.Empty<string>())));
        else if (pr.Checks == ChecksState.Pending)
            warnings.Add(Loc.Get("PrMergeWarnPending", "Checks have not finished yet."));
        if (string.Equals(pr.ReviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase))
            warnings.Add(Loc.Get("PrMergeWarnChanges", "Changes have been requested on this pull request."));
        return warnings;
    }

    /// <summary>Method and branch cleanup, or null when the user backed out. The Merge button is the confirmation.</summary>
    private Task<(PrMergeMethod Method, bool DeleteBranch)?> ShowMergeDialogAsync(PullRequestInfo pr)
    {
        var source = new TaskCompletionSource<(PrMergeMethod, bool)?>();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            source.SetResult(null);
            return source.Task;
        }

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(22, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = "#" + pr.Number + "  " + pr.Title,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var warning in MergeWarnings(pr))
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(WarningBg()),
                BorderBrush = new SolidColorBrush(WarningBorder()),
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5),
                Child = new TextBlock
                {
                    Text = "⚠ " + warning,
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(WarningText()),
                },
            });
        }

        panel.Children.Add(FieldLabel(Loc.Get("PrMergeMethodLabel", "Merge method")));
        var group = "prmerge" + pr.Number;
        var merge = new RadioButton { Content = Loc.Get("PrMergeCommit", "Merge commit"), GroupName = group, IsChecked = true };
        var squash = new RadioButton { Content = Loc.Get("PrMergeSquash", "Squash and merge"), GroupName = group };
        var rebase = new RadioButton { Content = Loc.Get("PrMergeRebase", "Rebase and merge"), GroupName = group };
        panel.Children.Add(merge);
        panel.Children.Add(squash);
        panel.Children.Add(rebase);

        var delete = new CheckBox
        {
            Content = string.Format(Loc.Get("PrMergeDeleteBranch", "Delete the branch {0} afterwards"), pr.HeadBranch),
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(delete);

        var ok = new Button
        {
            Content = Loc.Get("PrMergeConfirm", "Merge"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button
        {
            Content = Loc.Get("Cancel"),
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Get("PrMergeAction", "Merge..."),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(30, 30, 32)
                : Color.FromRgb(246, 246, 250)),
            Content = panel,
        };

        bool answered = false;
        ok.Click += (_, _) =>
        {
            var method = squash.IsChecked == true ? PrMergeMethod.Squash
                : rebase.IsChecked == true ? PrMergeMethod.Rebase
                : PrMergeMethod.Merge;
            answered = true;
            source.TrySetResult((method, delete.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(owner);
        return source.Task;
    }
}
