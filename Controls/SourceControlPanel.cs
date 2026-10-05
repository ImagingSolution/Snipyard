using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// The few things the panel needs from the window it sits in: somewhere to put text the user
/// wants the AI to read, the three dialogs the app already has, and a way to open the commit
/// history as an MDI child. Bundled into one record so the panel's constructor does not grow a
/// parameter per callback.
/// </summary>
/// <param name="OpenCommitGraph">
/// Opens the full history for a repository root (first argument) under a display label (second)
/// as a window on the MDI canvas. The panel does not own that window: it lives alongside the
/// terminals and file editors, not on top of them.
/// </param>
/// <param name="IsCommitGraphOpen">
/// True while any window anywhere in the application is already showing the history for this
/// repository root. The panel folds its own history section away for as long as that holds.
/// </param>
public sealed record SourceControlHost(
    Action<string> SendToTerminal,
    Action<string, string> ShowMessage,
    Func<string, string, Task<bool>> Confirm,
    Func<string, string, string, Task<string?>> TextInput,
    Action<string, string> OpenCommitGraph,
    Func<string, bool> IsCommitGraphOpen);

/// <summary>
/// Everything git and GitHub in one panel: what has changed, what is staged, the branch and how
/// far it has drifted from its remote, the open pull requests, and the history underneath.
///
/// It is built for someone who does not know git. Every button is one named action - 取得 / 取込
/// / 送信 rather than fetch / pull / push - and nothing here can lose committed work: pull
/// rebases, branch deletion is <c>-d</c> only, and a conflict is answered with "abort" or "ask
/// the AI" rather than a hand-written merge. When git or gh refuses, its own words are shown
/// unedited, because the reason is usually the instruction.
/// </summary>
public sealed class SourceControlPanel : UserControl
{
    /// <summary>How much history the embedded graph shows. The window is there for more.</summary>
    private const int GraphLimit = 200;

    /// <summary>Interval of the background fetch, when it is switched on.</summary>
    private static readonly TimeSpan AutoFetchInterval = TimeSpan.FromMinutes(5);

    private readonly bool _isDark;
    private readonly Typeface _mono;
    private readonly AppSettings _settings;
    private readonly CliProviderService _cli;
    private readonly SourceControlHost _host;

    // ── Repository state ──

    /// <summary>The project folder as the window gave it, before git had a say.</summary>
    private string _folder = "";

    /// <summary>Top of the working tree, or empty when the folder is not in a repository.</summary>
    private string _repo = "";

    private List<GitChange> _changes = new();
    private List<IgnoreEntry> _ignored = new();
    private BranchState _branch = BranchState.None;
    private RepoOperation _operation = RepoOperation.None;
    private List<string> _conflicts = new();
    private List<PullRequestInfo> _pullRequests = new();

    /// <summary>Null until gh has been asked once; false hides the pull-request section.</summary>
    private bool? _ghReady;

    /// <summary>Whether the repository open right now is hosted on GitHub at all.</summary>
    private bool _onGitHub;

    /// <summary>One write at a time - every button is rebuilt from the result of the last one.</summary>
    private bool _busy;

    /// <summary>Set while the "stage all" box is being brought in line with the list.</summary>
    private bool _suppressStageAll;

    /// <summary>A reload started later owns the view; earlier ones drop their results.</summary>
    private int _refreshGeneration;

    /// <summary>A SetRepository call started later owns the view; earlier ones drop their results.</summary>
    private int _setRepoGeneration;

    private bool _panelShown;

    private DispatcherTimer? _fetchTimer;
    private CancellationTokenSource? _generateCts;
    private CancellationTokenSource? _scanCts;

    // ── Controls ──

    private readonly Button _btnFetch;
    private readonly Button _btnPull;
    private readonly Button _btnPush;
    private readonly Button _btnNewBranch;
    private readonly Button _btnMerge;
    private readonly Button _btnPr;
    private readonly Button _btnRefresh;
    private readonly Button _btnExpand;

    private readonly Button _btnBranch;
    private readonly TextBlock _lblTracking;
    private readonly TextBlock _status;

    private readonly Border _conflictBanner;
    private readonly TextBlock _lblConflicts;
    private readonly Button _btnConflictAsk;
    private readonly Button _btnConflictContinue;
    private readonly Button _btnConflictAbort;

    private readonly FoldSection _prSection;
    private readonly StackPanel _prList;

    private readonly CheckBox _chkStageAll;
    private readonly TextBlock _lblSummary;
    private readonly Button _btnCheck;
    private readonly StackPanel _changesList;

    private readonly FoldSection _ignoreSection;
    private readonly StackPanel _ignoreList;

    private readonly Border _commitBox;
    private readonly TextBox _txtMessage;
    private readonly Button _btnGenerate;
    private readonly Button _btnLanguage;
    private readonly Button _btnCommit;
    private readonly Button _btnCommitPush;

    private readonly CommitGraphView _graph;
    private readonly Grid _centre;
    private readonly GridSplitter _splitter;
    private readonly Border _graphBorder;

    /// <summary>
    /// How tall the history was before it moved out into its own window, so the split the user
    /// dragged is still theirs when the window closes.
    /// </summary>
    private GridLength _graphRow = new(2, GridUnitType.Star);

    /// <summary>Raised after a write, so the window can bring its own git readouts up to date.</summary>
    public event EventHandler? GitChanged;

    /// <summary>
    /// Raised each time a reload reads the branch, so the activity bar's badge follows the
    /// panel's own fetches and pulls without reading the same state a second time.
    /// </summary>
    public event EventHandler<(string Repo, BranchState State)>? BranchStateRead;

    public SourceControlPanel(bool isDark, Typeface mono, AppSettings settings,
        CliProviderService cli, SourceControlHost host)
    {
        _isDark = isDark;
        _mono = mono;
        _settings = settings;
        _cli = cli;
        _host = host;

        // ── Toolbar ──

        _btnNewBranch = ToolButton(Loc.Get("NewBranch", "New branch"), "", OnNewBranch);
        _btnMerge = ToolButton(Loc.Get("MergeAction", "Merge"), Loc.Get("MergeTooltip", ""), OnMerge);
        _btnPr = ToolButton(Loc.Get("CreatePrAction", "Pull request"), "", OnCreatePullRequest);
        // The three remote actions read against the history rather than the file list - what
        // fetch found and what pull or push will move are all commits - so they sit on the
        // history's own heading row at glyph width, the way the graph view does it, and the
        // toolbar keeps its one line for the branch actions.
        _btnFetch = IconButton(IconFetch, ActionTip("FetchAction", "Fetch", "FetchTooltip"), OnFetch);
        _btnPull = IconButton(IconPull, ActionTip("PullAction", "Pull", "PullTooltip"), OnPull);
        _btnPush = IconButton(IconPush, ActionTip("PushAction", "Push", "PushTooltip"), OnPush);
        // Neither of these is a toolbar action: refresh only rereads what the panel already
        // shows, and the graph button opens a window. Both live further down, on a heading row
        // that has width to spare, at glyph width - the toolbar's line is for writes.
        _btnRefresh = GlyphButton("⟳", Loc.Get("Refresh", "Refresh"), () => _ = RefreshAsync());
        _btnExpand = IconButton(IconExpand, Loc.Get("OpenGraphAction", "Open in a window"), OpenGraphWindow);

        var branchRow = Row(_btnNewBranch, _btnMerge, _btnPr);

        var toolbarStack = new StackPanel { Spacing = 4, Margin = new Thickness(8, 6) };
        toolbarStack.Children.Add(branchRow);

        var toolbar = new Border
        {
            Child = toolbarStack,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0, 0, 0.5),
        };
        DockPanel.SetDock(toolbar, Dock.Top);

        // ── Branch row ──

