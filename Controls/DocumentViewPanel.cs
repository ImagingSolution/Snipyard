using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// Colours and metrics of the chat view, modelled on the Claude desktop app. Shared with the
/// composer that <see cref="Terminal.TerminalControl"/> draws under the transcript.
/// </summary>
public static class ChatTheme
{
    // Sampled from the desktop app's own dark and light themes
    public static Color Background(bool isDark) => isDark ? Color.FromRgb(21, 21, 21) : Color.FromRgb(252, 252, 251);
    public static Color UserBubble(bool isDark) => isDark ? Color.FromRgb(33, 33, 33) : Color.FromRgb(240, 240, 239);
    /// <summary>The user's own messages: a pale tab blue in light, a half-strength New Session blue in dark.</summary>
    public static Color UserMessage(bool isDark) => isDark ? Color.FromArgb(120, 0, 122, 255) : Color.FromArgb(20, 0, 122, 255);
    public static Color Surface(bool isDark) => isDark ? Color.FromRgb(32, 32, 31) : Color.FromRgb(255, 255, 255);
    public static Color Outline(bool isDark) => isDark ? Color.FromRgb(55, 55, 54) : Color.FromRgb(223, 223, 222);
    public static Color Hover(bool isDark) => isDark ? Color.FromRgb(40, 40, 40) : Color.FromRgb(240, 240, 239);
    public static Color Accent(bool isDark) => isDark ? Color.FromRgb(74, 144, 245) : Color.FromRgb(37, 99, 235);

    /// <summary>
    /// Prose face. The app ships its own Anthropic Sans; Segoe UI stands in for the Latin and
    /// Yu Gothic UI is what it falls back to for Japanese on Windows.
    /// </summary>
    public static readonly FontFamily BodyFont = new("Segoe UI, Yu Gothic UI, Meiryo UI");

    /// <summary>
    /// Prose size: same as the terminal font size, so the chat follows the user's font-size setting.
    /// </summary>
    public static double BodySize(double terminalFontSize) => terminalFontSize;
}

/// <summary>The reader's answer to one AskUserQuestion question: option indexes and typed text.</summary>
public record AskReply(bool Skipped, IReadOnlyList<int> Selected, string? OtherText);

/// <param name="SameTextLater">How many later prompts start with the same line: the CLI's list
/// shows only a line of each, so the match has to skip that many from the bottom.</param>
public record RewindTarget(string Uuid, string Text, int SameTextLater, bool Edit);

/// <summary>
/// The chat view: a Claude session's JSONL transcript rendered the way the desktop app shows a
/// conversation - user prompts as grey bubbles on the right, Claude's replies as plain Markdown
/// in a centred reading column, tool calls folded into one-line summaries that expand.
/// </summary>
public class DocumentViewPanel : Panel
{
    private readonly Border _header;
    private readonly TextBlock _titleText;
    private readonly Border _projectChip;
    private readonly TextBlock _projectText;
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _messagesStack;
    private readonly TextBlock _emptyLabel;
    private readonly Button _scrollDownButton;
    private readonly DispatcherTimer _pollTimer;
    private Control? _pendingView;
    private int _pendingUserCount;
    private DateTime _pendingSince;
    private string _pendingText = "";
    private static readonly TimeSpan PendingPromptTimeout = TimeSpan.FromSeconds(15);
    private readonly Control _workingView;
    private readonly WorkingSpinnerGlyph _workingGlyph;
    // The CLI's spinner line beside the glyph, e.g. "Compacting conversation… (22s · ↑ 1.4k tokens)"
    private readonly TextBlock _workingStatus;
    private bool _isWorking;
    // Prompts sent while Claude was busy, waiting for the turn to end
    private readonly StackPanel _queueView;
    private IReadOnlyList<string> _queueItems = Array.Empty<string>();
    // The selector the CLI is sitting on (permission, plan approval, menu), as a card after the reply
    private Control? _liveCard;

    /// <summary>The reader took back a queued prompt, by its index in the queue.</summary>
    public event Action<int>? QueuedPromptRemoved;

    // What is on screen, one entry per message, so a poll only rebuilds the changed tail
    private readonly List<string> _keys = new();
    private readonly List<Control?> _views = new();
    private readonly HashSet<string> _expandedGroups = new();
    // Single calls opened inside a group, by tool_use id
    private readonly HashSet<string> _expandedTools = new();
    // What the reader has picked on an open question, by tool_use id, so a rebuild keeps it
    private readonly Dictionary<string, AskState> _askStates = new();

    /// <summary>The reader submitted the open question: one reply per question, in order.</summary>
    public event Action<IReadOnlyList<AskUserQuestionItem>, IReadOnlyList<AskReply>>? AskAnswered;

    /// <summary>The reader dismissed the open question.</summary>
    public event Action? AskCancelled;

    /// <summary>
    /// The reader asked to go back to before a prompt: its uuid, its text, how many later prompts
    /// read the same (so the right one is picked from the CLI's list), and whether to edit it.
    /// </summary>
    public event Action<RewindTarget>? RewindRequested;

    // Set once a rewind went through: that bubble and everything after it stay hidden until the
    // transcript no longer has it on the live branch, which is when the next prompt forks it off
    private string? _hideFromUuid;
    private List<ConversationMessage> _shownMessages = new();
    /// <summary>
    /// Whether the CLI's question selector is on screen. The transcript cannot tell: a question
    /// cut off by a restart still reads as open until the next prompt is written.
    /// </summary>
    public Func<bool>? IsAskOpen { get; set; }

    // Task checklist and subagent list under the title, and the back bar while a subagent is open
    private readonly StackPanel _extrasPanel;
    private string _extrasKey = "";
    private bool _tasksExpanded = true;
    // The subagent transcript on screen in place of the session, or null for the session itself
    private string? _agentPath;
    private string _agentTitle = "";

    // The CLI's agent view - background sessions running in this folder - pinned above the input,
    // where the terminal shows its own background work
    private readonly Border _backgroundBar;
    private readonly StackPanel _backgroundList;
    private string _backgroundKey = "";
    private readonly List<(TextBlock Clock, DateTime Started)> _backgroundClocks = new();

    /// <summary>The folder the session runs in; background sessions are matched against it.</summary>
    public string? ProjectFolder { get; set; }

    /// <summary>What to call a background session, so a rename made in the windows panel shows here too.</summary>
    public static Func<BackgroundAgent, string>? BackgroundAgentName { get; set; }

    // Find bar (Ctrl+F): matches are message indices, boxed on a layer over the transcript
    private readonly Panel _scrollContent;
    private readonly Canvas _highlightLayer;
    private readonly Border _searchBar;
    private readonly TextBox _searchBox;
    private readonly TextBlock _searchCount;
    private List<ConversationMessage> _viewMessages = new();
    private readonly List<int> _matches = new();
    private int _matchIndex = -1;

    private string? _currentSessionPath;
    // Size of the transcript when it was last read. Not a line count: a poll that lands while
    // the CLI is still writing a row counts the half-written line, and the count stays the same
    // once the row is finished, so a question row would never get its card
    private long _lastFileLength;
    private bool _autoScroll = true;
    private bool _isDark;
    private Typeface _codeTypeface;
    private double _baseFontSize = ChatTheme.BodySize(14);
    private string _fontFamily = "Cascadia Mono, Consolas, Courier New";

