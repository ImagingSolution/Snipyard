using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Commit history for one repository, drawn as a graph with a detail pane underneath, in the
/// spirit of VS Code's Git Graph. Reading is the whole of it bar one row: it reads the log,
/// lists what a commit changed, and hands a single file's diff to <see cref="DiffWindow"/>.
///
/// The exception is the toolbar's fetch, pull and push. The sidebar folds its own history away
/// while this window is up, and those three went with it, so they are here for the same reason
/// the graph is - they are the actions that move what the graph draws. Nothing else here writes.
///
/// A panel rather than a window: the history opens as an MDI child on the main canvas, sized
/// and arranged by the same layout code as terminal and editor windows, so it has no chrome,
/// no preferred size, and no screen of its own to fit into.
/// </summary>
public class CommitGraphPanel : UserControl
{
    private const int PageSize = 500;

    private readonly bool _isDark;
    private readonly Typeface _mono;
    private readonly Action<string>? _sendComment;

    /// <summary>
    /// The top of the working tree. It starts as the project folder and is replaced with the
    /// repository root on the first load: git resolves pathspecs against the folder it runs in
    /// but reports paths from the root, so the two only agree once this has climbed.
    /// </summary>
    private string _repoRoot;

    private bool _rootResolved;

    private readonly CommitGraphView _view;
    private readonly ScrollViewer _scroller;
    private readonly Button _refreshButton;
    private readonly Button _loadMoreButton;
    private readonly Button _fetchButton;
    private readonly Button _pullButton;
    private readonly Button _pushButton;
    private readonly TextBlock _statusText;
    private readonly Grid _header;

    /// <summary>Where a refused git write is reported. Null leaves the failure unannounced.</summary>
    private readonly Action<string, string>? _showMessage;

    /// <summary>Asks before publishing. Null means this window cannot push at all.</summary>
    private readonly Func<string, string, Task<bool>>? _confirm;

    /// <summary>Prompts for a tag name. Null means "Create Tag..." does nothing when picked.</summary>
    private readonly Func<string, string, string, Task<string?>>? _textInput;

    /// <summary>Set while one of the three remote actions is in flight.</summary>
    private bool _gitBusy;

    private readonly TextBlock _detailHash;
    private readonly TextBlock _detailAuthor;
    private readonly TextBlock _detailDate;
    private readonly SelectableTextBlock _detailMessage;
    private readonly TextBlock _filesHeading;
    private readonly StackPanel _fileRows;

    private int _limit = PageSize;

    /// <summary>What double-clicking each listed file does, in the order the files are shown.</summary>
    private readonly List<Action> _fileActions = new();

    /// <summary>Guards against a slow detail load landing after the selection has moved on.</summary>
    private int _detailGeneration;

    /// <summary>The same, for reloads: Refresh and Load more can otherwise land out of order.</summary>
    private int _reloadGeneration;

    /// <summary>
    /// The working-tree listing the last reload read. The uncommitted row shows this rather than
    /// asking git again for output the reload has already read once.
    /// </summary>
    private List<GitChange> _workingTree = new();

    /// <summary>
    /// Set when a row is activated before its file list has arrived. Double-clicking a row that
    /// was not already selected starts the list loading on the first press and asks to open on
    /// the second, so the request has to outlive the wait.
    /// </summary>
    private bool _openFileWhenListed;

    /// <summary>Title for the MDI window this panel is put in, and for its strip button.</summary>
    public string GraphTitle { get; }

    /// <summary>Escape asks to be closed; the host window owns the actual closing.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised after fetch, pull or push, so the readouts outside this window - the status bar,
    /// the file tree, the source-control panel - are not left showing the branch as it was.
    /// </summary>
    public event EventHandler? GitChanged;

    /// <summary>Guards the first load: Loaded fires again if the panel is ever re-attached.</summary>
    private bool _loadStarted;

