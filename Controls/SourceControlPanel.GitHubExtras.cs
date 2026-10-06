using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// The GitHub Actions, Issues and Release half of the source-control panel. Sits under the
/// pull-request section and shows under the same condition (a GitHub remote and a usable gh).
/// </summary>
public sealed partial class SourceControlPanel
{
    private static readonly TimeSpan RunPollInterval = TimeSpan.FromSeconds(30);

    private FoldSection _actionsSection = null!;
    private FoldSection _issuesSection = null!;
    private StackPanel _runList = null!;
    private StackPanel _issueList = null!;
    private DispatcherTimer? _runTimer;
    private bool _extrasVisible;
    private bool _runsActive;

    /// <summary>Builds both sections and docks them into the panel root, below the PR section.</summary>
    private void AddGitHubExtraSections(DockPanel root)
    {
        _runList = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 4) };
        _issueList = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 4) };

        var releaseButton = ToolButton(Loc.Get("GhReleaseCreate"), "", () => _ = CreateReleaseAsync());
        var issueButton = ToolButton(Loc.Get("GhIssueCreate"), "", () => _ = CreateIssueAsync());

        _actionsSection = new FoldSection(Loc.Get("GhActionsFmt"), SectionBody(releaseButton, _runList), DimText(), _isDark)
        {
            IsVisible = false,
        };
        _actionsSection.Title = string.Format(Loc.Get("GhActionsFmt"), 0);
        _issuesSection = new FoldSection(Loc.Get("GhIssuesFmt"), SectionBody(issueButton, _issueList), DimText(), _isDark)
        {
            IsVisible = false,
        };
        _issuesSection.Title = string.Format(Loc.Get("GhIssuesFmt"), 0);

        DockPanel.SetDock(_actionsSection, Dock.Top);
        DockPanel.SetDock(_issuesSection, Dock.Top);
        root.Children.Add(_actionsSection);
        root.Children.Add(_issuesSection);
    }

    private Control SectionBody(Button action, StackPanel list)
    {
        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 168,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        action.Margin = new Thickness(8, 0, 8, 4);
        action.HorizontalAlignment = HorizontalAlignment.Left;
        var body = new StackPanel();
        body.Children.Add(action);
        body.Children.Add(scroller);
        return body;
    }

    private void SetGitHubExtrasVisible(bool show)
    {
        _extrasVisible = show;
        _actionsSection.IsVisible = show;
        _issuesSection.IsVisible = show;
        if (!show) _runTimer?.Stop();
    }

    private async Task RefreshGitHubExtrasAsync(int generation)
    {
        if (!_extrasVisible || _repo.Length == 0) return;

        var branch = _branch.Current;
        var runsTask = GitHubWorkflowCli.ListRunsAsync(_repo, branch);
        var issuesTask = GitHubWorkflowCli.ListIssuesAsync(_repo);
        var runs = await runsTask;
        var issues = await issuesTask;
        if (generation != _refreshGeneration) return;

        _actionsSection.Title = string.Format(Loc.Get("GhActionsFmt"), runs.Count);
        _runList.Children.Clear();
        if (runs.Count == 0) _runList.Children.Add(EmptyNote(Loc.Get("GhNoRuns")));
        foreach (var run in runs) _runList.Children.Add(BuildRunRow(run));

        _issuesSection.Title = string.Format(Loc.Get("GhIssuesFmt"), issues.Count);
        _issueList.Children.Clear();
        if (issues.Count == 0) _issueList.Children.Add(EmptyNote(Loc.Get("GhNoIssues")));
        foreach (var issue in issues) _issueList.Children.Add(BuildIssueRow(issue));

        // Runs finish on GitHub's clock; poll while any is unfinished and the panel is in view.
        _runsActive = runs.Any(r => r.IsActive);
        if (_runsActive && _panelShown)
        {
            if (_runTimer == null)
            {
                _runTimer = new DispatcherTimer { Interval = RunPollInterval };
                _runTimer.Tick += (_, _) =>
                {
                    if (!_panelShown || !_extrasVisible || !_runsActive) { _runTimer?.Stop(); return; }
                    _ = RefreshGitHubExtrasAsync(_refreshGeneration);
                };
            }
            if (!_runTimer.IsEnabled) _runTimer.Start();
        }
        else
        {
            _runTimer?.Stop();
        }
    }

    private TextBlock EmptyNote(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Margin = new Thickness(6, 2),
        Foreground = new SolidColorBrush(DimText()),
    };

    private Border RowFrame(Control content) => new()
    {
        Child = content,
        Padding = new Thickness(6, 3),
        CornerRadius = new CornerRadius(4),
        BorderBrush = new SolidColorBrush(Divider()),
        BorderThickness = new Thickness(0.5),
    };

    // ── Actions ────────────────────────────────────────────────────────

    private static (string Glyph, Color Color) RunIcon(WorkflowRunInfo run)
    {
        if (run.IsActive)
            return (string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase) ? "●" : "○",
                Color.FromRgb(255, 159, 10));
        return run.Conclusion.ToLowerInvariant() switch
        {
            "success" => ("✓", Color.FromRgb(48, 209, 88)),
            "failure" or "timed_out" or "startup_failure" => ("✗", Color.FromRgb(255, 69, 58)),
            _ => ("⊘", Color.FromRgb(142, 142, 147)),
        };
    }

    private Control BuildRunRow(WorkflowRunInfo run)
    {
        var (glyph, color) = RunIcon(run);
        var title = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Inlines = new Avalonia.Controls.Documents.InlineCollection
            {
                new Avalonia.Controls.Documents.Run(glyph + " ") { Foreground = new SolidColorBrush(color) },
                new Avalonia.Controls.Documents.Run(run.Name),
            },
        };
        var when = run.CreatedAt;
        if (DateTimeOffset.TryParse(run.CreatedAt, out var created))
            when = created.ToLocalTime().ToString("MM-dd HH:mm");
        var sha = run.HeadSha.Length > 7 ? run.HeadSha[..7] : run.HeadSha;
        var metaText = run.Event + "   " + sha + "   " + when;
        var meta = new TextBlock
        {
            Text = metaText,
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(title, run.Name + "\n" + metaText + "\n" + run.Status + (run.Conclusion.Length > 0 ? " / " + run.Conclusion : ""));

        var log = GlyphButton("📄", Loc.Get("GhRunLog"), () => _ = ShowFailedLogAsync(run));
        log.IsEnabled = run.IsFailed;
        var rerun = GlyphButton("↻", Loc.Get("GhRunRerun"), () => _ = RerunFailedAsync(run));
        rerun.IsEnabled = run.IsFailed;
        var open = GlyphButton("↗", Loc.Get("OpenInBrowser", "Open"), () => OpenUrl(run.Url));
        return RowWithActions(title, meta, Row(log, rerun, open));
    }

    private Control RowWithActions(Control title, Control meta, Control actions)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Grid.SetRow(meta, 1);
        actions.Margin = new Thickness(6, 0, 0, 0);
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1);
        Grid.SetRowSpan(actions, 2);
        grid.Children.Add(title);
        grid.Children.Add(meta);
        grid.Children.Add(actions);
        return RowFrame(grid);
    }

    private async Task ShowFailedLogAsync(WorkflowRunInfo run)
    {
        if (_repo.Length == 0) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = _mono.FontFamily,
            FontSize = 11.5,
            Text = Loc.Get("GhLoading"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var copy = new Button { Content = Loc.Get("GhCopy"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var close = new Button { Content = Loc.Get("GhClose"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        layout.Children.Add(box);

        var dialog = new Window
        {
            Title = string.Format(Loc.Get("GhRunLogTitleFmt"), run.Name),
            Width = 820,
            Height = 560,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(30, 30, 32) : Color.FromRgb(246, 246, 250)),
            Content = layout,
        };
        copy.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(box.Text ?? "");
        };
        close.Click += (_, _) => dialog.Close();

        _ = dialog.ShowDialog(owner);
        var result = await GitHubWorkflowCli.GetFailedLogAsync(_repo, run.Id);
        var text = GitHubWorkflowCli.TailLines(result.Ok ? result.StdOut : result.Message);
        box.Text = text.Length > 0 ? text : Loc.Get("GhRunLogEmpty");
        box.CaretIndex = box.Text.Length;
    }

    private async Task RerunFailedAsync(WorkflowRunInfo run)
    {
        if (_repo.Length == 0 || _busy) return;
        if (!await _host.Confirm(Loc.Get("GhRunRerun"), string.Format(Loc.Get("GhRunRerunConfirmFmt"), run.Name)))
            return;

        await RunAsync(Loc.Get("GhRerunning"), () => GitHubWorkflowCli.RerunFailedAsync(_repo, run.Id));
        _ = RefreshGitHubExtrasAsync(_refreshGeneration);
    }

    // ── Issues ─────────────────────────────────────────────────────────

    private Control BuildIssueRow(IssueInfo issue)
    {
        var title = new TextBlock
        {
            Text = "#" + issue.Number + "  " + issue.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var metaText = issue.Author + (issue.Labels.Count > 0 ? "   " + string.Join(", ", issue.Labels) : "");
        var meta = new TextBlock
        {
            Text = metaText,
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(title, "#" + issue.Number + "  " + issue.Title + "\n" + metaText);

        var branch = GlyphButton("⎇", Loc.Get("GhIssueBranch"), () => _ = CreateIssueBranchAsync(issue));
        var open = GlyphButton("↗", Loc.Get("OpenInBrowser", "Open"), () => OpenUrl(issue.Url));
        return RowWithActions(title, meta, Row(branch, open));
    }

    private async Task CreateIssueAsync()
    {
        if (_repo.Length == 0 || _busy) return;

        var titleBox = new TextBox { FontSize = 13, Padding = new Thickness(8, 6), PlaceholderText = Loc.Get("GhIssueTitleLabel") };
        var bodyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
            FontSize = 12.5,
            Padding = new Thickness(8, 6),
        };
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(FieldLabel(Loc.Get("GhIssueTitleLabel")));
        form.Children.Add(titleBox);
        form.Children.Add(FieldLabel(Loc.Get("GhIssueBodyLabel")));
        form.Children.Add(bodyBox);

        string title = "", body = "";
        bool ok = await ShowFormDialogAsync(Loc.Get("GhIssueCreate"), 520, form, Loc.Get("GhRelCreateAction"), () =>
        {
            title = (titleBox.Text ?? "").Trim();
            if (title.Length == 0) { titleBox.Focus(); return false; }
            body = bodyBox.Text ?? "";
            return true;
        }, titleBox);
        if (!ok) return;

        GitResult? created = null;
        await RunAsync(Loc.Get("GhIssueCreating"), async () =>
        {
            created = await GitHubWorkflowCli.CreateIssueAsync(_repo, title, body);
            return created;
        });
        _ = RefreshGitHubExtrasAsync(_refreshGeneration);

        if (created is { Ok: true })
        {
            var url = GitHubCli.ExtractUrl(created.StdOut);
            if (url.Length > 0 && await _host.Confirm(Loc.Get("GhIssueCreate"), string.Format(Loc.Get("GhIssueCreatedFmt"), url)))
                OpenUrl(url);
        }
    }

    private async Task CreateIssueBranchAsync(IssueInfo issue)
    {
        if (_repo.Length == 0 || _busy) return;

        var name = await _host.TextInput(Loc.Get("GhIssueBranch"),
            string.Format(Loc.Get("GhIssueBranchPromptFmt"), issue.Number),
            GitHubWorkflowCli.BuildIssueBranchName(issue.Number, issue.Title));
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.StartsWith('-')) return;

        // Same route as any other branch switch: local edits git cannot carry get the stash offer.
        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.SwitchOfferingStashAsync(_repo,
                () => GitWriteService.CreateBranchAsync(_repo, name), _host.Confirm, _host.ShowMessage));
    }

    // ── Release ────────────────────────────────────────────────────────

    private async Task CreateReleaseAsync()
    {
        if (_repo.Length == 0 || _busy) return;
        var currentBranch = _branch.Current;
        var tags = await GitHubWorkflowCli.ListTagsAsync(_repo);

        var tagBox = new TextBox { FontSize = 13, Padding = new Thickness(8, 6), PlaceholderText = Loc.Get("GhRelTagHint") };
        var tagPick = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = Loc.Get("GhRelTagPick"), ItemsSource = tags };
        tagPick.IsVisible = tags.Count > 0;
        var tagNote = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(DimText()), TextWrapping = TextWrapping.Wrap };
        var titleBox = new TextBox { FontSize = 13, Padding = new Thickness(8, 6) };
        var notesBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            FontSize = 12.5,
            Padding = new Thickness(8, 6),
        };
        var autoNotes = new CheckBox { Content = Loc.Get("GhRelAutoNotes"), IsChecked = true };
        notesBox.IsEnabled = false;
        autoNotes.IsCheckedChanged += (_, _) => notesBox.IsEnabled = autoNotes.IsChecked != true;

        var files = new List<string>();
        var fileText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(DimText()) };
        void ShowFiles() => fileText.Text = files.Count == 0
            ? Loc.Get("GhRelNoFiles")
            : string.Join("\n", files.Select(Path.GetFileName));
        ShowFiles();
        var addFiles = new Button { Content = Loc.Get("GhRelAddFiles"), FontSize = 11.5 };
        var clearFiles = new Button { Content = Loc.Get("GhRelClearFiles"), FontSize = 11.5 };
        var draft = new CheckBox { Content = Loc.Get("GhRelDraft") };
        var pre = new CheckBox { Content = Loc.Get("GhRelPre") };

        // The title follows the tag until the user writes their own.
        string lastTag = "";
        void OnTagChanged()
        {
            var tag = (tagBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(titleBox.Text) || titleBox.Text == lastTag) titleBox.Text = tag;
            lastTag = tag;
            tagNote.Text = tag.Length == 0 ? ""
                : tags.Contains(tag) ? Loc.Get("GhRelExistingTag")
                : string.Format(Loc.Get("GhRelNewTagFmt"), currentBranch);
        }
        tagBox.TextChanged += (_, _) => OnTagChanged();
        tagPick.SelectionChanged += (_, _) =>
        {
            if (tagPick.SelectedItem is string picked) tagBox.Text = picked;
        };

        var owner = TopLevel.GetTopLevel(this) as Window;
        addFiles.Click += async (_, _) =>
        {
            if (owner == null) return;
            var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = Loc.Get("GhRelFiles"),
            });
            foreach (var f in picked)
                if (f.TryGetLocalPath() is { Length: > 0 } path && !files.Contains(path)) files.Add(path);
            ShowFiles();
        };
        clearFiles.Click += (_, _) => { files.Clear(); ShowFiles(); };

        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(FieldLabel(Loc.Get("GhRelTag")));
        form.Children.Add(tagBox);
        form.Children.Add(tagPick);
        form.Children.Add(tagNote);
        form.Children.Add(FieldLabel(Loc.Get("GhRelTitle")));
        form.Children.Add(titleBox);
        form.Children.Add(FieldLabel(Loc.Get("GhRelNotes")));
        form.Children.Add(autoNotes);
        form.Children.Add(notesBox);
        form.Children.Add(FieldLabel(Loc.Get("GhRelFiles")));
        form.Children.Add(fileText);
        form.Children.Add(Row(addFiles, clearFiles));
        form.Children.Add(Row(draft, pre));

        string tagValue = "", titleValue = "";
        bool ok = await ShowFormDialogAsync(Loc.Get("GhReleaseCreate"), 540, form, Loc.Get("GhRelCreateAction"), () =>
        {
            tagValue = (tagBox.Text ?? "").Trim();
            if (tagValue.Length == 0 || tagValue.StartsWith('-')) { tagBox.Focus(); return false; }
            titleValue = (titleBox.Text ?? "").Trim();
            if (titleValue.Length == 0) titleValue = tagValue;
            return true;
        }, tagBox);
        if (!ok) return;

        bool isNewTag = !tags.Contains(tagValue);
        var cmd = GitHubWorkflowCli.BuildCreateRelease(tagValue, files, titleValue, notesBox.Text ?? "",
            autoNotes.IsChecked == true, draft.IsChecked == true, pre.IsChecked == true,
            isNewTag && currentBranch.Length > 0 ? currentBranch : null);

        var flags = string.Join(" ", new[] { draft.IsChecked == true ? "draft" : "", pre.IsChecked == true ? "prerelease" : "" }
            .Where(s => s.Length > 0));
        var targetNote = isNewTag ? string.Format(Loc.Get("GhRelNewTagFmt"), currentBranch) : Loc.Get("GhRelExistingTag");
        if (!await _host.Confirm(Loc.Get("GhRelConfirmTitle"),
                string.Format(Loc.Get("GhRelConfirmFmt"), tagValue, titleValue, files.Count, targetNote + (flags.Length > 0 ? "  [" + flags + "]" : ""))))
            return;

        GitResult? created = null;
        await RunAsync(Loc.Get("GhRelCreating"), async () =>
        {
            created = await GitHubWorkflowCli.CreateReleaseAsync(_repo, cmd);
            return created;
        });

        if (created is { Ok: true })
        {
            var url = GitHubCli.ExtractUrl(created.StdOut);
            if (url.Length > 0 && await _host.Confirm(Loc.Get("GhRelConfirmTitle"), string.Format(Loc.Get("GhRelDoneFmt"), url)))
                OpenUrl(url);
        }
    }

    // ── Dialog shell ───────────────────────────────────────────────────

    /// <summary>
    /// A modal form with Cancel / OK. <paramref name="accept"/> runs on OK: it reads the fields and
    /// returns false to keep the dialog open (a required field is empty).
    /// </summary>
    private Task<bool> ShowFormDialogAsync(string title, double width, Control form, string okText,
        Func<bool> accept, Control? focus)
    {
        var source = new TaskCompletionSource<bool>();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            source.SetResult(false);
            return source.Task;
        }

        var ok = new Button { Content = okText, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = Loc.Get("Cancel"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(22, 20) };
        panel.Children.Add(form);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(30, 30, 32) : Color.FromRgb(246, 246, 250)),
            Content = panel,
        };

        bool answered = false;
        ok.Click += (_, _) =>
        {
            if (!accept()) return;
            answered = true;
            source.TrySetResult(true);
            dialog.Close();
        };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(false); };

        _ = dialog.ShowDialog(owner);
        if (focus != null) Dispatcher.UIThread.Post(() => focus.Focus());
        return source.Task;
    }
}
