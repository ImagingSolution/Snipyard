using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// Working-tree work for the source control panel: discarding changes, amending the last
/// commit, and the stash stack. The git calls are in GitWorkingTreeService; this is the part
/// that asks the user first and puts the results on screen.
/// </summary>
public sealed partial class SourceControlPanel
{
    private CheckBox _chkAmend = null!;
    private FoldSection _stashSection = null!;
    private StackPanel _stashList = null!;
    private Grid _stashHost = null!;
    private Button _btnStash = null!;

    private List<StashEntry> _stashes = new();
    private string _stashRepo = "";
    private bool _hasCommit;
    private bool _suppressAmend;

    /// <summary>The text put in the message box when Amend was ticked, so unticking can take it back out.</summary>
    private string? _amendFilled;

    /// <summary>Stashes whose file list is unfolded, keyed by ref and message because the index shifts.</summary>
    private readonly HashSet<string> _openStashes = new();

    /// <summary>Builds the controls this partial owns. Called once from the constructor.</summary>
    private void InitWorkingTree()
    {
        _chkAmend = new CheckBox
        {
            Content = Loc.Get("AmendCheckbox", "Amend the last commit"),
            FontSize = 11.5,
            MinHeight = 0,
            IsVisible = false,
        };
        ToolTip.SetTip(_chkAmend, Loc.Get("AmendTooltip", ""));
        _chkAmend.IsCheckedChanged += (_, _) => OnAmendToggled();

        _stashList = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 4) };
        var scroller = new ScrollViewer
        {
            Content = _stashList,
            MaxHeight = 200,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _stashSection = new FoldSection(Loc.Get("StashSection", "Stash"), scroller, DimText(), _isDark);

        _btnStash = ToolButton(Loc.Get("StashPushAction", "Stash changes"),
            Loc.Get("StashPushTooltip", ""), () => _ = StashPushAsync());
        _btnStash.FontSize = 10.5;
        _btnStash.Padding = new Thickness(6, 1);
        _btnStash.Height = 18;
        _btnStash.MinHeight = 0;
        _btnStash.HorizontalAlignment = HorizontalAlignment.Right;
        _btnStash.VerticalAlignment = VerticalAlignment.Top;
        _btnStash.Margin = new Thickness(0, 2, 26, 0);

        // The fold section has no slot for an action on its heading, so the button floats over
        // the heading's right end, left of the drop marker. It is a sibling, not a child, so a
        // click on it does not also fold the section.
        _stashHost = new Grid { IsVisible = false };
        _stashHost.Children.Add(_stashSection);
        _stashHost.Children.Add(_btnStash);
        DockPanel.SetDock(_stashHost, Dock.Top);

        BuildStashList();
    }

    /// <summary>Called at the end of ApplyState: what this partial's controls may do right now.</summary>
    private void ApplyWorkingTreeState()
    {
        bool isRepo = _repo.Length > 0;
        bool idle = isRepo && !_busy;
        bool settled = idle && _operation == RepoOperation.None;

        if (_stashRepo != _repo)
        {
            _stashRepo = _repo;
            _stashes = new List<StashEntry>();
            _hasCommit = false;
            _openStashes.Clear();
            BuildStashList();
        }

        if (!_hasCommit && _chkAmend.IsChecked == true)
        {
            _suppressAmend = true;
            _chkAmend.IsChecked = false;
            _suppressAmend = false;
        }

        _chkAmend.IsVisible = isRepo;
        _chkAmend.IsEnabled = settled && _hasCommit;

        if (_chkAmend.IsChecked == true)
        {
            // Amending may change only the message, so nothing has to be staged. Commit & push
            // is off: the push of a rewritten commit is a force push, which is not offered here.
            _btnCommit.IsEnabled = settled;
            _btnCommitPush.IsEnabled = false;
        }

        _stashHost.IsVisible = isRepo;
        _btnStash.IsEnabled = settled;
    }

    // ── Discard ────────────────────────────────────────────────────────

    private MenuItem DiscardMenuItem(GitChange change)
    {
        var item = new MenuItem { Header = Loc.Get("DiscardChangeAction", "Discard changes") };
        item.Click += (_, _) => _ = DiscardAsync(new List<GitChange> { change });
        return item;
    }

    /// <summary>
    /// A section heading with "Discard all" at its right end. Added to the first heading of the
    /// list only, so there is one such button however the changes are split.
    /// </summary>
    private Control WithDiscardAll(TextBlock heading, bool first)
    {
        if (!first) return heading;

        var button = ToolButton(Loc.Get("DiscardAllAction", "Discard all"),
            Loc.Get("DiscardAllTooltip", ""), () => _ = DiscardAsync(_changes.ToList()));
        button.FontSize = 10;
        button.Padding = new Thickness(6, 0);
        button.Height = 18;
        button.MinHeight = 0;
        button.Margin = new Thickness(0, 4, 4, 0);
        button.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(button, 1);
        grid.Children.Add(heading);
        grid.Children.Add(button);
        return grid;
    }

    private async Task DiscardAsync(List<GitChange> changes)
    {
        if (_repo.Length == 0 || _busy || changes.Count == 0) return;

        string text;
        if (changes.Count == 1)
        {
            var c = changes[0];
            text = string.Format(Loc.Get(c.Untracked ? "DiscardUntrackedConfirmFmt" : "DiscardFileConfirmFmt"), c.Path);
        }
        else
        {
            text = string.Format(Loc.Get("DiscardAllConfirmFmt"), changes.Count);
        }

        if (!await _host.Confirm(Loc.Get("DiscardConfirmTitle", "Discard changes"), text)) return;

        var repo = _repo;
        await RunAsync(Loc.Get("DiscardingStatus", "Discarding changes..."),
            () => GitWorkingTreeService.DiscardAsync(repo, changes));
    }

    // ── Amend ──────────────────────────────────────────────────────────

    private async void OnAmendToggled()
    {
        if (_suppressAmend) return;

        if (_chkAmend.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(_txtMessage.Text))
            {
                var head = await GitWorkingTreeService.GetHeadMessageAsync(_repo);
                // Ticked and still empty after the wait: a message typed meanwhile is the user's.
                if (_chkAmend.IsChecked == true && string.IsNullOrWhiteSpace(_txtMessage.Text)
                    && head.Length > 0)
                {
                    _txtMessage.Text = head;
                    _amendFilled = head;
                }
            }
        }
        else if (_amendFilled != null)
        {
            // The message was HEAD's, not the user's: do not leave it to become a new commit.
            if (string.Equals(_txtMessage.Text?.Replace("\r\n", "\n").TrimEnd(), _amendFilled))
                _txtMessage.Text = "";
            _amendFilled = null;
        }

        ApplyState();
    }

    private async Task<bool> AmendCommitAsync()
    {
        var message = _txtMessage.Text ?? "";
        if (string.IsNullOrWhiteSpace(message))
        {
            _host.ShowMessage(Loc.Get("CommitAction"), Loc.Get("NoCommitMessage"));
            _txtMessage.Focus();
            return false;
        }

        var staged = _changes.Where(c => c.Staged).Select(c => c.Path).ToList();
        if (staged.Count > 0 && !await ConfirmRiskyAsync(_repo, staged, staging: false)) return false;

        var repo = _repo;
        if (await GitWorkingTreeService.IsHeadPublishedAsync(repo, _branch.HasUpstream, _branch.Ahead)
            && !await _host.Confirm(Loc.Get("AmendPublishedTitle", "Amend a pushed commit"),
                Loc.Get("AmendPublishedText")))
            return false;

        bool ok = await RunAsync(Loc.Get("AmendingStatus", "Amending..."),
            () => GitWorkingTreeService.AmendAsync(repo, message));
        if (ok)
        {
            _amendFilled = null;
            _txtMessage.Text = "";
            _suppressAmend = true;
            _chkAmend.IsChecked = false;
            _suppressAmend = false;
            ApplyState();
        }
        return ok;
    }

    // ── Stash ──────────────────────────────────────────────────────────

    /// <summary>Reads the stash stack and whether there is a commit to amend. Called from RefreshAsync.</summary>
    private async Task RefreshWorkingTreeAsync(int generation)
    {
        var repo = _repo;
        var listTask = GitWorkingTreeService.ListStashAsync(repo);
        var headTask = GitWorkingTreeService.HasCommitAsync(repo);
        try { await Task.WhenAll(listTask, headTask); } catch { }

        if (generation != _refreshGeneration) return;

        _stashRepo = repo;
        _stashes = listTask.IsCompletedSuccessfully ? listTask.Result : new List<StashEntry>();
        _hasCommit = headTask.IsCompletedSuccessfully && headTask.Result;
        BuildStashList();
        ApplyState();
    }

    private static string StashKey(StashEntry s) => s.Ref + "\u0001" + s.Message;

    private void BuildStashList()
    {
        _stashList.Children.Clear();
        _stashSection.Title = _stashes.Count == 0
            ? Loc.Get("StashSection", "Stash")
            : string.Format(Loc.Get("StashSectionFmt", "Stash ({0})"), _stashes.Count);

        if (_stashes.Count == 0)
        {
            _stashList.Children.Add(new TextBlock
            {
                Text = Loc.Get("StashEmpty", "No stashes"),
                FontSize = 11,
                Margin = new Thickness(6, 2),
                Foreground = new SolidColorBrush(DimText()),
            });
            return;
        }

        var live = new HashSet<string>(_stashes.Select(StashKey));
        _openStashes.RemoveWhere(k => !live.Contains(k));

        foreach (var s in _stashes)
            _stashList.Children.Add(BuildStashRow(s));
    }

    private Control BuildStashRow(StashEntry stash)
    {
        var repo = _repo;
        var key = StashKey(stash);

        var title = new TextBlock
        {
            Text = stash.Message.Length > 0 ? stash.Message : stash.Ref,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var sub = new TextBlock
        {
            Text = stash.Ref + (stash.Branch.Length > 0 ? "  -  " + stash.Branch : ""),
            FontSize = 9.5,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var texts = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(sub);

        var label = new Border
        {
            Child = texts,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(label, stash.Ref + "  " + stash.Message);

        Button Small(string textKey, string fallback, string tipKey, Action act)
        {
            var b = ToolButton(Loc.Get(textKey, fallback), Loc.Get(tipKey, ""), act);
            b.FontSize = 10;
            b.Padding = new Thickness(5, 0);
            b.Height = 18;
            b.MinHeight = 0;
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        var apply = Small("StashApplyAction", "Apply", "StashApplyTooltip", () => _ = StashApplyOrPopAsync(stash, pop: false));
        var pop = Small("StashPopAction", "Pop", "StashPopTooltip", () => _ = StashApplyOrPopAsync(stash, pop: true));
        var drop = Small("StashDropAction", "Drop", "StashDropTooltip", () => _ = StashDropAsync(stash));
        bool enabled = !_busy && _operation == RepoOperation.None;
        apply.IsEnabled = pop.IsEnabled = drop.IsEnabled = enabled;

        var buttons = Row(apply, pop, drop);
        buttons.Spacing = 2;
        buttons.VerticalAlignment = VerticalAlignment.Center;
        buttons.Margin = new Thickness(4, 0, 0, 0);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(label);
        grid.Children.Add(buttons);

        var files = new StackPanel { Spacing = 0, Margin = new Thickness(14, 0, 0, 2), IsVisible = false };

        var outer = new StackPanel { Spacing = 0 };
        outer.Children.Add(new Border
        {
            Child = grid,
            Padding = new Thickness(6, 2),
            CornerRadius = new CornerRadius(4),
        });
        outer.Children.Add(files);

        async Task ShowFilesAsync()
        {
            files.Children.Clear();
            files.Children.Add(StashNote(Loc.Get("StashFilesLoading", "Loading...")));
            var list = await GitWorkingTreeService.GetStashFilesAsync(repo, stash.Ref);
            files.Children.Clear();
            if (list.Count == 0)
            {
                files.Children.Add(StashNote(Loc.Get("StashNoFiles", "No files")));
                return;
            }
            foreach (var line in list)
            {
                files.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 10.5,
                    FontFamily = _mono.FontFamily,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1),
                });
            }
        }

        if (_openStashes.Contains(key))
        {
            files.IsVisible = true;
            _ = ShowFilesAsync();
        }

        label.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            if (_openStashes.Remove(key))
            {
                files.IsVisible = false;
                return;
            }
            _openStashes.Add(key);
            files.IsVisible = true;
            _ = ShowFilesAsync();
        };

        return outer;
    }

    private TextBlock StashNote(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        Opacity = 0.6,
    };

    private async Task StashPushAsync()
    {
        if (_repo.Length == 0 || _busy) return;

        if (_changes.Count == 0)
        {
            _host.ShowMessage(Loc.Get("StashPushAction", "Stash changes"),
                Loc.Get("StashNothingToSave", "There are no changes to stash."));
            return;
        }

        var message = await _host.TextInput(Loc.Get("StashPushAction", "Stash changes"),
            Loc.Get("StashPushPrompt", "Message (optional)"), "");
        if (message == null) return;

        var repo = _repo;
        await RunAsync(Loc.Get("StashPushingStatus", "Stashing..."),
            () => GitWorkingTreeService.StashPushAsync(repo, message));
    }

    private async Task StashApplyOrPopAsync(StashEntry stash, bool pop)
    {
        if (_repo.Length == 0 || _busy) return;

        var repo = _repo;
        // A conflict is the expected way for this to fail, and the banner already says so.
        await RunAsync(Loc.Get(pop ? "StashPoppingStatus" : "StashApplyingStatus", "Applying..."),
            () => pop
                ? GitWorkingTreeService.StashPopAsync(repo, stash.Ref)
                : GitWorkingTreeService.StashApplyAsync(repo, stash.Ref),
            quietOnConflict: true);
    }

    private async Task StashDropAsync(StashEntry stash)
    {
        if (_repo.Length == 0 || _busy) return;

        if (!await _host.Confirm(Loc.Get("StashDropConfirmTitle", "Drop stash"),
                string.Format(Loc.Get("StashDropConfirmFmt"), stash.Ref, stash.Message)))
            return;

        var repo = _repo;
        await RunAsync(Loc.Get("StashDroppingStatus", "Dropping..."),
            () => GitWorkingTreeService.StashDropAsync(repo, stash.Ref));
    }
}