    /// <param name="sendComment">
    /// Where a comment written against a diff goes. Passed straight through to
    /// <see cref="DiffWindow"/>; null leaves those windows read-only.
    /// </param>
    /// <param name="showMessage">Shows git's own words when it refuses one of the remote actions.</param>
    /// <param name="confirm">
    /// Asks before publishing. Leaving it null is what turns push off: the button stays, and says
    /// so when pressed, rather than sending commits out with nothing asked.
    /// </param>
    public CommitGraphPanel(string repoRoot, string repoLabel, bool isDark, Typeface mono,
        Action<string>? sendComment = null,
        Action<string, string>? showMessage = null,
        Func<string, string, Task<bool>>? confirm = null,
        Func<string, string, string, Task<string?>>? textInput = null)
    {
        _repoRoot = repoRoot;
        _isDark = isDark;
        _mono = mono;
        _sendComment = sendComment;
        _showMessage = showMessage;
        _confirm = confirm;
        _textInput = textInput;

        GraphTitle = string.IsNullOrEmpty(repoLabel)
            ? Loc.Get("CommitGraphTitle", "Commit Graph")
            : Loc.Get("CommitGraphTitle", "Commit Graph") + " - " + repoLabel;
        Background = new SolidColorBrush(Bg());

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };

        // -- Toolbar --
        _refreshButton = ToolButton(Loc.Get("GraphRefresh", "Refresh"));
        _refreshButton.Click += (_, _) => { _limit = PageSize; _ = ReloadAsync(); };

        _loadMoreButton = ToolButton(Loc.Get("GraphLoadMore", "Load more"));
        _loadMoreButton.IsEnabled = false;

        // Load more only lengthens the listing, so the commit being read stays selected and in
        // view instead of being thrown back to the top of the history.
        _loadMoreButton.Click += (_, _) => { _limit += PageSize; _ = ReloadAsync(keepSelection: true); };

        // The three the sidebar folded away with its own history. Spelled out rather than drawn:
        // this toolbar has the width the sidebar's heading row did not, and its neighbours are
        // words already.
        _fetchButton = ToolButton(Loc.Get("FetchAction", "Fetch"), Loc.Get("FetchTooltip", ""));
        _fetchButton.Click += (_, _) => OnFetch();

        _pullButton = ToolButton(Loc.Get("PullAction", "Pull"), Loc.Get("PullTooltip", ""));
        _pullButton.Click += (_, _) => OnPull();

        _pushButton = ToolButton(Loc.Get("PushAction", "Push"), Loc.Get("PushTooltip", ""));
        _pushButton.Click += (_, _) => OnPush();