        _btnBranch = new Button
        {
            Content = "-",
            FontSize = 12,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(_btnBranch, Loc.Get("BranchMenuTooltip", "Switch, create or delete a branch"));
        _btnBranch.Click += (_, _) => ShowBranchMenu();

        _lblTracking = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(DimText()),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var branchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var branchGlyph = new TextBlock
        {
            Text = "⎇",
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(DimText()),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _btnRefresh.HorizontalAlignment = HorizontalAlignment.Right;
        _btnRefresh.VerticalAlignment = VerticalAlignment.Center;
        _btnRefresh.Margin = new Thickness(6, 0, 0, 0);

        Grid.SetColumn(branchGlyph, 0);
        Grid.SetColumn(_btnBranch, 1);
        Grid.SetColumn(_lblTracking, 2);
        Grid.SetColumn(_btnRefresh, 3);
        branchGrid.Children.Add(branchGlyph);
        branchGrid.Children.Add(_btnBranch);
        branchGrid.Children.Add(_lblTracking);
        branchGrid.Children.Add(_btnRefresh);

        var branchBorder = new Border
        {
            Child = branchGrid,
            Padding = new Thickness(0, 2, 6, 2),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0.5, 0, 0.5),
        };
        DockPanel.SetDock(branchBorder, Dock.Top);

        // ── Status line ──

        _status = new TextBlock
        {
            FontSize = 11,
            IsVisible = false,
            Margin = new Thickness(10, 4, 10, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(DimText()),
        };
        DockPanel.SetDock(_status, Dock.Top);

        // ── Conflict banner ──

        _lblConflicts = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(WarningText()),
        };
        _btnConflictAsk = ToolButton(Loc.Get("AskAiAction", "Ask the AI"), "", OnAskAiAboutConflict);
        _btnConflictContinue = ToolButton(Loc.Get("ContinueAction", "Continue"), "", OnContinueOperation);
        _btnConflictAbort = ToolButton(Loc.Get("AbortAction", "Abort"), "", OnAbortOperation);

        var conflictStack = new StackPanel { Spacing = 6 };
        conflictStack.Children.Add(_lblConflicts);
        conflictStack.Children.Add(Row(_btnConflictAsk, _btnConflictContinue, _btnConflictAbort));

        _conflictBanner = new Border
        {
            Child = conflictStack,
            IsVisible = false,
            Padding = new Thickness(10, 8),
            Background = new SolidColorBrush(WarningBg()),
            BorderBrush = new SolidColorBrush(WarningBorder()),
            BorderThickness = new Thickness(0, 0, 0, 0.5),
        };
        DockPanel.SetDock(_conflictBanner, Dock.Top);

        // ── Pull requests ──

        _prList = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 4) };

        // Open pull requests are worth a look now and then, not a permanent share of a sidebar
        // that has a file list and a history to fit as well - so the section is a heading until
        // it is asked for, and gives the rows a scroller of their own rather than pushing
        // everything below it off the bottom when a repository has a dozen of them open.
        var prScroller = new ScrollViewer
        {
            Content = _prList,
            MaxHeight = 168,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _prSection = new FoldSection(
            Loc.Get("PullRequests", "Pull requests"), prScroller, DimText(), _isDark)
        {
            IsVisible = false,
        };
        DockPanel.SetDock(_prSection, Dock.Top);

        // ── Commit box ──

        _txtMessage = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 52,
            MaxHeight = 120,
            FontSize = 12,
            PlaceholderText = Loc.Get("CommitMessage", "Commit message"),
        };

        _btnGenerate = ToolButton(Loc.Get("GenerateMessage", "AI draft"), "", OnGenerateMessage);
        _btnLanguage = ToolButton("English", Loc.Get("CommitLanguageTooltip", ""), OnToggleLanguage);
        // Each name is written in its own language, so the button reads the same whichever
        // interface language is set. Wide enough for both, so toggling does not shift the row.
        _btnLanguage.MinWidth = 62;

        _btnCommit = ToolButton(Loc.Get("CommitAction", "Commit"), "", OnCommit);
        _btnCommit.HorizontalAlignment = HorizontalAlignment.Stretch;
        _btnCommitPush = ToolButton(Loc.Get("CommitAndPush", "Commit & push"), "", OnCommitAndPush);

        var draftRow = Row(_btnGenerate, _btnLanguage);
        var commitRow = Row(_btnCommit, _btnCommitPush);

        var commitStack = new StackPanel { Spacing = 6 };
        commitStack.Children.Add(_txtMessage);
        commitStack.Children.Add(draftRow);
        commitStack.Children.Add(commitRow);

        _commitBox = new Border
        {
            Child = commitStack,
            IsVisible = false,
            Padding = new Thickness(8, 8),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0.5, 0, 0),
        };
        DockPanel.SetDock(_commitBox, Dock.Bottom);

        // ── Changes list ──

        _chkStageAll = new CheckBox
        {
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_chkStageAll, Loc.Get("StageAll", "Stage all"));
        _chkStageAll.IsCheckedChanged += (_, _) => OnStageAllChanged();

        _lblSummary = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(DimText()),
        };

        // The name-and-size check, on demand rather than only in front of a stage. The point of
        // the button is that a file can be dealt with - ignored - before it is ever staged.
        _btnCheck = ToolButton(Loc.Get("RiskyFilesCheckAction", "Check"),
            Loc.Get("RiskyFilesCheckTooltip", ""), OnCheckRisky);
        _btnCheck.FontSize = 10.5;
        _btnCheck.Padding = new Thickness(6, 1);
        _btnCheck.VerticalAlignment = VerticalAlignment.Center;

        var listHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10, 6, 10, 2),
        };
        Grid.SetColumn(_chkStageAll, 0);
        Grid.SetColumn(_lblSummary, 1);
        Grid.SetColumn(_btnCheck, 2);
        listHeader.Children.Add(_chkStageAll);
        listHeader.Children.Add(_lblSummary);
        listHeader.Children.Add(_btnCheck);
        DockPanel.SetDock(listHeader, Dock.Top);

        _changesList = new StackPanel { Spacing = 0, Margin = new Thickness(4, 2) };

        // ── Ignored ──

        // Ignoring a file makes git stop reporting it, which is the whole point and also the
        // danger: without somewhere to see the list, the user has hidden files by a route they
        // cannot find again. This section is that route, collapsed until there is something in it.
        // The explanation is long and this panel is short, so it hangs off the header rather than
        // costing four lines of a list that only has room for a couple of rows. Both dialogs that
        // offer to ignore something spell it out in full at the point of choosing.
        _ignoreList = new StackPanel { Spacing = 0, Margin = new Thickness(4, 0, 4, 2) };
        _ignoreSection = new FoldSection(
            string.Format(Loc.Get("IgnoredSectionFmt", "Ignored ({0})"), 0),
            _ignoreList, DimText(), _isDark, Loc.Get("IgnoreNote", ""))
        {
            IsVisible = false,
        };

        // Inside the scroller, not docked below it: this half of the panel is a few rows tall, and
        // a bottom-docked section that expands to its content height leaves the change list with
        // nothing. Scrolled together, expanding costs scroll distance instead of visible rows.
        // Under the changes, where a list of things deliberately kept out of the way belongs: the
        // rows the user is working with keep the top of the scroller.
        var changesStack = new StackPanel { Spacing = 0 };
        changesStack.Children.Add(_changesList);
        changesStack.Children.Add(_ignoreSection);

        var changesScroller = new ScrollViewer
        {
            Content = changesStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var changesArea = new DockPanel();
        changesArea.Children.Add(listHeader);
        changesArea.Children.Add(changesScroller);

        // ── Graph ──

        _graph = new CommitGraphView(_isDark) { CompactColumns = true };
        _graph.RowActivated += (_, _) => OpenGraphWindow();
        _graph.CreateTagRequested += (_, commit) => _ = CreateTagAsync(commit);
        _graph.CheckoutRequested += (_, commit) => _ = CheckoutCommitAsync(commit);

        var graphScroller = new ScrollViewer
        {
            Content = _graph,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _graph.AttachScroller(graphScroller);

        var graphHeading = new TextBlock
        {
            Text = Loc.Get("HistorySection", "History"),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(DimText()),
        };

        // The heading row has width to spare and the buttons act on the graph directly under it,
        // so they sit here rather than on the branch row, which was only lending space.
        var graphActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        graphActions.Children.Add(_btnFetch);
        graphActions.Children.Add(_btnPull);
        graphActions.Children.Add(_btnPush);
        graphActions.Children.Add(_btnExpand);

        var graphHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 3, 0, 2),
        };
        Grid.SetColumn(graphHeading, 0);
        Grid.SetColumn(graphActions, 1);
        graphHeader.Children.Add(graphHeading);
        graphHeader.Children.Add(graphActions);
        DockPanel.SetDock(graphHeader, Dock.Top);

        var graphArea = new DockPanel();
        graphArea.Children.Add(graphHeader);
        graphArea.Children.Add(graphScroller);

        _graphBorder = new Border
        {
            Child = graphArea,
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0, 0.5, 0, 0),
        };

        // The two halves are resizable because which one matters depends on the moment: reviewing
        // a change wants the file list, working out what to branch from wants the history.
        _centre = new Grid { RowDefinitions = new RowDefinitions("3*,4,2*") };
        _splitter = new GridSplitter
        {
            Height = 4,
            ResizeDirection = GridResizeDirection.Rows,
            Background = new SolidColorBrush(Divider()),
        };
        Grid.SetRow(changesArea, 0);
        Grid.SetRow(_splitter, 1);
        Grid.SetRow(_graphBorder, 2);
        _centre.Children.Add(changesArea);
        _centre.Children.Add(_splitter);
        _centre.Children.Add(_graphBorder);

        var root = new DockPanel();
        root.Children.Add(toolbar);
        root.Children.Add(branchBorder);
        root.Children.Add(_status);
        root.Children.Add(_conflictBanner);
        root.Children.Add(_prSection);
        root.Children.Add(_commitBox);
        root.Children.Add(_centre);

        Content = root;
        ApplyState();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Points the panel at another project. Called for every route that changes the active
    /// project, including the ones that clear it, so the panel can never show a stale repository.
    /// </summary>
    public async void SetRepository(string? projectFolder)
    {
        var next = projectFolder ?? "";
        int generation = ++_setRepoGeneration;

        // Resolved before touching any state: the folder itself can be unchanged while its
        // repository status changed underneath it - "Create Repository..." and "git init" from
        // outside the app both run git init in the already-active project folder, and a clone
        // command run externally can do the same. Comparing only the folder path here used to
        // skip the reload entirely in that case, leaving the panel showing "not a repository"
        // (blank changes, blank history) even after the folder became one.
        var root = next.Length > 0 ? await Task.Run(() => GitCli.FindRepoRoot(next)) : null;
        var nextRepo = root ?? "";

        // A later call already owns the view.
        if (generation != _setRepoGeneration) return;

        if (string.Equals(next, _folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(nextRepo, _repo, StringComparison.OrdinalIgnoreCase))
            return;

        _scanCts?.Cancel();
        _folder = next;
        _repo = nextRepo;
        _changes = new List<GitChange>();
        _branch = BranchState.None;
        _operation = RepoOperation.None;
        _conflicts = new List<string>();
        _pullRequests = new List<PullRequestInfo>();
        _ignored = new List<IgnoreEntry>();
        _changesList.Children.Clear();
        _ignoreList.Children.Clear();
        _ignoreSection.IsVisible = false;
        _prList.Children.Clear();
        _graph.SetGraph(CommitGraphLayout.Build(new List<GitCommit>()), false);
        _txtMessage.Text = "";
        ApplyState();

        if (_panelShown) await RefreshAsync();
        else ApplyState();
    }

    /// <summary>Called when the panel becomes the visible one in the sidebar.</summary>
    public void OnPanelShown()
    {
        _panelShown = true;
        _ = RefreshAsync();

        if (_fetchTimer == null)
        {
            _fetchTimer = new DispatcherTimer { Interval = AutoFetchInterval };
            _fetchTimer.Tick += (_, _) => _ = AutoFetchAsync();
        }
        _fetchTimer.Start();

        // CI finishes on GitHub's clock, not on any change here, so the checks are polled
        if (_prTimer == null)
        {
            _prTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _prTimer.Tick += (_, _) => { if (_prSection.IsVisible) _ = RefreshPullRequestsAsync(_refreshGeneration); };
        }
        _prTimer.Start();
    }

    private DispatcherTimer? _prTimer;

    public void OnPanelHidden()
    {
        _panelShown = false;
        _fetchTimer?.Stop();
        _prTimer?.Stop();
    }

    /// <summary>
    /// The settings screen owns the same two preferences this panel shows on its toolbar, so a
    /// change there has to reach the commit-language toggle here.
    /// </summary>
    public void OnSettingsChanged() => ApplyState();

    /// <summary>
    /// The commit message as typed. The window rebuilds this panel to pick up a theme or a
    /// language, and a half-written message is the one thing in it the user cannot get back.
    /// </summary>
    public string DraftMessage
    {
        get => _txtMessage.Text ?? "";
        set => _txtMessage.Text = value;
    }

    // ── Reload ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rereads everything the panel shows. Cheap enough to call on any change - the four git
    /// readings run together - and safe to call while another reload is in flight.
    /// </summary>
    public async Task RefreshAsync()
    {
        // Nothing here is visible while another sidebar panel is up, and each reload is four
        // git processes, so a hidden panel simply waits for OnPanelShown.
        if (!_panelShown) return;

        int generation = ++_refreshGeneration;

        if (_repo.Length == 0)
        {
            _changes = new List<GitChange>();
            _ignored = new List<IgnoreEntry>();
            _changesList.Children.Clear();
            _ignoreList.Children.Clear();
            _ignoreSection.IsVisible = false;
            _lblSummary.Text = Loc.Get("NotAGitRepo");
            _branch = BranchState.None;
            _operation = RepoOperation.None;
            _conflicts = new List<string>();
            _graph.SetGraph(CommitGraphLayout.Build(new List<GitCommit>()), false);
            ApplyState();
            return;
        }

        var repo = _repo;
        if (_changes.Count == 0) _lblSummary.Text = Loc.Get("LoadingChanges");

        var changesTask = GitChangeService.GetChangesAsync(repo);
        var branchTask = GitWriteService.GetBranchStateAsync(repo);
        var operationTask = GitWriteService.GetRepoOperationAsync(repo);
        var conflictTask = GitWriteService.GetConflictsAsync(repo);
        var logTask = GitLogService.GetLogAsync(repo, GraphLimit);
        var ignoreTask = GitIgnoreService.ListAsync(repo);

        try
        {
            await Task.WhenAll(changesTask, branchTask, operationTask, conflictTask, logTask,
                ignoreTask);
        }
        catch
        {
            // Each service already answers with an empty result rather than throwing; this only
            // catches the unexpected, and the checks below then keep whatever did arrive.
        }

        if (generation != _refreshGeneration) return;

        _changes = Settled(changesTask) ?? new List<GitChange>();
        _branch = Settled(branchTask) ?? BranchState.None;
        _operation = operationTask.IsCompletedSuccessfully ? operationTask.Result : RepoOperation.None;
        _conflicts = Settled(conflictTask) ?? new List<string>();
        _ignored = Settled(ignoreTask) ?? new List<IgnoreEntry>();
        var commits = Settled(logTask) ?? new List<GitCommit>();

        BuildChangesList();
        BuildIgnoreList();
        _graph.SetGraph(CommitGraphLayout.Build(commits), _changes.Count > 0, keepSelection: true);
        ApplyState();
        if (branchTask.IsCompletedSuccessfully) BranchStateRead?.Invoke(this, (repo, _branch));

        _ = RefreshPullRequestsAsync(generation);
    }

    /// <summary>The result of a reading that finished, or null for one that faulted.</summary>
    private static T? Settled<T>(Task<T> task) where T : class =>
        task.IsCompletedSuccessfully ? task.Result : null;

    private async Task AutoFetchAsync()
    {
        if (!_settings.GitAutoFetch || !_panelShown || _busy || _repo.Length == 0) return;

        // Quiet on purpose: an offline laptop must not produce a dialog every five minutes.
        var result = await GitWriteService.FetchAsync(_repo, quiet: true);
        if (result.Ok) await RefreshAsync();
    }

    // ── Enabling and labelling ─────────────────────────────────────────

    /// <summary>
    /// Brings every control in line with what the repository currently is. The single place that
    /// decides what is clickable, so "busy" cannot leak a half-enabled toolbar.
    /// </summary>
    private void ApplyState()
    {
        bool isRepo = _repo.Length > 0;
        bool idle = isRepo && !_busy;
        bool settled = idle && _operation == RepoOperation.None;
        bool hasBranch = _branch.Current.Length > 0;
        int staged = _changes.Count(c => c.Staged);

        ApplyGraphVisibility();

        _btnFetch.IsEnabled = idle;
        _btnPull.IsEnabled = settled;
        _btnPush.IsEnabled = settled && hasBranch;
        _btnNewBranch.IsEnabled = settled;
        _btnMerge.IsEnabled = settled;
        _btnPr.IsEnabled = settled && hasBranch && _ghReady == true && _onGitHub;
        _btnRefresh.IsEnabled = idle;
        _btnExpand.IsEnabled = isRepo;
        _btnBranch.IsEnabled = settled;

        // At glyph width the counts no longer fit beside the label, so they go where the rest of
        // each button's meaning already is - the tooltip. The branch row keeps showing them.
        ToolTip.SetTip(_btnPull, ActionTip("PullAction", "Pull", "PullTooltip",
            _branch.Behind > 0 ? " ↓" + _branch.Behind : ""));
        ToolTip.SetTip(_btnPush, ActionTip("PushAction", "Push", "PushTooltip",
            _branch.Ahead > 0 ? " ↑" + _branch.Ahead : ""));

        _btnBranch.Content = hasBranch ? _branch.Current : "-";
        _lblTracking.Text = !hasBranch ? ""
            : !_branch.HasUpstream ? Loc.Get("NoUpstream", "not published")
            : _branch.Ahead == 0 && _branch.Behind == 0 ? Loc.Get("UpToDate", "up to date")
            : $"↑{_branch.Ahead} ↓{_branch.Behind}";

        _chkStageAll.IsVisible = isRepo && _changes.Count > 0;
        _btnCheck.IsVisible = isRepo;
        _btnCheck.IsEnabled = idle && _changes.Count > 0;
        _suppressStageAll = true;
        _chkStageAll.IsChecked = _changes.Count > 0 && staged == _changes.Count;
        _suppressStageAll = false;

        _commitBox.IsVisible = isRepo;
        _btnCommit.IsEnabled = settled && staged > 0;
        _btnCommitPush.IsEnabled = settled && staged > 0 && hasBranch;

        // A CLI without a one-shot mode cannot draft anything, and the tooltip says which.
        bool canDraft = !string.IsNullOrWhiteSpace(_cli.Active.OneShotArgs);
        _btnGenerate.IsEnabled = settled && staged > 0 && canDraft;
        ToolTip.SetTip(_btnGenerate, canDraft
            ? string.Format(Loc.Get("GenerateTooltipFmt", "Draft a message with {0}"), _cli.Active.Name)
            : string.Format(Loc.Get("GenerateUnavailableFmt", "{0} has no one-shot mode"), _cli.Active.Name));

        _btnLanguage.Content =
            CommitMessageService.ResolveLanguage(_settings.CommitMessageLanguage) == "ja" ? "日本語" : "English";

        bool interrupted = _operation != RepoOperation.None || _conflicts.Count > 0;
        _conflictBanner.IsVisible = isRepo && interrupted;
        if (interrupted)
        {
            _lblConflicts.Text = _conflicts.Count > 0
                ? string.Format(Loc.Get("ConflictCountFmt", "{0} file(s) conflict"), _conflicts.Count)
                : Loc.Get(_operation == RepoOperation.Rebase ? "RebaseInProgress" : "MergeInProgress",
                    "An operation is unfinished");
            _btnConflictAsk.IsEnabled = idle && _conflicts.Count > 0;
            _btnConflictContinue.IsEnabled = idle && _conflicts.Count == 0
                && _operation != RepoOperation.None;
            _btnConflictAbort.IsEnabled = idle && _operation != RepoOperation.None;
        }
    }

    private void BuildChangesList()
    {
        _changesList.Children.Clear();

        _lblSummary.Text = _changes.Count == 0
            ? Loc.Get("NoChanges")
            : string.Format(Loc.Get("ChangedFilesCount"), _changes.Count);

        if (_changes.Count == 0) return;

        // Staged first and under its own heading: what the next commit will contain is the one
        // thing a newcomer most often gets wrong.
        var staged = _changes.Where(c => c.Staged).ToList();
        var unstaged = _changes.Where(c => !c.Staged).ToList();

        if (staged.Count > 0)
        {
            _changesList.Children.Add(SectionHeading(
                string.Format(Loc.Get("StagedSectionFmt", "Staged ({0})"), staged.Count)));
            foreach (var change in staged)
                _changesList.Children.Add(BuildChangeRow(change));
        }

        if (unstaged.Count > 0)
        {
            _changesList.Children.Add(SectionHeading(
                string.Format(Loc.Get("UnstagedSectionFmt", "Changes ({0})"), unstaged.Count)));
            foreach (var change in unstaged)
                _changesList.Children.Add(BuildChangeRow(change));
        }
    }

    private TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        FontSize = 10.5,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(6, 6, 0, 2),
        Foreground = new SolidColorBrush(DimText()),
    };

    private Control BuildChangeRow(GitChange change)
    {
        var repo = _repo;
        var glyphColor = change.StatusGlyph switch
        {
            "A" => Color.FromRgb(48, 209, 88),
            "D" => Color.FromRgb(255, 69, 58),
            "R" => Color.FromRgb(10, 132, 255),
            "?" => Color.FromRgb(142, 142, 147),
            _ => Color.FromRgb(255, 214, 10),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*") };

        // Staged state is git's, not the panel's: the box shows what the index already holds and
        // a click asks git to change it, so the two can never drift.
        var stage = new CheckBox
        {
            IsChecked = change.Staged,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
        };
        ToolTip.SetTip(stage, Loc.Get(change.Staged ? "UnstageFile" : "StageFile"));
        stage.IsCheckedChanged += (_, _) =>
        {
            bool want = stage.IsChecked == true;
            if (want == change.Staged || _busy) return;
            var one = new List<string> { change.Path };
            _ = StageChangeAsync(repo, want, one);
        };

        var glyph = new TextBlock
        {
            Text = change.StatusGlyph,
            Width = 14,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(glyphColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = change.DisplayName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var dir = new TextBlock
        {
            Text = change.DisplayDir,
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // A Fluent CheckBox is 32 tall whatever its minimum is set to - the template holds a
        // band of that height - and squeezing it only makes the template clip its own tick. So
        // it keeps the 32 it wants, on a canvas that measures children freely and answers for
        // 20 itself, lifted by the half-difference so the 20px tick lands over the slot. What
        // the row is spaced by is the slot, which is the height of the tick and nothing more.
        var stageSlot = new Canvas
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 4, 0),
        };
        Canvas.SetLeft(stage, 0);
        Canvas.SetTop(stage, -6);
        stageSlot.Children.Add(stage);

        Grid.SetColumn(stageSlot, 0);
        Grid.SetColumn(glyph, 1);
        Grid.SetColumn(name, 2);
        Grid.SetColumn(dir, 3);
        grid.Children.Add(stageSlot);
        grid.Children.Add(glyph);
        grid.Children.Add(name);
        grid.Children.Add(dir);

        var row = new Border
        {
            Padding = new Thickness(6, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = grid,
        };
        ToolTip.SetTip(row, change.Path + "  -  " + change.StatusLabel);

        var hover = new SolidColorBrush(_isDark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0));
        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        // Left button only, or the right-click that opens the menu below would open the diff
        // window behind it as well.
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) ShowDiff(change);
        };

        var comment = new MenuItem { Header = Loc.Get("CommentOnFile", "Comment on this file...") };
        comment.Click += (_, _) => CommentOnFile(change);

        var ignore = new MenuItem { Header = Loc.Get("IgnoreFileAction", "Add to ignore list") };
        ignore.Click += (_, _) => _ = IgnorePathsAsync(repo, new List<string> { change.Path });

        row.ContextMenu = new ContextMenu { ItemsSource = new[] { comment, ignore } };

        return row;
    }

    // ── The ignore list ────────────────────────────────────────────────

    private void BuildIgnoreList()
    {
        _ignoreList.Children.Clear();

        _ignoreSection.Title = string.Format(
            Loc.Get("IgnoredSectionFmt", "Ignored ({0})"), _ignored.Count);
        _ignoreSection.IsVisible = _repo.Length > 0 && _ignored.Count > 0;
        if (_ignored.Count == 0) return;

        foreach (var entry in _ignored)
            _ignoreList.Children.Add(BuildIgnoreRow(entry));
    }

    /// <summary>
    /// One ignored entry, with the way it is ignored spelled out. The two mechanisms behave
    /// differently - one keeps a file out of every commit, the other only silences local edits
    /// to a file that stays in the repository - so the row says which, rather than presenting
    /// them as one undifferentiated list.
    /// </summary>
    private Control BuildIgnoreRow(IgnoreEntry entry)
    {
        var value = new TextBlock
        {
            Text = entry.Value,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var kind = new TextBlock
        {
            Text = Loc.Get(entry.Kind == IgnoreKind.SkipWorktree
                ? "IgnoreKindSkipWorktree"
                : "IgnoreKindExclude"),
            FontSize = 9.5,
            Opacity = 0.55,
            Margin = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var remove = GlyphButton("✕", Loc.Get("UnignoreAction", "Remove"),
            () => _ = UnignoreAsync(_repo, entry));
        remove.VerticalAlignment = VerticalAlignment.Center;

        // One line: the path takes what is left after a small label and a glyph, and the tooltip
        // has the whole of it when there is not enough. Two lines per entry was a section as tall
        // as the change list it sits above, for a list nobody reads twice.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(value, 0);
        Grid.SetColumn(kind, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(value);
        grid.Children.Add(kind);
        grid.Children.Add(remove);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 1),
            CornerRadius = new CornerRadius(4),
        };
        // The panel is narrow enough that anything below the top of the tree gets trimmed, and a
        // half-shown path is not something a user can act on.
        ToolTip.SetTip(row, entry.Value);
        return row;
    }

    /// <summary>
    /// Adds paths to the ignore list. Nothing is filtered out of <c>_changes</c> afterwards and
    /// nothing needs to be: git stops listing these paths, so the next reload drops them from
    /// the panel on its own - and from a "git add -A" typed into the terminal, which a display
    /// filter would not have touched.
    /// </summary>
    private async Task IgnorePathsAsync(string repo, List<string> paths)
    {
        if (repo.Length == 0 || paths.Count == 0) return;
        if (!await WaitForIdleAsync()) return;

        // RunAsync raises GitChanged when it finishes, which is what reloads the panel.
        await RunAsync(Loc.Get("IgnoringStatus", "Updating the ignore list..."),
            () => GitIgnoreService.AddAsync(repo, paths));
    }

    private async Task UnignoreAsync(string repo, IgnoreEntry entry)
    {
        if (repo.Length == 0) return;
        if (!await WaitForIdleAsync()) return;

        await RunAsync(Loc.Get("UnignoringStatus", "Removing from the ignore list..."),
            () => GitIgnoreService.RemoveAsync(repo, entry));
    }

    /// <summary>
    /// Waits out a git write already running, up to ten seconds. The panel's buttons are disabled
    /// while one is in flight, so returning on <c>_busy</c> is honest for them - but the ignore
    /// list is also reached from the scan's own dialog, which appears whenever the AI answers and
    /// can land on top of the five-minute auto-fetch. There the same check made the click do
    /// nothing and say nothing. Waiting also serialises the writes, which is what matters: two
    /// git commands rewriting the index at once is the one outcome to avoid.
    /// </summary>
    private async Task<bool> WaitForIdleAsync()
    {
        for (int i = 0; i < 100 && _busy; i++)
            await Task.Delay(100);

        return !_busy;
    }

    // ── Running one git write ──────────────────────────────────────────

    /// <summary>
    /// Runs one write with the whole panel disabled, shows git's own words when it refuses, and
    /// reloads afterwards whether it worked or not - a failed merge still leaves state behind.
    ///
    /// <paramref name="quietOnConflict"/> is for the two operations that are *meant* to stop on a
    /// conflict. There the non-zero exit is not news: the banner names the files and offers the way
    /// out, and a dialog reading "Git could not do that" on top of it only frightens.
    /// </summary>
    private async Task<bool> RunAsync(string status, Func<Task<GitResult>> work,
        bool quietOnConflict = false)
    {
        if (_busy) return false;

        _busy = true;
        _status.Text = status;
        _status.IsVisible = true;
        ApplyState();

        bool ok = false;
        try
        {
            var result = await work();
            ok = result.Ok;
            if (!ok && !(quietOnConflict && (await GitWriteService.GetConflictsAsync(_repo)).Count > 0))
            {
                var detail = result.Message;
                _host.ShowMessage(Loc.Get("GitFailedTitle"), GitErrorHints.Describe(detail));
            }
        }
        catch (Exception ex)
        {
            _host.ShowMessage(Loc.Get("GitFailedTitle"), ex.Message);
        }
        finally
        {
            _busy = false;
            _status.Text = "";
            _status.IsVisible = false;
            ApplyState();

            // The window reloads its own git readouts and calls back into RefreshAsync, so the
            // status bar and the panel can never disagree about which branch is checked out.
            GitChanged?.Invoke(this, EventArgs.Empty);
        }

        return ok;
    }

    // ── Remote ─────────────────────────────────────────────────────────

    private void OnFetch() =>
        _ = RunAsync(Loc.Get("FetchingStatus", "Fetching..."),
            () => GitWriteService.FetchAsync(_repo, quiet: false));

    private void OnPull() =>
        _ = RunAsync(Loc.Get("PullingStatus", "Pulling..."),
            () => GitWriteService.PullRebaseAsync(_repo), quietOnConflict: true);

    private async void OnPush()
    {
        if (_repo.Length == 0 || _busy) return;

        var state = _branch;
        if (state.Current.Length == 0) return;

        if (state.HasUpstream && state.Ahead == 0)
        {
            _host.ShowMessage(Loc.Get("PushAction"), Loc.Get("NothingToPush"));
            return;
        }

        // A brand-new repository (e.g. just `git init`'d) has no remote at all yet - offer to
        // create one on GitHub instead of letting a plain push fail with "no origin".
        if (!state.HasUpstream && !await GitWriteService.HasAnyRemoteAsync(_repo))
        {
            await CreateGitHubRepoAndPush();
            return;
        }

        // Publishing is outward-facing and awkward to walk back, so it is always confirmed.
        var detail = state.HasUpstream
            ? string.Format(Loc.Get("PushConfirmFmt"),
                string.Format(Loc.Get("BranchAheadFmt"), state.Ahead), state.Current)
            : Loc.Get("PushConfirmNewUpstream");

        if (!await _host.Confirm(Loc.Get("PushConfirmTitle"), detail)) return;

        await RunAsync(Loc.Get("PushingStatus", "Pushing..."),
            () => GitWriteService.PushAsync(_repo, state));
    }

    /// <summary>Creates a repository on GitHub for a local repo that has no remote yet, then pushes it.</summary>
    private async Task CreateGitHubRepoAndPush()
    {
        if (!await GitHubCli.IsReadyAsync())
        {
            _host.ShowMessage(Loc.Get("PushAction"), Loc.Get("GhNotReadyForCreate",
                "GitHub CLI (gh) is not installed or not signed in. Run 'gh auth login' first."));
            return;
        }

        // `gh repo create --push` fails with a cryptic "no commits found" when HEAD is still
        // unborn (fresh `git init`, nothing committed yet) - catch it here with a clear message
        // instead of letting that raw gh error reach the user.
        if (!await GitWriteService.HasAnyCommitAsync(_repo))
        {
            _host.ShowMessage(Loc.Get("PushAction"), Loc.Get("NoCommitsToPush",
                "There are no commits yet. Commit your changes before pushing."));
            return;
        }

        var owners = await GitHubCli.GetOwnersAsync();
        var suggested = Path.GetFileName(_repo.TrimEnd(Path.DirectorySeparatorChar));
        var input = await ShowCreateGitHubRepoDialogAsync(suggested, owners);
        if (input is null) return;

        await RunAsync(Loc.Get("CreateGitHubRepoStatus", "Creating on GitHub..."),
            () => GitHubCli.CreateAndPushAsync(_repo, input.Value.Owner, input.Value.Name,
                input.Value.Description, input.Value.IsPrivate));
    }

    /// <summary>Owner, name, description, and visibility for a new GitHub repo, or null when cancelled.</summary>
    private Task<(string Owner, string Name, string Description, bool IsPrivate)?> ShowCreateGitHubRepoDialogAsync(
        string suggestedName, IReadOnlyList<string> owners)
    {
        var source = new TaskCompletionSource<(string, string, string, bool)?>();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            source.SetResult(null);
            return source.Task;
        }

        var ownerBox = new ComboBox
        {
            ItemsSource = owners,
            SelectedIndex = owners.Count > 0 ? 0 : -1,
            IsVisible = owners.Count > 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var nameBox = new TextBox
        {
            Text = suggestedName,
            FontSize = 13,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("CreateGitHubRepoNameWatermark", "Repository name"),
        };
        var descriptionBox = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("CreateGitHubRepoDescriptionWatermark", "(optional)"),
        };
        var privateBox = new CheckBox
        {
            Content = Loc.Get("CreateGitHubRepoPrivateCheckbox", "Private repository"),
            IsChecked = true,
        };

        var ok = new Button
        {
            Content = Loc.Get("Create", "Create"),
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
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(22, 20) };
        if (owners.Count > 0)
        {
            panel.Children.Add(FieldLabel(Loc.Get("CreateGitHubRepoOwnerLabel", "Owner")));
            panel.Children.Add(ownerBox);
        }
        panel.Children.Add(FieldLabel(Loc.Get("CreateGitHubRepoNameLabel", "Repository name")));
        panel.Children.Add(nameBox);
        panel.Children.Add(FieldLabel(Loc.Get("CreateGitHubRepoDescriptionLabel", "Description")));
        panel.Children.Add(descriptionBox);
        panel.Children.Add(privateBox);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Get("CreateGitHubRepoTitle", "Create Repository on GitHub"),
            Width = 420,
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
            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0) { nameBox.Focus(); return; }

            var selectedOwner = ownerBox.IsVisible ? (ownerBox.SelectedItem as string ?? "") : "";
            answered = true;
            source.TrySetResult((selectedOwner, name, (descriptionBox.Text ?? "").Trim(),
                privateBox.IsChecked == true));
            dialog.Close();
        };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.Post(() => { nameBox.Focus(); nameBox.SelectAll(); });
        return source.Task;
    }

    // ── Branches ───────────────────────────────────────────────────────

    private async void ShowBranchMenu()
    {
        if (_repo.Length == 0 || _busy) return;

        var repo = _repo;
        var (branches, current) = await GitWriteService.GetBranchesAsync(repo);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        // Rebuilt on every click: branches come and go, unlike the fixed model and effort lists.
        var inWorktrees = await WorktreeService.GetBranchesInOtherWorktreesAsync(repo);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        var flyout = new MenuFlyout { Placement = PlacementMode.Bottom };
        foreach (var branch in branches)
        {
            var name = branch;
            var item = new MenuItem
            {
                Header = name == current ? "✓ " + name : "   " + name,
                IsEnabled = name != current,
            };
            // Greyed but still clickable, so the click can say why the switch is not possible.
            if (name != current && inWorktrees.ContainsKey(name))
            {
                item.Header = "   " + name + "  " + Loc.Get("BranchInWorktreeTag");
                item.Opacity = 0.5;
            }
            item.Click += (_, _) => SwitchBranch(name);
            flyout.Items.Add(item);
        }

        if (branches.Count > 0) flyout.Items.Add(new Separator());

        var create = new MenuItem { Header = Loc.Get("NewBranch") };
        create.Click += (_, _) => OnNewBranch();
        flyout.Items.Add(create);

        var others = branches.Where(b => b != current).ToList();
        if (others.Count > 0)
        {
            var deleteItems = new List<MenuItem>();
            foreach (var branch in others)
            {
                var name = branch;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => DeleteBranch(name);
                deleteItems.Add(item);
            }
            flyout.Items.Add(new MenuItem
            {
                Header = Loc.Get("DeleteBranch", "Delete branch"),
                ItemsSource = deleteItems,
            });
        }

        flyout.ShowAt(_btnBranch);
    }

    private async void SwitchBranch(string branch)
    {
        // Checked again here rather than trusting the menu: a worktree may have been opened since.
        var inWorktrees = await WorktreeService.GetBranchesInOtherWorktreesAsync(_repo);
        if (inWorktrees.TryGetValue(branch, out var worktreePath))
        {
            _host.ShowMessage(Loc.Get("BranchInWorktreeTitle"),
                string.Format(Loc.Get("BranchInWorktreeFmt"), branch, worktreePath));
            return;
        }

        if (!await _host.Confirm(
                Loc.Get("SwitchBranchConfirmTitle"),
                string.Format(Loc.Get("SwitchBranchConfirmFmt"), branch)))
            return;

        // git switch refuses on conflicting local changes rather than carrying them across, so
        // a dirty tree surfaces as git's own message instead of silently moving the work.
        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.CheckoutBranchAsync(_repo, branch));
    }

    private async void OnNewBranch()
    {
        if (_repo.Length == 0 || _busy) return;

        var name = await _host.TextInput(Loc.Get("NewBranch"), Loc.Get("NewBranchPrompt"), "");
        if (string.IsNullOrWhiteSpace(name)) return;

        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.CreateBranchAsync(_repo, name.Trim()));
    }

    private async Task CreateTagAsync(GitCommit commit)
    {
        if (_repo.Length == 0 || _busy) return;

        var name = await _host.TextInput(Loc.Get("CreateTagAction", "Create Tag..."),
            Loc.Get("CreateTagPrompt", "Tag name"), "");
        if (string.IsNullOrWhiteSpace(name)) return;

        await RunAsync(Loc.Get("CreatingTagStatus", "Creating tag..."),
            () => GitWriteService.CreateTagAsync(_repo, name.Trim(), commit.Hash));
    }

    private async Task CheckoutCommitAsync(GitCommit commit)
    {
        if (_repo.Length == 0 || _busy) return;

        if (!await _host.Confirm(Loc.Get("CheckoutCommitConfirmTitle"),
                string.Format(Loc.Get("CheckoutCommitConfirmFmt"), commit.ShortHash, commit.Subject)))
            return;

        await RunAsync(Loc.Get("SwitchingStatus", "Switching..."),
            () => GitWriteService.CheckoutCommitAsync(_repo, commit.Hash));
    }

    private async void DeleteBranch(string branch)
    {
        if (!await _host.Confirm(
                Loc.Get("DeleteBranchConfirmTitle", "Delete branch"),
                string.Format(Loc.Get("DeleteBranchConfirmFmt",
                    "Delete the local branch \"{0}\"? Work that has not been merged is kept - git refuses in that case."),
                    branch)))
            return;

        await RunAsync(Loc.Get("DeletingBranchStatus", "Deleting..."),
            () => GitWriteService.DeleteBranchAsync(_repo, branch));
    }

    private async void OnMerge()
    {
        if (_repo.Length == 0 || _busy) return;

        var repo = _repo;
        var (branches, current) = await GitWriteService.GetBranchesAsync(repo);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        var others = branches.Where(b => b != current).ToList();
        if (others.Count == 0)
        {
            _host.ShowMessage(Loc.Get("MergeAction", "Merge"),
                Loc.Get("NoOtherBranch", "There is no other branch to merge."));
            return;
        }

        var flyout = new MenuFlyout { Placement = PlacementMode.Bottom };
        foreach (var branch in others)
        {
            var name = branch;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => MergeBranch(name, current);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_btnMerge);
    }

    private async void MergeBranch(string source, string target)
    {
        if (!await _host.Confirm(
                Loc.Get("MergeConfirmTitle", "Merge"),
                string.Format(Loc.Get("MergeConfirmFmt", "Bring \"{0}\" into \"{1}\"?"), source, target)))
            return;

        await RunAsync(Loc.Get("MergingStatus", "Merging..."),
            () => GitWriteService.MergeAsync(_repo, source), quietOnConflict: true);
    }

    // ── Conflicts ──────────────────────────────────────────────────────

    private async void OnAbortOperation()
    {
        var operation = _operation;
        if (operation == RepoOperation.None) return;

        if (!await _host.Confirm(
                Loc.Get("AbortConfirmTitle", "Abort"),
                Loc.Get("AbortConfirmText",
                    "Put the repository back exactly as it was before this operation started?")))
            return;

        await RunAsync(Loc.Get("AbortingStatus", "Aborting..."),
            () => GitWriteService.AbortAsync(_repo, operation));
    }

    private void OnContinueOperation()
    {
        var operation = _operation;
        if (operation == RepoOperation.None) return;

        _ = RunAsync(Loc.Get("ContinuingStatus", "Continuing..."),
            () => GitWriteService.ContinueAsync(_repo, operation));
    }

    /// <summary>
    /// Hands the conflict to the session: which files, and the one command that finishes the job
    /// once they are resolved. Written in whichever language commit messages are set to, because
    /// that is the language the user picked to be talked to in.
    ///
    /// One line, no breaks: this is typed into the session rather than pasted, so a newline
    /// here would submit half a sentence.
    /// </summary>
    private void OnAskAiAboutConflict()
    {
        if (_conflicts.Count == 0) return;

        string finish = _operation == RepoOperation.Rebase
            ? "git rebase --continue"
            : "git merge --continue";

        var files = string.Join(", ", _conflicts);
        var sb = new StringBuilder();
        bool ja = CommitMessageService.ResolveLanguage(_settings.CommitMessageLanguage) == "ja";

        if (ja)
        {
            sb.Append("git のコンフリクトを解決してください。衝突しているファイル: ").Append(files).Append("。");
            sb.Append("それぞれの衝突箇所を確認し、両方の意図を残す形で解決してください。");
            sb.Append("解決したら該当ファイルをステージして `").Append(finish).Append("` を実行してください。");
        }
        else
        {
            sb.Append("Please resolve these git conflicts: ").Append(files).Append(". ");
            sb.Append("Look at each conflict and keep what both sides intended. ");
            sb.Append("When they are resolved, stage the files and run `").Append(finish).Append("`.");
        }

        _host.SendToTerminal(sb.ToString());
    }

    // ── Commit ─────────────────────────────────────────────────────────

    private void OnStageAllChanged()
    {
        if (_suppressStageAll || _busy || _repo.Length == 0) return;

        bool stage = _chkStageAll.IsChecked == true;
        var paths = _changes
            .Where(c => c.Staged != stage)
            .Select(c => c.Path)
            .ToList();
        if (paths.Count == 0) return;

        _ = StageChangeAsync(_repo, stage, paths);
    }

    /// <summary>
    /// Stages or unstages the given paths, then - only for a stage that succeeded - hands the
    /// paths it just staged to the secret scan. Unstaging never triggers a scan: it can only make
    /// what is staged safer, never riskier.
    /// </summary>
    private async Task StageChangeAsync(string repo, bool stage, List<string> paths)
    {
        if (stage && !await ConfirmRiskyAsync(repo, paths, staging: true))
        {
            // The row's checkbox has already flipped itself; the reload is what puts it back.
            GitChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        bool ok = await RunAsync(Loc.Get(stage ? "StagingStatus" : "UnstagingStatus", "..."),
            () => stage
                ? GitWriteService.StageAsync(repo, paths)
                : GitWriteService.UnstageAsync(repo, paths));

        if (ok && stage) TriggerSecretScan(repo, paths);
    }

    /// <summary>What the user did with a list of flagged files.</summary>
    private enum RiskyChoice
    {
        /// <summary>Backed out. The action that raised the warning does not happen.</summary>
        Cancel,

        /// <summary>Said the files are fine. The action goes ahead unchanged.</summary>
        Proceed,

        /// <summary>Asked for the ticked files to stop appearing at all.</summary>
        Ignore,
    }

    private sealed record RiskyOutcome(RiskyChoice Choice, List<string> Paths);

    /// <summary>
    /// True when nothing in <paramref name="paths"/> is the kind of file that does not belong in a
    /// commit, and otherwise whatever the user answers to being shown the list. This is the cheap
    /// half of the two checks: it reads names and sizes, so it can run in front of the action it
    /// guards, where the AI scan can only follow behind one.
    ///
    /// Answering with the ignore list aborts the action as surely as cancelling does - a file the
    /// user has just said must never be committed is not one to stage on the way out.
    /// </summary>
    private async Task<bool> ConfirmRiskyAsync(string repo, List<string> paths, bool staging)
    {
        var risks = StagingPolicy.Inspect(repo, paths);
        if (risks.Count == 0) return true;

        var outcome = await ShowRiskyDialogAsync(risks,
            Loc.Get(staging ? "RiskyFilesStageIntro" : "RiskyFilesCommitIntro"),
            allowProceed: true);

        if (outcome.Choice == RiskyChoice.Ignore && outcome.Paths.Count > 0)
            await IgnorePathsAsync(repo, outcome.Paths);

        return outcome.Choice == RiskyChoice.Proceed;
    }

    /// <summary>
    /// The same check, run because the user asked rather than because they staged something. It
    /// is the half of the feature that ends the loop the warning alone could not: a file flagged
    /// here can be dealt with once, before it is ever staged, instead of producing the same
    /// dialog on every attempt.
    /// </summary>
    private async void OnCheckRisky()
    {
        if (_repo.Length == 0 || _busy) return;

        var repo = _repo;
        var paths = _changes.Select(c => c.Path).ToList();
        if (paths.Count == 0) return;

        // Inspect walks the contents of a collapsed folder, so it does not belong on the UI thread.
        var risks = await Task.Run(() => StagingPolicy.Inspect(repo, paths));
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        if (risks.Count == 0)
        {
            _host.ShowMessage(Loc.Get("RiskyFilesTitle"), Loc.Get("RiskyFilesNone"));
            return;
        }

        var outcome = await ShowRiskyDialogAsync(risks,
            Loc.Get("RiskyFilesCheckIntro"), allowProceed: false);

        if (outcome.Choice == RiskyChoice.Ignore && outcome.Paths.Count > 0)
            await IgnorePathsAsync(repo, outcome.Paths);
    }

    /// <summary>
    /// The flagged files with a tick box each, and the three things that can be done about them.
    /// Every file is ticked to begin with, because a list the user asked to see is a list they
    /// most likely want all of; untick is for the one exception in it.
    ///
    /// <paramref name="allowProceed"/> is false when nothing is waiting on the answer - the check
    /// button - and there is therefore nothing to continue with.
    /// </summary>
    private Task<RiskyOutcome> ShowRiskyDialogAsync(IReadOnlyList<StagingRisk> risks,
        string intro, bool allowProceed)
    {
        var none = new RiskyOutcome(RiskyChoice.Cancel, new List<string>());
        if (TopLevel.GetTopLevel(this) is not Window owner) return Task.FromResult(none);

        var introText = new TextBlock
        {
            Text = intro,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        var boxes = new List<CheckBox>();
        var list = new StackPanel { Spacing = 1, Margin = new Thickness(0, 10, 0, 0) };

        foreach (var risk in risks)
        {
            var path = new TextBlock
            {
                Text = risk.Path,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            var reason = new TextBlock
            {
                Text = risk.Reason,
                FontSize = 10.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            };
            var label = new StackPanel { Spacing = 1 };
            label.Children.Add(path);
            label.Children.Add(reason);

            var box = new CheckBox
            {
                IsChecked = true,
                Content = label,
                Tag = risk.Path,
                Padding = new Thickness(6, 0, 0, 0),
            };
            boxes.Add(box);
            list.Children.Add(box);
        }

        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 260,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var note = new TextBlock
        {
            Text = Loc.Get("IgnoreNote", ""),
            FontSize = 10.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ignore = new Button
        {
            Content = Loc.Get("IgnoreFileAction", "Add to ignore list"),
            MinWidth = 130,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var proceed = new Button
        {
            Content = Loc.Get("RiskyFilesProceed", "Continue anyway"),
            MinWidth = 110,
            IsVisible = allowProceed,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button
        {
            Content = Loc.Get(allowProceed ? "Cancel" : "Close"),
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        void SyncIgnore() => ignore.IsEnabled = boxes.Any(b => b.IsChecked == true);
        foreach (var box in boxes) box.IsCheckedChanged += (_, _) => SyncIgnore();
        SyncIgnore();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ignore);
        buttons.Children.Add(proceed);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(22, 20) };
        panel.Children.Add(introText);
        panel.Children.Add(scroller);
        panel.Children.Add(note);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Get("RiskyFilesTitle"),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(30, 30, 32)
                : Color.FromRgb(246, 246, 250)),
            Content = panel,
        };

        var answer = new TaskCompletionSource<RiskyOutcome>();
        var result = none;

        ignore.Click += (_, _) =>
        {
            result = new RiskyOutcome(RiskyChoice.Ignore,
                boxes.Where(b => b.IsChecked == true)
                    .Select(b => (string)(b.Tag ?? ""))
                    .Where(p => p.Length > 0)
                    .ToList());
            dialog.Close();
        };
        proceed.Click += (_, _) =>
        {
            result = new RiskyOutcome(RiskyChoice.Proceed, new List<string>());
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        // Closed rather than each button, so the window's own close box lands on Cancel too.
        dialog.Closed += (_, _) => answer.TrySetResult(result);

        _ = dialog.ShowDialog(owner);
        return answer.Task;
    }

    // ── Secret scan ────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background check of everything now staged, cancelling whatever scan was already
    /// running - only the most recent staged set is worth an answer about. A result only reaches
    /// the user when it is RISK: SAFE and "no verdict at all" (wrong provider, empty diff, an
    /// answer the parser could not read) both mean silence, because a scan this app cannot back
    /// up must never masquerade as a clean bill of health.
    /// </summary>
    private void TriggerSecretScan(string repo, List<string> justStagedPaths)
    {
        var provider = _cli.Active;
        if (string.IsNullOrWhiteSpace(provider.OneShotArgs)) return;

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var language = _settings.CommitMessageLanguage;

        _ = ScanAsync();

        async Task ScanAsync()
        {
            SecretScanResult? result;
            try
            {
                result = await SecretScanService.CheckStagedAsync(repo, provider, language, cts.Token);
            }
            catch
            {
                // A scan the panel cannot complete must never block staging - it just says nothing.
                return;
            }

            if (cts.IsCancellationRequested) return;
            if (result == null || result.Verdict != SecretScanVerdict.Risk) return;
            if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

            ShowSecretWarning(result, repo, justStagedPaths);
        }
    }

    /// <summary>
    /// The dialog for a RISK verdict: the AI's own words, and a choice between leaving the flagged
    /// file(s) staged, unstaging them, or keeping git from reporting them again.
    /// </summary>
    private void ShowSecretWarning(SecretScanResult result, string repo, List<string> justStagedPaths)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        // Every answer here acts on the files the scan flagged, which is not the same list as the
        // stage action that set it off: the scan reads the whole index, so a file staged earlier
        // can be the one it objects to. Acting on the stage action instead left the flagged file
        // exactly where it was and quietly ignored an innocent one in its place. A verdict that
        // names no file at all - only the older parse path can produce one - falls back to it.
        var flagged = result.Paths.Count > 0 ? result.Paths.ToList() : justStagedPaths;

        var intro = new TextBlock
        {
            Text = Loc.Get("SecretScanIntro", "The AI flagged this staged change:"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var detail = new TextBlock
        {
            Text = result.Detail,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var detailScroller = new ScrollViewer
        {
            Content = detail,
            MaxHeight = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var ignore = new Button
        {
            Content = Loc.Get("SecretScanIgnore", "Add to ignore list"),
            MinWidth = 130,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var unstage = new Button
        {
            Content = Loc.Get("SecretScanUnstage", "Unstage"),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var keep = new Button
        {
            Content = Loc.Get("SecretScanContinue", "Keep staged"),
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ignore);
        buttons.Children.Add(unstage);
        buttons.Children.Add(keep);

        var note = new TextBlock
        {
            Text = Loc.Get("IgnoreNote", ""),
            FontSize = 10.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var panel = new StackPanel { Margin = new Thickness(22, 20) };
        panel.Children.Add(intro);
        panel.Children.Add(detailScroller);
        panel.Children.Add(note);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Get("SecretScanTitle", "Possibly sensitive content"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(_isDark
                ? Color.FromRgb(30, 30, 32)
                : Color.FromRgb(246, 246, 250)),
            Content = panel,
        };

        unstage.Click += (_, _) =>
        {
            dialog.Close();
            if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;
            _ = UnstageFlaggedAsync();
        };
        // Unstaging as well, but for good: the ignore list takes these paths out of the index on
        // its way to making git stop reporting them, so this is the stronger of the two answers.
        ignore.Click += (_, _) =>
        {
            dialog.Close();
            if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;
            _ = IgnorePathsAsync(repo, flagged);
        };
        keep.Click += (_, _) => dialog.Close();

        _ = dialog.ShowDialog(owner);

        async Task UnstageFlaggedAsync()
        {
            if (!await WaitForIdleAsync()) return;
            await RunAsync(Loc.Get("UnstagingStatus", "..."),
                () => GitWriteService.UnstageAsync(repo, flagged));
        }
    }

    private async void OnCommit() => await CommitAsync();

    private async void OnCommitAndPush()
    {
        if (!await CommitAsync()) return;

        // The branch state the commit produced, not the one the button was drawn from.
        var state = await GitWriteService.GetBranchStateAsync(_repo);
        if (state.Current.Length == 0) return;

        await RunAsync(Loc.Get("PushingStatus", "Pushing..."),
            () => GitWriteService.PushAsync(_repo, state));
    }

    private async Task<bool> CommitAsync()
    {
        if (_repo.Length == 0 || _busy) return false;

        if (!_changes.Any(c => c.Staged))
        {
            _host.ShowMessage(Loc.Get("CommitAction"), Loc.Get("NothingStaged"));
            return false;
        }

        // Everything staged, not just what this panel staged: the index may have been filled by
        // another tool, or by a session that ended before the commit did.
        var staged = _changes.Where(c => c.Staged).Select(c => c.Path).ToList();
        if (!await ConfirmRiskyAsync(_repo, staged, staging: false)) return false;

        var message = _txtMessage.Text ?? "";
        if (string.IsNullOrWhiteSpace(message))
        {
            _host.ShowMessage(Loc.Get("CommitAction"), Loc.Get("NoCommitMessage"));
            _txtMessage.Focus();
            return false;
        }

        bool ok = await RunAsync(Loc.Get("CommittingStatus", "Committing..."),
            () => GitWriteService.CommitAsync(_repo, message));
        if (ok) _txtMessage.Text = "";
        return ok;
    }

    private void OnToggleLanguage()
    {
        _settings.CommitMessageLanguage =
            CommitMessageService.ResolveLanguage(_settings.CommitMessageLanguage) == "ja" ? "en" : "ja";
        _settings.Save();
        ApplyState();
    }

    private async void OnGenerateMessage()
    {
        if (_repo.Length == 0 || _busy) return;

        var provider = _cli.Active;
        if (string.IsNullOrWhiteSpace(provider.OneShotArgs)) return;

        _generateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _generateCts = cts;

        _busy = true;
        _btnGenerate.Content = Loc.Get("GeneratingMessage", "Drafting...");
        ApplyState();

        string? text = null;
        try
        {
            text = await CommitMessageService.GenerateAsync(_repo, provider,
                _settings.CommitMessageLanguage, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A second click replaced this draft; the newer one owns the box.
        }
        catch (Exception ex)
        {
            _host.ShowMessage(Loc.Get("GenerateMessage", "AI draft"), ex.Message);
        }
        finally
        {
            _busy = false;
            _btnGenerate.Content = Loc.Get("GenerateMessage", "AI draft");
            ApplyState();
        }

        if (cts.IsCancellationRequested) return;

        if (string.IsNullOrWhiteSpace(text))
        {
            _host.ShowMessage(Loc.Get("GenerateMessage", "AI draft"),
                Loc.Get("GenerateFailed", "The CLI returned no message. Write one by hand, or check the one-shot arguments in providers.json."));
            return;
        }

        _txtMessage.Text = text;
        _txtMessage.Focus();
    }

    // ── Pull requests ──────────────────────────────────────────────────

    private async Task RefreshPullRequestsAsync(int generation)
    {
        _ghReady ??= await GitHubCli.IsReadyAsync();
        if (generation != _refreshGeneration) return;

        _onGitHub = _repo.Length > 0 && await GitHubCli.HasGitHubRemoteAsync(_repo);
        if (generation != _refreshGeneration) return;

        bool show = _ghReady == true && _onGitHub;
        _prSection.IsVisible = show;
        ApplyState();
        if (!show) return;

        var list = await GitHubCli.ListAsync(_repo);
        if (generation != _refreshGeneration) return;

        _pullRequests = list;
        _prSection.Title = string.Format(Loc.Get("PullRequestsFmt", "Pull requests ({0})"), list.Count);
        BuildPullRequestList();
    }

    private void BuildPullRequestList()
    {
        _prList.Children.Clear();

        if (_pullRequests.Count == 0)
        {
            _prList.Children.Add(new TextBlock
            {
                Text = Loc.Get("NoPullRequests", "No open pull requests"),
                FontSize = 11,
                Margin = new Thickness(6, 2),
                Foreground = new SolidColorBrush(DimText()),
            });
            return;
        }

        foreach (var pr in _pullRequests)
            _prList.Children.Add(BuildPullRequestRow(pr));
    }

    private Control BuildPullRequestRow(PullRequestInfo pr)
    {
        var title = new TextBlock
        {
            Text = "#" + pr.Number + "  " + pr.Title,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var approved = string.Equals(pr.ReviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase);
        var metaText = pr.Author + "   " + pr.HeadBranch + " → " + pr.BaseBranch
            + (pr.IsDraft ? "   " + Loc.Get("PrDraft", "draft") : "")
            + (approved ? "   ✓ " + Loc.Get("PrApproved", "approved") : "");
        var meta = new TextBlock
        {
            Text = metaText,
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Beside the title rather than on a line of their own: two words of button were costing
        // a third of every row's height for something the tooltip says just as well.
        var approve = GlyphButton("✓", Loc.Get("ApproveAction", "Approve"), () => ApprovePullRequest(pr));
        approve.IsEnabled = !approved;
        var open = GlyphButton("↗", Loc.Get("OpenInBrowser", "Open"), () => OpenUrl(pr.Url));
        var feedbackBody = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 2) };
        var feedback = GlyphButton("💬", Loc.Get("PrFeedbackAction"), () =>
        {
            if (!_expandedPrs.Remove(pr.Number)) _expandedPrs.Add(pr.Number);
            feedbackBody.IsVisible = _expandedPrs.Contains(pr.Number);
            if (feedbackBody.IsVisible) _ = LoadFeedbackAsync(pr, feedbackBody);
        });
        feedbackBody.IsVisible = _expandedPrs.Contains(pr.Number);
        if (feedbackBody.IsVisible) _ = LoadFeedbackAsync(pr, feedbackBody);

        // CI, as one coloured dot ahead of the title; the tooltip names what failed
        if (pr.Checks != ChecksState.None)
        {
            var color = pr.Checks switch
            {
                ChecksState.Passing => Color.FromRgb(48, 209, 88),
                ChecksState.Failing => Color.FromRgb(255, 69, 58),
                _ => Color.FromRgb(255, 159, 10),
            };
            title.Inlines = new Avalonia.Controls.Documents.InlineCollection
            {
                new Avalonia.Controls.Documents.Run("● ") { Foreground = new SolidColorBrush(color) },
                new Avalonia.Controls.Documents.Run(title.Text),
            };
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Grid.SetColumn(title, 0);
        Grid.SetRow(title, 0);
        Grid.SetColumn(meta, 0);
        Grid.SetRow(meta, 1);

        var actions = Row(feedback, approve, open);
        actions.Margin = new Thickness(6, 0, 0, 0);
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1);
        Grid.SetRow(actions, 0);
        Grid.SetRowSpan(actions, 2);

        grid.Children.Add(title);
        grid.Children.Add(meta);
        grid.Children.Add(actions);
        Grid.SetRow(feedbackBody, 2);
        Grid.SetColumnSpan(feedbackBody, 2);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Children.Add(feedbackBody);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 3),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Divider()),
            BorderThickness = new Thickness(0.5),
        };
        // A branch pair does not fit a sidebar, so the row is where the whole of it lives.
        // On the two text lines, not the row: the row also holds the fold-out of comments
        var tip = "#" + pr.Number + "  " + pr.Title + "\n" + metaText;
        ToolTip.SetTip(title, tip);
        ToolTip.SetTip(meta, tip);
        return row;
    }

    /// <summary>
    /// Fills a pull request's fold-out: its failing checks and every reviewer comment, each with
    /// a button that hands it to the AI as a request to deal with it.
    /// </summary>
    private async Task LoadFeedbackAsync(PullRequestInfo pr, StackPanel body)
    {
        body.Children.Clear();
        body.Children.Add(Dim(Loc.Get("PrFeedbackLoading")));
        var items = await GitHubCli.GetFeedbackAsync(_repo, pr.Number);
        body.Children.Clear();

        if (pr.Checks == ChecksState.Failing)
        {
            var names = string.Join(", ", pr.FailingChecks ?? Array.Empty<string>());
            var fix = new Button { Content = Loc.Get("PrFixChecks"), FontSize = 11, Padding = new Thickness(8, 2) };
            fix.Click += (_, _) => _host.SendToTerminal(string.Format(Loc.Get("PrFixChecksPrompt"), pr.Number, names));
            body.Children.Add(Wrap(string.Format(Loc.Get("PrChecksFailing"), names), Color.FromRgb(255, 69, 58)));
            body.Children.Add(fix);
        }
        else if (pr.Checks != ChecksState.None)
        {
            body.Children.Add(Dim(Loc.Get(pr.Checks == ChecksState.Passing ? "PrChecksPassing" : "PrChecksPending")));
        }

        if (items.Count == 0)
        {
            body.Children.Add(Dim(Loc.Get("PrNoFeedback")));
            return;
        }

        var all = new Button { Content = Loc.Get("PrAddressAll"), FontSize = 11, Padding = new Thickness(8, 2) };
        all.Click += (_, _) => _host.SendToTerminal(string.Format(Loc.Get("PrAddressPrompt"), pr.Number,
            string.Join(" / ", items.Select(FeedbackLine))));
        body.Children.Add(all);

        foreach (var item in items)
        {
            var head = item.Author + (item.Path != null ? "  " + item.Path + (item.Line != null ? ":" + item.Line : "") : "");
            var one = GlyphButton("→", Loc.Get("PrAddressOne"), () =>
                _host.SendToTerminal(string.Format(Loc.Get("PrAddressPrompt"), pr.Number, FeedbackLine(item))));
            DockPanel.SetDock(one, Dock.Right);
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(Dim(head));
            var bodyText = Wrap(item.Body.Length > 400 ? item.Body[..400] + "…" : item.Body, null);
            text.Children.Add(bodyText);
            var line = new DockPanel { LastChildFill = true };
            line.Children.Add(one);
            line.Children.Add(text);
            body.Children.Add(new Border
            {
                Child = line,
                Padding = new Thickness(6, 3),
                BorderBrush = new SolidColorBrush(Divider()),
                BorderThickness = new Thickness(2, 0, 0, 0),
            });
        }

        TextBlock Dim(string s) => new()
        {
            Text = s, FontSize = 10, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        TextBlock Wrap(string s, Color? color)
        {
            var t = new TextBlock { Text = s, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            if (color != null) t.Foreground = new SolidColorBrush(color.Value);
            return t;
        }
    }

    /// <summary>One piece of feedback on one line, where it is if it is on a line of code.</summary>
    private static string FeedbackLine(PrFeedback f)
    {
        var body = f.Body.Replace("\r", " ").Replace("\n", " ");
        return f.Path != null
            ? $"[{f.Path}{(f.Line != null ? ":" + f.Line : "")}] {f.Author}: {body}"
            : $"{f.Author}: {body}";
    }

    private readonly HashSet<int> _expandedPrs = new();

    private async void ApprovePullRequest(PullRequestInfo pr)
    {
        if (_busy || _repo.Length == 0) return;

        // GitHub does not let an author approve their own pull request. That refusal comes back
        // as gh's message and is shown as-is: it is the answer, not a failure to hide.
        await RunAsync(Loc.Get("ApprovingStatus", "Approving..."),
            () => GitHubCli.ApproveAsync(_repo, pr.Number));
    }

    private async void OnCreatePullRequest()
    {
        if (_repo.Length == 0 || _busy || _ghReady != true || !_onGitHub) return;

        // `gh pr create` refuses outright when the current branch has never been pushed
        // ("aborted: you must first push the current branch to a remote") - catch it here with
        // a clear message instead of letting that raw gh error reach the user.
        if (!_branch.HasUpstream)
        {
            _host.ShowMessage(Loc.Get("CreatePrAction", "Pull request"),
                Loc.Get("PrNeedsPushFirst", "Push the current branch before creating a pull request."));
            return;
        }

        var repo = _repo;
        var subjectTask = GitWriteService.GetLastSubjectAsync(repo);
        var baseTask = GitHubCli.GetDefaultBranchAsync(repo);
        await Task.WhenAll(subjectTask, baseTask);
        if (!string.Equals(repo, _repo, StringComparison.OrdinalIgnoreCase)) return;

        var draft = await ShowPullRequestDialogAsync(subjectTask.Result, baseTask.Result);
        if (draft == null) return;

        string createdUrl = "";
        await RunAsync(Loc.Get("CreatingPrStatus", "Creating the pull request..."), async () =>
        {
            var result = await GitHubCli.CreateAsync(repo, draft.Value.Title, draft.Value.Body,
                draft.Value.Base);
            if (result.Ok) createdUrl = GitHubCli.ExtractUrl(result.StdOut + "\n" + result.StdErr);
            return result;
        });

        // gh has already published it by the time the URL comes back, so opening it is the last
        // step rather than a second confirmation.
        if (createdUrl.Length > 0) OpenUrl(createdUrl);
    }

    /// <summary>The three things gh needs, or null when the user backed out.</summary>
    private Task<(string Title, string Body, string Base)?> ShowPullRequestDialogAsync(
        string defaultTitle, string defaultBase)
    {
        var source = new TaskCompletionSource<(string, string, string)?>();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null)
        {
            source.SetResult(null);
            return source.Task;
        }

        var titleBox = new TextBox
        {
            Text = defaultTitle,
            FontSize = 13,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("PrTitleLabel", "Title"),
        };
        var bodyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 140,
            FontSize = 12.5,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("PrBodyLabel", "What does this change, and why?"),
        };
        var baseBox = new TextBox
        {
            Text = defaultBase,
            FontSize = 13,
            Padding = new Thickness(8, 6),
            PlaceholderText = Loc.Get("PrBaseLabel", "Merge into"),
        };

        var ok = new Button
        {
            Content = Loc.Get("CreatePrAction", "Create"),
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
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(22, 20) };
        panel.Children.Add(FieldLabel(Loc.Get("PrTitleLabel", "Title")));
        panel.Children.Add(titleBox);
        panel.Children.Add(FieldLabel(Loc.Get("PrBodyLabel", "Description")));
        panel.Children.Add(bodyBox);
        panel.Children.Add(FieldLabel(Loc.Get("PrBaseLabel", "Merge into")));
        panel.Children.Add(baseBox);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Get("CreatePrAction", "Pull request"),
            Width = 520,
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
            var title = (titleBox.Text ?? "").Trim();
            if (title.Length == 0) { titleBox.Focus(); return; }

            answered = true;
            source.TrySetResult((title, bodyBox.Text ?? "", (baseBox.Text ?? "").Trim()));
            dialog.Close();
        };
        cancel.Click += (_, _) => { answered = true; source.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => { if (!answered) source.TrySetResult(null); };

        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.Post(() => { titleBox.Focus(); titleBox.SelectAll(); });
        return source.Task;
    }

    private TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Foreground = new SolidColorBrush(DimText()),
    };

    // ── Windows this panel opens ───────────────────────────────────────

    private void OpenGraphWindow()
    {
        if (_repo.Length == 0) return;

        _host.OpenCommitGraph(_repo, System.IO.Path.GetFileName(_repo.TrimEnd('\\', '/')));
        ApplyGraphVisibility();
    }

    /// <summary>
    /// Called when a commit-history window opens or closes anywhere in the application, since
    /// either can be the one this panel's repository was showing.
    /// </summary>
    public void OnCommitGraphWindowsChanged() => ApplyGraphVisibility();

    /// <summary>
    /// The history is either in this panel or in its own window, never both: the same commits
    /// drawn twice are two readouts to keep in step, and the sidebar copy is the one with no room
    /// to be read. So the section folds away while the window is up and the file list takes the
    /// whole panel, and it comes back - at the height the user last dragged it to - when the
    /// window closes. Fetch, pull and push go with it; the window carries its own.
    /// </summary>
    private void ApplyGraphVisibility()
    {
        var show = !_host.IsCommitGraphOpen(_repo);
        if (show == _graphBorder.IsVisible) return;

        if (show)
        {
            _centre.RowDefinitions[1].Height = new GridLength(4);
            _centre.RowDefinitions[2].Height = _graphRow;
        }
        else
        {
            // Hiding the controls is not enough: a star-sized row keeps its share of the panel
            // whether or not anything is drawn in it, so the row itself has to go to nothing for
            // the change list to gain the space.
            _graphRow = _centre.RowDefinitions[2].Height;
            _centre.RowDefinitions[1].Height = new GridLength(0);
            _centre.RowDefinitions[2].Height = new GridLength(0);
        }

        _splitter.IsVisible = show;
        _graphBorder.IsVisible = show;
    }

    private async void ShowDiff(GitChange change)
    {
        string diff;
        try
        {
            diff = await GitChangeService.GetDiffAsync(_repo, change);
        }
        catch
        {
            diff = "";
        }

        if (string.IsNullOrWhiteSpace(diff))
        {
            _host.ShowMessage(Loc.Get("Diff"), Loc.Get("DiffEmpty"));
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        new DiffWindow(change.Path, diff, _isDark, _mono, change.Path, _host.SendToTerminal)
            .Show(owner);
    }

    /// <summary>
    /// Asks for a comment about a whole file and hands it to the session as "@path &lt;comment&gt;".
    /// The diff window does the same for a range of lines; this is the version for a file the
    /// user has already made up their mind about.
    /// </summary>
    private async void CommentOnFile(GitChange change)
    {
        var comment = await _host.TextInput(
            change.Path,
            Loc.Get("CommentOnFileHint", "What should change in this file?"),
            "");
        if (comment == null) return;

        // Only real line breaks: a comment may legitimately mention "\n" and mean the text.
        comment = System.Text.RegularExpressions.Regex.Replace(comment, @"\s*\r?\n\s*", " ").Trim();
        _host.SendToTerminal(comment.Length > 0
            ? "@" + change.Path + " " + comment
            : "@" + change.Path + " ");
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // No browser association, or the user cancelled the shell prompt.
        }
    }

    // ── Small builders ─────────────────────────────────────────────────

    private Button ToolButton(string text, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 11.5,
            Padding = new Thickness(8, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        if (tooltip.Length > 0) ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// The tooltip for a button that shows only a glyph: the name the button lost, then the
    /// sentence explaining it, so hovering answers both "what is this" and "what will it do".
    /// </summary>
    private static string ActionTip(string nameKey, string fallback, string tipKey, string suffix = "")
    {
        var tip = Loc.Get(tipKey, "");
        return Loc.Get(nameKey, fallback) + suffix + (tip.Length > 0 ? " - " + tip : "");
    }

    // The history header's actions drawn the way the editor next door draws them, so the row
    // reads as four actions rather than four unrelated arrows: a commit on a branch line with
    // the arrow that moves it, and a window in front of a window. Traced from the codicons of
    // the same names (repo-fetch, repo-pull, repo-push, multiple-windows) on their 16x16 grid.
    // F0 keeps the even-odd rule each of them needs - without it the ring fills in as a dot and
    // the window becomes a solid block.

    private const string IconFetch =
        "F0 M7.5 3C7.776 3 8 2.776 8 2.5V1.5C8 1.224 7.776 1 7.5 1C7.224 1 7 1.224 7 1.5V2.5C7 2.776 7.224 3 7.5 3Z " +
        "M7.5 10C7.372 10 7.245 9.95 7.15 9.85L4.15 6.85C4.05 6.755 4 6.628 4 6.5C4 6.372 4.05 6.245 4.15 6.15C4.245 6.05 4.373 6 4.5 6C4.627 6 4.755 6.05 4.85 6.15L7 8.29V7.5C7 7.22 7.22 7 7.5 7C7.78 7 8 7.22 8 7.5V8.29L10.15 6.15C10.245 6.05 10.372 6 10.5 6C10.628 6 10.755 6.05 10.85 6.15C10.95 6.245 11 6.373 11 6.5C11 6.627 10.95 6.755 10.85 6.85L7.85 9.85C7.755 9.95 7.628 10 7.5 10Z " +
        "M9.95 13H12.5C12.78 13 13 13.22 13 13.5C13 13.78 12.78 14 12.5 14H9.95C9.72 15.14 8.71 16 7.5 16C6.29 16 5.28 15.14 5.05 14H2.5C2.22 14 2 13.78 2 13.5C2 13.22 2.22 13 2.5 13H5.05C5.28 11.86 6.29 11 7.5 11C8.71 11 9.72 11.86 9.95 13ZM7.5 15C8.15 15 8.71 14.58 8.91 14C8.97 13.84 9 13.68 9 13.5C9 13.32 8.97 13.16 8.91 13C8.71 12.42 8.15 12 7.5 12C6.85 12 6.29 12.42 6.09 13C6.03 13.16 6 13.32 6 13.5C6 13.68 6.03 13.84 6.09 14C6.29 14.58 6.85 15 7.5 15Z " +
        "M8 5.5C8 5.776 7.776 6 7.5 6C7.224 6 7 5.776 7 5.5V4.5C7 4.224 7.224 4 7.5 4C7.776 4 8 4.224 8 4.5V5.5Z";

    private const string IconPull =
        "F0 M4.85 6.15C4.755 6.05 4.627 6 4.5 6C4.372 6 4.245 6.05 4.15 6.15C4.05 6.245 4 6.373 4 6.5C4 6.627 4.05 6.755 4.15 6.85L7.15 9.85C7.245 9.95 7.372 10 7.5 10C7.628 10 7.755 9.95 7.85 9.85L10.85 6.85C10.95 6.755 11 6.628 11 6.5C11 6.372 10.95 6.245 10.85 6.15C10.755 6.05 10.627 6 10.5 6C10.373 6 10.245 6.05 10.15 6.15L8 8.29V1.5C8 1.22 7.78 1 7.5 1C7.22 1 7 1.22 7 1.5V8.29L4.85 6.15Z " +
        "M9.95 13H12.5C12.78 13 13 13.22 13 13.5C13 13.78 12.78 14 12.5 14H9.95C9.72 15.14 8.71 16 7.5 16C6.29 16 5.28 15.14 5.05 14H2.5C2.22 14 2 13.78 2 13.5C2 13.22 2.22 13 2.5 13H5.05C5.28 11.86 6.29 11 7.5 11C8.71 11 9.72 11.86 9.95 13ZM6.09 14C6.29 14.58 6.85 15 7.5 15C8.15 15 8.71 14.58 8.91 14C8.97 13.84 9 13.68 9 13.5C9 13.32 8.97 13.16 8.91 13C8.71 12.42 8.15 12 7.5 12C6.85 12 6.29 12.42 6.09 13C6.03 13.16 6 13.32 6 13.5C6 13.68 6.03 13.84 6.09 14Z";

    private const string IconPush =
        "F0 M4.85 4.85C4.755 4.95 4.627 5 4.5 5C4.372 5 4.245 4.95 4.15 4.85C4.05 4.755 4 4.627 4 4.5C4 4.373 4.05 4.245 4.15 4.15L7.15 1.15C7.245 1.05 7.372 1 7.5 1C7.628 1 7.755 1.05 7.85 1.15L10.85 4.15C10.95 4.245 11 4.372 11 4.5C11 4.628 10.95 4.755 10.85 4.85C10.755 4.95 10.627 5 10.5 5C10.373 5 10.245 4.95 10.15 4.85L8 2.71V9.5C8 9.78 7.78 10 7.5 10C7.22 10 7 9.78 7 9.5V2.71L4.85 4.85Z " +
        "M9.95 13H12.5C12.78 13 13 13.22 13 13.5C13 13.78 12.78 14 12.5 14H9.95C9.72 15.14 8.71 16 7.5 16C6.29 16 5.28 15.14 5.05 14H2.5C2.22 14 2 13.78 2 13.5C2 13.22 2.22 13 2.5 13H5.05C5.28 11.86 6.29 11 7.5 11C8.71 11 9.72 11.86 9.95 13ZM6.09 14C6.29 14.58 6.85 15 7.5 15C8.15 15 8.71 14.58 8.91 14C8.97 13.84 9 13.68 9 13.5C9 13.32 8.97 13.16 8.91 13C8.71 12.42 8.15 12 7.5 12C6.85 12 6.29 12.42 6.09 13C6.03 13.16 6 13.32 6 13.5C6 13.68 6.03 13.84 6.09 14Z";

    private const string IconExpand =
        "F0 M10.5 13C11.878 13 13 11.879 13 10.5V3.5C13 2.121 11.878 1 10.5 1H3.5C2.122 1 1 2.121 1 3.5V10.5C1 11.879 2.122 13 3.5 13H10.5ZM3.5 2H10.5C11.327 2 12 2.673 12 3.5V4H2V3.5C2 2.673 2.673 2 3.5 2ZM2 10.5V5H12V10.5C12 11.327 11.327 12 10.5 12H3.5C2.673 12 2 11.327 2 10.5ZM15 5.5V10.5C15 12.98 12.98 15 10.5 15H5.5C4.68 15 3.96 14.61 3.5 14H10.5C12.43 14 14 12.43 14 10.5V3.5C14.61 3.96 15 4.68 15 5.5Z";

    /// <summary>The side of the codicon grid every icon here was drawn on.</summary>
    private const double IconGrid = 16;

    /// <summary>How much of that grid the icons take up on screen.</summary>
    private const double IconSize = 14;

    /// <summary>
    /// <see cref="GlyphButton"/> with a drawn icon in place of the glyph. The icon takes no
    /// Foreground of its own so it dims with the button when the action is unavailable.
    /// </summary>
    private Button IconButton(string pathData, string tooltip, Action onClick)
    {
        // PathIcon scales what is drawn, not the grid it was drawn on, so asking for one size
        // would blow a narrow icon up until it out-weighed a square one beside it. Sizing each
        // to its own bounds against the shared grid is what keeps the row to one scale.
        var data = Geometry.Parse(pathData);
        var bounds = data.Bounds;

        var button = new Button
        {
            Content = new PathIcon
            {
                Data = data,
                Width = bounds.Width * IconSize / IconGrid,
                Height = bounds.Height * IconSize / IconGrid,
            },
            Width = 22,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (tooltip.Length > 0) ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// A button the width of its glyph, for a row that has none to spare. What it does is on the
    /// tooltip, which is where a sidebar this narrow has to keep it.
    /// </summary>
    private Button GlyphButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            FontSize = 11,
            Width = 22,
            Height = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (tooltip.Length > 0) ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    /// <summary>
    /// A section the user can fold away, headed like the panel's other headings - a chevron and
    /// a word, on one line. The theme's Expander does the same job in a 40px header inside a
    /// framed box of its own, which in a sidebar this tall costs two file rows per section that
    /// the change list never gets back; and it sizes to its content, so the header sits short of
    /// the panel edge while its rows run past it.
    /// </summary>
    private sealed class FoldSection : StackPanel
    {
        private const string Folded = "▸";
        private const string Unfolded = "▾";
        private const string FoldedDrop = "▼";
        private const string UnfoldedDrop = "▲";

        private readonly TextBlock _chevron;
        private readonly TextBlock _drop;
        private readonly TextBlock _label;
        private readonly Control _body;
        private bool _isOpen;

        public FoldSection(string title, Control body, Color text, bool isDark, string tip = "")
        {
            _body = body;
            _body.IsVisible = false;

            _chevron = new TextBlock
            {
                Text = Folded,
                Width = 12,
                FontSize = 9,
                Foreground = new SolidColorBrush(text),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _label = new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(text),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // A second marker at the panel edge, where a drop-down keeps its arrow. The heading
            // reads as a heading, and a heading is not obviously something to click - the left
            // chevron is small and easy to take for a bullet, so the affordance is repeated
            // where the eye already looks for one.
            _drop = new TextBlock
            {
                Text = FoldedDrop,
                FontSize = 8,
                Opacity = 0.75,
                Margin = new Thickness(6, 0, 2, 0),
                Foreground = new SolidColorBrush(text),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(_chevron, 0);
            Grid.SetColumn(_label, 1);
            Grid.SetColumn(_drop, 2);
            grid.Children.Add(_chevron);
            grid.Children.Add(_label);
            grid.Children.Add(_drop);

            var header = new Border
            {
                Child = grid,
                Padding = new Thickness(8, 4),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            if (tip.Length > 0) ToolTip.SetTip(header, tip);

            var hover = new SolidColorBrush(isDark
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(20, 0, 0, 0));
            header.PointerEntered += (_, _) => header.Background = hover;
            header.PointerExited += (_, _) => header.Background = Brushes.Transparent;
            header.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) IsOpen = !IsOpen;
            };

            Children.Add(header);
            Children.Add(_body);
        }

        public string Title
        {
            get => _label.Text ?? "";
            set => _label.Text = value;
        }

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                _isOpen = value;
                _chevron.Text = value ? Unfolded : Folded;
                _drop.Text = value ? UnfoldedDrop : FoldedDrop;
                _body.IsVisible = value;
            }
        }
    }

    // ── Theme ──────────────────────────────────────────────────────────

    private Color Divider() => _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215);

    private Color DimText() => _isDark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(84, 84, 92);

    private Color WarningBg() =>
        _isDark ? Color.FromRgb(58, 44, 24) : Color.FromRgb(255, 248, 225);

    private Color WarningBorder() =>
        _isDark ? Color.FromRgb(120, 90, 40) : Color.FromRgb(232, 200, 120);

    private Color WarningText() =>
        _isDark ? Color.FromRgb(240, 200, 120) : Color.FromRgb(140, 90, 10);
}