    public DocumentViewPanel(bool isDark, Typeface codeTypeface)
    {
        _isDark = isDark;
        _codeTypeface = codeTypeface;
        // Every TextBlock below inherits the prose face; code sets its own
        SetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, ChatTheme.BodyFont);
        // The answers' blue-and-white selection for every text in the view, the user's own
        // posts included; the theme's default is a dark grey that leaves the text dark
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<SelectableTextBlock>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(SelectableTextBlock.SelectionBrushProperty, MarkdownParser.ChatSelectionBg),
                new Avalonia.Styling.Setter(SelectableTextBlock.SelectionForegroundBrushProperty, Brushes.White),
            },
        });

        _titleText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _projectText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        _projectChip = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _projectText,
            IsVisible = false,
        };
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        // The title may trim, the chip never does
        _titleText.MaxWidth = 520;
        Grid.SetColumn(_projectChip, 1);
        headerRow.Children.Add(_titleText);
        headerRow.Children.Add(_projectChip);
        _extrasPanel = new StackPanel { Spacing = 4 };
        _header = new Border
        {
            Padding = new Thickness(16, 9),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Spacing = 6, Children = { headerRow, _extrasPanel } },
        };
        Children.Add(_header);

        _messagesStack = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(24, 20, 24, 28),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _emptyLabel = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0),
            FontSize = 13,
        };
        var scrollContent = new Panel();
        scrollContent.Children.Add(_emptyLabel);
        scrollContent.Children.Add(_messagesStack);
        _highlightLayer = new Canvas { IsHitTestVisible = false };
        scrollContent.Children.Add(_highlightLayer);
        _scrollContent = scrollContent;
        // Boxes follow the bubbles when a reply grows or the window is resized
        _messagesStack.LayoutUpdated += (_, _) => { if (_searchBar?.IsVisible == true) PlaceHighlights(); };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            // Keep the bar on screen: auto-hide leaves no sign of where a long transcript is scrolled to
            AllowAutoHide = false,
            Content = scrollContent,
        };
        _scrollViewer.ScrollChanged += OnScrollChanged;
        Children.Add(_scrollViewer);
        ChatSelection.Attach(_messagesStack, _scrollViewer);

        _backgroundList = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 14, 0) };
        _backgroundBar = new Border
        {
            Padding = new Thickness(16, 6, 16, 6),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = false,
            Child = new ScrollViewer
            {
                MaxHeight = 220,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = _backgroundList,
            },
        };
        Children.Add(_backgroundBar);

        // Round "jump to latest" button, shown once the reader has scrolled away from the end
        _scrollDownButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false,
            Focusable = false,
        };
        ToolTip.SetTip(_scrollDownButton, Loc.Get("ChatScrollToBottom"));
        _scrollDownButton.Click += (_, _) =>
        {
            _autoScroll = true;
            ScrollToBottom();
        };
        Children.Add(_scrollDownButton);

        _searchBox = new TextBox
        {
            Width = 240,
            FontSize = 13,
            PlaceholderText = Loc.Get("ChatSearchPlaceholder"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _searchBox.TextChanged += (_, _) => UpdateMatches(jumpToNearest: true);
        _searchBox.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _searchCount = new TextBlock { FontSize = 12, MinWidth = 56, VerticalAlignment = VerticalAlignment.Center };
        Button SearchButton(string glyph, string tipKey, Action onClick)
        {
            var b = new Button
            {
                Content = glyph,
                FontSize = 12,
                Padding = new Thickness(7, 3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            ToolTip.SetTip(b, Loc.Get(tipKey));
            b.Click += (_, _) => onClick();
            return b;
        }
        _searchBar = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    _searchBox,
                    _searchCount,
                    SearchButton("▲", "ChatSearchPrev", () => StepMatch(-1)),
                    SearchButton("▼", "ChatSearchNext", () => StepMatch(1)),
                    SearchButton("✕", "ChatSearchClose", HideSearch),
                },
            },
        };
        Children.Add(_searchBar);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ShowSearch(); e.Handled = true; }
            else if (e.Key == Key.F3 && _searchBar.IsVisible) { StepMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _workingStatus = new TextBlock
        {
            Text = " ",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _workingGlyph = new WorkingSpinnerGlyph { VerticalAlignment = VerticalAlignment.Center };
        _workingView = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { _workingGlyph, _workingStatus },
        };
        _queueView = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };

        ClipToBounds = true;
        ApplyChrome();
        SetEmptyState(Loc.Get("NoSession", "No session loaded"));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += OnPollTick;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _header.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double headerH = _header.DesiredSize.Height;
        _backgroundBar.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double barH = _backgroundBar.IsVisible ? _backgroundBar.DesiredSize.Height : 0;
        _scrollViewer.Measure(new Size(availableSize.Width, Math.Max(0, availableSize.Height - headerH - barH)));
        _scrollDownButton.Measure(new Size(32, 32));
        _searchBar.Measure(availableSize);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double headerH = _header.DesiredSize.Height;
        _header.Arrange(new Rect(0, 0, finalSize.Width, headerH));
        double barH = _backgroundBar.IsVisible ? _backgroundBar.DesiredSize.Height : 0;
        double scrollH = Math.Max(0, finalSize.Height - headerH - barH);
        _scrollViewer.MaxHeight = scrollH;
        _scrollViewer.Arrange(new Rect(0, headerH, finalSize.Width, scrollH));
        _backgroundBar.Arrange(new Rect(0, headerH + scrollH, finalSize.Width, barH));
        _scrollDownButton.Arrange(new Rect((finalSize.Width - 32) / 2, headerH + scrollH - 32 - 12, 32, 32));
        var sb = _searchBar.DesiredSize;
        _searchBar.Arrange(new Rect(Math.Max(0, finalSize.Width - sb.Width - 24), headerH + 8, sb.Width, sb.Height));
        return finalSize;
    }

    // ── Public API (driven by TerminalControl) ──

    public void LoadSession(string jsonlPath)
    {
        if (jsonlPath != _currentSessionPath) _hideFromUuid = null;
        _currentSessionPath = jsonlPath;
        _agentPath = null;
        // A first prompt creates the session file, so the file turns up only after the prompt
        // was sent: keep its stand-in bubble and let the prompt's text in the new file retire it
        if (_pendingView != null && DateTime.UtcNow - _pendingSince <= PendingPromptTimeout)
            _pendingUserCount = int.MaxValue;
        else
            _pendingView = null;
        _autoScroll = true;
        ClearViews();
        Refresh(force: true);
        ScrollToBottom();
    }

    /// <summary>
    /// Shows a just-sent prompt straight away. The CLI writes it to the transcript a moment later
    /// and the poll picks it up after that; until then this bubble stands in for it.
    /// </summary>
    public void ShowPendingPrompt(string text)
    {
        RemovePendingView();
        // A prompt goes to the session, so show the session it lands in
        if (_agentPath != null) ShowAgent(null, "");
        // Slash commands are not always written to the transcript as a prompt
        // No session file yet (a new session's first prompt) still gets its bubble at once
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/')) return;

        _pendingUserCount = _keys.Count(k => k.StartsWith("0|"));
        _pendingSince = DateTime.UtcNow;
        _pendingText = text.Trim();
        var msg = new ConversationMessage(MessageRole.User, text, DateTime.Now, null, false, false);
        var view = CreateUserView(msg, _keys.Count > 0 ? msg : null);
        _pendingView = view;
        _messagesStack.Children.Add(view);
        PlaceWorkingView();
        SetEmptyState(null);
        _autoScroll = true;
        ScrollToBottom();

        // The poll retires the bubble, but without a session file there is no poll
        DispatcherTimer.RunOnce(() =>
        {
            if (!ReferenceEquals(_pendingView, view)) return;
            RemovePendingView();
            _pendingView = null;
            PlaceWorkingView();
            SetEmptyState(_messagesStack.Children.Count == 0 ? "" : null);
        }, PendingPromptTimeout + TimeSpan.FromSeconds(1));
    }

    /// <summary>Whether the transcript now holds the prompt the stand-in bubble shows.</summary>
    private bool PendingPromptArrived(List<ConversationMessage> messages)
    {
        if (messages.Count(m => m.Role == MessageRole.User) > _pendingUserCount) return true;
        // Counting works within one file; only a file loaded after the send needs the text
        if (_pendingUserCount != int.MaxValue) return false;
        var last = messages.LastOrDefault(m => m.Role == MessageRole.User && !m.IsToolUse);
        // The transcript's copy may carry attachment paths the bubble left out
        return last != null && _pendingText.Length > 0
            && last.Text.Contains(_pendingText, StringComparison.Ordinal);
    }

    private void RemovePendingView()
    {
        if (_pendingView != null) _messagesStack.Children.Remove(_pendingView);
    }

    /// <summary>
    /// Whether the CLI is mid-turn; shows the spinner under the transcript while it is, with the
    /// terminal's spinner line beside it when there is one.
    /// </summary>
    public void SetWorking(bool working, string? status = null)
    {
        // Never empty, so the row keeps its height when the status line goes away
        status = working && !string.IsNullOrEmpty(status) ? status : " ";
        if (_workingStatus.Text != status)
        {
            _workingStatus.Text = status;
            _workingStatus.FontSize = _baseFontSize * 0.9;
            _workingStatus.Foreground = Brush(Palette.Dim);
        }
        if (working == _isWorking) return;
        _isWorking = working;
        PlaceWorkingView();
        if (_autoScroll) ScrollToBottom();
    }

    /// <summary>
    /// Whether a prompt sent now would land mid-turn: the CLI is working, or a prompt was just
    /// sent and the CLI has not started on it yet.
    /// </summary>
    public bool IsBusy => _isWorking || _pendingView != null;

    /// <summary>Whether a just-sent prompt is still waiting to show up in the transcript.</summary>
    public bool HasPendingPrompt => _pendingView != null;

    /// <summary>
    /// Whether the transcript shows a question still waiting on an answer. The CLI may hold the
    /// question back from the transcript until it is answered, so the selector can be on screen
    /// while this is false.
    /// </summary>
    public bool HasOpenAskCard => _shownMessages.Any(m => m.PendingAskId != null);

    /// <summary>Shows the prompts waiting for the turn to end, as chips under the transcript.</summary>
    public void SetQueue(IReadOnlyList<string> items)
    {
        _queueItems = items.ToArray();
        RebuildQueueView();
        PlaceWorkingView();
        if (_autoScroll) ScrollToBottom();
    }

    private void RebuildQueueView()
    {
        _queueView.Children.Clear();
        var pal = Palette;
        for (int i = 0; i < _queueItems.Count; i++)
        {
            int index = i;
            var text = _queueItems[i].ReplaceLineEndings(" ").Trim();
            var label = new TextBlock
            {
                Text = Loc.Get("ChatQueued"),
                FontSize = _baseFontSize * 0.8,
                Foreground = Brush(pal.Dim),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var body = new TextBlock
            {
                Text = text,
                FontSize = _baseFontSize * 0.9,
                Foreground = Brush(pal.Fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 520,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(body, _queueItems[i]);
            var remove = new Button
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(5, 1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brush(pal.Dim),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
            ToolTip.SetTip(remove, Loc.Get("ChatQueueRemove"));
            remove.Click += (_, _) => QueuedPromptRemoved?.Invoke(index);
            _queueView.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush(ChatTheme.Outline(_isDark)),
                Background = Brush(ChatTheme.Surface(_isDark)),
                Padding = new Thickness(10, 3, 4, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { label, body, remove },
                },
            });
        }
    }

    // The spinner stays the last thing in the column, with the queue after it. A just-sent prompt
    // shows the spinner too, since the CLI takes a moment to start its own.
    private void PlaceWorkingView()
    {
        if (_liveCard != null) _messagesStack.Children.Remove(_liveCard);
        _messagesStack.Children.Remove(_workingView);
        _messagesStack.Children.Remove(_queueView);
        // A fixed-width cell, so the text beside it holds still while the glyph changes shape
        _workingGlyph.FontSize = _baseFontSize;
        _workingGlyph.Width = _baseFontSize * 1.2;
        _workingStatus.FontSize = _baseFontSize * 0.9;
        if (_liveCard != null) _messagesStack.Children.Add(_liveCard);
        // Between turns the row stays in place, only blank: the CLI's working state flickers
        // during a reply, and taking the row out shortened the column and moved the text
        bool busy = IsBusy;
        _workingView.Opacity = busy ? 1 : 0;
        _workingGlyph.Spinning = busy;
        if (busy || _messagesStack.Children.Count > 0) _messagesStack.Children.Add(_workingView);
        if (_queueItems.Count > 0) _messagesStack.Children.Add(_queueView);
    }

    /// <summary>
    /// Shows the card for the selector the CLI is waiting on after the last reply, where it reads
    /// as part of the conversation instead of covering it; null takes it down.
    /// </summary>
    public void SetLiveCard(Control? card)
    {
        if (ReferenceEquals(card, _liveCard)) return;
        if (_liveCard != null) _messagesStack.Children.Remove(_liveCard);
        _liveCard = card;
        PlaceWorkingView();
        if (card == null) return;
        SetEmptyState(null);
        _autoScroll = true;
        ScrollToBottom();
    }

    /// <summary>Hides a rewound prompt and what followed it (see <see cref="_hideFromUuid"/>).</summary>
    public void HideFrom(string uuid)
    {
        _hideFromUuid = uuid;
        Refresh(force: false);
        if (_autoScroll) ScrollToBottom();
    }

    /// <summary>A short-lived line under the transcript, for something the reader asked for that did not happen.</summary>
    public void ShowNotice(string text)
    {
        var line = CreateStatusLine(text, Palette.Dim);
        _messagesStack.Children.Add(line);
        if (_autoScroll) ScrollToBottom();
        DispatcherTimer.RunOnce(() => _messagesStack.Children.Remove(line), TimeSpan.FromSeconds(6));
    }

    public void StartPolling()
    {
        UpdateBackgroundAgents();
        _pollTimer.Start();
    }

    public void StopPolling() => _pollTimer.Stop();

    public void Clear()
    {
        ClearViews();
        _pendingView = null;
        _currentSessionPath = null;
        _agentPath = null;
        _extrasPanel.Children.Clear();
        _extrasKey = "";
        _lastFileLength = 0;
        SetEmptyState(Loc.Get("NoSession", "No session loaded"));
        _titleText.Text = "";
        _projectChip.IsVisible = false;
    }

    public void SetFont(string fontFamily, double fontSize)
    {
        _fontFamily = fontFamily + ", Consolas, Courier New";
        _codeTypeface = new Typeface(_fontFamily);
        _baseFontSize = ChatTheme.BodySize(fontSize);
        Rebuild();
    }

    public void UpdateTheme(bool isDark)
    {
        _isDark = isDark;
        ApplyChrome();
        Rebuild();
    }

    // ── Loading ──

    private void Rebuild()
    {
        ClearViews();
        Refresh(force: true);
    }

    private void ClearViews()
    {
        _messagesStack.Children.Clear();
        _keys.Clear();
        _views.Clear();
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        UpdateBackgroundAgents();
        var path = _agentPath ?? _currentSessionPath;
        if (string.IsNullOrEmpty(path)) return;
        if (!System.IO.File.Exists(path)) return;
        bool pendingExpired = _pendingView != null && DateTime.UtcNow - _pendingSince > PendingPromptTimeout;
        if (FileLength(path) == _lastFileLength && !pendingExpired) return;

        if (Refresh(force: false) && _autoScroll)
            ScrollToBottom();
    }

    /// <summary>Re-reads the transcript and brings the view in line; true if anything changed.</summary>
    private bool Refresh(bool force)
    {
        var path = _agentPath ?? _currentSessionPath;
        if (path == null) return false;

        // Taken before reading, so bytes that land during the read trigger another pass
        _lastFileLength = FileLength(path);
        var messages = SessionMessageReader.ReadSession(path);
        if (_agentPath == null)
        {
            if (_hideFromUuid != null)
            {
                int cut = messages.FindIndex(m => m.Uuid == _hideFromUuid);
                if (cut >= 0) messages.RemoveRange(cut, messages.Count - cut);
                else _hideFromUuid = null;
            }
            _shownMessages = messages;
            UpdateHeader(path, messages);
        }
        _viewMessages = messages;
        UpdateExtras(messages);
        RemovePendingView();
        if (_liveCard != null) _messagesStack.Children.Remove(_liveCard);
        _messagesStack.Children.Remove(_workingView);
        _messagesStack.Children.Remove(_queueView);

        // Keep the longest prefix that is unchanged and rebuild only what follows it, so
        // expanded groups and the scroll position survive a new reply arriving.
        int common = 0;
        if (!force)
            while (common < _keys.Count && common < messages.Count && _keys[common] == KeyOf(messages[common]))
                common++;

        bool changed = common < _keys.Count || common < messages.Count;
        for (int k = _keys.Count - 1; k >= common; k--)
        {
            if (_views[k] != null) _messagesStack.Children.Remove(_views[k]!);
            _keys.RemoveAt(k);
            _views.RemoveAt(k);
        }
        for (int k = common; k < messages.Count; k++)
        {
            var key = KeyOf(messages[k]);
            var view = CreateMessageView(messages[k], key, k > 0 ? messages[k - 1] : null);
            if (view != null) _messagesStack.Children.Add(view);
            _keys.Add(key);
            _views.Add(view);
        }

        // Keep the stand-in bubble last until the transcript holds the prompt it stands for
        if (_pendingView != null)
        {
            if (PendingPromptArrived(messages) ||DateTime.UtcNow - _pendingSince > PendingPromptTimeout)
                _pendingView = null;
            else
                _messagesStack.Children.Add(_pendingView);
        }
        PlaceWorkingView();
        if (_searchBar.IsVisible && changed) UpdateMatches(jumpToNearest: false);

        SetEmptyState(_messagesStack.Children.Count == 0 ? "" : null);
        return changed;
    }

    private static string KeyOf(ConversationMessage m)
    {
        // A call's result arriving changes its key, so the row loses its "running" mark
        var tools = m.Tools == null ? "" : string.Join("\u001F",
            m.Tools.Select(t => t.Name + ":" + t.Detail + ":" + (t.Result == null ? "-" : t.IsError ? "e" : "r")));
        var answers = m.AskUser == null ? "" : string.Join("\u001F", m.AskUser.Answers.Select(a => a.Key + "=" + a.Value));
        return $"{(int)m.Role}|{m.IsToolUse}|{m.IsThinking}|{m.IsToolRejection}|{m.Images?.Count ?? 0}|{tools}|{answers}|{m.PendingAskId}|{m.Narration}|{m.Text}";
    }

    private void UpdateHeader(string path, List<ConversationMessage> messages)
    {
        var meta = SessionMessageReader.ReadSessionMeta(path);
        string? title = meta.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            var first = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Text));
            title = first?.Text.Split('\n')[0].Trim();
        }
        if (string.IsNullOrWhiteSpace(title))
            title = System.IO.Path.GetFileNameWithoutExtension(path);
        _titleText.Text = title;
        ToolTip.SetTip(_titleText, title);

        var project = string.IsNullOrEmpty(meta.Cwd) ? null : System.IO.Path.GetFileName(meta.Cwd.TrimEnd('\\', '/'));
        _projectText.Text = project ?? "";
        _projectChip.IsVisible = !string.IsNullOrEmpty(project);
        ToolTip.SetTip(_projectChip, meta.Cwd);
    }

    // ── Find in conversation ──

    public void ShowSearch()
    {
        _searchBar.IsVisible = true;
        _searchBox.Focus();
        _searchBox.SelectAll();
        UpdateMatches(jumpToNearest: true);
    }

    /// <summary>The find bar closed, so the composer can take the keyboard back.</summary>
    public event Action? SearchClosed;

    private void HideSearch()
    {
        _searchBar.IsVisible = false;
        _matches.Clear();
        _matchIndex = -1;
        _highlightLayer.Children.Clear();
        SearchClosed?.Invoke();
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.F3:
                StepMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                HideSearch();
                e.Handled = true;
                break;
        }
    }

    // What the reader can see of a message: its text, what was said beside its tools, and the
    // one-line summary of each call
    private static string SearchableText(ConversationMessage m)
    {
        var sb = new System.Text.StringBuilder(m.Text);
        if (m.Narration != null) sb.Append('\n').Append(m.Narration);
        if (m.Tools != null)
            foreach (var t in m.Tools) sb.Append('\n').Append(t.Name).Append(' ').Append(t.Detail);
        if (m.AskUser != null)
            foreach (var q in m.AskUser.Questions)
            {
                sb.Append('\n').Append(q.Question);
                foreach (var o in q.Options) sb.Append('\n').Append(o.Label);
            }
        return sb.ToString();
    }

    /// <summary>
    /// Finds the messages holding the query. A new query lands on the match nearest the end,
    /// where the reader usually is; a transcript update keeps the current match where it was.
    /// </summary>
    private void UpdateMatches(bool jumpToNearest)
    {
        int keep = _matchIndex >= 0 && _matchIndex < _matches.Count ? _matches[_matchIndex] : -1;
        _matches.Clear();
        _matchIndex = -1;
        var query = _searchBox.Text?.Trim() ?? "";
        if (query.Length > 0)
            for (int i = 0; i < _viewMessages.Count && i < _views.Count; i++)
                if (_views[i] != null && SearchableText(_viewMessages[i]).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    _matches.Add(i);

        if (_matches.Count > 0)
        {
            _matchIndex = jumpToNearest || keep < 0 ? _matches.Count - 1 : Math.Max(0, _matches.IndexOf(keep));
            if (jumpToNearest) BringMatchIntoView();
        }
        _searchCount.Text = query.Length == 0 ? ""
            : _matches.Count == 0 ? Loc.Get("ChatSearchNone") : $"{_matchIndex + 1}/{_matches.Count}";
        PlaceHighlights();
    }

    private void StepMatch(int delta)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
        _searchCount.Text = $"{_matchIndex + 1}/{_matches.Count}";
        BringMatchIntoView();
        PlaceHighlights();
    }

    private void BringMatchIntoView()
    {
        if (_matchIndex < 0 || _views[_matches[_matchIndex]] is not { } view) return;
        _autoScroll = false;
        // Wait for a fresh view to be laid out before asking where it is
        Dispatcher.UIThread.Post(() =>
        {
            var p = view.TranslatePoint(new Point(0, 0), _scrollContent);
            if (p == null) return;
            double target = p.Value.Y - Math.Max(0, (_scrollViewer.Viewport.Height - view.Bounds.Height) / 3);
            _scrollViewer.Offset = new Vector(0, Math.Max(0, target));
        }, DispatcherPriority.Background);
    }

    private void PlaceHighlights()
    {
        var pal = Palette;
        int needed = _matches.Count;
        while (_highlightLayer.Children.Count > needed) _highlightLayer.Children.RemoveAt(_highlightLayer.Children.Count - 1);
        while (_highlightLayer.Children.Count < needed)
            _highlightLayer.Children.Add(new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5) });
        for (int k = 0; k < needed; k++)
        {
            var box = (Border)_highlightLayer.Children[k];
            var view = _views[_matches[k]];
            var p = view?.TranslatePoint(new Point(0, 0), _scrollContent);
            if (view == null || p == null) { box.IsVisible = false; continue; }
            bool current = k == _matchIndex;
            box.IsVisible = true;
            box.Width = view.Bounds.Width + 12;
            box.Height = view.Bounds.Height + 8;
            Canvas.SetLeft(box, p.Value.X - 6);
            Canvas.SetTop(box, p.Value.Y - 4);
            box.Background = new SolidColorBrush(AccentColor, current ? 0.14 : 0.06);
            box.BorderBrush = current ? Brush(AccentColor) : Brushes.Transparent;
        }
    }

    // ── Tasks and subagents ──

    private Color AccentColor => ChatTheme.Accent(_isDark);
    private static readonly Color DoneColor = Color.FromRgb(96, 165, 96);

    /// <summary>The reader asked to open a file a tool call worked on, in an editor window.</summary>
    public event Action<string>? FileOpenRequested;

    /// <summary>Opens a subagent's transcript in place of the session, or goes back with null.</summary>
    private void ShowAgent(string? transcriptPath, string title)
    {
        _agentPath = transcriptPath;
        _agentTitle = title;
        _pendingView = null;
        _autoScroll = true;
        ClearViews();
        Refresh(force: true);
        ScrollToBottom();
    }

    /// <summary>
    /// Rebuilds the strip under the title: the back bar while a subagent is open, otherwise the
    /// task checklist and the subagent list, each folded to one line until clicked.
    /// </summary>
    private void UpdateExtras(List<ConversationMessage> messages)
    {
        List<ChatTask> tasks = new();
        if (_agentPath == null && _currentSessionPath != null)
            tasks = ChatTaskTracker.ExtractTasks(messages);
        var key = string.Join("\u001E",
            _isDark, _baseFontSize, _agentPath, _agentTitle, _tasksExpanded,
            string.Join("\u001F", tasks.Select(t => $"{t.Id}|{t.Status}|{t.Subject}|{t.ActiveForm}")));
        if (key == _extrasKey) return;
        _extrasKey = key;
        _extrasPanel.Children.Clear();

        var pal = Palette;
        double size = _baseFontSize * 0.85;

        if (_agentPath != null)
        {
            var back = FlatButton(Loc.Get("ChatBackToMain"), size, AccentColor);
            back.Click += (_, _) => ShowAgent(null, "");
            _extrasPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    back,
                    new TextBlock
                    {
                        Text = string.Format(Loc.Get("ChatSubagentTitle"), _agentTitle),
                        FontSize = size,
                        Foreground = Brush(pal.Dim),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            });
            return;
        }
        if (tasks.Count == 0) return;

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        _extrasPanel.Children.Add(toggles);

        {
            int done =tasks.Count(t => t.Status == ChatTaskStatus.Completed);
            var toggle = FlatButton((_tasksExpanded ? "▾ " : "▸ ")
                + string.Format(Loc.Get("ChatTasks"), done, tasks.Count), size, pal.Fg);
            toggle.Click += (_, _) => { _tasksExpanded = !_tasksExpanded; UpdateExtras(messages); };
            toggles.Children.Add(toggle);

            // Folded, the line still says what Claude is on right now
            var current = tasks.FirstOrDefault(t => t.Status == ChatTaskStatus.InProgress);
            if (!_tasksExpanded && current != null)
                toggles.Children.Add(new TextBlock
                {
                    Text = current.ActiveForm ?? current.Subject,
                    FontSize = size,
                    Foreground = Brush(AccentColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 600,
                });
        }
        if (_tasksExpanded)
        {
            var list = new StackPanel { Spacing = 2, Margin = new Thickness(14, 0, 0, 2) };
            foreach (var t in tasks)
            {
                var (glyph, color) = t.Status switch
                {
                    ChatTaskStatus.Completed => ("✓", DoneColor),
                    ChatTaskStatus.InProgress => ("◐", AccentColor),
                    _ => ("○", pal.Dim),
                };
                list.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = glyph, FontSize = size, Foreground = Brush(color), Width = 14 },
                        new TextBlock
                        {
                            Text = t.Status == ChatTaskStatus.InProgress ? t.ActiveForm ?? t.Subject : t.Subject,
                            FontSize = size,
                            Foreground = Brush(t.Status == ChatTaskStatus.Completed ? pal.Dim : pal.Fg),
                            FontWeight = t.Status == ChatTaskStatus.InProgress ? FontWeight.SemiBold : FontWeight.Normal,
                            TextDecorations = t.Status == ChatTaskStatus.Completed ? TextDecorations.Strikethrough : null,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                });
            }
            _extrasPanel.Children.Add(list);
        }
    }

    private Button FlatButton(string text, double size, Color fg) => new()
    {
        Content = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 },
        Foreground = Brush(fg),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0, 1),
        Cursor = new Cursor(StandardCursorType.Hand),
        VerticalAlignment = VerticalAlignment.Center,
        Focusable = false,
    };

    private void SetEmptyState(string? text)
    {
        if (text != null) _emptyLabel.Text = text;
        _emptyLabel.IsVisible = !string.IsNullOrEmpty(text) && _messagesStack.Children.Count == 0;
    }

    // ── Theme ──

    private SolidColorBrush Brush(Color c) => new(c);
    private MarkdownParser.ChatPalette Palette => MarkdownParser.ChatPalette.For(_isDark);

    private void ApplyChrome()
    {
        var pal = Palette;
        Background = Brush(ChatTheme.Background(_isDark));
        _header.Background = Brush(ChatTheme.Background(_isDark));
        _workingGlyph.Foreground = Brush(ChatTheme.Accent(_isDark));
        _header.BorderBrush = Brush(pal.Border);
        _titleText.Foreground = Brush(pal.Fg);
        _projectChip.Background = Brush(ChatTheme.UserBubble(_isDark));
        _projectText.Foreground = Brush(pal.Dim);
        _emptyLabel.Foreground = Brush(pal.Dim);
        _searchBar.Background = Brush(ChatTheme.Surface(_isDark));
        _searchBar.BorderBrush = Brush(ChatTheme.Outline(_isDark));
        _searchCount.Foreground = Brush(pal.Dim);
        foreach (var b in ((StackPanel)_searchBar.Child!).Children.OfType<Button>())
            b.Foreground = Brush(pal.Fg);

        _scrollDownButton.Background = Brush(ChatTheme.Surface(_isDark));
        _scrollDownButton.BorderBrush = Brush(ChatTheme.Outline(_isDark));
        _scrollDownButton.Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M1,1 L6,6 L11,1"),
            Stroke = Brush(pal.Dim),
            StrokeThickness = 1.6,
            Width = 11,
            Height = 6,
            Stretch = Stretch.Uniform,
        };
        _backgroundBar.Background = Brush(ChatTheme.Background(_isDark));
        _backgroundBar.BorderBrush = Brush(pal.Border);
        RebuildQueueView();
        _backgroundKey = "";
        UpdateBackgroundAgents();
    }

    /// <summary>
    /// Lists what is working behind the prompt above the input: the live background sessions of
    /// this folder, then the subagents this session still has running - the same rows the
    /// windows panel puts under the window. Hidden when there are none.
    /// </summary>
    private void UpdateBackgroundAgents()
    {
        var agents = AgentViewMonitor.ReadActive(ProjectFolder);
        var subagents = SubagentMonitor.ReadRunning(_currentSessionPath);
        var subagentDir = _currentSessionPath == null ? null
            : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_currentSessionPath) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(_currentSessionPath), "subagents");
        string? TranscriptOf(SubagentRun s)
        {
            if (subagentDir == null) return null;
            var path = System.IO.Path.Combine(subagentDir, "agent-" + s.Id + ".jsonl");
            return System.IO.File.Exists(path) ? path : null;
        }
        string Name(BackgroundAgent a) => BackgroundAgentName?.Invoke(a) ?? a.Name;
        static string Elapsed(DateTime started)
        {
            var span = DateTime.Now - started;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            return span.TotalHours >= 1 ? (int)span.TotalHours + "h" + span.Minutes + "m"
                : span.TotalMinutes >= 1 ? span.Minutes + "m" + span.Seconds + "s"
                : span.Seconds + "s";
        }

        var key = string.Join("\u001E", _isDark, _baseFontSize,
            string.Join("\u001F", agents.Select(a =>
                $"{a.Id}|{a.State}|{a.ProcessAlive}|{Name(a)}|{a.Detail}|{a.Started.Ticks}")),
            string.Join("\u001F", subagents.Select(s =>
                $"{s.Id}|{s.AgentType}|{s.Label}|{s.Model}|{s.Started.Ticks}|{TranscriptOf(s) != null}")));
        // The clocks tick in place: rebuilding the rows every second would drop a hovered tooltip
        if (key == _backgroundKey)
        {
            foreach (var (clock, started) in _backgroundClocks)
                clock.Text = Elapsed(started);
            return;
        }
        _backgroundKey = key;
        _backgroundList.Children.Clear();
        _backgroundClocks.Clear();

        bool wasVisible = _backgroundBar.IsVisible;
        _backgroundBar.IsVisible = agents.Count + subagents.Count > 0;
        var pal = Palette;
        double size = _baseFontSize * 0.85;
        TextBlock SectionTitle(string text) => new()
        {
            Text = text,
            FontSize = size * 0.9,
            Foreground = Brush(pal.Dim),
        };
        TextBlock Clock(DateTime started)
        {
            var clock = new TextBlock
            {
                Text = Elapsed(started),
                FontSize = size * 0.9,
                Foreground = Brush(pal.Dim),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            _backgroundClocks.Add((clock, started));
            return clock;
        }
        if (agents.Count > 0)
        {
            _backgroundList.Children.Add(SectionTitle(string.Format(Loc.Get("ChatBackgroundAgents"), agents.Count)));
            foreach (var a in agents)
            {
                bool blocked = string.Equals(a.State, "blocked", StringComparison.OrdinalIgnoreCase);
                // The windows panel's reading: orange mid-turn, yellow waiting on the user,
                // hollow when no process is behind the session right now
                var color = blocked ? Color.FromRgb(255, 214, 10) : Color.FromRgb(255, 159, 10);
                var dot = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = a.ProcessAlive ? Brush(color) : null,
                    Stroke = a.ProcessAlive ? null : Brush(color),
                    StrokeThickness = 1,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 8, 0),
                };
                var name = new TextBlock
                {
                    Text = Name(a),
                    FontSize = size,
                    Foreground = Brush(pal.Fg),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var detail = new TextBlock
                {
                    Text = blocked ? Loc.Get("AgentStateBlocked") : a.Detail ?? "",
                    FontSize = size * 0.95,
                    Foreground = Brush(blocked ? color : pal.Dim),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                var elapsed = Clock(a.Started);
                // The name keeps up to half the row; the summary takes what is left
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
                name.MaxWidth = 360;
                Grid.SetColumn(name, 1);
                Grid.SetColumn(detail, 2);
                Grid.SetColumn(elapsed, 3);
                row.Children.Add(dot);
                row.Children.Add(name);
                row.Children.Add(detail);
                row.Children.Add(elapsed);

                var tip = Name(a) + Environment.NewLine
                    + Loc.Get(blocked ? "AgentStateBlocked" : "AgentStateWorking") + "  ·  " + a.Id;
                if (!string.IsNullOrEmpty(a.Detail)) tip += Environment.NewLine + a.Detail;
                if (!string.IsNullOrEmpty(a.Intent) && a.Intent != a.Name)
                    tip += Environment.NewLine + Environment.NewLine + a.Intent;
                ToolTip.SetTip(row, tip);
                _backgroundList.Children.Add(row);
            }
        }
        if (subagents.Count > 0)
        {
            if (agents.Count > 0) _backgroundList.Children.Last().Margin = new Thickness(0, 0, 0, 4);
            _backgroundList.Children.Add(SectionTitle(string.Format(Loc.Get("ChatSubagents"), subagents.Count)));
            foreach (var s in subagents)
            {
                var type = string.IsNullOrEmpty(s.AgentType) ? "general-purpose" : s.AgentType;
                var label = s.Label == s.Id ? type : $"{type} — {s.Label}";
                var transcript = TranscriptOf(s);
                bool canOpen = transcript != null;

                var dot = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Brush(AccentColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2 + (s.Depth - 1) * 12, 0, 8, 0),
                };
                // Clicking opens its transcript in place, as the header's list does
                var name = FlatButton(label, size, canOpen ? pal.Fg : pal.Dim);
                name.IsEnabled = canOpen;
                name.MaxWidth = 480;
                name.MinHeight = 0;
                name.Padding = new Thickness(0);
                name.VerticalAlignment = VerticalAlignment.Center;
                if (canOpen) name.Click += (_, _) => ShowAgent(transcript, label);
                var state = new TextBlock
                {
                    Text = Loc.Get("ChatSubagentRunning"),
                    FontSize = size * 0.9,
                    Foreground = Brush(AccentColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                var elapsed = Clock(s.Started);

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
                Grid.SetColumn(name, 1);
                Grid.SetColumn(state, 2);
                Grid.SetColumn(elapsed, 3);
                row.Children.Add(dot);
                row.Children.Add(name);
                row.Children.Add(state);
                row.Children.Add(elapsed);

                var tip = label + Environment.NewLine
                    + Loc.Get(canOpen ? "ChatSubagentOpen" : "ChatSubagentNoTranscript");
                if (!string.IsNullOrEmpty(s.Model)) tip += Environment.NewLine + s.Model;
                ToolTip.SetTip(row, tip);
                _backgroundList.Children.Add(row);
            }
        }
        if (wasVisible != _backgroundBar.IsVisible) InvalidateMeasure();
    }

    // ── Message views ──

    private Control? CreateMessageView(ConversationMessage msg, string key, ConversationMessage? previous)
    {
        if (msg.PendingAskId != null && msg.AskUser != null) return CreateAskPromptView(msg.PendingAskId, msg.AskUser);
        if (msg.AskUser != null) return CreateAskUserView(msg);
        if (msg.IsToolRejection) return CreateToolRejectionLine(msg);
        if (msg.Role == MessageRole.User) return CreateUserView(msg, previous);
        if (msg.IsToolUse && msg.Tools is { Count: > 0 }) return CreateToolGroup(msg, key);
        if (string.IsNullOrWhiteSpace(msg.Text)) return null;
        if (msg.IsThinking) return CreateThinkingView(msg, key);
        if (msg.Role == MessageRole.System) return CreateStatusLine(msg.Text, Palette.Dim);
        return CreateAssistantView(msg);
    }

    private Control CreateUserView(ConversationMessage msg, ConversationMessage? previous)
    {
        var pal = Palette;
        var column = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            // A new prompt opens a new exchange: give it room above
            Margin = new Thickness(56, previous == null ? 0 : 14, 0, 4),
        };

        if (msg.Images is { Count: > 0 })
        {
            var thumbs = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var img in msg.Images)
            {
                var thumb = CreateImageThumb(img);
                if (thumb != null) thumbs.Children.Add(thumb);
            }
            if (thumbs.Children.Count > 0) column.Children.Add(thumbs);
        }

        if (!string.IsNullOrWhiteSpace(msg.Text))
        {
            var bubble = new Border
            {
                Background = Brush(ChatTheme.UserMessage(_isDark)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 9),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(56, 0, 0, 0),
                Child = new SelectableTextBlock
                {
                    Text = msg.Text,
                    FontSize = _baseFontSize,
                    Foreground = Brush(pal.Fg),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = Math.Round(_baseFontSize * 1.6),
                },
            };
            column.Children.Add(bubble);
            // The hover row carries the time; a tooltip there would cover its links
            if (IsRewindable(msg)) AddRewindActions(column, msg);
            else if (msg.Timestamp.HasValue)
                ToolTip.SetTip(bubble, msg.Timestamp.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm"));
        }
        return column;
    }

    // What the CLI lists under /rewind: prompts the reader typed, not notices it wrote itself
    // A subagent's prompts are not the session's, so the CLI cannot rewind to them
    private bool IsRewindable(ConversationMessage msg) =>
        _agentPath == null && msg.Uuid != null && msg.Images is not { Count: > 0 }
        && !msg.Text.StartsWith('<') && !msg.Text.StartsWith("[Request interrupted");

    internal static string FirstLine(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Trim().Split('\n')[0], @"\s+", " ").Trim();

    /// <summary>Edit / Rewind links under a prompt, shown while the pointer is over it.</summary>
    private void AddRewindActions(StackPanel column, ConversationMessage msg)
    {
        var pal = Palette;
        Button Link(string key, bool edit)
        {
            var b = new Button
            {
                Content = Loc.Get(key),
                FontSize = _baseFontSize * 0.8,
                Padding = new Thickness(6, 1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brush(pal.Dim),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false,
            };
            ToolTip.SetTip(b, Loc.Get(key + "Tip"));
            b.Click += (_, _) =>
            {
                // Count from what is on screen when clicked, not when the bubble was built
                int at = _shownMessages.FindIndex(m => m.Uuid == msg.Uuid);
                var line = FirstLine(msg.Text);
                int later = at < 0 ? 0 : _shownMessages.Skip(at + 1)
                    .Count(m => m.Role == MessageRole.User && IsRewindable(m) && FirstLine(m.Text) == line);
                RewindRequested?.Invoke(new RewindTarget(msg.Uuid!, msg.Text, later, edit));
            };
            return b;
        }
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 2,
            Opacity = 0,
            Margin = new Thickness(0, -4, 0, 0),
            Children = { Link("ChatEdit", true), Link("ChatRewind", false) },
        };
        if (msg.Timestamp.HasValue)
            actions.Children.Insert(0, new TextBlock
            {
                Text = msg.Timestamp.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),
                FontSize = _baseFontSize * 0.8,
                Foreground = Brush(pal.Dim),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        column.Children.Add(actions);
        column.Background = Brushes.Transparent;   // hit-test the gaps too, so the links stay reachable
        column.PointerEntered += (_, _) => actions.Opacity = 1;
        column.PointerExited += (_, _) => actions.Opacity = 0;
    }

    private Control? CreateImageThumb(ChatImage img)
    {
        Bitmap? bitmap = null;
        try
        {
            if (img.Path != null)
            {
                using var fs = System.IO.File.OpenRead(img.Path);
                bitmap = Bitmap.DecodeToWidth(fs, 240);
            }
            else if (img.Base64 != null)
            {
                using var ms = new System.IO.MemoryStream(Convert.FromBase64String(img.Base64));
                bitmap = Bitmap.DecodeToWidth(ms, 240);
            }
        }
        catch { }
        if (bitmap == null) return null;

        var frame = new Border
        {
            Width = 120,
            Height = 120,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            Background = Brush(ChatTheme.UserBubble(_isDark)),
            ClipToBounds = true,
            Margin = new Thickness(6, 0, 0, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill },
        };
        frame.PointerPressed += (_, e) =>
        {
            var path = img.Path ?? SaveInlineImage(img.Base64!);
            if (path != null)
                ImageViewerWindow.Open(path, TopLevel.GetTopLevel(this) as Window);
            e.Handled = true;
        };
        return frame;
    }

    /// <summary>An inline image has no file behind it; the viewer needs one, so write it to temp.</summary>
    private static string? SaveInlineImage(string base64)
    {
        try
        {
            var dir = AppPaths.Temp;
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"chat_{(uint)base64.GetHashCode():x8}.png");
            if (!System.IO.File.Exists(path))
                System.IO.File.WriteAllBytes(path, Convert.FromBase64String(base64));
            return path;
        }
        catch { return null; }
    }

    private Control CreateAssistantView(ConversationMessage msg)
    {
        var content = new StackPanel { Spacing = 2 };
        foreach (var ctrl in MarkdownParser.Parse(msg.Text, _isDark, _codeTypeface, _baseFontSize, chatStyle: true))
            content.Children.Add(ctrl);

        // Copy action under the reply, revealed on hover as in the desktop app
        var copyIcon = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M5,5 H13 V13 H5 Z M3,10 H1 V1 H10 V3"),
            Stroke = Brush(Palette.Dim),
            StrokeThickness = 1.2,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
        };
        var copyButton = new Button
        {
            Content = copyIcon,
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = 0,
            IsHitTestVisible = false,
            Focusable = false,
            // Hangs below the reply instead of adding a row, so replies stay tightly spaced
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-6, 0, 0, -26),
        };
        ToolTip.SetTip(copyButton, Loc.Get("CopyCode", "Copy"));
        var text = msg.Text;
        copyButton.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(copyButton)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(text);
            copyButton.Content = new TextBlock { Text = "✓", FontSize = 12, Foreground = Brush(Palette.Dim) };
            await System.Threading.Tasks.Task.Delay(1500);
            copyButton.Content = copyIcon;
        };

        var view = new Panel { Background = Brushes.Transparent };
        view.Children.Add(content);
        view.Children.Add(copyButton);
        view.PointerEntered += (_, _) => { copyButton.Opacity = 1; copyButton.IsHitTestVisible = true; };
        view.PointerExited += (_, _) => { copyButton.Opacity = 0; copyButton.IsHitTestVisible = false; };
        return view;
    }

    /// <summary>
    /// A run of tool calls as one quiet line - "ran 3 commands, read 2 files ›" - that opens
    /// into the individual calls.
    /// </summary>
    private Control CreateToolGroup(ConversationMessage msg, string key)
    {
        var pal = Palette;
        var tools = msg.Tools!;
        // Keyed by the first call, which stays put while results and later calls change the message
        if (tools[0].Id != null) key = "g:" + tools[0].Id;
        bool expanded = _expandedGroups.Contains(key);

        var chevron = new TextBlock
        {
            Text = expanded ? "⌄" : "›",
            FontSize = _baseFontSize,
            Foreground = Brush(pal.Dim),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var summary = new TextBlock
        {
            Text = SummarizeTools(tools),
            FontSize = _baseFontSize * 0.95,
            Foreground = Brush(pal.Dim),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(summary);
        headerRow.Children.Add(chevron);
        var header = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 3),
            Margin = new Thickness(-6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerRow,
        };

        var details = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 4, 0, 2),
            IsVisible = expanded,
        };
        foreach (var tool in tools)
            details.Children.Add(CreateToolRow(tool));
        var detailCard = new Border
        {
            BorderBrush = Brush(pal.Border),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 2, 0, 0),
            Child = details,
            IsVisible = expanded,
        };
        details.IsVisible = true;

        header.PointerEntered += (_, _) => header.Background = Brush(ChatTheme.Hover(_isDark));
        header.PointerExited += (_, _) => header.Background = Brushes.Transparent;
        header.PointerPressed += (_, e) =>
        {
            bool open = !detailCard.IsVisible;
            detailCard.IsVisible = open;
            chevron.Text = open ? "⌄" : "›";
            if (open) _expandedGroups.Add(key); else _expandedGroups.Remove(key);
            e.Handled = true;
        };

        var group = new StackPanel();
        if (!string.IsNullOrWhiteSpace(msg.Narration))
            group.Children.Add(new SelectableTextBlock
            {
                Text = msg.Narration,
                FontSize = _baseFontSize * 0.95,
                Foreground = Brush(pal.Dim),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });
        group.Children.Add(header);
        group.Children.Add(detailCard);
        return group;
    }

    /// <summary>
    /// One call in an opened group: name, argument and a status mark, opening further into what
    /// went in and what came back.
    /// </summary>
    private Control CreateToolRow(ToolCall tool)
    {
        var pal = Palette;
        var errColor = _isDark ? Color.FromRgb(240, 120, 110) : Color.FromRgb(190, 60, 50);
        string rowKey = tool.Id ?? tool.Name + ":" + tool.Detail;
        bool open = _expandedTools.Contains(rowKey);

        var line = new TextBlock
        {
            FontSize = _baseFontSize * 0.88,
            Foreground = Brush(pal.Dim),
            TextWrapping = TextWrapping.Wrap,
        };
        var chevron = new Avalonia.Controls.Documents.Run(open ? "⌄ " : "› ");
        line.Inlines!.Add(chevron);
        line.Inlines.Add(new Avalonia.Controls.Documents.Run(tool.Name)
        {
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(pal.Fg),
        });
        if (!string.IsNullOrEmpty(tool.Detail))
        {
            line.Inlines.Add(new Avalonia.Controls.Documents.Run("  "));
            line.Inlines.Add(new Avalonia.Controls.Documents.Run(tool.Detail)
            {
                FontFamily = new FontFamily(_codeTypeface.FontFamily.Name),
            });
        }
        if (tool.IsError)
            line.Inlines.Add(new Avalonia.Controls.Documents.Run("  ✗") { Foreground = Brush(errColor) });
        else if (tool.Result == null)
            line.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + Loc.Get("ChatToolRunning")));
        if (tool.Name is "Agent" or "Task" && tool.Id != null && _currentSessionPath != null)
            line.Inlines.Add(new Avalonia.Controls.Documents.InlineUIContainer(CreateAgentLink(tool, _currentSessionPath)));

        var rowHeader = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1),
            Margin = new Thickness(-4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = line,
        };
        var body = new ContentControl { IsVisible = open, Margin = new Thickness(14, 4, 0, 6) };
        if (open) body.Content = CreateToolBody(tool);

        rowHeader.PointerEntered += (_, _) => rowHeader.Background = Brush(ChatTheme.Hover(_isDark));
        rowHeader.PointerExited += (_, _) => rowHeader.Background = Brushes.Transparent;
        rowHeader.PointerPressed += (_, e) =>
        {
            bool nowOpen = !body.IsVisible;
            // Built on first open: a long session holds hundreds of calls
            if (nowOpen && body.Content == null) body.Content = CreateToolBody(tool);
            body.IsVisible = nowOpen;
            chevron.Text = nowOpen ? "⌄ " : "› ";
            if (nowOpen) _expandedTools.Add(rowKey); else _expandedTools.Remove(rowKey);
            e.Handled = true;
        };

        var row = new StackPanel();
        row.Children.Add(rowHeader);
        row.Children.Add(body);
        return row;
    }

    /// <summary>
    /// "Open conversation" after an Agent call: shows the subagent's own transcript in place.
    /// The transcript is looked up on click - it is written while the agent runs, so it is often
    /// not there yet when the row is built.
    /// </summary>
    private Control CreateAgentLink(ToolCall tool, string sessionPath)
    {
        double size = _baseFontSize * 0.85;
        var link = FlatButton(Loc.Get("ChatSubagentOpenLink"), size, AccentColor);
        link.Margin = new Thickness(10, 0, 0, 0);
        link.Padding = new Thickness(0);
        link.MinHeight = 0;
        ToolTip.SetTip(link, Loc.Get("ChatSubagentOpen"));
        link.Click += (_, e) =>
        {
            e.Handled = true;
            var path = ChatTaskTracker.TranscriptFor(sessionPath, tool.Id!);
            if (path != null) ShowAgent(path, ChatTaskTracker.AgentLabel(tool));
            else ToolTip.SetTip(link, Loc.Get("ChatSubagentNoTranscript"));
        };
        return link;
    }

    /// <summary>What a call did, shaped by its kind: a command and its output, an edit as a diff.</summary>
    private Control CreateToolBody(ToolCall tool)
    {
        var stack = new StackPanel { Spacing = 6 };
        System.Text.Json.JsonElement input = default;
        bool hasInput = false;
        System.Text.Json.JsonDocument? doc = null;
        try
        {
            if (tool.InputJson != null)
            {
                doc = System.Text.Json.JsonDocument.Parse(tool.InputJson);
                input = doc.RootElement;
                hasInput = input.ValueKind == System.Text.Json.JsonValueKind.Object;
            }
        }
        catch { }

        string? Str(string name) => hasInput && input.TryGetProperty(name, out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

        var targetFile = Str("file_path") ?? Str("notebook_path");
        if (FileOpenRequested != null && !string.IsNullOrEmpty(targetFile) && System.IO.File.Exists(targetFile))
        {
            var openLink = new TextBlock
            {
                Text = "↗ " + Loc.Get("OpenInEditor"),
                FontSize = _baseFontSize * 0.82,
                Foreground = Brush(ChatTheme.Accent(_isDark)),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            openLink.PointerPressed += (_, e) =>
            {
                FileOpenRequested?.Invoke(targetFile);
                e.Handled = true;
            };
            stack.Children.Add(openLink);
        }

        switch (tool.Name)
        {
            case "Bash":
            case "PowerShell":
                stack.Children.Add(CreateCodeBox((tool.Name == "Bash" ? "$ " : "PS> ") + (Str("command") ?? tool.Detail ?? ""), null));
                AddResult(stack, tool);
                break;
            case "Edit":
                stack.Children.Add(CreateDiffBox(Str("file_path"), new[] { (Str("old_string") ?? "", Str("new_string") ?? "") }));
                if (tool.IsError) AddResult(stack, tool);
                break;
            case "MultiEdit":
                var pairs = new List<(string, string)>();
                if (hasInput && input.TryGetProperty("edits", out var edits) && edits.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var ed in edits.EnumerateArray())
                        pairs.Add((ed.TryGetProperty("old_string", out var o) ? o.GetString() ?? "" : "",
                                   ed.TryGetProperty("new_string", out var n) ? n.GetString() ?? "" : ""));
                stack.Children.Add(CreateDiffBox(Str("file_path"), pairs));
                if (tool.IsError) AddResult(stack, tool);
                break;
            case "Write":
                stack.Children.Add(CreateDiffBox(Str("file_path"), new[] { ("", Str("content") ?? "") }));
                if (tool.IsError) AddResult(stack, tool);
                break;
            case "Read":
            case "Grep":
            case "Glob":
                AddResult(stack, tool);
                break;
            default:
                if (tool.InputJson != null)
                {
                    stack.Children.Add(CreateCaption(Loc.Get("ChatToolInput")));
                    stack.Children.Add(CreateCodeBox(PrettyJson(tool.InputJson), null));
                }
                AddResult(stack, tool);
                break;
        }
        doc?.Dispose();
        return stack;
    }

    private void AddResult(StackPanel stack, ToolCall tool)
    {
        if (tool.Result == null) return;
        var errColor = _isDark ? Color.FromRgb(240, 120, 110) : Color.FromRgb(190, 60, 50);
        stack.Children.Add(CreateCaption(Loc.Get(tool.IsError ? "ChatToolError" : "ChatToolOutput")));
        var text = string.IsNullOrWhiteSpace(tool.Result) ? Loc.Get("ChatToolNoOutput") : tool.Result.TrimEnd();
        stack.Children.Add(CreateCodeBox(text, tool.IsError ? errColor : null));
    }

    private Control CreateCaption(string text) => new TextBlock
    {
        Text = text,
        FontSize = _baseFontSize * 0.78,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush(Palette.Dim),
    };

    private static string PrettyJson(string json)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(d.RootElement,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
        }
        catch { return json; }
    }

    /// <summary>Monospace, selectable, height-capped box for commands and tool output.</summary>
    private Control CreateCodeBox(string text, Color? foreground)
    {
        var pal = Palette;
        return new Border
        {
            Background = Brush(ChatTheme.Surface(_isDark)),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Child = new ScrollViewer
            {
                MaxHeight = 320,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily(_codeTypeface.FontFamily.Name),
                    FontSize = _baseFontSize * 0.82,
                    Foreground = Brush(foreground ?? pal.Fg),
                },
            },
        };
    }

    /// <summary>
    /// An edit as removed and added lines. Lines both sides share at the start and end are shown
    /// once as context, so a one-line change in a long block reads as one line.
    /// </summary>
    private Control CreateDiffBox(string? filePath, IEnumerable<(string Old, string New)> edits)
    {
        var pal = Palette;
        var addBg = _isDark ? Color.FromArgb(60, 60, 170, 90) : Color.FromArgb(70, 120, 210, 130);
        var delBg = _isDark ? Color.FromArgb(60, 210, 70, 70) : Color.FromArgb(70, 240, 120, 120);
        var mono = new FontFamily(_codeTypeface.FontFamily.Name);
        var lines = new StackPanel();
        int shown = 0;
        const int MaxLines = 400;

        void AddLine(string prefix, string text, Color? bg)
        {
            if (shown++ >= MaxLines) return;
            lines.Children.Add(new Border
            {
                Background = bg.HasValue ? Brush(bg.Value) : Brushes.Transparent,
                Child = new SelectableTextBlock
                {
                    Text = prefix + text,
                    FontFamily = mono,
                    FontSize = _baseFontSize * 0.82,
                    Foreground = Brush(pal.Fg),
                },
            });
        }

        bool first = true;
        foreach (var (oldText, newText) in edits)
        {
            if (!first) AddLine("", "⋯", null);
            first = false;
            var a = oldText.Length == 0 ? Array.Empty<string>() : oldText.Replace("\r\n", "\n").Split('\n');
            var b = newText.Length == 0 ? Array.Empty<string>() : newText.Replace("\r\n", "\n").Split('\n');
            int pre = 0;
            while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
            for (int k = Math.Max(0, pre - 2); k < pre; k++) AddLine("  ", a[k], null);
            for (int k = pre; k < a.Length - suf; k++) AddLine("- ", a[k], delBg);
            for (int k = pre; k < b.Length - suf; k++) AddLine("+ ", b[k], addBg);
            for (int k = a.Length - suf; k < Math.Min(a.Length, a.Length - suf + 2); k++) AddLine("  ", a[k], null);
        }
        if (shown > MaxLines) AddLine("", $"… (+{shown - MaxLines})", null);

        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrEmpty(filePath))
            stack.Children.Add(new TextBlock
            {
                Text = filePath,
                FontFamily = mono,
                FontSize = _baseFontSize * 0.78,
                Foreground = Brush(pal.Dim),
                TextTrimming = TextTrimming.PrefixCharacterEllipsis,
            });
        stack.Children.Add(new Border
        {
            Background = Brush(ChatTheme.Surface(_isDark)),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0, 4),
            ClipToBounds = true,
            Child = new ScrollViewer
            {
                MaxHeight = 360,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = lines,
            },
        });
        return stack;
    }

    /// <summary>A stored thought, folded to one quiet line that opens into the text.</summary>
    private Control CreateThinkingView(ConversationMessage msg, string key)
    {
        var pal = Palette;
        bool open = _expandedGroups.Contains(key);
        var headerText = new TextBlock
        {
            Text = (open ? "⌄ " : "› ") + Loc.Get("ChatThinking"),
            FontSize = _baseFontSize * 0.95,
            FontStyle = FontStyle.Italic,
            Foreground = Brush(pal.Dim),
        };
        var header = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 3),
            Margin = new Thickness(-6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = headerText,
        };
        var body = new Border
        {
            BorderBrush = Brush(pal.Border),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            IsVisible = open,
            Child = new SelectableTextBlock
            {
                Text = msg.Text,
                FontSize = _baseFontSize * 0.9,
                Foreground = Brush(pal.Dim),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        header.PointerEntered += (_, _) => header.Background = Brush(ChatTheme.Hover(_isDark));
        header.PointerExited += (_, _) => header.Background = Brushes.Transparent;
        header.PointerPressed += (_, e) =>
        {
            bool now = !body.IsVisible;
            body.IsVisible = now;
            headerText.Text = (now ? "⌄ " : "› ") + Loc.Get("ChatThinking");
            if (now) _expandedGroups.Add(key); else _expandedGroups.Remove(key);
            e.Handled = true;
        };
        var view = new StackPanel();
        view.Children.Add(header);
        view.Children.Add(body);
        return view;
    }

    /// <summary>"ran 2 commands, read a file" in the UI language, grouped by what the tools do.</summary>
    internal static string SummarizeTools(IReadOnlyList<ToolCall> tools)
    {
        int commands = 0, reads = 0, edits = 0, searches = 0, agents = 0, web = 0;
        var skills = new List<string>();
        var others = new List<string>();
        foreach (var t in tools)
        {
            switch (t.Name)
            {
                case "Bash": case "PowerShell": case "BashOutput": case "Monitor": commands++; break;
                case "Read": reads++; break;
                case "Edit": case "Write": case "MultiEdit": case "NotebookEdit": edits++; break;
                case "Grep": case "Glob": case "LSP": searches++; break;
                case "Agent": case "Task": agents++; break;
                case "WebFetch": case "WebSearch": web++; break;
                case "Skill": skills.Add(t.Detail ?? "Skill"); break;
                default:
                    var name = t.Name.StartsWith("mcp__", StringComparison.Ordinal) ? t.Name.Split("__")[^1] : t.Name;
                    if (!others.Contains(name)) others.Add(name);
                    break;
            }
        }

        var parts = new List<string>();
        void Add(string key, int n)
        {
            if (n > 0) parts.Add(string.Format(Loc.Get(key), n, n == 1 ? "" : "s", n == 1 ? "" : "es"));
        }
        foreach (var s in skills) parts.Add(string.Format(Loc.Get("ChatToolSkill"), s));
        Add("ChatToolCommands", commands);
        Add("ChatToolRead", reads);
        Add("ChatToolEdit", edits);
        Add("ChatToolSearch", searches);
        Add("ChatToolAgent", agents);
        Add("ChatToolWeb", web);
        if (others.Count > 0) parts.Add(string.Format(Loc.Get("ChatToolOther"), string.Join(", ", others)));

        var text = string.Join(Loc.Get("ChatToolJoin"), parts);
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
    }

    private Control CreateStatusLine(string text, Color color) => new TextBlock
    {
        Text = text,
        FontSize = _baseFontSize * 0.9,
        Foreground = Brush(color),
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 1),
    };

    private Control CreateToolRejectionLine(ConversationMessage msg)
    {
        var color = _isDark ? Color.FromRgb(240, 120, 110) : Color.FromRgb(190, 60, 50);
        var text = msg.ToolName != null ? string.Format(Loc.Get("ChatToolRejected"), msg.ToolName) : msg.Text;
        return CreateStatusLine("⊘ " + text, color);
    }

    // ── AskUserQuestion ──

    private Control? CreateAskUserView(ConversationMessage msg)
    {
        if (msg.AskUser == null) return null;

        var container = new StackPanel { Spacing = 8, Margin = new Thickness(0, 2) };
        foreach (var question in msg.AskUser.Questions)
        {
            container.Children.Add(CreateQuestionCard(question, msg.AskUser.Answers, msg.AskUser.Notes));

            // The answer reads as the user's reply
            if (msg.AskUser.Answers.TryGetValue(question.Question, out var answer) && answer != null)
            {
                container.Children.Add(new Border
                {
                    Background = Brush(ChatTheme.UserMessage(_isDark)),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(14, 9),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(56, 0, 0, 0),
                    Child = new SelectableTextBlock
                    {
                        Text = answer,
                        FontSize = _baseFontSize,
                        Foreground = Brush(Palette.Fg),
                        TextWrapping = TextWrapping.Wrap,
                    },
                });
            }
        }
        return container;
    }

    /// <summary>
    /// Splits a recorded answer into the options it picked and any typed text. The CLI joins
    /// a multi-select answer with ", " (typed text last), so labels are matched as whole
    /// segments, longest first, in case a label itself contains ", ".
    /// </summary>
    private static (HashSet<string> Picked, string? Typed) SplitAnswer(AskUserQuestionItem question, string? answer)
    {
        var picked = new HashSet<string>();
        if (string.IsNullOrEmpty(answer)) return (picked, null);
        if (question.Options.Any(o => o.Label == answer))
        {
            picked.Add(answer);
            return (picked, null);
        }
        if (!question.MultiSelect) return (picked, answer);

        var rest = ", " + answer + ", ";
        foreach (var label in question.Options.Select(o => o.Label)
                     .Where(l => l.Length > 0).OrderByDescending(l => l.Length))
        {
            var seg = ", " + label + ", ";
            int at = rest.IndexOf(seg, StringComparison.Ordinal);
            if (at < 0) continue;
            picked.Add(label);
            rest = rest.Remove(at, seg.Length - 2);
        }
        var typed = rest.Trim().Trim(',').Trim();
        return (picked, typed.Length > 0 ? typed : null);
    }

    private Control CreateQuestionCard(
        AskUserQuestionItem question,
        Dictionary<string, string> answers,
        Dictionary<string, string>? notes)
    {
        var pal = Palette;
        var accent = ChatTheme.Accent(_isDark);
        var selectedBg = Color.FromArgb(_isDark ? (byte)40 : (byte)28, accent.R, accent.G, accent.B);

        var stack = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(question.Header))
        {
            stack.Children.Add(new TextBlock
            {
                Text = question.Header,
                FontSize = _baseFontSize * 0.85,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(pal.Dim),
            });
        }
        stack.Children.Add(new SelectableTextBlock
        {
            Text = question.Question,
            FontSize = _baseFontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(pal.Fg),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        answers.TryGetValue(question.Question, out var selectedAnswer);
        var (picked, typedAnswer) = SplitAnswer(question, selectedAnswer);

        foreach (var option in question.Options)
        {
            bool isSelected = picked.Contains(option.Label);
            var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            labelRow.Children.Add(new TextBlock
            {
                Text = question.MultiSelect ? (isSelected ? "■" : "□") : (isSelected ? "●" : "○"),
                FontSize = _baseFontSize - 2,
                Foreground = Brush(isSelected ? accent : pal.Dim),
                VerticalAlignment = VerticalAlignment.Center,
            });
            labelRow.Children.Add(new TextBlock
            {
                Text = option.Label,
                FontSize = _baseFontSize * 0.95,
                Foreground = Brush(pal.Fg),
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var optionStack = new StackPanel { Spacing = 1 };
            optionStack.Children.Add(labelRow);
            if (!string.IsNullOrWhiteSpace(option.Description))
            {
                optionStack.Children.Add(new TextBlock
                {
                    Text = option.Description,
                    FontSize = _baseFontSize * 0.85,
                    Foreground = Brush(pal.Dim),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20, 0, 0, 0),
                });
            }
            stack.Children.Add(new Border
            {
                Background = isSelected ? Brush(selectedBg) : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5),
                Child = optionStack,
            });
        }

        // A typed answer that is none of the options
        if (!string.IsNullOrEmpty(typedAnswer))
        {
            stack.Children.Add(new Border
            {
                Background = Brush(selectedBg),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5),
                Child = new SelectableTextBlock
                {
                    Text = "✎ " + typedAnswer,
                    FontSize = _baseFontSize * 0.95,
                    Foreground = Brush(pal.Fg),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        if (notes != null && notes.TryGetValue(question.Question, out var userNotes)
            && !string.IsNullOrWhiteSpace(userNotes))
        {
            stack.Children.Add(new Border
            {
                BorderBrush = Brush(pal.Border),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 6, 0, 0),
                Margin = new Thickness(0, 2, 0, 0),
                Child = new TextBlock
                {
                    Text = "Note: " + userNotes,
                    FontSize = _baseFontSize * 0.85,
                    FontStyle = FontStyle.Italic,
                    Foreground = Brush(pal.Dim),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        return new Border
        {
            Background = Brush(ChatTheme.Surface(_isDark)),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 0, 56, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = stack,
        };
    }

    // ── Open AskUserQuestion ──

    /// <summary>
    /// The question the CLI is waiting on, drawn the way the desktop app asks it: one question
    /// per page, the options as rows, a free-text "Other", and Back / Skip / Next-or-Submit.
    /// Nothing reaches the CLI until Submit; the answers are then keyed into its selector.
    /// </summary>
    private Control CreateAskPromptView(string askId, AskUserData data)
    {
        var questions = data.Questions;
        if (!_askStates.TryGetValue(askId, out var state) || state.Count != questions.Count)
            _askStates[askId] = state = new AskState(questions.Count);

        var card = new Border
        {
            Background = Brush(ChatTheme.Surface(_isDark)),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 4),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 2, Blur = 10, Color = Color.FromArgb(_isDark ? (byte)60 : (byte)22, 0, 0, 0) }),
        };
        void Render() => card.Child = BuildAskPage(questions, state, Render);
        Render();
        return card;
    }

    private Control BuildAskPage(List<AskUserQuestionItem> questions, AskState state, Action rerender)
    {
        var pal = Palette;
        int page = state.Page = Math.Clamp(state.Page, 0, questions.Count - 1);
        var q = questions[page];
        bool last = page == questions.Count - 1;
        if (state.Dismissed) return new Panel();
        bool live = !state.Sent && !state.Stale;
        var root = new StackPanel();

        // Nothing goes to the terminal unless the selector is really there to take the keys
        bool CheckOpen()
        {
            if (IsAskOpen == null || IsAskOpen()) return true;
            state.Stale = true;
            rerender();
            return false;
        }

        // Header: "1/2", the question, collapse and cancel
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        header.Children.Add(new Border
        {
            Background = Brush(_isDark ? Color.FromArgb(70, 74, 144, 245) : Color.FromRgb(219, 232, 254)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"{page + 1}/{questions.Count}",
                FontSize = _baseFontSize * 0.8,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush(_isDark ? Color.FromRgb(170, 200, 250) : Color.FromRgb(30, 64, 175)),
            },
        });
        var title = new TextBlock
        {
            Text = q.Question,
            FontSize = _baseFontSize * 0.95,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(pal.Fg),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var collapse = AskIconButton(state.Collapsed ? "M1,6 L6,1 L11,6" : "M1,1 L6,6 L11,1", Loc.Get("AskCollapse"), true, () =>
        {
            state.Collapsed = !state.Collapsed;
            rerender();
        });
        Grid.SetColumn(collapse, 2);
        header.Children.Add(collapse);
        var close = AskIconButton("M1,1 L10,10 M10,1 L1,10", Loc.Get("AskCancel"), !state.Sent, () =>
        {
            if (state.Stale) { state.Dismissed = true; rerender(); return; }
            if (!CheckOpen()) return;
            state.Sent = true;
            rerender();
            AskCancelled?.Invoke();
        });
        Grid.SetColumn(close, 3);
        header.Children.Add(close);
        root.Children.Add(header);
        if (state.Stale)
            root.Children.Add(new TextBlock
            {
                Text = Loc.Get("AskNotOpen"),
                FontSize = _baseFontSize * 0.85,
                Foreground = Brush(pal.Dim),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        if (state.Collapsed) return root;

        // Every mark on the page is redrawn from the state, so a click never rebuilds the
        // page (which would take the focus out of the Other box)
        var marks = new List<Action>();
        void RefreshMarks() { foreach (var m in marks) m(); }

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        for (int i = 0; i < q.Options.Count; i++)
        {
            int index = i;
            var opt = q.Options[i];
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = opt.Label,
                FontSize = _baseFontSize * 0.95,
                Foreground = Brush(pal.Fg),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(opt.Description))
                text.Children.Add(new TextBlock
                {
                    Text = opt.Description,
                    FontSize = _baseFontSize * 0.82,
                    Foreground = Brush(pal.Dim),
                    TextWrapping = TextWrapping.Wrap,
                });
            var row = AskOptionRow(text, q.MultiSelect, () => state.Selected[page].Contains(index), live, marks, () =>
            {
                if (q.MultiSelect)
                {
                    if (!state.Selected[page].Remove(index)) state.Selected[page].Add(index);
                    RefreshMarks();
                    return;
                }
                state.Selected[page].Clear();
                state.Selected[page].Add(index);
                state.OtherOn[page] = false;
                state.Skipped[page] = false;
                // A single choice is the answer: move on, as the CLI does
                if (!last) { state.Page++; rerender(); }
                else RefreshMarks();
            });
            list.Children.Add(row);
        }

        // "Other": a row of its own with a text box under it
        var otherBox = new TextBox
        {
            Text = state.Other[page],
            Watermark = Loc.Get("AskOtherPlaceholder"),
            FontSize = _baseFontSize * 0.88,
            Foreground = Brush(pal.Fg),
            Background = Brush(ChatTheme.Surface(_isDark)),
            BorderBrush = Brush(ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5),
            MinHeight = 0,
            Margin = new Thickness(0, 8, 0, 0),
            IsEnabled = live,
        };
        var otherContent = new StackPanel();
        otherContent.Children.Add(new TextBlock
        {
            Text = Loc.Get("AskOther"),
            FontSize = _baseFontSize * 0.95,
            Foreground = Brush(pal.Fg),
        });
        otherContent.Children.Add(otherBox);
        list.Children.Add(AskOptionRow(otherContent, q.MultiSelect, () => state.OtherOn[page], live, marks, () =>
        {
            state.OtherOn[page] = q.MultiSelect ? !state.OtherOn[page] : true;
            if (!q.MultiSelect) state.Selected[page].Clear();
            if (state.OtherOn[page]) otherBox.Focus();
            RefreshMarks();
        }));
        otherBox.TextChanged += (_, _) =>
        {
            state.Other[page] = otherBox.Text ?? "";
            if (!string.IsNullOrWhiteSpace(otherBox.Text) && !state.OtherOn[page])
            {
                state.OtherOn[page] = true;
                if (!q.MultiSelect) state.Selected[page].Clear();
            }
            RefreshMarks();
        };
        root.Children.Add(list);

        // Footer: Back on the left; Skip and Next / Submit on the right
        void Submit()
        {
            if (!CheckOpen()) return;
            state.Sent = true;
            var replies = new List<AskReply>();
            for (int k = 0; k < questions.Count; k++)
            {
                bool answered = !state.Skipped[k] && state.IsAnswered(k);
                string? other = state.OtherOn[k] && !string.IsNullOrWhiteSpace(state.Other[k]) ? state.Other[k].Trim() : null;
                replies.Add(answered
                    ? new AskReply(false, state.Selected[k].ToList(), other)
                    : new AskReply(true, Array.Empty<int>(), null));
            }
            rerender();
            AskAnswered?.Invoke(questions, replies);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        if (page > 0)
            footer.Children.Add(AskTextButton(Loc.Get("AskBack"), false, () => live, () => { state.Page--; rerender(); }));
        if (questions.Count > 1)
        {
            var skip = AskTextButton(Loc.Get("AskSkip"), false, () => live, () =>
            {
                state.Skipped[page] = true;
                if (last) Submit();
                else { state.Page++; rerender(); }
            });
            skip.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(skip, 2);
            footer.Children.Add(skip);
        }
        var primaryText = state.Sent ? Loc.Get("AskSending") : Loc.Get(last ? "AskSubmit" : "AskNext");
        var primary = AskTextButton(primaryText, true, () => live && state.IsAnswered(page), () =>
        {
            state.Skipped[page] = false;
            if (last) Submit();
            else { state.Page++; rerender(); }
        });
        marks.Add(() => ((Action)primary.Tag!)());
        Grid.SetColumn(primary, 3);
        footer.Children.Add(primary);
        root.Children.Add(footer);

        RefreshMarks();
        return root;
    }

    /// <summary>
    /// One choice: a grey row that turns white and outlined when picked, with a round (single)
    /// or square (multi) mark on the right.
    /// </summary>
    private Border AskOptionRow(Control content, bool multi, Func<bool> isOn, bool live, List<Action> marks, Action onClick)
    {
        var mark = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(multi ? 3 : 8),
            BorderThickness = new Thickness(1.2),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 2, 0, 0),
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(content);
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);
        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7),
            Child = grid,
            Cursor = live ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
        };
        var blue = _isDark ? Color.FromRgb(74, 144, 245) : Color.FromRgb(37, 99, 235);
        bool hover = false;
        void Apply()
        {
            bool on = isOn();
            row.Background = Brush(on ? ChatTheme.Surface(_isDark)
                : hover && live ? ChatTheme.Outline(_isDark) : ChatTheme.Hover(_isDark));
            row.BorderBrush = on ? Brush(ChatTheme.Outline(_isDark)) : Brushes.Transparent;
            mark.Background = on ? Brush(blue) : Brush(ChatTheme.Surface(_isDark));
            mark.BorderBrush = Brush(on ? blue : _isDark ? Color.FromRgb(95, 95, 94) : Color.FromRgb(190, 190, 188));
            mark.Child = !on ? null : multi
                ? new Avalonia.Controls.Shapes.Path
                {
                    Data = Geometry.Parse("M1,5 L4,8 L10,1"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.8,
                    Width = 10,
                    Height = 8,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White };
        }
        marks.Add(Apply);
        row.PointerEntered += (_, _) => { hover = true; Apply(); };
        row.PointerExited += (_, _) => { hover = false; Apply(); };
        row.PointerPressed += (_, e) =>
        {
            // Clicks inside the Other box are typing, not a toggle
            if (!live || e.Source is Visual v && v.FindAncestorOfType<TextBox>(includeSelf: true) != null) return;
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            onClick();
        };
        return row;
    }

    /// <summary>
    /// A footer button. Drawn as a Border rather than a Button so the theme's hover and
    /// disabled looks do not repaint it; its Tag re-evaluates whether it is enabled.
    /// </summary>
    private Border AskTextButton(string text, bool primary, Func<bool> isEnabled, Action onClick)
    {
        var label = new TextBlock { Text = text, FontSize = _baseFontSize * 0.85, FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal };
        var button = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 3),
            Child = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fg = Palette.Fg;
        var bg = ChatTheme.Background(_isDark);
        void Apply()
        {
            bool on = isEnabled();
            button.Cursor = on ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            if (primary)
            {
                var fill = on ? fg : _isDark ? Color.FromRgb(80, 80, 79) : Color.FromRgb(170, 170, 168);
                button.Background = Brush(fill);
                button.BorderBrush = Brush(fill);
                label.Foreground = Brush(bg);
            }
            else
            {
                button.Background = Brush(ChatTheme.Surface(_isDark));
                button.BorderBrush = Brush(ChatTheme.Outline(_isDark));
                label.Foreground = Brush(on ? fg : Palette.Dim);
            }
        }
        button.Tag = (Action)Apply;
        Apply();
        button.PointerPressed += (_, e) =>
        {
            if (!isEnabled() || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        return button;
    }

    private Border AskIconButton(string geometry, string tip, bool enabled, Action onClick)
    {
        var button = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = enabled ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Opacity = enabled ? 1 : 0.4,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(geometry),
                Stroke = Brush(Palette.Fg),
                StrokeThickness = 1.4,
                Width = 10,
                Height = 10,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(button, tip);
        if (!enabled) return button;
        button.PointerEntered += (_, _) => button.Background = Brush(ChatTheme.Hover(_isDark));
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        return button;
    }

    /// <summary>What the reader has picked so far on an open question, one slot per question.</summary>
    private sealed class AskState
    {
        public int Page;
        public bool Sent;
        public bool Stale;       // the CLI was no longer asking when an answer was tried
        public bool Dismissed;
        public bool Collapsed;
        public readonly List<SortedSet<int>> Selected = new();
        public readonly List<string> Other = new();
        public readonly List<bool> OtherOn = new();
        public readonly List<bool> Skipped = new();
        public int Count => Selected.Count;

        public AskState(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Selected.Add(new SortedSet<int>());
                Other.Add("");
                OtherOn.Add(false);
                Skipped.Add(false);
            }
        }

        public bool IsAnswered(int q) => Selected[q].Count > 0 || (OtherOn[q] && !string.IsNullOrWhiteSpace(Other[q]));
    }

    // ── Scrolling ──

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = _scrollViewer;
        double fromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        // Content growing under the reader is not the reader scrolling away
        if (e.ExtentDelta.Y == 0 || fromBottom < 20)
            _autoScroll = fromBottom < 20;
        _scrollDownButton.IsVisible = fromBottom > 80;
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() => _scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static long FileLength(string filePath)
    {
        // Through a handle: FileInfo reads the directory entry, which NTFS updates lazily while
        // the CLI holds the file open
        try
        {
            using var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            return stream.Length;
        }
        catch { return -1; }
    }
}

/// <summary>
/// The "working" mark: a braille-dot spinner, the kind terminal tools use. Deliberately not the
/// Claude CLI's orange star, which reads as Anthropic's logo.
/// </summary>
public class WorkingSpinnerGlyph : TextBlock
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    // Segoe UI Symbol has the braille block; Inter has none of it, and falling through to the
    // emoji font would change the weight between frames.
    private static readonly FontFamily GlyphFont = new("Segoe UI Symbol,Segoe UI Emoji,Segoe UI");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private int _frame;
    private bool _spinning = true;

    /// <summary>Off while the row is only holding its place, so a hidden glyph does not redraw.</summary>
    public bool Spinning
    {
        get => _spinning;
        set
        {
            _spinning = value;
            if (value && VisualRoot != null) _timer.Start();
            else _timer.Stop();
        }
    }

    public WorkingSpinnerGlyph()
    {
        FontFamily = GlyphFont;
        TextAlignment = TextAlignment.Center;
        IsHitTestVisible = false;
        Text = Frames[0];
        _timer.Tick += (_, _) => Text = Frames[_frame = (_frame + 1) % Frames.Length];
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_spinning) _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