        var toolbarDivider = new Border
        {
            Width = 1,
            Margin = new Thickness(4, 3, 10, 3),
            Background = new SolidColorBrush(Divider()),
        };

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(DimText()),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 6),
        };
        toolbar.Children.Add(_refreshButton);
        toolbar.Children.Add(_loadMoreButton);
        toolbar.Children.Add(toolbarDivider);
        toolbar.Children.Add(_fetchButton);
        toolbar.Children.Add(_pullButton);
        toolbar.Children.Add(_pushButton);
        toolbar.Children.Add(_statusText);
        ApplyRemoteState();

        // -- Column header, aligned to the graph once its width is known --
        _header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 0),
        };
        AddHeaderCell(Loc.Get("GraphColumnDescription", "Description"), 0, double.NaN);
        AddHeaderCell(Loc.Get("GraphColumnAuthor", "Author"), 1, CommitGraphView.AuthorWidth);
        AddHeaderCell(Loc.Get("GraphColumnDate", "Date"), 2, CommitGraphView.DateWidth);

        var headerBorder = new Border
        {
            Child = _header,
            Padding = new Thickness(0, 4),
            Background = new SolidColorBrush(PanelBg()),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 1, 0, 1),
        };

        // -- Graph list --
        _view = new CommitGraphView(_isDark);
        _view.SelectionChanged += (_, _) => ShowSelection();
        _view.RowActivated += (_, _) => OpenSelectedFileDiff();
        _view.CreateTagRequested += (_, commit) => _ = CreateTagAsync(commit);
        _view.CheckoutRequested += (_, commit) => _ = CheckoutCommitAsync(commit);

        _scroller = new ScrollViewer
        {
            Content = _view,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        _view.AttachScroller(_scroller);

        // -- Detail pane --
        _detailHash = new TextBlock
        {
            FontFamily = _mono.FontFamily,
            FontSize = 12,
            Foreground = new SolidColorBrush(TextColor()),
        };
        _detailAuthor = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(DimText()) };
        _detailDate = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(DimText()) };

        var metaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(12, 8, 12, 6),
        };
        metaRow.Children.Add(_detailHash);
        metaRow.Children.Add(_detailAuthor);
        metaRow.Children.Add(_detailDate);

        _detailMessage = new SelectableTextBlock
        {
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(TextColor()),
            Margin = new Thickness(12, 0, 12, 10),
        };

        _filesHeading = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(DimText()),
            Margin = new Thickness(12, 0, 12, 4),
        };

        _fileRows = new StackPanel { Margin = new Thickness(4, 0, 4, 8) };

        var detailBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,340") };

        var messageScroller = new ScrollViewer
        {
            Content = _detailMessage,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetColumn(messageScroller, 0);
        detailBody.Children.Add(messageScroller);

        var filesPanel = new DockPanel();
        DockPanel.SetDock(_filesHeading, Dock.Top);
        filesPanel.Children.Add(_filesHeading);
        filesPanel.Children.Add(new ScrollViewer
        {
            Content = _fileRows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });

        var filesBorder = new Border
        {
            Child = filesPanel,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        Grid.SetColumn(filesBorder, 1);
        detailBody.Children.Add(filesBorder);

        var detailPane = new DockPanel { Background = new SolidColorBrush(PanelBg()) };
        DockPanel.SetDock(metaRow, Dock.Top);
        detailPane.Children.Add(metaRow);
        detailPane.Children.Add(detailBody);

        // -- Assembly --
        // Proportional rather than a fixed detail height: as an MDI child this is laid out into
        // whatever slot the canvas hands it, and a fixed 260 left a cascaded window showing its
        // detail pane and a single commit row.
        var split = new Grid
        {
            RowDefinitions = new RowDefinitions("2*,Auto,*"),
        };

        var listArea = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        listArea.Children.Add(headerBorder);
        listArea.Children.Add(_scroller);
        Grid.SetRow(listArea, 0);
        split.Children.Add(listArea);

        var splitter = new GridSplitter
        {
            Height = 4,
            Background = new SolidColorBrush(Divider()),
            ResizeDirection = GridResizeDirection.Rows,
        };
        Grid.SetRow(splitter, 1);
        split.Children.Add(splitter);

        Grid.SetRow(detailPane, 2);
        split.Children.Add(detailPane);

        var toolbarBorder = new Border
        {
            Child = toolbar,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var root = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        root.Children.Add(toolbarBorder);
        root.Children.Add(split);
        Content = root;

        // The log is read once the panel is on screen rather than in the constructor, so the
        // window it lives in is laid out and showing before git is asked anything.
        Loaded += (_, _) =>
        {
            if (_loadStarted) return;
            _loadStarted = true;
            _ = ReloadAsync();
        };
    }

    // -- Loading --------------------------------------------------------

    private async Task ReloadAsync(bool keepSelection = false)
    {
        int generation = ++_reloadGeneration;

        _statusText.Text = Loc.Get("GraphLoading", "Loading...");
        _refreshButton.IsEnabled = false;
        _loadMoreButton.IsEnabled = false;

        try
        {
            if (!_rootResolved)
            {
                _rootResolved = true;
                var top = await Task.Run(() => GitCli.FindRepoRoot(_repoRoot));
                if (generation != _reloadGeneration) return;
                if (!string.IsNullOrEmpty(top)) _repoRoot = top!;
            }

            // The two readings are independent, so they run together rather than one after the
            // other. The working-tree listing doubles as the answer to whether there is anything
            // uncommitted to show, which is why the row it feeds never asks git a second time.
            var logTask = GitLogService.GetLogAsync(_repoRoot, _limit);
            var changesTask = GitChangeService.GetChangesAsync(_repoRoot);
            await Task.WhenAll(logTask, changesTask);

            // A reload started while this one was in flight owns the view now.
            if (generation != _reloadGeneration) return;

            var commits = logTask.Result;
            _workingTree = changesTask.Result;

            _view.SetGraph(CommitGraphLayout.Build(commits), _workingTree.Count > 0, keepSelection);
            _header.Margin = new Thickness(_view.GraphWidth, 0, 0, 0);

            _statusText.Text = commits.Count == 0
                ? Loc.Get("GraphNoCommits", "No commits to show")
                : Format(Loc.Get("GraphCommitCountFmt", "{0} commits"), commits.Count);

            // A short page means the log ran out, so there is nothing further to fetch.
            _loadMoreButton.IsEnabled = commits.Count >= _limit;
        }
        finally
        {
            if (generation == _reloadGeneration) _refreshButton.IsEnabled = true;
        }
    }

    // -- Remote ---------------------------------------------------------

    private void OnFetch() =>
        _ = RunGitAsync(Loc.Get("FetchingStatus", "Fetching..."),
            () => GitWriteService.FetchAsync(_repoRoot, quiet: false));

    private void OnPull() =>
        _ = RunGitAsync(Loc.Get("PullingStatus", "Pulling..."),
            () => GitWriteService.PullRebaseAsync(_repoRoot), quietOnConflict: true);

    private async void OnPush()
    {
        if (_gitBusy || _confirm == null) return;

        // The sidebar keeps a branch state to hand; this window does not follow the branch
        // between clicks, so it reads it fresh and pushes exactly what it just read.
        var state = await GitWriteService.GetBranchStateAsync(_repoRoot);
        if (state.Current.Length == 0) return;

        if (state.HasUpstream && state.Ahead == 0)
        {
            _showMessage?.Invoke(Loc.Get("PushAction"), Loc.Get("NothingToPush"));
            return;
        }

        // Publishing is outward-facing and awkward to walk back, so it is always confirmed.
        var detail = state.HasUpstream
            ? string.Format(CultureInfo.CurrentCulture, Loc.Get("PushConfirmFmt"),
                Format(Loc.Get("BranchAheadFmt"), state.Ahead), state.Current)
            : Loc.Get("PushConfirmNewUpstream");

        if (!await _confirm(Loc.Get("PushConfirmTitle"), detail)) return;

        await RunGitAsync(Loc.Get("PushingStatus", "Pushing..."),
            () => GitWriteService.PushAsync(_repoRoot, state));
    }

    private async Task CreateTagAsync(GitCommit commit)
    {
        if (_textInput == null) return;

        var name = await _textInput(Loc.Get("CreateTagAction", "Create Tag..."),
            Loc.Get("CreateTagPrompt", "Tag name"), "");
        if (string.IsNullOrWhiteSpace(name)) return;

        await RunGitAsync(Loc.Get("CreatingTagStatus", "Creating tag..."),
            () => GitWriteService.CreateTagAsync(_repoRoot, name.Trim(), commit.Hash));
    }

    private async Task CheckoutCommitAsync(GitCommit commit)
    {
        // Moving the working tree is always confirmed: a detached HEAD is easy to lose work on.
        if (_gitBusy || _confirm == null) return;
        if (!await _confirm(Loc.Get("CheckoutCommitConfirmTitle"),
                string.Format(CultureInfo.CurrentCulture, Loc.Get("CheckoutCommitConfirmFmt"),
                    commit.ShortHash, commit.Subject)))
            return;

        await RunGitAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.CheckoutCommitAsync(_repoRoot, commit.Hash));
    }

    /// <summary>
    /// Runs one remote action with the three buttons out of reach, shows git's own words when it
    /// refuses, and reloads afterwards whether it worked or not - a stopped rebase still leaves
    /// the graph looking different from before.
    ///
    /// <paramref name="quietOnConflict"/> is for pull, which is *meant* to stop on a conflict.
    /// There the non-zero exit is not news, and the place that names the conflicting files and
    /// offers the way out is the sidebar's banner, not a dialog over the history.
    /// </summary>
    private async Task RunGitAsync(string status, Func<Task<GitResult>> work,
        bool quietOnConflict = false)
    {
        if (_gitBusy) return;

        _gitBusy = true;
        _statusText.Text = status;
        ApplyRemoteState();

        try
        {
            var result = await work();
            if (!result.Ok &&
                !(quietOnConflict && (await GitWriteService.GetConflictsAsync(_repoRoot)).Count > 0))
            {
                var detail = result.Message;
                _showMessage?.Invoke(Loc.Get("GitFailedTitle"), GitErrorHints.Describe(detail));
            }
        }
        catch (Exception ex)
        {
            _showMessage?.Invoke(Loc.Get("GitFailedTitle"), ex.Message);
        }
        finally
        {
            _gitBusy = false;
            ApplyRemoteState();

            // The graph is the readout of what just moved, so it reloads before anything else is
            // told - keeping the selection, since a fetch leaves the commit being read in place.
            await ReloadAsync(keepSelection: true);
            GitChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyRemoteState()
    {
        _fetchButton.IsEnabled = !_gitBusy;
        _pullButton.IsEnabled = !_gitBusy;

        // Without somewhere to ask, push is not on offer at all, and the button says so by
        // being dead rather than by doing nothing when pressed.
        _pushButton.IsEnabled = !_gitBusy && _confirm != null;
    }

    // -- Detail pane ----------------------------------------------------

    private void ShowSelection()
    {
        int generation = ++_detailGeneration;

        _fileRows.Children.Clear();
        _fileActions.Clear();

        // A request to open a file belongs to the selection that was showing when it was made.
        _openFileWhenListed = false;

        if (_view.IsUncommittedSelected)
        {
            _detailHash.Text = "";
            _detailAuthor.Text = "";
            _detailDate.Text = "";
            _detailMessage.Text = Loc.Get("GraphUncommitted", "Uncommitted Changes");
            ShowWorkingTreeFiles();
            return;
        }

        var commit = _view.SelectedCommit;
        if (commit == null)
        {
            _detailHash.Text = "";
            _detailAuthor.Text = "";
            _detailDate.Text = "";
            _detailMessage.Text = "";
            _filesHeading.Text = "";
            return;
        }

        _detailHash.Text = commit.Hash;
        _detailAuthor.Text = string.IsNullOrEmpty(commit.AuthorEmail)
            ? commit.Author
            : $"{commit.Author} <{commit.AuthorEmail}>";
        _detailDate.Text = DescribeDates(commit);

        _detailMessage.Text = string.IsNullOrEmpty(commit.Body)
            ? commit.Subject
            : commit.Subject + "\n\n" + commit.Body;

        _filesHeading.Text = Loc.Get("GraphChangedFiles", "Changed files");
        _ = LoadCommitFilesAsync(commit, generation);
    }

    private async Task LoadCommitFilesAsync(GitCommit commit, int generation)
    {
        var files = await GitLogService.GetCommitFilesAsync(_repoRoot, commit.Hash);
        if (generation != _detailGeneration) return;

        _filesHeading.Text = Format(Loc.Get("GraphChangedFilesFmt", "Changed files ({0})"), files.Count);

        if (files.Count == 0)
            _fileRows.Children.Add(EmptyNote(Loc.Get("GraphNoFiles", "No files changed")));

        foreach (var file in files)
        {
            var captured = file;
            AddFileRow(FileRow(file.StatusGlyph, file.Path, file.OldPath),
                () => _ = OpenCommitDiffAsync(commit, captured));
        }

        OpenPendingFile();
    }

    private void ShowWorkingTreeFiles()
    {
        _filesHeading.Text = Format(Loc.Get("GraphChangedFilesFmt", "Changed files ({0})"), _workingTree.Count);

        if (_workingTree.Count == 0)
            _fileRows.Children.Add(EmptyNote(Loc.Get("GraphNoFiles", "No files changed")));

        foreach (var change in _workingTree)
        {
            var captured = change;
            AddFileRow(FileRow(change.StatusGlyph, change.Path, null),
                () => _ = OpenWorkingTreeDiffAsync(captured));
        }

        OpenPendingFile();
    }

    private void AddFileRow(Control row, Action open)
    {
        row.DoubleTapped += (_, _) => open();
        _fileRows.Children.Add(row);
        _fileActions.Add(open);
    }

    private void OpenSelectedFileDiff()
    {
        // Double-clicking a commit row opens the first file it touched, which is the quickest
        // way into a one-file commit; the file list stays there for anything larger.
        if (_fileActions.Count > 0)
        {
            _fileActions[0]();
            return;
        }

        // The list is still on its way -- on a row that was not already selected, this gesture's
        // own first press is what started it. Hold the request rather than dropping it.
        _openFileWhenListed = true;
    }

    /// <summary>Honours a row activation that arrived before the file list did.</summary>
    private void OpenPendingFile()
    {
        if (!_openFileWhenListed) return;
        _openFileWhenListed = false;
        if (_fileActions.Count > 0) _fileActions[0]();
    }

    private async Task OpenCommitDiffAsync(GitCommit commit, GitFileChange file)
    {
        var diff = await GitLogService.GetCommitFileDiffAsync(_repoRoot, commit.Hash, file.Path);
        ShowDiff($"{file.Path} @ {commit.ShortHash}", diff, file.Path);
    }

    private async Task OpenWorkingTreeDiffAsync(GitChange change)
    {
        var diff = await GitChangeService.GetDiffAsync(_repoRoot, change);
        ShowDiff(change.Path, diff, change.Path);
    }

    private void ShowDiff(string title, string diff, string filePath)
    {
        // No diff text is an answer in its own right -- a binary file, a mode change, a rename
        // that moved nothing -- so it opens a window saying so. Swallowing it here made
        // double-clicking such a file indistinguishable from a broken control.
        if (string.IsNullOrWhiteSpace(diff))
            diff = Loc.Get("GraphNoTextualDiff", "No textual changes (binary, mode, or rename only)");

        var window = new DiffWindow(title, diff, _isDark, _mono, filePath, _sendComment);

        // The panel has no window of its own any more, so the diff is owned by whatever window
        // the MDI canvas is in - the main window, in practice.
        if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner);
        else window.Show();
    }

    /// <summary>
    /// The date the listing is ordered by, and the author date alongside it when a rebase or a
    /// cherry-pick has left the two saying different things.
    /// </summary>
    private static string DescribeDates(GitCommit commit)
    {
        if (commit.CommitDate == default) return "";

        string committed = Stamp(commit.CommitDate);
        if (commit.Date == default || commit.Date == commit.CommitDate) return committed;

        return committed + "  " + Format(Loc.Get("GraphAuthoredFmt", "(authored {0})"), Stamp(commit.Date));
    }

    private static string Stamp(DateTimeOffset date) =>
        date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    // -- Small pieces ---------------------------------------------------

    private Control FileRow(string glyph, string path, string? oldPath)
    {
        var statusColor = glyph switch
        {
            "A" => Color.FromRgb(0x30, 0xD1, 0x58),
            "D" => Color.FromRgb(0xFF, 0x45, 0x3A),
            "R" or "C" => Color.FromRgb(0xD2, 0x99, 0x22),
            _ => Color.FromRgb(0x3B, 0x8E, 0xEA),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 2),
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = _mono.FontFamily,
            FontSize = 11,
            Width = 12,
            Foreground = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = oldPath == null ? path : $"{path}  ({oldPath})",
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(TextColor()),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Child = panel,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            CornerRadius = new CornerRadius(4),
        };
    }

    private TextBlock EmptyNote(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(DimText()),
        Margin = new Thickness(12, 4),
    };

    private void AddHeaderCell(string text, int column, double width)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(DimText()),
            Margin = new Thickness(0, 0, 0, 0),
        };
        if (!double.IsNaN(width)) block.Width = width;
        Grid.SetColumn(block, column);
        _header.Children.Add(block);
    }

    private Button ToolButton(string text) => new()
    {
        Content = text,
        FontSize = 12,
        Padding = new Thickness(10, 4),
        Background = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)),
        Foreground = new SolidColorBrush(TextColor()),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(4),
        Cursor = new Cursor(StandardCursorType.Hand),
        Margin = new Thickness(0, 0, 6, 0),
    };

    private Button ToolButton(string text, string tooltip)
    {
        var button = ToolButton(text);
        if (tooltip.Length > 0) ToolTip.SetTip(button, tooltip);
        return button;
    }

    private static string Format(string format, object arg)
    {
        try { return string.Format(CultureInfo.CurrentCulture, format, arg); }
        catch { return format; }
    }

    // -- Theme ----------------------------------------------------------

    private Color Bg() => _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);

    private Color PanelBg() => _isDark ? Color.FromRgb(36, 36, 38) : Color.FromRgb(246, 246, 249);

    private Color Divider() => _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215);

    private Color TextColor() => _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);

    private Color DimText() => _isDark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(84, 84, 92);
}
