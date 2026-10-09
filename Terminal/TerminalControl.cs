using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Snipyard.Terminal;

public partial class TerminalControl : Control, IDisposable
{
    private TerminalBuffer _buffer;
    private VtParser _parser;
    private PseudoConsole? _pty;
    private double _cellWidth;
    private double _cellHeight;
    private Typeface _typeface;
    private double _fontSize = 14;
    private bool _disposed;
    private string? _workingDirectory;
    private DateTime _startedUtc = DateTime.MinValue;
    private bool _isDark = true;

    // Selection state
    private bool _isSelecting;
    private bool _hasSelection;
    private int _selStartRow, _selStartCol;
    private int _selEndRow, _selEndCol;
    // Column the rows of a selection start at. Zero everywhere except inside the
    // CLI's input block, whose rows are indented past the prompt marker: there the
    // gutter is layout, not text, and selecting it would only copy stray spaces.
    private int _selLeftMargin;

    // Shift+arrow selection. Held as two caret positions - one before the first
    // selected character, one after the last - because that is the only way growing
    // and shrinking a selection behaves the way an editor's does. Converted to the
    // inclusive cell pair above whenever it changes.
    private bool _kbSelecting;
    private int _kbAnchorRow, _kbAnchorCol;
    private int _kbFocusRow, _kbFocusCol;
    private int _kbDesiredCol = -1;      // column Up/Down tries to hold on to

    // Input boundary tracking: records where editable input begins
    private bool _inputStartPending = true;
    private int _inputStartAbsRow;
    private int _inputStartCol;

    // Click-to-move caret: guards the arrow-key convergence loop against re-entry
    private bool _caretMoveInProgress;

    // Undo for the CLI's input line. The CLI owns that line and offers no undo of its
    // own, so Snipyard keeps snapshots of the text and rewrites the line to put one
    // back. See PushUndo / UndoAsync.
    private readonly List<InputSnapshot> _undoStack = new();
    private readonly List<InputSnapshot> _redoStack = new();
    private UndoKind _lastUndoKind = UndoKind.None;
    private bool _undoRunOpen;
    private long _lastUndoTick;

    // Caret blink, in step with the input box's own caret
    private DispatcherTimer? _caretBlinkTimer;
    private bool _caretOn = true;
    private int _lastCaretRow = -1;
    private int _lastCaretCol = -1;

    // Scrollbar drag state
    private bool _isScrollbarDragging;
    private double _scrollbarDragStartY;
    private int _scrollbarDragStartOffset;
    private const double ScrollbarWidth = 10;
    private const double ScrollbarThumbMinHeight = 20;

    // Input TextBox at bottom
    private readonly TextBox _inputTextBox;
    private readonly Button _expandButton;

    /// <summary>Interrupts the turn. One for each input layout; only one is ever on screen.</summary>
    private readonly Button _stopButton;
    private Button _expandedStopButton = null!;

    private const double InputBoxHeight = 28;
    private const double InputBoxMargin = 2;
    private const double ExpandButtonWidth = 32;

    // Chat view composer: the same input row, dressed as the desktop app's rounded card. The
    // text box grows with its content between the two heights; the buttons sit on a row below.
    private readonly Border _chatBackdrop = new() { IsVisible = false, IsHitTestVisible = false };
    private readonly Border _chatCard = new()
    {
        IsVisible = false,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(16),
        Cursor = new Cursor(StandardCursorType.Ibeam),
    };
    private Button _chatAttachButton = null!;
    private Button _chatSendButton = null!;
    private double _chatInputHeight = ChatInputMinHeight;
    private const double ChatInputMinHeight = 26;
    private const double ChatInputMaxHeight = 180;
    private const double ChatButtonSize = 30;
    private const double ChatCardPadX = 14, ChatCardPadTop = 10, ChatCardPadBottom = 8;
    private const double ChatGapTop = 6, ChatGapBottom = 14;
    private bool ChatComposerShown => _isDocumentView && !_isExpanded;
    private double ChatComposerHeight =>
        ChatGapTop + ChatCardPadTop + AttachmentStripHeight + _chatInputHeight + 4 + ChatButtonSize + ChatCardPadBottom + ChatGapBottom;

    // Indeterminate progress line pinned to the bottom edge of the input row
    private readonly Controls.MarqueeBar _marquee;

    // Expanded input panel
    private Border _expandedPanel = null!;
    private TextBox _expandedTextBox = null!;
    private Border _dragHandle = null!;
    private Button _collapseButton = null!;
    private Button _sendButton = null!;
    private Button _attachButton = null!;
    private bool _isExpanded;
    private double _expandedHeight; // absolute pixels
    private bool _isDragResizing;
    private double _dragResizeStartY;
    private double _dragResizeStartHeight;

    // Search bar state
    private Border? _searchBar;
    private TextBox? _searchTextBox;
    private TextBlock? _searchCountLabel;
    private bool _searchVisible;
    private string _searchTerm = "";
    private bool _searchRegex;
    private bool _searchCaseSensitive;
    private ToggleButton? _searchRegexToggle;
    private ToggleButton? _searchCaseToggle;
    private readonly List<(int absRow, int col, int length)> _searchMatches = new();
    private int _searchCurrentIndex = -1;

    // Prompt navigation state: tracks absolute row positions where user submitted input
    private readonly List<int> _userInputRows = new();
    private Border? _promptNavBar;
    private TextBlock? _promptNavLabel;
    private int _promptNavCurrentIndex = -1;

    // Chart/diagram rendering state
    private readonly CodeBlockDetector _codeBlockDetector = new();
    private DispatcherTimer? _codeBlockScanTimer;
    private bool _codeBlockScanPending;
    private int _lastCachedBlockCount;
    private readonly List<CodeBlockInfo> _cachedDiagrams = new();
    // Cache for parsed Excalidraw elements to survive terminal reflow on resize
    private List<System.Text.Json.JsonElement>? _excalidrawCacheDrawables;
    private double _excalidrawCacheMinX, _excalidrawCacheMinY, _excalidrawCacheMaxX, _excalidrawCacheMaxY;
    public bool EnableChartRendering { get; set; } = true;

    /// <summary>
    /// Whether to watch for the CLI's permission prompt and offer the Yes/Always/No overlay.
    /// The detection strings and the 1/2/3 replies are Claude Code specific, so other CLIs
    /// turn this off.
    /// </summary>
    public bool EnablePermissionOverlay { get; set; } = true;

    /// <summary>
    /// Command written to the PTY on shutdown, e.g. "/exit\r". Empty means terminate the
    /// process directly instead.
    /// </summary>
    public string ExitCommand { get; set; } = "/exit\r";

    // Document view mode state
    private bool _isDocumentView;
    private Controls.DocumentViewPanel? _docViewPanel;
    private string? _docViewSessionPath;
    private DispatcherTimer? _permissionCheckTimer;
    private Border? _permissionOverlay;
    public bool IsDocumentView => _isDocumentView;
    public event Action<bool>? DocumentViewChanged;

    public string TabTitle { get; private set; } = "Console";
    public bool IsManualTitle { get; set; }
    public string? FirstUserInput { get; set; }
    private bool _firstInputCaptured;
    private readonly System.Text.StringBuilder _firstInputBuffer = new();
    public event Action<string>? TitleChanged;
    public event Action? Exited;

    /// <summary>Raised whenever the user submits a prompt, carrying the text when it is known.</summary>
    public event Action<string?>? PromptSubmitted;

    /// <summary>
    /// Asked before a prompt leaves for the CLI, with its text and a callback that sends it.
    /// Returning false holds the prompt where it is - the shell uses this to stop a send into
    /// a session whose prompt cache has lapsed, and calls the callback if the user goes ahead.
    /// </summary>
    public Func<string, Action, bool>? SubmitGate { get; set; }

    /// <summary>True while the CLI process is alive.</summary>
    public bool IsProcessRunning => _pty?.IsRunning == true;

    /// <summary>
    /// The pid ConPTY started, which is the host shell - cmd.exe or PowerShell - not the CLI.
    /// The CLI runs underneath it, so anything that needs to identify this window's CLI process
    /// walks down from here. Zero before the process starts.
    /// </summary>
    public int ShellProcessId => _pty?.ProcessId ?? 0;
    public event Action? Clicked;
    public event Action<double>? FontSizeChanged;

    public bool IsDarkTheme
    {
        get => _isDark;
        set
        {
            _isDark = value;
            ApplyThemeColors();
        }
    }

    private void ApplyThemeColors()
    {
        var fg = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(38, 40, 44);       // Light: near-black body text
        var bg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(242, 242, 242);      // Light: Tango input bg
        var bgDeep = _isDark ? Color.FromRgb(34, 34, 36) : Color.FromRgb(255, 255, 255);  // Light: white
        var border = _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200);
        var subtle = _isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(85, 85, 93);

        ApplyInputChrome();

        // Expanded panel
        _expandedPanel.Background = new SolidColorBrush(bgDeep);
        _expandedPanel.BorderBrush = new SolidColorBrush(border);
        _expandedTextBox.Background = new SolidColorBrush(bgDeep);
        _expandedTextBox.Foreground = new SolidColorBrush(fg);
        _expandedTextBox.CaretBrush = new SolidColorBrush(fg);
        _dragHandle.Background = new SolidColorBrush(border);
        _collapseButton.Background = new SolidColorBrush(bg);
        _collapseButton.Foreground = new SolidColorBrush(subtle);
        _sendButton.Background = new SolidColorBrush(Color.FromRgb(0, 122, 255));

        // Search bar
        if (_searchBar != null)
        {
            _searchBar.Background = new SolidColorBrush(_isDark ? Color.FromRgb(38, 38, 40) : Color.FromRgb(245, 245, 248));
            _searchBar.BorderBrush = new SolidColorBrush(border);
        }
        if (_searchTextBox != null)
        {
            _searchTextBox.Background = new SolidColorBrush(bg);
            _searchTextBox.Foreground = new SolidColorBrush(fg);
            _searchTextBox.BorderBrush = new SolidColorBrush(border);
        }

        // Document view theme
        _docViewPanel?.UpdateTheme(_isDark);
        ApplySidePaneTheme();

        InvalidateVisual();
    }

    private static readonly string[] ChatChromeResourceKeys =
    {
        "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
        "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
        "TextControlBorderThemeThicknessFocused", "TextControlPlaceholderForeground",
        "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused",
    };

    /// <summary>
    /// Styles the input row for the current view: the terminal's flat strip, or the chat view's
    /// borderless box inside a rounded card. Both are the same TextBox, so every path that
    /// feeds it (IME, paste, drop, history) works the same in either.
    /// </summary>
    private void ApplyInputChrome()
    {
        var fg = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(38, 40, 44);
        var bg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(242, 242, 242);
        var border = _isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200);
        var subtle = _isDark ? Color.FromRgb(160, 160, 165) : Color.FromRgb(85, 85, 93);

        _attachStrip.UpdateTheme(_isDark);
        bool chat = _isDocumentView;
        _chatBackdrop.IsVisible = chat;
        _chatCard.IsVisible = chat;
        _chatAttachButton.IsVisible = chat;
        _chatSendButton.IsVisible = chat;
        _sidePaneToggle.IsVisible = chat;
        UpdateSidePaneToggleLook();

        if (!chat)
        {
            _inputTextBox.Foreground = new SolidColorBrush(fg);
            _inputTextBox.Background = new SolidColorBrush(bg);
            _inputTextBox.BorderBrush = new SolidColorBrush(border);
            _inputTextBox.BorderThickness = new Thickness(0, 1, 0, 0);
            _inputTextBox.Padding = new Thickness(6, 4);
            _inputTextBox.FontFamily = _typeface.FontFamily;
            _inputTextBox.FontSize = _fontSize;
            _inputTextBox.PlaceholderText = "IME input here — auto-sent on commit";
            _inputTextBox.TextWrapping = TextWrapping.NoWrap;
            _inputTextBox.AcceptsReturn = false;
            _inputTextBox.ClearValue(MinHeightProperty);
            _inputTextBox.ClearValue(TextBox.SelectionBrushProperty);
            _inputTextBox.ClearValue(TextBox.SelectionForegroundBrushProperty);
            foreach (var key in ChatChromeResourceKeys)
                _inputTextBox.Resources.Remove(key);

            _expandButton.Background = new SolidColorBrush(bg);
            _expandButton.Foreground = new SolidColorBrush(subtle);
            _expandButton.CornerRadius = new CornerRadius(0);
            _stopButton.CornerRadius = new CornerRadius(0);
            _attachStrip.BorderThickness = new Thickness(0, 1, 0, 0);
            return;
        }

        var pal = Services.MarkdownParser.ChatPalette.For(_isDark);
        var surface = Controls.ChatTheme.Surface(_isDark);
        _chatBackdrop.Background = new SolidColorBrush(Controls.ChatTheme.Background(_isDark));
        _chatCard.Background = new SolidColorBrush(surface);
        _chatCard.BorderBrush = new SolidColorBrush(Controls.ChatTheme.Outline(_isDark));

        _inputTextBox.Foreground = new SolidColorBrush(pal.Fg);
        _inputTextBox.Background = Brushes.Transparent;
        _inputTextBox.BorderThickness = new Thickness(0);
        _inputTextBox.Padding = new Thickness(2, 4);
        _inputTextBox.FontFamily = Controls.ChatTheme.BodyFont;
        _inputTextBox.FontSize = Controls.ChatTheme.BodySize(_fontSize);
        ApplyChatPlaceholder();
        _inputTextBox.TextWrapping = TextWrapping.Wrap;
        _inputTextBox.AcceptsReturn = true;
        _inputTextBox.MinHeight = 0;
        // Selected text as in the replies: solid blue with white text, not the theme's dark grey
        _inputTextBox.SelectionBrush = Services.MarkdownParser.ChatSelectionBg;
        _inputTextBox.SelectionForegroundBrush = Brushes.White;
        // Fluent repaints the box on hover and focus; inside the card it has to stay invisible
        var res = _inputTextBox.Resources;
        // Each key gets a value of its own type: the focused box reads its keys live, and a brush
        // parked even for a moment on the thickness key is an InvalidCastException that kills the app
        foreach (var key in ChatChromeResourceKeys)
            res[key] = key.Contains("Thickness") ? new Thickness(0)
                : key.Contains("Placeholder") ? new SolidColorBrush(pal.Dim)
                : Brushes.Transparent;

        _expandButton.Background = Brushes.Transparent;
        _expandButton.Foreground = new SolidColorBrush(pal.Dim);
        _expandButton.CornerRadius = new CornerRadius(8);
        _stopButton.CornerRadius = new CornerRadius(ChatButtonSize / 2);
        _attachStrip.Background = Brushes.Transparent;
        _attachStrip.BorderThickness = new Thickness(0);

        _chatAttachButton.BorderBrush = new SolidColorBrush(Controls.ChatTheme.Outline(_isDark));
        if (_chatAttachButton.Content is Avalonia.Controls.Shapes.Path plus)
            plus.Stroke = new SolidColorBrush(pal.Dim);
        UpdateChatSendState();
    }

    /// <summary>The send button lights up once there is something to send.</summary>
    private void UpdateChatSendState()
    {
        bool ready = !string.IsNullOrWhiteSpace(_inputTextBox.Text) || _attachStrip.HasItems;
        var pal = Services.MarkdownParser.ChatPalette.For(_isDark);
        _chatSendButton.Background = new SolidColorBrush(ready
            ? Controls.ChatTheme.Accent(_isDark)
            : (_isDark ? Color.FromRgb(70, 69, 65) : Color.FromRgb(226, 224, 219)));
        if (_chatSendButton.Content is Avalonia.Controls.Shapes.Path arrow)
            arrow.Stroke = new SolidColorBrush(ready ? Colors.White : pal.Dim);
    }

    private void BuildChatComposer()
    {
        _chatAttachButton = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M6,0 V12 M0,6 H12"),
                StrokeThickness = 1.5,
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
            },
            Width = ChatButtonSize,
            Height = ChatButtonSize,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            IsVisible = false,
        };
        ToolTip.SetTip(_chatAttachButton, Services.Loc.Get("ChatAttach"));
        _chatAttachButton.Click += (_, _) => _ = AttachFilesAsync();

        _chatSendButton = new Button
        {
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M6,12 V1 M1,6 L6,1 L11,6"),
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 11,
                Height = 12,
                Stretch = Stretch.Uniform,
            },
            Width = ChatButtonSize,
            Height = ChatButtonSize,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            IsVisible = false,
        };
        ToolTip.SetTip(_chatSendButton, Services.Loc.Get("ChatSend"));
        _chatSendButton.Click += (_, _) =>
        {
            SubmitChatInput();
            _inputTextBox.Focus();
        };

        // A click anywhere on the card is a click into the text
        _chatCard.PointerPressed += (_, e) =>
        {
            Clicked?.Invoke();
            _inputTextBox.Focus();
            e.Handled = true;
        };

        _inputTextBox.TextChanged += (_, _) =>
        {
            if (!_isDocumentView) return;
            UpdateChatSendState();
            InvalidateMeasure();
            // The terminal's own prompt completes as it goes; the chat composer has to do it here
            OnCompletionTextChanged(_inputTextBox, slashToo: true);
        };

        // Behind everything else: the card is drawn first and the input row lands on top of it
        VisualChildren.Insert(0, _chatCard);
        LogicalChildren.Add(_chatCard);
        VisualChildren.Insert(0, _chatBackdrop);
        LogicalChildren.Add(_chatBackdrop);
        VisualChildren.Add(_chatAttachButton);
        LogicalChildren.Add(_chatAttachButton);
        VisualChildren.Add(_chatSendButton);
        LogicalChildren.Add(_chatSendButton);
        BuildSidePaneToggle();
    }

    /// <summary>A file a tool call worked on was clicked in Chat View; the shell opens it in an editor window.</summary>
    public event Action<string>? FileOpenRequested;

    /// <summary>Sends what the chat view's box holds, with any attached images. False if empty.</summary>
    private bool SubmitChatInput()
    {
        if (string.IsNullOrEmpty(_inputTextBox.Text) && !_attachStrip.HasItems) return false;

        bool withImages = _attachStrip.HasItems;
        // Mid-turn, the prompt waits its turn instead of being typed over Claude's work
        if (_isDocumentView && (_docViewPanel?.IsBusy == true || _sendQueue.Count > 0))
        {
            var shown = _inputTextBox.Text ?? "";
            _sendQueue.Add(new QueuedPrompt(shown, JoinWithAttachments(shown), withImages));
            _inputTextBox.Text = "";
            _inputTextBox.CaretIndex = 0;
            _docViewPanel?.SetQueue(_sendQueue.Select(q => q.Shown).ToList());
            return true;
        }

        if (SubmitGate is { } gate && !gate(_inputTextBox.Text ?? "", () => SubmitChatInput()))
            return true;

        _docViewPanel?.ShowPendingPrompt(_inputTextBox.Text ?? "");
        var text = JoinWithAttachments(_inputTextBox.Text ?? "");
        PromptSubmitted?.Invoke(text);
        WriteAndSubmit(text, withImages);
        _inputTextBox.Text = "";
        _inputTextBox.CaretIndex = 0;

        // Capture first input as tab title
        if (!_firstInputCaptured)
        {
            _firstInputCaptured = true;
            FirstUserInput = text.Trim();
            var summary = FirstUserInput;
            if (summary.Length > 30) summary = summary[..30] + "...";
            if (!string.IsNullOrWhiteSpace(summary))
                TitleChanged?.Invoke(summary);
        }
        return true;
    }

    // Terminal area height = total height - input area - expanded panel
    private double ExpandedPanelHeight => _isExpanded ? _expandedHeight : 0;
    private double InputAreaHeight => ChatComposerShown
        ? ChatComposerHeight
        : (_isExpanded ? 0 : InputBoxHeight + InputBoxMargin) + AttachmentStripHeight;

    /// <summary>Pasted/dropped images waiting to go out with the next submit.</summary>
    private readonly Controls.ImageAttachmentStrip _attachStrip = new();
    private double AttachmentStripHeight => _attachStrip.HasItems ? Controls.ImageAttachmentStrip.StripHeight : 0;
    private double TerminalAreaHeight => TerminalInSidePane
        ? SideTerminalRect.Height
        : Math.Max(0, Bounds.Height - InputAreaHeight - ExpandedPanelHeight);

    public void SetFont(string fontFamily, double fontSize)
    {
        _typeface = new Typeface(fontFamily + ", Consolas, Courier New");
        _fontSize = fontSize;
        ApplyInputChrome();
        _docViewPanel?.SetFont(fontFamily, fontSize);
        MeasureCellSize();
        RecalcTerminalSize();
        InvalidateVisual();
    }

    public TerminalControl()
    {
        _typeface = new Typeface("Cascadia Mono, Consolas, Courier New, monospace");
        _buffer = new TerminalBuffer(24, 80);
        _parser = new VtParser(_buffer);
        _parser.TitleChanged += title =>
        {
            TabTitle = title;
            Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
        };

        ClipToBounds = true;
        // Ctrl+wheel zooms on the way down, before the chat view's ScrollViewer takes the wheel
        // for scrolling: on the bubble it only got through when there was nowhere left to scroll
        AddHandler(PointerWheelChangedEvent, OnZoomWheel, RoutingStrategies.Tunnel);

        // Built here but added to the visual tree last, so it paints over the input row
        _marquee = new Controls.MarqueeBar();

        // Create input TextBox at the bottom
        _inputTextBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),   // Apple elevated surface
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),  // Apple separator
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 4),
            FontSize = _fontSize,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            PlaceholderText = "IME input here — auto-sent on commit",
            Focusable = true,
            AcceptsReturn = false,
        };

        // Handle Enter key to send text to PTY
        _inputTextBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Intercept TextInput: half-width chars go directly to PTY,
        // full-width (IME committed) chars also go to PTY immediately
        _inputTextBox.AddHandler(TextInputEvent, OnInputTextInput, RoutingStrategies.Tunnel);

        // Forward click to activate MDI window
        _inputTextBox.PointerPressed += (s, e) => Clicked?.Invoke();

        // Blink the terminal caret on the same 500ms cadence as the caret in the input
        // box below it. The timer runs only while that box holds focus, which is also
        // the only time the caret is drawn, so an unfocused terminal never repaints for
        // a blink — and in an MDI window only the active session's caret is ticking.
        _caretBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _caretBlinkTimer.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            InvalidateVisual();
        };
        _inputTextBox.GotFocus += (_, _) => RestartCaretBlink();
        _inputTextBox.LostFocus += (_, _) =>
        {
            _caretBlinkTimer?.Stop();
            _caretOn = true;
            InvalidateVisual();
        };

        // Expand button (▲)
        _expandButton = new Button
        {
            Content = "\u25B2",
            FontSize = 10,
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            // No rule of its own. It used to carry the input row's top hairline, but a button
            // takes its content height rather than filling the row, so the line came out part
            // way down the cell instead of along the top: lost against a dark surface, a stray
            // stroke across a light one. The text box beside it already draws that edge.
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0),
            Width = ExpandButtonWidth,
            CornerRadius = new CornerRadius(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        ToolTip.SetTip(_expandButton, "Expand input (multi-line)");
        _expandButton.Click += (_, _) => ToggleExpandedMode();

        // Stop, for the collapsed input row. Sits at the right end of the text box and only
        // while this session is mid-turn - it belongs to the window that is working, which is
        // not always the one the status bar is describing.
        // Stretch rather than a fixed height: the button then fills the arranged row exactly as
        // the expander beside it does, instead of sitting short with a gap above it.
        // Borderless too, and for a second reason: the hairline it carried was written out
        // dark and never repainted for the light theme, so a black bar sat across the top of a
        // pale red button.
        _stopButton = NewStopButton(new Thickness(8, 0));
        _stopButton.VerticalAlignment = VerticalAlignment.Stretch;
        _stopButton.CornerRadius = new CornerRadius(0);

        // Build expanded input panel
        BuildExpandedPanel();

        VisualChildren.Add(_inputTextBox);
        LogicalChildren.Add(_inputTextBox);
        VisualChildren.Add(_stopButton);
        LogicalChildren.Add(_stopButton);
        VisualChildren.Add(_expandButton);
        LogicalChildren.Add(_expandButton);
        VisualChildren.Add(_expandedPanel);
        LogicalChildren.Add(_expandedPanel);
        VisualChildren.Add(_attachStrip);
        LogicalChildren.Add(_attachStrip);
        _attachStrip.ItemsChanged += () =>
        {
            // The strip takes its height from the terminal, so the PTY follows it
            RecalcTerminalSize();
            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
            if (_isDocumentView) UpdateChatSendState();
        };

        BuildChatComposer();

        // Build search bar
        BuildSearchBar();

        // Visual children paint in the order they are added, so the marquee goes last to sit
        // on top of the input row rather than behind the text box's own background.
        VisualChildren.Add(_marquee);
        LogicalChildren.Add(_marquee);

        // Enable file drag & drop
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver);

        MeasureCellSize();
        ApplyThemeColors();

        _buffer.BufferChanged += () =>
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
            ScheduleCodeBlockScan();
        };
    }

    private void ScheduleCodeBlockScan()
    {
        if (_codeBlockScanPending) return;
        _codeBlockScanPending = true;

        if (_codeBlockScanTimer == null)
        {
            _codeBlockScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _codeBlockScanTimer.Tick += (_, _) =>
            {
                _codeBlockScanTimer.Stop();
                _codeBlockScanPending = false;
                if (!_buffer.IsAltBuffer && EnableChartRendering)
                {
                    _codeBlockDetector.IncrementalScan(_buffer);
                    AutoCacheNewDiagrams();
                    InvalidateVisual();
                }
            };
        }

        _codeBlockScanTimer.Stop();
        _codeBlockScanTimer.Start();
    }

    private static bool IsHalfWidth(string text)
    {
        foreach (char c in text)
        {
            if (c > '\u007E') return false;
        }
        return true;
    }

    /// <summary>
    /// Set to true when IME text is committed via TextInput.
    /// On the next KeyDown, any TextBox remnants are force-cleared.
    /// </summary>
    private bool _imeJustCommitted;

    private void OnInputTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        if (_swallowCardSpace)
        {
            _swallowCardSpace = false;
            if (e.Text == " ") { e.Handled = true; return; }
        }

        // Filter out control characters (e.g., Backspace generates '\b' via TextInput
        // which would double-send with the KeyDown handler's \x7f)
        foreach (char c in e.Text)
        {
            if (c >= ' ') // Only process printable characters (U+0020 and above)
            {
                goto hasPrintable;
            }
        }
        return; // All control characters — ignore
    hasPrintable:

        // Document view mode: accumulate all text in the input box until Enter
        if (_isDocumentView)
        {
            // A panel such as /config filters its rows as you type: the text is its, not a prompt
            if (_panelText != null)
            {
                SendPanelKey(e.Text);
                e.Handled = true;
                return;
            }
            // Let the TextBox handle the input naturally (don't send to PTY)
            // Text stays in the input box until Enter is pressed
            return;
        }

        // Track input start on first text input after prompt
        if (_inputStartPending)
        {
            _inputStartAbsRow = ScreenRowToAbsolute(_buffer.CursorRow);
            _inputStartCol = _buffer.CursorCol;
            _inputStartPending = false;
            System.Diagnostics.Debug.WriteLine($"[InputStart] recorded at ({_inputStartAbsRow},{_inputStartCol})");
        }

        // Capture first user input for tab title
        if (!_firstInputCaptured)
            _firstInputBuffer.Append(e.Text);

        // Printable text committed (half-width direct or IME confirmed) — send to PTY.
        // Typing over a selection replaces it, the way a text editor does. If an edit
        // is already in flight the character still goes straight through rather than
        // being dropped on the floor.
        PushUndo(_hasSelection ? UndoKind.Structural : UndoKind.Typing);
        if (_hasSelection && !_caretMoveInProgress
            && TryGetEditableSelection(out _, out _, out _, out _))
        {
            string typed = e.Text;
            RunCaretEdit(() => ReplaceSelectionAsync(() =>
            {
                _pty?.WriteInput(typed);
                return Task.CompletedTask;
            }));
        }
        else
        {
            if (_hasSelection) ClearSelection();
            _pty?.WriteInput(e.Text);
        }
        e.Handled = true;

        // Mark that we just committed, so next KeyDown clears remnants
        _imeJustCommitted = true;
        _inputTextBox.Text = "";
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // After IME commit, force-clear any preedit remnants that Avalonia
        // may have re-inserted after our clear in OnInputTextInput
        if (_imeJustCommitted && !_isDocumentView)
        {
            _imeJustCommitted = false;
            _inputTextBox.Text = "";
            _inputTextBox.CaretIndex = 0;
        }

        if (_isDocumentView && _completionPopup is { IsOpen: true } && _completionTarget == _inputTextBox
            && HandleCompletionKey(e))
        {
            e.Handled = true;
            return;
        }

        // An open card takes Up/Down/Enter while the composer is empty
        if (_isDocumentView && string.IsNullOrEmpty(_inputTextBox.Text) && HandleCardKey(e, focus: false))
        {
            e.Handled = true;
            return;
        }

        // Chat view: the prompt box edits like Notepad instead of passing Ctrl keys to the CLI
        if (_isDocumentView && HandleChatEditKey(e))
        {
            e.Handled = true;
            return;
        }

        // In document view: text stays in input box, allow editing freely
        // Only intercept Enter, Escape, and Ctrl shortcuts
        if (_isDocumentView && !string.IsNullOrEmpty(_inputTextBox.Text))
        {
            if (e.Key == Key.Escape)
            {
                _inputTextBox.Text = "";
                e.Handled = true;
                return;
            }
            // Let Enter through to be handled below (sends accumulated text)
            if (e.Key == Key.Enter)
                goto handleKeys;
            // Let Ctrl shortcuts through
            bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (!isCtrl && !IsAltV(e))
                return; // Let TextBox handle normal editing (Backspace, arrows, etc.)
        }

        // If TextBox has text, IME composition is in progress.
        // Let TextBox handle keys (Backspace deletes preedit, etc.)
        // Exception: Ctrl+C/V/F must always work regardless of IME state
        if (!string.IsNullOrEmpty(_inputTextBox.Text))
        {
            if (e.Key == Key.Escape)
            {
                _inputTextBox.Text = "";
                e.Handled = true;
                return;
            }
            bool isCtrlShortcut = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && e.Key is Key.C or Key.V or Key.F or Key.Up or Key.Down;
            if (!isCtrlShortcut && !IsAltV(e))
                return;
        }
        handleKeys:

        // Track input start: record cursor position on first interaction after prompt
        if (_inputStartPending)
        {
            _inputStartAbsRow = ScreenRowToAbsolute(_buffer.CursorRow);
            _inputStartCol = _buffer.CursorCol;
            _inputStartPending = false;
            System.Diagnostics.Debug.WriteLine($"[InputStart] recorded at ({_inputStartAbsRow},{_inputStartCol})");
        }

        // Ctrl+C: copy selection or send SIGINT
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_hasSelection)
            {
                var text = GetSelectedText();
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null && !string.IsNullOrEmpty(text))
                    await clipboard.SetTextAsync(text);
                ClearSelection();
            }
            else
            {
                _inputStartPending = true;
                ClearUndo();
                _pty?.WriteInput("\x03");
            }
            e.Handled = true;
            return;
        }

        // Ctrl+F: toggle search bar
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Chat view searches the conversation, not the terminal grid behind it
            if (_isDocumentView && _docViewPanel != null) _docViewPanel.ShowSearch();
            else if (_searchVisible) HideSearchBar(); else ShowSearchBar();
            e.Handled = true;
            return;
        }

        // Ctrl+Up/Down: navigate between user prompts
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Up)
            {
                System.Diagnostics.Debug.WriteLine($"[PromptNav] Ctrl+Up pressed. _userInputRows={_userInputRows.Count}, scrollback={_buffer.Scrollback.Count}");
                NavigatePrompt(-1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Down)
            {
                System.Diagnostics.Debug.WriteLine($"[PromptNav] Ctrl+Down pressed. _userInputRows={_userInputRows.Count}, scrollback={_buffer.Scrollback.Count}");
                NavigatePrompt(1);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+0: reset font size to default
        if (e.Key == Key.D0 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SetFont(_typeface.FontFamily.Name, 14);
            FontSizeChanged?.Invoke(14);
            e.Handled = true;
            return;
        }

        // Ctrl+V: paste
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_isDocumentView)
            {
                // Document view: paste into IME input box
                _ = PasteToInputBoxAsync();
            }
            else if (_hasSelection && !_caretMoveInProgress)
            {
                // Terminal mode with a selection: paste over it, as an editor would
                PushUndo(UndoKind.Structural);
                RunCaretEdit(() => ReplaceSelectionAsync(PasteFromClipboardAsync));
            }
            else
            {
                // Terminal mode: paste directly to PTY
                PushUndo(UndoKind.Structural);
                _ = PasteFromClipboardAsync();
            }
            e.Handled = true;
            return;
        }

        // Alt+V: Claude Code CLI's image-paste key. The CLI can't read the clipboard
        // through ConPTY, so paste the image as a file path, same as Ctrl+V does.
        // With no image on the clipboard the key goes through as Meta-v.
        if (IsAltV(e))
        {
            _ = PasteImageOrMetaVAsync();
            e.Handled = true;
            return;
        }

        // Ctrl+X: cut the selection. Without one there is nothing to cut, so the key
        // falls through to the PTY the way any other unhandled key does.
        if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _hasSelection)
        {
            PushUndo(UndoKind.Structural);
            RunCaretEdit(CutSelectionAsync);
            e.Handled = true;
            return;
        }

        // Submitting or breaking the line leaves a keyboard selection pointing at
        // text that is about to move, so it goes first.
        if (_kbSelecting && e.Key == Key.Enter)
            ClearSelection();

        // Shift+Enter: send newline (line feed) for multi-line input
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (_isDocumentView)
            {
                // Chat view: a new line in the box, sent with the rest on Enter. Typed in as
                // an edit, so it replaces the selection and Ctrl+Z can take it back.
                _inputTextBox.SelectedText = "\n";
            }
            else
            {
                _pty?.WriteInput("\n");
            }
            e.Handled = true;
            return;
        }

        // Terminal view: the CLI's ghost-text suggestion works as in the chat view - Tab takes it
        // into the prompt for editing, Enter sends it
        string? ghost = !_isDocumentView && e.KeyModifiers == KeyModifiers.None
            && e.Key is Key.Tab or Key.Enter && !_hasSelection && !IsPermissionPromptOnScreen()
            ? ReadPromptSuggestion() : null;
        if (ghost != null && !_firstInputCaptured)
            _firstInputBuffer.Append(ghost);
        if (ghost != null && e.Key == Key.Tab)
        {
            PushUndo(UndoKind.Structural);
            _pty?.WriteInput(ghost);
            e.Handled = true;
            return;
        }

        // Enter: send text to PTY
        if (e.Key == Key.Enter)
        {
            // Document view mode: send accumulated text from input box, then \r.
            // An empty box sends the CLI's suggested prompt shown as its placeholder.
            if (_isDocumentView && string.IsNullOrEmpty(_inputTextBox.Text) && !_attachStrip.HasItems
                && _promptSuggestion is { } suggested)
                _inputTextBox.Text = suggested;
            if (_isDocumentView && SubmitChatInput())
            {
                e.Handled = true;
                return;
            }

            // The plain terminal keeps the prompt in the CLI's own line, so holding it only
            // means not pressing Enter yet; going ahead replays the key.
            if (!_isDocumentView && SubmitGate is { } gate
                && !gate(ghost ?? ReadSubmittedLine() ?? "", ReplayEnter))
            {
                e.Handled = true;
                return;
            }

            // Record input position for prompt navigation
            int submitRow = ScreenRowToAbsolute(_buffer.CursorRow);
            // Only record if it's a different position from the last recorded one
            if (_userInputRows.Count == 0 || Math.Abs(_userInputRows[^1] - _inputStartAbsRow) > 1)
                _userInputRows.Add(_inputStartAbsRow);

            // Capture first input as tab title
            if (!_firstInputCaptured && _firstInputBuffer.Length > 0)
            {
                _firstInputCaptured = true;
                FirstUserInput = _firstInputBuffer.ToString().Trim();
                var summary = FirstUserInput;
                if (summary.Length > 30) summary = summary[..30] + "...";
                if (!string.IsNullOrWhiteSpace(summary))
                    TitleChanged?.Invoke(summary);
            }
            PromptSubmitted?.Invoke(ghost ?? ReadSubmittedLine());
            _inputStartPending = true;
            ClearUndo();
            // Attached images ride along with a prompt, never with an answer to a
            // permission question, which Enter also confirms
            if (ghost != null)
            {
                bool withImages = _attachStrip.HasItems;
                WriteAndSubmit(withImages ? ghost + " " + _attachStrip.TakeReferences() : ghost, withImages);
            }
            else if (_attachStrip.HasItems && !IsPermissionPromptOnScreen())
                WriteAndSubmit(" " + _attachStrip.TakeReferences(), true);
            else
                _pty?.WriteInput("\r");
            e.Handled = true;
            return;
        }

        // Tab in an empty chat box: take the suggested prompt into the box for editing
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _isDocumentView
            && string.IsNullOrEmpty(_inputTextBox.Text) && _promptSuggestion is { } suggestion)
        {
            _inputTextBox.Text = suggestion;
            _inputTextBox.CaretIndex = suggestion.Length;
            e.Handled = true;
            return;
        }

        // Escape: collapse expanded panel if open, otherwise send to PTY
        if (e.Key == Key.Escape)
        {
            if (_isExpanded)
                CollapseInputPanel();
            else
                _pty?.WriteInput("\x1b");
            e.Handled = true;
            return;
        }

        // Backspace / Delete with selection: delete all selected characters
        if (_hasSelection && (e.Key == Key.Back || e.Key == Key.Delete))
        {
            PushUndo(UndoKind.Structural);
            RunCaretEdit(DeleteSelectionAsync);
            e.Handled = true;
            return;
        }

        // Shift+arrows: extend a selection over the CLI's input, the way an editor
        // does. Nothing is sent to the CLI - the selection is ours to draw, and the
        // keys that act on one (Ctrl+C, Ctrl+X, Backspace, typing over it) already
        // know how to reach the text through the caret.
        if (!_isDocumentView
            && (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt)) == KeyModifiers.Shift
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
            && ExtendKeyboardSelection(e.Key))
        {
            e.Handled = true;
            return;
        }

        // A plain caret move collapses a keyboard selection, as it would in an editor.
        if (_kbSelecting && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                                  or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            ClearSelection();

        // Forward navigation/editing keys directly to PTY
        {
            string? seq = e.Key switch
            {
                Key.Back => "\x7f",
                Key.Delete => "\x1b[3~",
                Key.Up => "\x1b[A",
                Key.Down => "\x1b[B",
                Key.Left => "\x1b[D",
                Key.Right => "\x1b[C",
                Key.Home => "\x1b[H",
                Key.End => "\x1b[F",
                Key.PageUp => "\x1b[5~",
                Key.PageDown => "\x1b[6~",
                Key.Tab => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "\x1b[Z" : "\t",
                Key.F1 => "\x1bOP",
                Key.F2 => "\x1bOQ",
                Key.F3 => "\x1bOR",
                Key.F4 => "\x1bOS",
                Key.F5 => "\x1b[15~",
                Key.F6 => "\x1b[17~",
                Key.F7 => "\x1b[18~",
                Key.F8 => "\x1b[19~",
                Key.F9 => "\x1b[20~",
                Key.F10 => "\x1b[21~",
                Key.F11 => "\x1b[23~",
                Key.F12 => "\x1b[24~",
                _ => null
            };

            if (seq != null)
            {
                // Deleting a character is undoable. Moving the caret is not, but it does
                // close the current run, so what gets typed next undoes on its own.
                if (e.Key is Key.Back or Key.Delete) PushUndo(UndoKind.Deleting);
                else BreakUndoRun();
                _pty?.WriteInput(seq);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+D: send EOF
        if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _pty?.WriteInput("\x04");
            e.Handled = true;
            return;
        }

        // Ctrl+Z undoes the last edit to the CLI's input line, Ctrl+Shift+Z redoes it.
        // With nothing of ours to put back, the key keeps its terminal meaning and
        // goes through as SIGTSTP.
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            bool redo = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var stack = redo ? _redoStack : _undoStack;
            if (!alt && !_isDocumentView && !_caretMoveInProgress && stack.Count > 0)
                RunCaretEdit(redo ? RedoAsync : UndoAsync);
            else
                _pty?.WriteInput("\x1a");
            e.Handled = true;
            return;
        }

        // Ctrl+Y: redo, the binding Windows editors use.
        if (e.Key == Key.Y
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.Control)
        {
            if (!_isDocumentView && !_caretMoveInProgress && _redoStack.Count > 0)
                RunCaretEdit(RedoAsync);
            else
                _pty?.WriteInput("\x19");
            e.Handled = true;
            return;
        }

        // Ctrl+L: clear screen
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _pty?.WriteInput("\x0c");
            e.Handled = true;
            return;
        }

        // Generic Ctrl+letter: send corresponding control character to PTY
        // (Ctrl+A=0x01, Ctrl+B=0x02, ..., Ctrl+O=0x0F, ..., Ctrl+Z=0x1A)
        // Only pure Ctrl (no Shift/Alt) to avoid hijacking Ctrl+Shift shortcuts
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.Control
            && e.Key >= Key.A && e.Key <= Key.Z)
        {
            char controlChar = (char)(e.Key - Key.A + 1);
            _pty?.WriteInput(controlChar.ToString());
            e.Handled = true;
            return;
        }
    }

    private static bool IsAltV(KeyEventArgs e) =>
        e.Key == Key.V
        && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)) == KeyModifiers.Alt;

    /// <summary>
    /// Attaches whatever images the clipboard holds — copied image files, or a bitmap
    /// saved to a temp PNG — to the thumbnail strip. False when there were none.
    /// </summary>
    private async Task<bool> TryAttachClipboardImagesAsync(IClipboard clipboard)
    {
        try
        {
            // Files copied in Explorer: only the images among them become attachments
            var files = await clipboard.TryGetFilesAsync();
            if (files != null)
            {
                var images = files
                    .Select(f => f.Path?.LocalPath)
                    .Where(p => !string.IsNullOrEmpty(p) && Controls.ImageAttachmentStrip.IsImageFile(p!))
                    .ToList();
                foreach (var path in images)
                    _attachStrip.Add(path!, FileReference(path!));
                if (images.Count > 0) return true;
            }

            // TryGetBitmapAsync covers every bitmap flavour Windows offers (PNG, CF_DIB, …),
            // so the format probing the old clipboard API needed is no longer necessary.
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap == null) return false;
            var tempPath = SaveClipboardImage(bitmap);
            if (tempPath == null) return false;
            _attachStrip.Add(tempPath, tempPath.Contains(' ') ? $"\"{tempPath}\"" : tempPath);
            return true;
        }
        catch { return false; }
    }

    private async Task PasteImageOrMetaVAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null || !await TryAttachClipboardImagesAsync(clipboard))
            _pty?.WriteInput("\x1bv");
    }

    private async Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        // Images go to the thumbnail strip and out with the next submit
        if (await TryAttachClipboardImagesAsync(clipboard))
            return;

        // Fallback: paste text
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            if (_buffer.BracketedPasteMode)
                _pty?.WriteInput("\x1b[200~" + text + "\x1b[201~");
            else
                _pty?.WriteInput(text);
        }
    }

    private static string? SaveClipboardImage(Avalonia.Media.Imaging.Bitmap image)
    {
        try
        {
            var tempDir = Services.AppPaths.Temp;
            Directory.CreateDirectory(tempDir);
            var fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(tempDir, fileName);
            image.Save(filePath, PngBitmapEncoderOptions.Default);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    // Scroll offset: 0 = bottom (live), >0 = scrolled up into history
    private int _scrollOffset;

    private void MeasureCellSize()
    {
        var ft = new FormattedText("M", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White);
        _cellWidth = ft.Width;
        _cellHeight = ft.Height;
    }

    private void RecalcTerminalSize()
    {
        // The chat view leaves the PTY alone unless the grid is showing in its side pane
        if (_isDocumentView && !TerminalInSidePane) return;
        double viewW = TermViewWidth;
        if (_cellWidth <= 0 || _cellHeight <= 0 || viewW <= 0) return;
        double termH = TerminalAreaHeight;
        // Leave the scrollbar its own strip; cells reaching into it paint over the bar
        int newCols = Math.Max(10, (int)((viewW - ScrollbarWidth) / _cellWidth));
        int newRows = Math.Max(5, (int)(termH / _cellHeight));
        if (newCols != _buffer.Cols || newRows != _buffer.Rows)
        {
            _buffer.Resize(newRows, newCols);
            _pty?.Resize(newCols, newRows);
        }
    }

    public void StartProcess(string command, string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
        double termH = TerminalAreaHeight;
        int cols = Math.Max(10, (int)((Bounds.Width - ScrollbarWidth) / _cellWidth));
        int rows = Math.Max(5, (int)(termH / _cellHeight));
        if (cols < 10) cols = 80;
        if (rows < 5) rows = 24;

        _buffer = new TerminalBuffer(rows, cols);
        _parser = new VtParser(_buffer);
        _parser.TitleChanged += title =>
        {
            TabTitle = title;
            Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
        };
        _buffer.BufferChanged += () =>
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
            ScheduleCodeBlockScan();
        };

        _pty = new PseudoConsole();
        _pty.OutputReceived += data =>
        {
            _parser.Process(new ReadOnlySpan<byte>(data));
            ScanForLocalServer(data);
            _scrollOffset = 0;
            if (_promptNavBar is { IsVisible: true })
                Dispatcher.UIThread.Post(HidePromptNavBar);
        };
        _pty.ProcessExited += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _parser.Process("\r\n[Process exited]\r\n");
                var hint = LaunchCollisionHint();
                if (hint is not null) _parser.Process(hint);
                Exited?.Invoke();
            });
        };

        _startedUtc = DateTime.UtcNow;
        _pty.Start(command, workingDirectory, cols, rows);

        // Claude Code only — other CLIs prompt differently, and the flag is off for them.
        StartPermissionWatch();
    }

    /// <summary>
    /// A launch that collides with a background agent dies before its prompt appears, leaving one
    /// line of CLI notice and a bare "[Process exited]". Spell out what happened and what to do
    /// about it. Null for an ordinary exit.
    /// </summary>
    private string? LaunchCollisionHint()
    {
        try
        {
            // A session that ran a while and then quit is just a session that ended.
            if (DateTime.UtcNow - _startedUtc > TimeSpan.FromSeconds(30)) return null;
            if (!GetScreenText(0).Contains(Services.RunningSessionService.CollisionMarker,
                    StringComparison.OrdinalIgnoreCase))
                return null;

            var hint = Services.Loc.Get("SessionBusyHint",
                    "This session is already open in a background agent, so the CLI exited without "
                    + "a prompt.\nRun `claude agents` in a terminal to attach to it and /exit to "
                    + "release it, or start a new session.")
                .Replace("\r\n", "\n").Replace("\n", "\r\n");
            return $"\u001b[33m{hint}\u001b[0m\r\n";
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether the CLI is mid-turn. Drives the progress line under the input row; the window
    /// does not have to be the active one for it to run.
    /// </summary>
    public bool IsGenerating
    {
        get => _marquee.IsActive;
        set
        {
            if (_marquee.IsActive == value) return;
            _marquee.IsActive = value;

            // Stop only exists while there is something to stop. The collapsed one takes width
            // from the text box, so the row has to be laid out again either way.
            _stopButton.IsVisible = value;
            _expandedStopButton.IsVisible = value;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    /// <summary>
    /// Escape is what actually interrupts the CLI; this is the same key with a label on it, for
    /// anyone who does not know that. Focus goes back to the terminal so the next keystroke
    /// lands where the user expects rather than on the button.
    /// </summary>
    private Button NewStopButton(Thickness padding)
    {
        var button = new Button
        {
            Content = "■ " + Services.Loc.Get("StopTask", "Stop"),
            FontSize = 10,
            Padding = padding,
            Background = new SolidColorBrush(Color.FromArgb(36, 255, 69, 58)),
            Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            // Avalonia's default leaves the label at the top of the button box, which reads as
            // the whole control sitting high in the input row.
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,
            IsVisible = false,
        };
        ToolTip.SetTip(button, Services.Loc.Get("StopTaskTooltip", "Stop what the AI is doing (Esc)"));
        button.Click += (_, _) =>
        {
            if (_isDocumentView)
            {
                StopTurn();
                return;
            }
            SendText("\x1b");
            FocusTerminal();
        };
        return button;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ChatComposerShown)
        {
            // The box takes the height its text wraps to, within bounds, and the transcript
            // above gives up that much room
            // The width being measured for, not the last one arranged: going by Bounds, a window
            // that just got narrower lays out at its old width and clips the right-hand side
            double w = ChatWidth(double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width);
            var card = ChatCardRect(w, 0);
            _inputTextBox.Measure(new Size(Math.Max(0, card.Width - ChatCardPadX * 2), double.PositiveInfinity));
            _chatInputHeight = Math.Clamp(_inputTextBox.DesiredSize.Height, ChatInputMinHeight, ChatInputMaxHeight);
            _stopButton.Measure(new Size(w, ChatButtonSize));
            _expandButton.Measure(new Size(ChatButtonSize, ChatButtonSize));
            _chatAttachButton.Measure(new Size(ChatButtonSize, ChatButtonSize));
            _chatSendButton.Measure(new Size(ChatButtonSize, ChatButtonSize));
            _sidePaneToggle.Measure(new Size(ChatButtonSize, ChatButtonSize));
            _chatBackdrop.Measure(new Size(w, ChatComposerHeight));
            _chatCard.Measure(card.Size);
        }
        else
        {
            _stopButton.Measure(new Size(availableSize.Width, InputBoxHeight));
            double stopW = _stopButton.IsVisible ? _stopButton.DesiredSize.Width : 0;
            double tbW = Math.Max(0, availableSize.Width - ExpandButtonWidth - stopW);
            _inputTextBox.Measure(new Size(tbW, InputBoxHeight));
            _expandButton.Measure(new Size(ExpandButtonWidth, InputBoxHeight));
        }
        _marquee.Measure(new Size(availableSize.Width, Controls.MarqueeBar.LineHeight));
        if (_isExpanded)
            _expandedPanel.Measure(new Size(availableSize.Width, _expandedHeight));
        _searchBar?.Measure(availableSize);
        _attachStrip.Measure(new Size(availableSize.Width, Controls.ImageAttachmentStrip.StripHeight));
        if (_isDocumentView && _docViewPanel != null)
        {
            // Prefer the size being measured for; Bounds only stands in when that is unbounded,
            // since it still holds the previous layout's size and would keep bubbles off-screen
            double actualH = double.IsFinite(availableSize.Height) ? availableSize.Height : Bounds.Height;
            double docH = Math.Max(0, actualH - InputAreaHeight - ExpandedPanelHeight);
            _docViewPanel.Measure(new Size(
                ChatWidth(double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width),
                docH));
        }
        MeasureSidePane(availableSize);
        return availableSize;
    }

    /// <summary>The composer card: spans the window like the transcript, above a margin.</summary>
    private Rect ChatCardRect(double width, double height)
    {
        double cardW = Math.Max(0, width - 48);
        double cardH = ChatComposerHeight - ChatGapTop - ChatGapBottom;
        return new Rect((width - cardW) / 2, height - ChatGapBottom - cardH, cardW, cardH);
    }

    private void ArrangeChatComposer(Size finalSize)
    {
        var hidden = new Rect(0, finalSize.Height, 0, 0);
        if (!ChatComposerShown)
        {
            _chatBackdrop.Arrange(hidden);
            _chatCard.Arrange(hidden);
            _chatAttachButton.Arrange(hidden);
            _chatSendButton.Arrange(hidden);
            _sidePaneToggle.Arrange(hidden);
            return;
        }

        double areaH = ChatComposerHeight;
        double chatW = ChatWidth(finalSize.Width);
        _chatBackdrop.Arrange(new Rect(0, finalSize.Height - areaH, chatW, areaH));
        var card = ChatCardRect(chatW, finalSize.Height);
        _chatCard.Arrange(card);

        double y = card.Y + ChatCardPadTop;
        double stripH = AttachmentStripHeight;
        _attachStrip.Arrange(stripH > 0 ? new Rect(card.X + 4, y, Math.Max(0, card.Width - 8), stripH) : hidden);
        y += stripH;
        _inputTextBox.Arrange(new Rect(card.X + ChatCardPadX, y, Math.Max(0, card.Width - ChatCardPadX * 2), _chatInputHeight));
        y += _chatInputHeight + 4;

        double s = ChatButtonSize;
        _chatAttachButton.Arrange(new Rect(card.X + 8, y, s, s));
        double right = card.Right - 8 - s;
        _chatSendButton.Arrange(new Rect(right, y, s, s));
        if (_stopButton.IsVisible)
        {
            double stopW = _stopButton.DesiredSize.Width;
            right -= stopW + 6;
            _stopButton.Arrange(new Rect(right, y, stopW, s));
        }
        else
        {
            _stopButton.Arrange(hidden);
        }
        _expandButton.Arrange(new Rect(right - 4 - s, y, s, s));
        _sidePaneToggle.Arrange(new Rect(right - 6 - s * 2, y, s, s));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeChatComposer(finalSize);
        if (ChatComposerShown)
        {
            // Everything in the input row was placed inside the card
        }
        else if (_isExpanded)
        {
            // Expanded: panel at bottom, hide input row. Stop rides along in the panel's own
            // bottom-right button row, so the loose one is parked off-screen with the rest.
            double epY = finalSize.Height - _expandedHeight;
            _expandedPanel.Arrange(new Rect(0, epY, finalSize.Width, _expandedHeight));
            // Move input row off-screen
            _inputTextBox.Arrange(new Rect(0, finalSize.Height, 0, 0));
            _stopButton.Arrange(new Rect(0, finalSize.Height, 0, 0));
            _expandButton.Arrange(new Rect(0, finalSize.Height, 0, 0));
        }
        else
        {
            // Normal: input row at bottom, with Stop between the text box and the expander so
            // the expander keeps the far-right position it holds when nothing is running.
            double tbY = finalSize.Height - InputBoxHeight;
            double stopW = _stopButton.IsVisible ? _stopButton.DesiredSize.Width : 0;
            double tbW = Math.Max(0, finalSize.Width - ExpandButtonWidth - stopW);
            _inputTextBox.Arrange(new Rect(0, tbY, tbW, InputBoxHeight));
            _stopButton.Arrange(new Rect(tbW, tbY, stopW, InputBoxHeight));
            _expandButton.Arrange(new Rect(tbW + stopW, tbY, ExpandButtonWidth, InputBoxHeight));
        }

        // Attachment thumbnails sit directly above whichever input is showing
        if (!ChatComposerShown)
        {
            double stripH = AttachmentStripHeight;
            double stripY = Math.Max(0, finalSize.Height - InputAreaHeight - ExpandedPanelHeight);
            _attachStrip.Arrange(stripH > 0
                ? new Rect(0, stripY, finalSize.Width, stripH)
                : new Rect(0, finalSize.Height, 0, 0));
        }

        // Always the bottom edge of the control, in both layouts. The input row is flush with
        // that edge either way, so the line stays inside it and never shifts when the input is
        // expanded or collapsed. It overlays rather than adds height, so the PTY is not resized.
        _marquee.Arrange(new Rect(
            0,
            Math.Max(0, finalSize.Height - Controls.MarqueeBar.LineHeight),
            finalSize.Width,
            Controls.MarqueeBar.LineHeight));

        // Position document view panel (fills terminal area)
        if (_docViewPanel != null)
        {
            if (_isDocumentView)
            {
                double docH = Math.Max(0, finalSize.Height - InputAreaHeight - ExpandedPanelHeight);
                _docViewPanel.Arrange(new Rect(0, 0, ChatWidth(finalSize.Width), docH));
            }
            else
            {
                _docViewPanel.Arrange(new Rect(0, finalSize.Height, 0, 0));
            }
        }
        ArrangeSidePane(finalSize);

        // Position search bar at top-right
        if (_searchBar != null && _searchVisible)
        {
            double sbW = Math.Min(_searchBar.DesiredSize.Width, finalSize.Width);
            _searchBar.Arrange(new Rect(finalSize.Width - sbW, 0, sbW, _searchBar.DesiredSize.Height));
        }

        return finalSize;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RecalcTerminalSize();
        InvalidateVisual();
    }

    private bool IsOnScrollbar(Point pos)
    {
        return _buffer.Scrollback.Count > 0 && !_buffer.IsAltBuffer && pos.X >= TermViewWidth - ScrollbarWidth && pos.Y < TerminalAreaHeight;
    }

    private (double y, double height) GetScrollbarThumb()
    {
        double termH = TerminalAreaHeight;
        double totalLines = _buffer.Scrollback.Count + _buffer.Rows;
        double viewportRatio = (double)_buffer.Rows / totalLines;
        double thumbH = Math.Max(ScrollbarThumbMinHeight, termH * viewportRatio);
        double trackH = termH - thumbH;
        double maxOffset = _buffer.Scrollback.Count;
        double thumbY = maxOffset > 0 ? trackH * (1.0 - (double)_scrollOffset / maxOffset) : trackH;
        return (thumbY, thumbH);
    }

    private (int row, int col) PointToCell(Point pos)
    {
        int row = Math.Clamp((int)(pos.Y / _cellHeight), 0, _buffer.Rows - 1);
        double x = 0;
        int col = 0;
        for (; col < _buffer.Cols; col++)
        {
            var cell = GetCellAt(row, col);
            if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                continue;
            bool isWide = TerminalBuffer.IsWideChar(cell.Character);
            double cellW = isWide ? _cellWidth * 2 : _cellWidth;
            if (x + cellW / 2 > pos.X) break;
            x += cellW;
        }
        return (row, Math.Clamp(col, 0, _buffer.Cols - 1));
    }

    private int ScreenRowToAbsolute(int screenRow)
    {
        return _buffer.Scrollback.Count - _scrollOffset + screenRow;
    }

    private int AbsoluteToScreenRow(int absRow)
    {
        return absRow - (_buffer.Scrollback.Count - _scrollOffset);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Right-click on diagram: show context menu
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && !_isDocumentView)
        {
            var pos = e.GetPosition(this);
            var diagramBlock = HitTestDiagram(pos);
            if (diagramBlock != null)
            {
                ShowDiagramContextMenu(diagramBlock, pos);
                e.Handled = true;
                return;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Clicked?.Invoke();
            var pos = e.GetPosition(this);

            // In the chat view the grid only takes clicks inside the side pane's terminal
            if (_isDocumentView)
            {
                if (!TerminalInSidePane || !SideTerminalRect.Contains(pos))
                {
                    _inputTextBox.Focus();
                    return;
                }
                pos = TermPoint(pos);
            }

            // Click in input box area - let TextBox handle it
            if (pos.Y >= TerminalAreaHeight)
            {
                _inputTextBox.Focus();
                return;
            }

            // Focus the input TextBox for keyboard input
            _inputTextBox.Focus();

            // Scrollbar hit test
            if (IsOnScrollbar(pos))
            {
                var (thumbY, thumbH) = GetScrollbarThumb();
                if (pos.Y >= thumbY && pos.Y <= thumbY + thumbH)
                {
                    _isScrollbarDragging = true;
                    _scrollbarDragStartY = pos.Y;
                    _scrollbarDragStartOffset = _scrollOffset;
                }
                else
                {
                    double trackH = TerminalAreaHeight - thumbH;
                    double ratio = Math.Clamp(pos.Y / (trackH > 0 ? trackH : 1), 0, 1);
                    _scrollOffset = (int)((1.0 - ratio) * _buffer.Scrollback.Count);
                    InvalidateVisual();
                }
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            // Text selection
            var (row, col) = PointToCell(pos);
            _selStartRow = ScreenRowToAbsolute(row);
            _selStartCol = col;
            _selEndRow = _selStartRow;
            _selEndCol = _selStartCol;
            _isSelecting = true;
            _hasSelection = false;
            _kbSelecting = false;
            _kbDesiredCol = -1;
            _selLeftMargin = 0;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isScrollbarDragging)
        {
            var pos = TermPoint(e.GetPosition(this));
            var (_, thumbH) = GetScrollbarThumb();
            double trackH = TerminalAreaHeight - thumbH;
            if (trackH > 0)
            {
                double deltaY = pos.Y - _scrollbarDragStartY;
                double deltaRatio = deltaY / trackH;
                int newOffset = _scrollbarDragStartOffset - (int)(deltaRatio * _buffer.Scrollback.Count);
                _scrollOffset = Math.Clamp(newOffset, 0, _buffer.Scrollback.Count);
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        if (_isSelecting)
        {
            var (row, col) = PointToCell(TermPoint(e.GetPosition(this)));
            _selEndRow = ScreenRowToAbsolute(row);
            _selEndCol = col;
            _hasSelection = (_selStartRow != _selEndRow || _selStartCol != _selEndCol);
            UpdateSelectionMargin();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isScrollbarDragging)
        {
            _isScrollbarDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_isSelecting)
        {
            _isSelecting = false;
            e.Pointer.Capture(null);
            e.Handled = true;

            // A press and release on the same cell is a click, not a drag, so it
            // asks for the caret rather than for a selection.
            if (!_hasSelection)
                TryMoveCaretToClick(AbsoluteToScreenRow(_selEndRow), _selEndCol);
        }
    }

    private void GetOrderedSelection(out int startRow, out int startCol, out int endRow, out int endCol)
    {
        if (_selStartRow < _selEndRow || (_selStartRow == _selEndRow && _selStartCol <= _selEndCol))
        {
            startRow = _selStartRow; startCol = _selStartCol;
            endRow = _selEndRow; endCol = _selEndCol;
        }
        else
        {
            startRow = _selEndRow; startCol = _selEndCol;
            endRow = _selStartRow; endCol = _selStartCol;
        }
    }

    // Decides where the rows of the current selection begin. The CLI indents every
    // row of its input block to line up past the prompt marker, so a selection that
    // stays inside the block starts at the text rather than in that gutter; drag a
    // selection over ordinary output and column 0 is content again, margin zero.
    private void UpdateSelectionMargin()
    {
        _selLeftMargin = 0;
        if (!_hasSelection || _scrollOffset != 0) return;
        if (!TryGetInputBlock(out int blockTop, out int blockBottom, out int textLeft)) return;

        GetOrderedSelection(out int sr, out _, out int er, out _);
        int startRow = AbsoluteToScreenRow(sr);
        int endRow = AbsoluteToScreenRow(er);
        if (startRow >= blockTop && endRow <= blockBottom)
            _selLeftMargin = textLeft;
    }

    private bool IsCellSelected(int screenRow, int col)
    {
        if (!_hasSelection) return false;
        int absRow = ScreenRowToAbsolute(screenRow);
        GetOrderedSelection(out int sr, out int sc, out int er, out int ec);
        if (absRow < sr || absRow > er) return false;
        if (col < _selLeftMargin) return false;
        if (absRow == sr && col < sc) return false;
        if (absRow == er && col > ec) return false;
        return true;
    }

    // The column the selected rows share as their left edge: the rightmost one that
    // still has nothing but blanks to its left on every row. Rows that are blank all
    // the way across say nothing about it and are passed over, as are rows that only
    // exist because the one above them ran out of width - their left edge is the
    // wrap's, not the block's. Never reaches further right than the text does, so a
    // selection that already starts at column zero - anything outside the CLI's
    // indented message column - is left as it is.
    private int GetSelectionIndent(int startRow, int endRow, JoinKind[] runsOn)
    {
        int indent = int.MaxValue;
        for (int absRow = startRow; absRow <= endRow; absRow++)
        {
            if (absRow > startRow && runsOn[absRow - 1 - startRow] != JoinKind.Break) continue;
            int rowEnd = RowWidth(absRow) - _selLeftMargin - 1;
            int first = FirstUsedCol(absRow, _selLeftMargin, rowEnd);
            if (first < 0) continue;
            if (first < indent) indent = first;
            if (indent <= _selLeftMargin) break;   // cannot get any shallower
        }
        return indent == int.MaxValue ? _selLeftMargin : indent;
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return "";
        GetOrderedSelection(out int sr, out int sc, out int er, out int ec);

        // Which of these rows are a row only because the line was too long for the
        // screen. The CLI wraps its own output to the terminal's width and starts the
        // remainder on the next row, so the break between them is the screen's, not the
        // text's, and copying it out hands back a command or a path snapped in two.
        var runsOn = new JoinKind[Math.Max(0, er - sr)];
        for (int i = 0; i < runsOn.Length; i++)
            runsOn[i] = RowRunsOn(sr + i);

        // Code the CLI prints to be copied sits indented - past the message bullet,
        // and again for the block itself - and that gutter is layout, not text. Taking
        // off the shallowest indentation the selected rows share clears it from every
        // row while leaving the code's own nesting, which is the part that matters.
        int indent = GetSelectionIndent(sr, er, runsOn);

        var sb = new System.Text.StringBuilder();
        for (int absRow = sr; absRow <= er; absRow++)
        {
            // The input block is padded on the right by as much as it is indented on
            // the left, so a row there ends short of the terminal's own edge. Reading
            // past it would fold that padding into a soft-wrapped line.
            int rowEnd = RowWidth(absRow) - _selLeftMargin - 1;
            bool continued = absRow > sr && runsOn[absRow - 1 - sr] != JoinKind.Break;
            // What a continuation carries on its left is the space the CLI used to line
            // it up under the row above - the wrap showing, not text - so such a row
            // starts where its own first character does.
            int colStart = continued
                ? Math.Max(_selLeftMargin, FirstUsedCol(absRow, _selLeftMargin, rowEnd))
                : Math.Max(indent, (absRow == sr) ? sc : 0);
            int colEnd = Math.Min(rowEnd, (absRow == er) ? ec : rowEnd);
            for (int col = colStart; col <= colEnd; col++)
            {
                var cell = GetCellAtAbs(absRow, col);
                // Skip wide-char trail cells (their content is '\0')
                if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                    continue;
                sb.Append(cell.Text);
            }
            if (absRow < er)
            {
                // Blank cells at the end of a row are never text. At a real line break
                // they are the unwritten rest of the line; on a row that wrapped they
                // are the room the next word needed and could not get. Either way what
                // goes in their place is decided below, not carried over from the grid.
                int len = sb.Length;
                while (len > 0 && sb[len - 1] == ' ') len--;
                sb.Length = len;

                switch (runsOn[absRow - sr])
                {
                    case JoinKind.Break: sb.AppendLine(); break;
                    case JoinKind.JoinWithSpace: sb.Append(' '); break;
                    // JoinKind.Join: the wrap cut through a word; the text carries
                    // straight on with nothing in between.
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    // Is this row's line carried on by the row below it? Two things wrap a line here
    // and only one of them leaves a mark in the buffer. The terminal's own wrap sets the
    // buffer's flag, and it only ever fires on text that ran past the last column, so
    // there is no space in it to put back. The CLI's wrap - the common one, because it
    // lays its output out itself - sets nothing at all, and has to be read off the row.
    private JoinKind RowRunsOn(int absRow)
    {
        var kind = RowJoinKind(absRow, _selLeftMargin);
        if (kind != JoinKind.Break) return kind;
        if (_selLeftMargin > 0) return JoinKind.Break;
        if (IsFramedBoundary(absRow, _selLeftMargin)) return JoinKind.Break;

        int sbCount = _buffer.Scrollback.Count;
        bool flagged = absRow < sbCount
            ? _buffer.IsScrollbackLineWrapped(absRow)
            : _buffer.IsLineWrapped(absRow - sbCount);
        return flagged ? JoinKind.Join : JoinKind.Break;
    }

    private void ClearSelection()
    {
        _hasSelection = false;
        _isSelecting = false;
        _kbSelecting = false;
        _kbDesiredCol = -1;
        _selLeftMargin = 0;
        InvalidateVisual();
    }

    // ── Keyboard selection (Shift+arrows) ──

    // Moves one end of the selection by a character or a row. Nothing goes to the
    // CLI: the caret stays where the CLI put it, which is where the edit keys expect
    // to start walking from. The walk stays inside the CLI's input block, because a
    // selection outside it is not editable and there would be no keyboard way back.
    // Returns false when there is no input block to walk, leaving the key to the CLI.
    private bool ExtendKeyboardSelection(Key key)
    {
        if (_pty == null || _scrollOffset != 0) return false;
        if (!TryGetInputBlock(out int blockTop, out int blockBottom, out int textLeft))
            return false;

        bool InBlock(int r) => r >= blockTop && r <= blockBottom;

        int row, col;
        if (_kbSelecting && InBlock(_kbAnchorRow) && InBlock(_kbFocusRow))
        {
            row = _kbFocusRow;
            col = ClampToRowContent(row, _kbFocusCol, textLeft);
        }
        else if (_hasSelection
                 && TryGetEditableSelection(out int msr, out int msc, out int mer, out int mec))
        {
            // Carry on from a selection made with the mouse rather than dropping it.
            _kbAnchorRow = msr;
            _kbAnchorCol = ClampToRowContent(msr, msc, textLeft);
            row = mer;
            col = ClampToRowContent(mer, NextCol(mer, mec), textLeft);
            _kbDesiredCol = -1;
            _kbSelecting = true;
        }
        else
        {
            // A fresh selection starts at the CLI's own caret.
            row = _buffer.CursorRow;
            if (!InBlock(row)) return false;
            col = ClampToRowContent(row, _buffer.CursorCol, textLeft);
            _kbAnchorRow = row;
            _kbAnchorCol = col;
            _kbDesiredCol = -1;
            _kbSelecting = true;
        }

        switch (key)
        {
            case Key.Left:
                if (col > textLeft) col = PrevCol(row, col);
                else if (row > blockTop) { row--; col = RowContentEnd(row, textLeft); }
                _kbDesiredCol = -1;
                break;

            case Key.Right:
                if (col < RowContentEnd(row, textLeft)) col = NextCol(row, col);
                else if (row < blockBottom) { row++; col = textLeft; }
                _kbDesiredCol = -1;
                break;

            case Key.Up:
                if (_kbDesiredCol < 0) _kbDesiredCol = col;
                if (row > blockTop)
                {
                    row--;
                    col = SnapToCharStart(row, ClampToRowContent(row, _kbDesiredCol, textLeft));
                }
                // Already on the top row: take its start, and let go of the column
                // being held on to, since the caret is visibly no longer in it.
                else _kbDesiredCol = col = textLeft;
                break;

            case Key.Down:
                if (_kbDesiredCol < 0) _kbDesiredCol = col;
                if (row < blockBottom)
                {
                    row++;
                    col = SnapToCharStart(row, ClampToRowContent(row, _kbDesiredCol, textLeft));
                }
                // Likewise on the bottom row: take its end and hold on to that instead.
                else _kbDesiredCol = col = RowContentEnd(row, textLeft);
                break;
        }

        _kbFocusRow = row;
        _kbFocusCol = col;
        ApplyKeyboardSelection(textLeft);
        return true;
    }

    // Turns the caret pair into the inclusive cell pair the rest of the selection
    // code speaks: the far caret steps back one character to name the last cell.
    private void ApplyKeyboardSelection(int textLeft)
    {
        int loRow = _kbAnchorRow, loCol = _kbAnchorCol;
        int hiRow = _kbFocusRow, hiCol = _kbFocusCol;
        if (hiRow < loRow || (hiRow == loRow && hiCol < loCol))
        {
            (loRow, hiRow) = (hiRow, loRow);
            (loCol, hiCol) = (hiCol, loCol);
        }

        if (loRow == hiRow && loCol >= hiCol)
        {
            _hasSelection = false;                          // walked back onto the anchor
            InvalidateVisual();
            return;
        }

        int endRow, endCol;
        if (hiRow > loRow && hiCol <= textLeft)
        {
            // The far caret sits at the start of its row, so the last selected cell
            // is the last one on the row above.
            endRow = hiRow - 1;
            endCol = _buffer.Cols - 1;
        }
        else
        {
            endRow = hiRow;
            endCol = PrevCol(hiRow, hiCol);
        }

        _selStartRow = ScreenRowToAbsolute(loRow);
        _selStartCol = loCol;
        _selEndRow = ScreenRowToAbsolute(endRow);
        _selEndCol = endCol;
        _hasSelection = true;
        _isSelecting = false;
        _selLeftMargin = textLeft;      // the walk never leaves the input block
        InvalidateVisual();
    }

    // A caret never sits between the halves of a double-width character, so these
    // three step over the trailing half rather than into it.
    private int SnapToCharStart(int row, int col)
    {
        while (col > 0 && GetCellAt(row, col).Attributes.HasFlag(CellAttributes.WideCharTrail))
            col--;
        return col;
    }

    private int NextCol(int row, int col)
    {
        int c = col + 1;
        while (c < _buffer.Cols && GetCellAt(row, c).Attributes.HasFlag(CellAttributes.WideCharTrail))
            c++;
        return c;
    }

    private int PrevCol(int row, int col) => SnapToCharStart(row, col - 1);

    private TerminalCell GetCellAtAbs(int absRow, int col)
    {
        int scrollbackCount = _buffer.Scrollback.Count;
        if (absRow < scrollbackCount)
        {
            var line = _buffer.GetScrollbackLine(absRow);
            return (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
        }
        int bufRow = absRow - scrollbackCount;
        return (bufRow >= 0 && bufRow < _buffer.Rows && col >= 0 && col < _buffer.Cols)
            ? _buffer.GetCell(bufRow, col) : TerminalCell.Empty;
    }

    private int CountCharsBetweenCols(int row, int fromCol, int toCol)
    {
        if (fromCol == toCol) return 0;
        int startCol = Math.Min(fromCol, toCol);
        int endCol = Math.Max(fromCol, toCol);
        int count = 0;
        for (int col = startCol; col < endCol && col < _buffer.Cols; col++)
        {
            if (!_buffer.GetCell(row, col).Attributes.HasFlag(CellAttributes.WideCharTrail))
                count++;
        }
        return toCol > fromCol ? count : -count;
    }

    // Lights the caret and restarts its blink cycle. A caret that is being moved or
    // typed at should stay solid rather than wink out mid-keystroke, which is how the
    // input box's own caret behaves.
    private void RestartCaretBlink()
    {
        _caretOn = true;
        _caretBlinkTimer?.Stop();
        _caretBlinkTimer?.Start();
    }

    // ── Click-to-move caret ──

    // The CLI owns the cursor, so it can only be moved by sending it arrow keys.
    // Counting how many to send means reading the CLI's own input layout off the
    // grid, and that reading can be wrong — the wrap point of a soft-wrapped line
    // may or may not hold a character. Rather than trust one estimate, the move
    // runs as a short convergence loop: send the estimate, look at where the
    // cursor actually landed, send the remainder. The correction after the first
    // hop is almost always a same-row count, which is exact.
    private const int MaxCaretMoveChars = 400;
    private const int MaxInputBlockRows = 12;
    private const int CaretMoveAttempts = 4;
    private const int CaretSettleMs = 70;    // time for the CLI to redraw after a batch

    // Runs one caret-driven edit at a time. These all talk to the CLI by sending keys
    // and waiting to see what it did, so a second one starting mid-flight would be
    // reading a grid the first is still changing.
    private async void RunCaretEdit(Func<Task> edit)
    {
        if (_caretMoveInProgress) return;
        _caretMoveInProgress = true;
        try
        {
            await edit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CaretEdit] {ex.Message}");
        }
        finally
        {
            _caretMoveInProgress = false;
        }
    }

    private void TryMoveCaretToClick(int screenRow, int targetCol)
    {
        BreakUndoRun();
        RunCaretEdit(() => MoveCaretToAsync(screenRow, targetCol));
    }

    // Walks the caret to a cell and reports how many characters it travelled
    // (positive to the right). Each batch is sized by an estimate that skips the
    // character a soft wrap swallows, so the estimate can only fall short, never
    // overshoot — no arrow is ever absorbed at an edge, and the batches therefore
    // add up to the true distance. That makes the return value exact, which is how
    // the selection edits below learn how long a selection really is.
    private async Task<int> MoveCaretToAsync(int screenRow, int targetCol)
    {
        if (_pty == null) return 0;
        if (_scrollOffset != 0) return 0;    // scrolled back: the cursor is not on screen
        if (screenRow < 0 || screenRow >= _buffer.Rows) return 0;
        // The alternate buffer is no signal either way here: the Claude Code CLI
        // draws its prompt on it. Finding the prompt row is what tells us the
        // target is on an editable line, and that check happens below.

        int blockTop, blockBottom, textLeft;
        if (!TryGetInputBlock(out blockTop, out blockBottom, out textLeft))
        {
            // No prompt row found. Same-row moves need no layout guesswork, so they
            // still work; anything crossing a row would be guessing and is dropped.
            if (screenRow != _buffer.CursorRow) return 0;
            blockTop = blockBottom = screenRow;
            textLeft = 0;
        }
        if (screenRow < blockTop || screenRow > blockBottom) return 0;

        targetCol = ClampToRowContent(screenRow, targetCol, textLeft);

        int moved = 0;
        int lastRow = -1, lastCol = -1;
        for (int attempt = 0; attempt < CaretMoveAttempts; attempt++)
        {
            int fromRow = _buffer.CursorRow;
            int fromCol = _buffer.CursorCol;
            if (fromRow == screenRow && fromCol == targetCol) break;

            // The previous batch moved nothing, so the caret is against an edge
            // the estimate does not know about. Stop rather than thrash.
            if (fromRow == lastRow && fromCol == lastCol) break;
            lastRow = fromRow;
            lastCol = fromCol;

            int delta = EstimateCaretDelta(fromRow, fromCol, screenRow, targetCol,
                                           blockTop, blockBottom, textLeft);
            if (delta == 0 || Math.Abs(delta) > MaxCaretMoveChars) break;

            string key = delta > 0 ? "\x1b[C" : "\x1b[D";
            var sb = new System.Text.StringBuilder(key.Length * Math.Abs(delta));
            for (int i = 0; i < Math.Abs(delta); i++) sb.Append(key);
            _pty.WriteInput(sb.ToString());
            moved += delta;

            await Task.Delay(CaretSettleMs);
            if (_pty == null) break;
        }
        return moved;
    }

    // ── Editing the selection ──

    // True when the whole selection sits on the CLI's editable input line. That is
    // the only place a delete can be honoured: a selection up in the output belongs
    // to the scrollback, and sending backspaces for it would eat the prompt instead
    // of the text the user highlighted.
    private bool TryGetEditableSelection(out int startRow, out int startCol,
                                         out int endRow, out int endCol)
    {
        startRow = startCol = endRow = endCol = 0;
        if (!_hasSelection || _pty == null || _scrollOffset != 0) return false;
        if (!TryGetInputBlock(out int blockTop, out int blockBottom, out int textLeft)) return false;

        GetOrderedSelection(out int sr, out int sc, out int er, out int ec);
        startRow = AbsoluteToScreenRow(sr);
        endRow = AbsoluteToScreenRow(er);
        // The gutter the block is indented by is layout, not text. It is left out of
        // what gets drawn and copied, so it has to be left out of what gets deleted
        // too - otherwise a drag that began on the prompt marker eats two characters
        // that were never highlighted.
        startCol = Math.Max(sc, textLeft);
        endCol = ec;
        if (startRow < blockTop || endRow > blockBottom || startRow > endRow) return false;
        return startRow < endRow || startCol <= endCol;
    }

    private async Task DeleteSelectionAsync()
    {
        if (!TryGetEditableSelection(out int sr, out int sc, out int er, out int ec)) return;
        ClearSelection();

        int count;
        if (sr == er)
        {
            // One row: the grid gives the character count exactly, no walk needed.
            count = CountCharsBetweenCols(sr, sc, ec + 1);
            await MoveCaretToAsync(er, ec + 1);
        }
        else
        {
            // Across a wrap the grid alone cannot say how many characters there are,
            // so walk the caret over the selection and let the walk count them. This
            // happens before anything is deleted, while the layout is still still.
            await MoveCaretToAsync(sr, sc);
            count = await MoveCaretToAsync(er, ec + 1);
        }

        if (count <= 0 || count > MaxCaretMoveChars) return;

        var sb = new System.Text.StringBuilder(count);
        for (int i = 0; i < count; i++) sb.Append('\x7f');
        _pty?.WriteInput(sb.ToString());
    }

    private async Task CutSelectionAsync()
    {
        var text = GetSelectedText();       // before DeleteSelectionAsync clears it
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && !string.IsNullOrEmpty(text))
            await clipboard.SetTextAsync(text);
        await DeleteSelectionAsync();
    }

    // Deletes the selection, then lets the caller put something in its place — the
    // paste, or the character that was just typed.
    private async Task ReplaceSelectionAsync(Func<Task> write)
    {
        await DeleteSelectionAsync();
        await Task.Delay(CaretSettleMs);    // let the deletion land before writing over it
        await write();
    }

    // ── Undo ──

    // What an edit was, for the purpose of grouping. A burst of typing, or a run of
    // backspaces, folds into a single undo step the way an editor's does; anything
    // else stands on its own.
    private enum UndoKind { None, Typing, Deleting, Structural }

    // The input line as it stood before an edit, and where the caret sat in it.
    private readonly record struct InputSnapshot(string Text, int CaretOffset);

    private const int MaxUndoDepth = 50;
    private const int UndoCoalesceMs = 1000;

    // Records the input line before an edit changes it, so it has to run before the
    // edit reaches the PTY, while the grid still shows the old text.
    private void PushUndo(UndoKind kind)
    {
        if (_isDocumentView || _caretMoveInProgress) return;

        long now = Environment.TickCount64;
        bool sameRun = _undoRunOpen
                       && kind == _lastUndoKind
                       && kind is UndoKind.Typing or UndoKind.Deleting
                       && now - _lastUndoTick < UndoCoalesceMs;
        _lastUndoKind = kind;
        _lastUndoTick = now;
        if (sameRun) return;

        _undoRunOpen = false;
        if (!TryReadInputBlock(out string text, out int caret)) return;

        // Nothing has changed since the last snapshot, so a second copy of it would
        // only spend a Ctrl+Z doing nothing.
        if (_undoStack.Count == 0 || _undoStack[^1].Text != text)
        {
            _undoStack.Add(new InputSnapshot(text, caret));
            if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
            // Editing after an undo abandons what was undone, the way an editor does.
            _redoStack.Clear();
        }
        _undoRunOpen = true;
    }

    // Ends the current run: the next edit gets an undo step of its own.
    private void BreakUndoRun()
    {
        _undoRunOpen = false;
        _lastUndoKind = UndoKind.None;
    }

    // Submitting or interrupting throws the line away, and every snapshot of it with it.
    private void ClearUndo()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        BreakUndoRun();
    }

    // Undo and redo are the same move in opposite directions: take the line back to
    // the snapshot on top of one stack, and leave where it was on top of the other.
    private Task UndoAsync() => ApplySnapshotAsync(_undoStack, _redoStack);
    private Task RedoAsync() => ApplySnapshotAsync(_redoStack, _undoStack);

    private async Task ApplySnapshotAsync(List<InputSnapshot> from, List<InputSnapshot> to)
    {
        if (from.Count == 0) return;
        // Read the line before touching it - this is what the other stack goes back to.
        if (!TryReadInputBlock(out string current, out int currentCaret)) return;

        var snap = from[^1];
        BreakUndoRun();
        ClearSelection();

        // The stacks only move once the line has actually come clean; a move that
        // could not empty it has changed nothing worth stepping back over.
        if (!await ClearInputLineAsync() || _pty == null) return;
        from.RemoveAt(from.Count - 1);

        if (snap.Text.Length > 0)
        {
            _pty.WriteInput(snap.Text);
            await Task.Delay(CaretSettleMs);
        }

        // Typing leaves the caret after the last character; walk it back to where the
        // snapshot had it.
        int tail = snap.Text.Length - snap.CaretOffset;
        if (tail > 0 && tail <= MaxCaretMoveChars)
        {
            var back = new System.Text.StringBuilder(tail * 3);
            for (int i = 0; i < tail; i++) back.Append("\x1b[D");
            _pty?.WriteInput(back.ToString());
        }

        to.Add(new InputSnapshot(current, currentCaret));
        if (to.Count > MaxUndoDepth) to.RemoveAt(0);
    }

    // Empties the CLI's input line: walk the caret to the end, walk it back to the
    // start to learn the exact length, then delete forward that many times. More than
    // one pass, because a pass can only count what the grid shows and the grid cannot
    // show a character a soft wrap swallowed - whatever the first pass leaves behind,
    // the next one can see. Returns false if the line would not come clean, which is
    // the signal not to type over what is left of it.
    private async Task<bool> ClearInputLineAsync()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            if (_pty == null || _scrollOffset != 0) return false;
            if (!TryGetInputBlock(out _, out int bottom, out int textLeft)) return false;

            await MoveCaretToAsync(bottom, RowContentEnd(bottom, textLeft));
            if (_pty == null) return false;
            if (!TryGetInputBlock(out int top, out _, out textLeft)) return false;

            int count = -await MoveCaretToAsync(top, textLeft);
            if (count < 0 || count > MaxCaretMoveChars) return false;
            if (count == 0) break;

            var del = new System.Text.StringBuilder(count * 4);
            for (int i = 0; i < count; i++) del.Append("\x1b[3~");
            _pty?.WriteInput(del.ToString());
            await Task.Delay(CaretSettleMs);
        }
        return TryReadInputBlock(out string left, out _) && left.Length == 0;
    }

    // Reads the CLI's input line off the grid: the text it holds, and how far into
    // that text the caret sits. The spaces a row ends on are the block's own padding
    // rather than text, so they come off; a row that ran out of room joins the next.
    private bool TryReadInputBlock(out string text, out int caretOffset)
    {
        text = "";
        caretOffset = 0;
        if (_pty == null || _scrollOffset != 0) return false;
        if (!TryGetInputBlock(out int top, out int bottom, out int textLeft)) return false;

        var sb = new System.Text.StringBuilder();
        int rowEnd = _buffer.Cols - textLeft - 1;
        int caret = -1;
        for (int row = top; row <= bottom; row++)
        {
            int rowStart = sb.Length;
            for (int col = textLeft; col <= rowEnd && col < _buffer.Cols; col++)
            {
                if (caret < 0 && row == _buffer.CursorRow && col == _buffer.CursorCol)
                    caret = sb.Length;
                var cell = GetCellAt(row, col);
                if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail)) continue;
                sb.Append(cell.Text);
            }
            if (caret < 0 && row == _buffer.CursorRow) caret = sb.Length;

            int len = sb.Length;
            while (len > rowStart && sb[len - 1] == ' ') len--;
            sb.Length = len;
            if (caret > len) caret = len;

            if (row < bottom)
            {
                switch (RowJoinKind(ScreenRowToAbsolute(row), textLeft))
                {
                    case JoinKind.Break: sb.Append('\n'); break;
                    case JoinKind.JoinWithSpace: sb.Append(' '); break;
                }
            }
        }

        text = sb.ToString();
        caretOffset = Math.Clamp(caret < 0 ? text.Length : caret, 0, text.Length);
        return true;
    }

    // Locates the CLI's input block: the prompt row, which carries the marker in
    // the first column, plus the wrapped rows under it, which the CLI indents to
    // line up after that marker. Returns false when the cursor is not sitting in
    // such a block, which is the signal to fall back to same-row moves only.
    private bool TryGetInputBlock(out int topRow, out int bottomRow, out int textLeft)
    {
        topRow = bottomRow = -1;
        textLeft = 0;

        int cursorRow = _buffer.CursorRow;
        int promptRow = -1;
        for (int r = cursorRow; r >= 0 && cursorRow - r < MaxInputBlockRows; r--)
        {
            int first = FirstNonBlankCol(r);
            if (first < 0) return false;                 // blank row: above the input
            int c = GetCellAt(r, first).Character;
            if (first == 0 && (c == '>' || c == '❯'))
            {
                promptRow = r;
                textLeft = first + 2;                    // past the marker and its space
                break;
            }
            if (first < 2) return false;                 // some other line: not the input
        }
        if (promptRow < 0) return false;

        bottomRow = promptRow;
        for (int r = promptRow + 1; r < _buffer.Rows && r - promptRow < MaxInputBlockRows; r++)
        {
            if (FirstNonBlankCol(r) < textLeft) break;   // dedents: past the input
            bottomRow = r;
        }
        topRow = promptRow;
        return cursorRow >= topRow && cursorRow <= bottomRow;
    }

    private int EstimateCaretDelta(int fromRow, int fromCol, int toRow, int toCol,
                                   int blockTop, int blockBottom, int textLeft)
    {
        if (fromRow == toRow) return CountCharsBetweenCols(fromRow, fromCol, toCol);

        bool forward = toRow > fromRow;
        int lo = forward ? fromRow : toRow;
        int hi = forward ? toRow : fromRow;
        int loCol = forward ? fromCol : toCol;
        int hiCol = forward ? toCol : fromCol;
        if (lo < blockTop || hi > blockBottom) return 0;

        int count = CountCharsBetweenCols(lo, loCol, RowContentEnd(lo, textLeft));
        for (int r = lo + 1; r < hi; r++)
            count += CountCharsBetweenCols(r, textLeft, RowContentEnd(r, textLeft));
        count += CountCharsBetweenCols(hi, textLeft, hiCol);
        return forward ? count : -count;
    }

    // One column past the row's last character — the caret position at end of line.
    private int RowContentEnd(int row, int textLeft)
    {
        return Math.Max(textLeft, LastNonBlankCol(row) + 1);
    }

    private int ClampToRowContent(int row, int col, int textLeft)
    {
        return Math.Clamp(col, textLeft, RowContentEnd(row, textLeft));
    }

    private int FirstNonBlankCol(int row)
    {
        for (int col = 0; col < _buffer.Cols; col++)
            if (GetCellAt(row, col).Character > ' ') return col;
        return -1;
    }

    private int LastNonBlankCol(int row)
    {
        for (int col = _buffer.Cols - 1; col >= 0; col--)
            if (GetCellAt(row, col).Character > ' ') return col;
        return -1;
    }

    // How wide the terminal was when this row was written. Scrollback holds each line in
    // an array cut to the width of the day, and a resize since then leaves the old lines
    // ending short of - or running past - today's right edge. Reading them against the
    // current width would call a line that filled its own last column merely short, and
    // that is exactly the line that ran out of room and carried on below.
    private int RowWidth(int absRow)
    {
        var sb = _buffer.Scrollback;
        if (absRow >= 0 && absRow < sb.Count)
        {
            int len = sb[absRow].Length;
            if (len > 0) return len;
        }
        return _buffer.Cols;
    }

    // The first and last column of a row that hold something - a glyph, or the trailing
    // half of a double-width one, whose cell reads '\0' but is occupied all the same.
    // -1 when there is nothing between the two bounds.
    private int FirstUsedCol(int absRow, int from, int to)
    {
        int width = RowWidth(absRow);
        for (int col = Math.Max(0, from); col <= to && col < width; col++)
        {
            var cell = GetCellAtAbs(absRow, col);
            if (cell.Character > ' ' || cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                return col;
        }
        return -1;
    }

    private int LastUsedCol(int absRow, int from, int to)
    {
        for (int col = Math.Min(to, RowWidth(absRow) - 1); col >= from && col >= 0; col--)
        {
            var cell = GetCellAtAbs(absRow, col);
            if (cell.Character > ' ' || cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                return col;
        }
        return -1;
    }

    // Box drawing and the block elements beside it are how the CLI frames its panels.
    // A row that ends in one, or a row that opens with one, is a border rather than
    // text that ran out of room; joining either to its neighbour would flatten a frame
    // into a single line.
    private static bool IsFrameChar(int c) => c >= 0x2500 && c <= 0x259F;

    // Is the boundary between this row and the next one the edge of a drawn frame?
    // Worth asking on its own because a border runs the full width of the terminal, and
    // so leaves behind every sign of a line that overflowed - the wrap flag included.
    private bool IsFramedBoundary(int absRow, int margin)
    {
        int used = LastUsedCol(absRow, margin, RowWidth(absRow) - margin - 1);
        if (used >= 0 && IsFrameChar(GetCellAtAbs(absRow, used).Character)) return true;

        int nextFirst = FirstUsedCol(absRow + 1, margin, RowWidth(absRow + 1) - margin - 1);
        return nextFirst >= 0 && IsFrameChar(GetCellAtAbs(absRow + 1, nextFirst).Character);
    }

    // What holds the end of one row to the start of the next.
    private enum JoinKind
    {
        Break,          // the line ended here; the rows are separate lines
        Join,           // the wrap fell inside a word; the halves belong together as they are
        JoinWithSpace,  // the wrap fell on a space, which has to be put back
    }

    // The columns the row's opening word needs before it can share a row with anything.
    // A word runs to the next space, so all of it has to fit; text written in
    // double-width characters breaks between characters instead, so for that only the
    // first character counts.
    private int NextWordCols(int absRow, int from, int to, out bool isWide)
    {
        isWide = from < to &&
            GetCellAtAbs(absRow, from + 1).Attributes.HasFlag(CellAttributes.WideCharTrail);
        if (isWide) return 2;

        int cols = 0;
        for (int col = from; col <= to; col++)
        {
            if (GetCellAtAbs(absRow, col).Character <= ' ') break;   // a space, or a trail cell
            if (col < to &&
                GetCellAtAbs(absRow, col + 1).Attributes.HasFlag(CellAttributes.WideCharTrail))
                break;                                               // a wide character ends it
            cols++;
        }
        return Math.Max(1, cols);
    }

    // Did this row run out of room, or did its line end here? Nothing in the buffer says
    // so: the CLI lays its own output out to the terminal's width and closes every row
    // with a line break of its own, so a row it wrapped is indistinguishable from a row
    // whose line simply stopped there. What tells them apart is why the break fell where
    // it did. A line wraps when the next word will not fit in what is left of the row, so
    // that is the question to ask - and a row that had the room to take that word kept
    // nothing back, which means its line ended of its own accord. Asking instead whether
    // the row reached the last column would only ever catch the one word too long to
    // break at all: the CLI breaks between words, and that leaves the right edge ragged.
    //
    // A box padded by `margin` columns on the right just as it is indented by that many
    // on the left has its last usable column at the row's own width, less the margin -
    // each row measured at the width it was written at, which for a line that has since
    // scrolled into the scrollback is not necessarily today's.
    private JoinKind RowJoinKind(int absRow, int margin)
    {
        int boxRight = RowWidth(absRow) - margin - 1;

        int used = LastUsedCol(absRow, margin, boxRight);
        if (used < 0) return JoinKind.Break;           // a blank row carries nothing on

        // The next row's first character is not always in its first column - the CLI
        // indents what it wraps to line up under the row above.
        int nextRight = RowWidth(absRow + 1) - margin - 1;
        int nextFirst = FirstUsedCol(absRow + 1, margin, nextRight);
        if (nextFirst < 0) return JoinKind.Break;      // nothing follows to carry on

        if (IsFramedBoundary(absRow, margin)) return JoinKind.Break;

        int wordCols = NextWordCols(absRow + 1, nextFirst, nextRight, out bool wordIsWide);
        int free = boxRight - used;                    // columns still going spare here
        if (free >= wordCols + 1) return JoinKind.Break;   // it would have fitted; the line ended

        // A break that left columns free fell on the space between two words, and that
        // space is part of the text. One that filled the row to its edge - or stopped a
        // column short only because a double-width character will not straddle it - cut
        // through a word, whose halves go back together with nothing between them.
        return (free > 0 && !wordIsWide) ? JoinKind.JoinWithSpace : JoinKind.Join;
    }

    // ── Expanded Input Panel ──

    private void BuildExpandedPanel()
    {
        // Drag handle bar at top
        _dragHandle = new Border
        {
            Height = 4,
            Background = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };
        _dragHandle.PointerPressed += OnDragHandlePressed;
        _dragHandle.PointerMoved += OnDragHandleMoved;
        _dragHandle.PointerReleased += OnDragHandleReleased;

        // Multi-line text box
        _expandedTextBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = new SolidColorBrush(Color.FromRgb(34, 34, 36)),
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6),
            FontSize = _fontSize,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            PlaceholderText = "Multi-line input (Enter=newline, Ctrl+Enter=send)",
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _expandedTextBox.AddHandler(KeyDownEvent, OnExpandedKeyDown, RoutingStrategies.Tunnel);
        _expandedTextBox.TextChanged += (_, _) => OnExpandedTextChanged();

        // Collapse button (▼)
        _collapseButton = new Button
        {
            Content = "\u25BC", FontSize = 10,
            Padding = new Thickness(8, 4),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 0),
            Focusable = false,
        };
        ToolTip.SetTip(_collapseButton, "Collapse input (Escape)");
        _collapseButton.Click += (_, _) => CollapseInputPanel();

        // Send button (▶)
        _sendButton = new Button
        {
            Content = "\u25B6", FontSize = 10,
            Padding = new Thickness(8, 4),
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
        };
        ToolTip.SetTip(_sendButton, "Send message (Ctrl+Enter)");
        _sendButton.Click += (_, _) => SendExpandedText();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 8, 4),
        };
        // Attach (+): the CLI takes files as paths on the prompt, so picking one only has to
        // write its path into the box - the same shape a pasted screenshot arrives in.
        _attachButton = new Button
        {
            Content = "+", FontSize = 13,
            Padding = new Thickness(9, 1),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(_attachButton, Services.Loc.Get("AttachFiles", "Attach files"));
        _attachButton.Click += (_, _) => _ = AttachFilesAsync();
        buttonPanel.Children.Add(_attachButton);

        // Stop leads the row so Send keeps the far-right spot it has always had.
        _expandedStopButton = NewStopButton(new Thickness(8, 4));
        _expandedStopButton.Margin = new Thickness(0, 0, 4, 0);
        buttonPanel.Children.Add(_expandedStopButton);
        buttonPanel.Children.Add(_collapseButton);
        buttonPanel.Children.Add(_sendButton);

        var dock = new DockPanel();
        DockPanel.SetDock(_dragHandle, Dock.Top);
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        dock.Children.Add(_dragHandle);
        dock.Children.Add(buttonPanel);
        dock.Children.Add(_expandedTextBox);

        _expandedPanel = new Border
        {
            Child = dock,
            Background = new SolidColorBrush(Color.FromRgb(34, 34, 36)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 56, 58)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            IsVisible = false,
        };
    }

    private void ToggleExpandedMode()
    {
        if (_isExpanded)
            CollapseInputPanel();
        else
            ExpandInputPanel();
    }

    private void ExpandInputPanel()
    {
        _isExpanded = true;
        _expandedHeight = Math.Max(80, Bounds.Height * 0.3);
        _expandedPanel.IsVisible = true;

        // Transfer text from IME input to expanded input (useful in document view mode)
        if (_isDocumentView && !string.IsNullOrEmpty(_inputTextBox.Text))
        {
            _expandedTextBox.Text = _inputTextBox.Text;
            _expandedTextBox.CaretIndex = _expandedTextBox.Text.Length;
            _inputTextBox.Text = "";
        }

        _inputTextBox.IsVisible = false;
        _expandButton.IsVisible = false;
        _expandedTextBox.Focus();
        RecalcTerminalSize();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void CollapseInputPanel()
    {
        // Move text to normal input (send to PTY without submitting)
        var text = _expandedTextBox.Text;
        if (!string.IsNullOrEmpty(text))
        {
            // Normalize to single \n, then remove consecutive blank lines
            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
            while (normalized.Contains("\n\n"))
                normalized = normalized.Replace("\n\n", "\n");
            _pty?.WriteInput(normalized);
            _expandedTextBox.Text = "";
        }

        _isExpanded = false;
        _expandedPanel.IsVisible = false;
        _inputTextBox.IsVisible = true;
        _expandButton.IsVisible = true;
        _inputTextBox.Focus();
        RecalcTerminalSize();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>The typed text with the attached images' references after it; empties the strip.</summary>
    private string JoinWithAttachments(string text)
    {
        var refs = _attachStrip.TakeReferences();
        if (refs.Length == 0) return text;
        return string.IsNullOrWhiteSpace(text) ? refs : text.TrimEnd() + " " + refs;
    }

    /// <summary>
    /// Types <paramref name="text"/> into the CLI and submits it. When image references
    /// are in it, Enter waits a moment: arriving in the same burst as the paths, the CLI
    /// takes it as part of a paste and turns the paths into attachments without sending.
    /// </summary>
    private async void WriteAndSubmit(string text, bool withImages)
    {
        // A burst is only a guess the CLI makes; one it misses sends the prompt line by line, so a
        // multi-line prompt goes as a marked paste whenever the CLI takes one
        if (_buffer.BracketedPasteMode && (text.Contains('\n') || text.Contains('\r')))
            _pty?.WriteInput("\x1b[200~" + text + "\x1b[201~");
        else
            _pty?.WriteInput(text);
        // Text arriving in one burst reads as a paste to the CLI, and a CR inside a paste is a
        // newline rather than a submit. Attached image paths also need time to be resolved.
        await Task.Delay(withImages ? AttachmentSubmitDelayMs : SubmitDelayMs);
        _pty?.WriteInput("\r");
    }

    /// <summary>
    /// Keys the chat view's answers into the CLI's AskUserQuestion selector. Measured against
    /// Claude Code 2.1.283, where each question opens with the caret on option 1:
    /// - a digit picks that option; on a single-select question it also moves to the next one
    ///   (with only one single-select question it submits outright), on multi-select it toggles
    /// - the free-text row sits after the options: arrow down onto it and type, and it is ticked
    /// - multi-select ends with a Next/Submit row under the free-text row; Enter there moves on
    /// - Right arrow leaves a question unanswered
    /// - after the last question comes a review page whose default is "Submit answers"
    /// </summary>
    private async void AnswerAskUserQuestion(IReadOnlyList<Services.AskUserQuestionItem> questions, IReadOnlyList<Controls.AskReply> replies)
    {
        const string down = "\x1b[B", right = "\x1b[C";
        for (int i = 0; i < questions.Count && i < replies.Count; i++)
        {
            var q = questions[i];
            var r = replies[i];
            int n = q.Options.Count;
            var other = r.OtherText?.Replace("\r", " ").Replace("\n", " ");
            // When any question carries option previews, the CLI lays every page out without
            // the "Type something" and Next rows: arrowing down past the options lands on
            // "Chat about this", and Enter there abandons the whole selector. So the free
            // text only goes in where its row is on screen, and pages are left with Right.
            bool freeTextRow = GetScreenText(0).Contains("Type something", StringComparison.Ordinal);
            var keys = new List<string>();
            if (r.Skipped)
            {
                keys.Add(right);
            }
            else if (!q.MultiSelect)
            {
                if ((other == null || !freeTextRow) && r.Selected.Count > 0)
                {
                    keys.Add((r.Selected[0] + 1).ToString());
                    // On a preview page a digit only moves the caret; Enter picks the option
                    if (!freeTextRow) keys.Add("\r");
                }
                else if (other != null && freeTextRow)
                {
                    for (int k = 0; k < n; k++) keys.Add(down);
                    keys.Add(other);
                    keys.Add("\r");
                }
                else
                    keys.Add(right);
            }
            else
            {
                foreach (var idx in r.Selected) keys.Add((idx + 1).ToString());
                if (other != null && freeTextRow)
                {
                    for (int k = 0; k < n; k++) keys.Add(down);
                    keys.Add(other);
                }
                keys.Add(right);
            }

            var before = GetScreenText(0);
            foreach (var key in keys)
            {
                _pty?.WriteInput(key);
                // Paced as measured: keys arriving in one burst read to the CLI as a paste
                await Task.Delay(AskKeyDelayMs);
            }
            // Let the page turn before reading the next one
            for (int w = 0; w < 10 && GetScreenText(0) == before; w++) await Task.Delay(100);
            await Task.Delay(150);
            if (!IsAskSelectorOnScreen()) return;
        }

        await FinishAskSubmitAsync();
    }

    /// <summary>
    /// Drives the selector the rest of the way to submitted by reading the screen rather than
    /// trusting the key count: a key that lands while the CLI is still turning a page is lost,
    /// which left it parked on a question or on the review page with the caret off "Submit
    /// answers". Stops as soon as the selector is gone.
    /// </summary>
    private async Task FinishAskSubmitAsync()
    {
        const string up = "\x1b[A", right = "\x1b[C";
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(attempt == 0 ? 400 : 300);
            if (!IsAskSelectorOnScreen() && !GetScreenText(0).Contains("Submit answers", StringComparison.Ordinal))
                return;

            var lines = GetScreenText(0).Split('\n');
            var submitLine = lines.FirstOrDefault(l => l.Contains("Submit answers", StringComparison.Ordinal));
            if (submitLine == null)
                _pty?.WriteInput(right);          // still on a question: move on to the review page
            else if (System.Text.RegularExpressions.Regex.IsMatch(submitLine, @"^[\s│|]*[❯>]"))
                _pty?.WriteInput("\r");
            else
                _pty?.WriteInput(up);             // caret sits on Cancel below it
        }
    }

    /// <summary>
    /// The AskUserQuestion selector is up: every page of it lists a "Type something" (or "Chat about this") row and
    /// ends with an "Esc to cancel" hint. Without it, the keys would land in the prompt.
    /// </summary>
    private bool IsAskSelectorOnScreen()
    {
        // A question with option previews has no "Type something" row; it shows "Chat about
        // this" under the options instead
        var screen = GetScreenText(0);
        // The closing review page has neither row nor the Esc hint
        if (screen.Contains("Review your answers", StringComparison.Ordinal)
            && screen.Contains("Submit answers", StringComparison.Ordinal))
            return true;
        return (screen.Contains("Type something", StringComparison.Ordinal)
                || screen.Contains("Chat about this", StringComparison.Ordinal))
            && screen.Contains("Esc to cancel", StringComparison.Ordinal);
    }

    private const int AskKeyDelayMs = 120;
    private const int AttachmentSubmitDelayMs = 300;
    private const int SubmitDelayMs = 150;

    private void SendExpandedText()
    {
        bool withImages = _attachStrip.HasItems;
        var text = JoinWithAttachments(_expandedTextBox.Text ?? "");
        if (!string.IsNullOrEmpty(text))
        {
            if (_isDocumentView) _docViewPanel?.ShowPendingPrompt(_expandedTextBox.Text ?? "");
            // Record input position for prompt navigation
            int submitRow = _inputStartAbsRow;
            if (_userInputRows.Count == 0 || Math.Abs(_userInputRows[^1] - submitRow) > 1)
                _userInputRows.Add(submitRow);

            // Capture first input as tab title
            if (!_firstInputCaptured)
            {
                _firstInputCaptured = true;
                FirstUserInput = text.Replace("\r", " ").Replace("\n", " ").Trim();
                var summary = FirstUserInput;
                if (summary.Length > 30) summary = summary[..30] + "...";
                if (!string.IsNullOrWhiteSpace(summary))
                    TitleChanged?.Invoke(summary);
            }
            PromptSubmitted?.Invoke(text);
            WriteAndSubmit(text, withImages);
            _expandedTextBox.Text = "";
        }
        _expandedTextBox.Focus();
    }

    private void OnExpandedKeyDown(object? sender, KeyEventArgs e)
    {
        // The completion list owns the keys it needs while it is up, so Enter picks a file
        // rather than breaking the line and Escape closes the list rather than the panel.
        if (_completionPopup is { IsOpen: true } && HandleCompletionKey(e))
        {
            e.Handled = true;
            return;
        }

        // Ctrl+Enter: send
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SendExpandedText();
            e.Handled = true;
            return;
        }
        // Escape: collapse
        if (e.Key == Key.Escape)
        {
            CollapseInputPanel();
            e.Handled = true;
            return;
        }
        // Enter without modifiers: just newline (AcceptsReturn handles it)
    }

    // ── @ completion ───────────────────────────────────────────────────

    private Popup? _completionPopup;
    private ListBox? _completionList;

    /// <summary>Where the '@' that opened the list sits in the text box.</summary>
    private int _completionAnchor = -1;

    /// <summary>Guards the text box's own change event while the completion rewrites it.</summary>
    private bool _completionInserting;

    /// <summary>
    /// Watches what is being typed and offers the project's files after an '@'. The trigger is
    /// the same one the CLI uses, so a completed reference is exactly what would have been
    /// typed by hand.
    /// </summary>
    private void OnExpandedTextChanged() => OnCompletionTextChanged(_expandedTextBox, slashToo: false);

    /// <summary>The text box the open list completes into: the expanded editor or the chat composer.</summary>
    private TextBox? _completionTarget;

    /// <summary>Whether the open list is commands (a leading '/') rather than files.</summary>
    private bool _completionSlash;

    /// <summary>Supplies the slash commands for a project folder; set by the shell, which knows the provider.</summary>
    public Func<string?, IReadOnlyList<Services.SlashCommand>>? SlashCommandSource { get; set; }

    /// <summary>One row of the list: what goes into the box, and what the row shows.</summary>
    private sealed record CompletionItem(string Insert, string Label)
    {
        public override string ToString() => Label;
    }

    private void OnCompletionTextChanged(TextBox box, bool slashToo)
    {
        if (_completionInserting) return;

        var text = box.Text ?? "";
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);

        // A '/' that opens the box and has not been typed past yet names a command
        if (slashToo && text.StartsWith('/') && !text[..caret].Any(char.IsWhiteSpace) && caret > 0)
        {
            _completionTarget = box;
            _completionSlash = true;
            _completionAnchor = 0;
            ShowSlashCompletion(text[1..caret]);
            return;
        }

        int at = FindCompletionAnchor(text, caret);
        if (at < 0)
        {
            CloseCompletion();
            return;
        }

        _completionTarget = box;
        _completionSlash = false;
        _completionAnchor = at;
        _ = ShowCompletionAsync(text.Substring(at + 1, caret - at - 1));
    }

    private void ShowSlashCompletion(string query)
    {
        var commands = SlashCommandSource?.Invoke(_workingDirectory) ?? Array.Empty<Services.SlashCommand>();
        bool japanese = Services.Loc.Language == "日本語";
        // Names that start with the query first, then ones that merely contain it
        var matches = commands
            .Select(c => (c, name: c.Name.TrimStart('/')))
            .Where(x => x.name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(x => x.name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(x =>
            {
                var desc = japanese && !string.IsNullOrEmpty(x.c.DescriptionJa) ? x.c.DescriptionJa : x.c.Description;
                return new CompletionItem("/" + x.name, string.IsNullOrEmpty(desc) ? "/" + x.name : $"/{x.name}  —  {desc}");
            })
            .ToList();
        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }
        OpenCompletion(matches);
    }

    private void OpenCompletion(List<CompletionItem> items)
    {
        EnsureCompletionPopup();
        _completionPopup!.PlacementTarget = _completionTarget;
        _completionList!.ItemsSource = items;
        _completionList.SelectedIndex = 0;
        _completionPopup.IsOpen = true;
    }

    /// <summary>
    /// The '@' the caret is currently attached to, or -1. It has to start a word and there can
    /// be no whitespace between it and the caret - an '@' in an email address or one the user
    /// has already typed past is not an invitation to complete anything.
    /// </summary>
    private static int FindCompletionAnchor(string text, int caret)
    {
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '@')
                return i == 0 || char.IsWhiteSpace(text[i - 1]) ? i : -1;
            if (char.IsWhiteSpace(c)) return -1;
        }
        return -1;
    }

    private async Task ShowCompletionAsync(string query)
    {
        var files = await Services.ProjectFileIndex.ListAsync(_workingDirectory);
        if (files.Count == 0)
        {
            CloseCompletion();
            return;
        }

        // The caret has moved on while the walk was running - whatever it is doing now, this
        // answer is no longer about it.
        if (_completionAnchor < 0) return;

        var matches = Services.ProjectFileIndex.Rank(files, query, 12);
        if (matches.Count == 0)
        {
            CloseCompletion();
            return;
        }

        OpenCompletion(matches.Select(p => new CompletionItem(p, p)).ToList());
    }

    private void EnsureCompletionPopup()
    {
        if (_completionPopup != null) return;

        _completionList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 240,
            Focusable = false,
        };
        _completionList.DoubleTapped += (_, _) => AcceptCompletion();

        _completionPopup = new Popup
        {
            PlacementTarget = _expandedTextBox,
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = new SolidColorBrush(_isDark ? Color.FromRgb(40, 40, 42) : Color.FromRgb(250, 250, 252)),
                BorderBrush = new SolidColorBrush(_isDark ? Color.FromRgb(70, 70, 74) : Color.FromRgb(200, 200, 205)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                MinWidth = 320,
                Child = _completionList,
            },
        };
        _completionPopup.Closed += (_, _) => _completionAnchor = -1;

        VisualChildren.Add(_completionPopup);
        LogicalChildren.Add(_completionPopup);
    }

    /// <summary>Returns true when the key belonged to the completion list.</summary>
    private bool HandleCompletionKey(KeyEventArgs e)
    {
        var list = _completionList;
        if (list == null || list.ItemCount == 0) return false;

        switch (e.Key)
        {
            case Key.Down:
                list.SelectedIndex = (list.SelectedIndex + 1) % list.ItemCount;
                return true;
            case Key.Up:
                list.SelectedIndex = (list.SelectedIndex - 1 + list.ItemCount) % list.ItemCount;
                return true;
            case Key.Enter:
            case Key.Tab:
                AcceptCompletion();
                return true;
            case Key.Escape:
                CloseCompletion();
                return true;
            default:
                return false;
        }
    }

    private void AcceptCompletion()
    {
        var box = _completionTarget;
        if (_completionList?.SelectedItem is not CompletionItem item || _completionAnchor < 0 || box == null)
        {
            CloseCompletion();
            return;
        }

        var text = box.Text ?? "";
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        int start = _completionAnchor;
        if (start >= text.Length || caret < start) { CloseCompletion(); return; }

        // A path with a space in it has to survive the CLI's own argument splitting.
        var path = item.Insert;
        var inserted = _completionSlash ? path + " "
            : "@" + (path.Contains(' ') ? "\"" + path + "\"" : path) + " ";

        _completionInserting = true;
        try
        {
            box.Text = text[..start] + inserted + text[caret..];
            box.CaretIndex = start + inserted.Length;
        }
        finally
        {
            _completionInserting = false;
        }

        CloseCompletion();
    }

    private void CloseCompletion()
    {
        _completionAnchor = -1;
        if (_completionPopup != null) _completionPopup.IsOpen = false;
    }

    /// <summary>
    /// Picks files to hand to the AI. The CLI reads them off the prompt as paths, which is the
    /// same thing a pasted screenshot ends up as, so this only has to put the path in the box.
    /// </summary>
    private async Task AttachFilesAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Services.Loc.Get("AttachFiles", "Attach files"),
            AllowMultiple = true,
        };

        if (!string.IsNullOrEmpty(_workingDirectory) && Directory.Exists(_workingDirectory))
        {
            try { options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(_workingDirectory); }
            catch { /* an unreachable folder just means the picker opens wherever it likes */ }
        }

        var picked = await storage.OpenFilePickerAsync(options);
        if (picked.Count == 0) return;

        // The chat composer: images become thumbnails like a paste, anything else an @ reference
        if (_isDocumentView && !_isExpanded)
        {
            var refs = new List<string>();
            foreach (var file in picked)
            {
                var path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) continue;
                if (Controls.ImageAttachmentStrip.IsImageFile(path))
                    _attachStrip.Add(path, FileReference(path));
                else
                    refs.Add(FileReference(path));
            }
            if (refs.Count > 0)
            {
                var text = _inputTextBox.Text ?? "";
                int caret = Math.Clamp(_inputTextBox.CaretIndex, 0, text.Length);
                var snippet = (caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "") + string.Join(" ", refs) + " ";
                _completionInserting = true;
                try
                {
                    _inputTextBox.Text = text[..caret] + snippet + text[caret..];
                    _inputTextBox.CaretIndex = caret + snippet.Length;
                }
                finally { _completionInserting = false; }
            }
            UpdateChatSendState();
            InvalidateMeasure();
            _inputTextBox.Focus();
            return;
        }

        var parts = new List<string>();
        foreach (var file in picked)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;

            path = RelativeToWorkingDirectory(path);
            parts.Add(path.Contains(' ') ? "\"" + path + "\"" : path);
        }
        if (parts.Count == 0) return;

        InsertIntoExpandedInput(string.Join(" ", parts) + " ");
    }

    /// <summary>
    /// Names a file the way the session would: relative when it is inside the folder the CLI is
    /// running in, absolute when it is not. The session's own files then read the same whether
    /// they were picked or completed with an @.
    /// </summary>
    private string RelativeToWorkingDirectory(string path)
    {
        if (string.IsNullOrEmpty(_workingDirectory)) return path;

        try
        {
            var root = Path.GetFullPath(_workingDirectory);
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;

            var relative = Path.GetRelativePath(root, full);
            return relative.StartsWith("..", StringComparison.Ordinal)
                ? path
                : relative.Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private void InsertIntoExpandedInput(string snippet)
    {
        var text = _expandedTextBox.Text ?? "";
        int caret = Math.Clamp(_expandedTextBox.CaretIndex, 0, text.Length);

        _completionInserting = true;
        try
        {
            _expandedTextBox.Text = text[..caret] + snippet + text[caret..];
            _expandedTextBox.CaretIndex = caret + snippet.Length;
        }
        finally
        {
            _completionInserting = false;
        }

        _expandedTextBox.Focus();
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(_dragHandle).Properties.IsLeftButtonPressed)
        {
            _isDragResizing = true;
            _dragResizeStartY = e.GetPosition(this).Y;
            _dragResizeStartHeight = _expandedHeight;
            e.Pointer.Capture(_dragHandle);
            e.Handled = true;
        }
    }

    private void OnDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragResizing) return;
        double currentY = e.GetPosition(this).Y;
        double delta = _dragResizeStartY - currentY;
        double newHeight = Math.Clamp(_dragResizeStartHeight + delta, 80, Bounds.Height * 0.7);
        _expandedHeight = newHeight;
        RecalcTerminalSize();
        InvalidateMeasure();
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragResizing)
        {
            _isDragResizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // ── Search Bar ──

    private void BuildSearchBar()
    {
        _searchTextBox = new TextBox
        {
            PlaceholderText = "Search...",
            FontSize = 12,
            MinWidth = 180,
            Padding = new Thickness(6, 3),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 225)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        _searchTextBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchTextBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) OnSearchTextChanged();
        };

        _searchCountLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 50,
        };

        var prevBtn = new Button
        {
            Content = "\u25B2", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        prevBtn.Click += (_, _) => SearchNavigate(-1);

        var nextBtn = new Button
        {
            Content = "\u25BC", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        nextBtn.Click += (_, _) => SearchNavigate(1);

        var closeBtn = new Button
        {
            Content = "\u00D7", FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeBtn.Click += (_, _) => HideSearchBar();

        _searchRegexToggle = new ToggleButton
        {
            Content = ".*", FontSize = 10,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_searchRegexToggle, "Regex");
        _searchRegexToggle.IsCheckedChanged += (_, _) => { _searchRegex = _searchRegexToggle.IsChecked == true; UpdateSearchMatches(); };

        _searchCaseToggle = new ToggleButton
        {
            Content = "Aa", FontSize = 10,
            Padding = new Thickness(4, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 185)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(_searchCaseToggle, "Match Case");
        _searchCaseToggle.IsCheckedChanged += (_, _) => { _searchCaseSensitive = _searchCaseToggle.IsChecked == true; UpdateSearchMatches(); };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        panel.Children.Add(_searchTextBox);
        panel.Children.Add(_searchRegexToggle);
        panel.Children.Add(_searchCaseToggle);
        panel.Children.Add(_searchCountLabel);
        panel.Children.Add(prevBtn);
        panel.Children.Add(nextBtn);
        panel.Children.Add(closeBtn);

        _searchBar = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };

        VisualChildren.Add(_searchBar);
        LogicalChildren.Add(_searchBar);
    }

    public void ShowSearchBar()
    {
        if (_searchBar == null) return;
        _searchVisible = true;
        _searchBar.IsVisible = true;
        PrepareHistorySearch();
        if (!string.IsNullOrEmpty(_searchTerm)) UpdateSearchMatches();
        _searchTextBox?.Focus();
        _searchTextBox?.SelectAll();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void HideSearchBar()
    {
        if (_searchBar == null) return;
        _searchVisible = false;
        _searchBar.IsVisible = false;
        _searchMatches.Clear();
        _searchCurrentIndex = -1;
        _searchTerm = "";
        EndHistorySearch();
        _inputTextBox.Focus();
        InvalidateVisual();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideSearchBar(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { SearchNavigate(-1); e.Handled = true; }
        else if (e.Key == Key.Enter) { SearchNavigate(1); e.Handled = true; }
    }

    private void OnSearchTextChanged()
    {
        var term = _searchTextBox?.Text ?? "";
        if (term == _searchTerm) return;
        _searchTerm = term;
        UpdateSearchMatches();
    }

    private void UpdateSearchMatches()
    {
        _searchMatches.Clear();
        _searchCurrentIndex = -1;

        _histMatches.Clear();
        if (string.IsNullOrEmpty(_searchTerm))
        {
            _historyShown = false;
            _searchCountLabel!.Text = "";
            InvalidateVisual();
            return;
        }

        var comparison = _searchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        System.Text.RegularExpressions.Regex? regex = null;
        if (_searchRegex)
        {
            try
            {
                var opts = _searchCaseSensitive
                    ? System.Text.RegularExpressions.RegexOptions.None
                    : System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                regex = new System.Text.RegularExpressions.Regex(_searchTerm, opts);
            }
            catch { /* invalid regex — skip */ _searchCountLabel!.Text = "!"; _historyShown = false; InvalidateVisual(); return; }
        }

        if (_historyMode)
        {
            // Same matching as the Chat View's find unless Match Case is on
            UpdateHistoryMatches(regex, _searchCaseSensitive ? StringComparison.Ordinal : StringComparison.CurrentCultureIgnoreCase);
            UpdateSearchCountLabel();
            InvalidateVisual();
            return;
        }

        // The alternate buffer (the Claude CLI's screen) has no history of its own: the
        // scrollback is whatever the main buffer held before the CLI started, and is not
        // shown. Searching it would jump the view somewhere the user cannot see.
        int scrollbackCount = _buffer.Scrollback.Count;
        int totalRows = scrollbackCount + _buffer.Rows;
        int firstRow = _buffer.IsAltBuffer ? scrollbackCount : 0;
        for (int absRow = firstRow; absRow < totalRows; absRow++)
        {
            var rowText = GetRowText(absRow, out var colOf);
            if (regex != null)
            {
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(rowText))
                    if (m.Length > 0) AddSearchMatch(absRow, colOf, m.Index, m.Length);
            }
            else
            {
                int idx = 0;
                while ((idx = rowText.IndexOf(_searchTerm, idx, comparison)) >= 0)
                {
                    AddSearchMatch(absRow, colOf, idx, _searchTerm.Length);
                    idx += _searchTerm.Length;
                }
            }
        }

        _searchCurrentIndex = _searchMatches.Count > 0 ? 0 : -1;
        UpdateSearchCountLabel();
        ScrollToCurrentMatch();
        InvalidateVisual();
    }

    /// <summary>
    /// Best-effort text of the line the user just submitted. Typing in the plain terminal goes
    /// straight to the PTY, so the only copy of it is what the CLI echoed into the cell grid;
    /// the CLI's own prompt decoration is trimmed off the front.
    /// </summary>
    private void ReplayEnter() =>
        OnInputKeyDown(this, new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private string? ReadSubmittedLine()
    {
        try
        {
            var text = GetRowText(_inputStartAbsRow).TrimEnd();
            text = text.TrimStart('│', '╭', '╰', '>', '❯', ' ');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    /// <summary>
    /// Records a match found at string offset <paramref name="index"/> as cell columns.
    /// Wide characters span two cells but one char, and astral characters (emoji) are two
    /// chars in one cell, so the string offset and the column drift apart along the row.
    /// </summary>
    private void AddSearchMatch(int absRow, List<int> colOf, int index, int length)
    {
        int startCol = colOf[index];
        int endCol = colOf[index + length];   // colOf has a sentinel for the end of the row
        _searchMatches.Add((absRow, startCol, Math.Max(1, endCol - startCol)));
    }

    /// <summary>
    /// Row text plus, for each char of it, the cell column it came from (with one extra
    /// entry for the end of the row), so string offsets can be mapped back to the grid.
    /// </summary>
    private string GetRowText(int absRow, out List<int> colOf)
    {
        var sb = new System.Text.StringBuilder();
        colOf = new List<int>(_buffer.Cols + 1);
        int scrollbackCount = _buffer.Scrollback.Count;
        for (int col = 0; col < _buffer.Cols; col++)
        {
            TerminalCell cell;
            if (absRow < scrollbackCount)
            {
                var line = _buffer.GetScrollbackLine(absRow);
                cell = (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
            }
            else
            {
                cell = _buffer.GetCell(absRow - scrollbackCount, col);
            }
            if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail)) continue;
            var text = cell.Text;
            sb.Append(text);
            for (int i = 0; i < text.Length; i++) colOf.Add(col);
        }
        colOf.Add(_buffer.Cols);
        return sb.ToString();
    }

    private string GetRowText(int absRow)
    {
        var sb = new System.Text.StringBuilder();
        int scrollbackCount = _buffer.Scrollback.Count;
        for (int col = 0; col < _buffer.Cols; col++)
        {
            TerminalCell cell;
            if (absRow < scrollbackCount)
            {
                var line = _buffer.GetScrollbackLine(absRow);
                cell = (line != null && col < line.Length) ? line[col] : TerminalCell.Empty;
            }
            else
            {
                cell = _buffer.GetCell(absRow - scrollbackCount, col);
            }
            if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail)) continue;
            sb.Append(cell.Text);
        }
        return sb.ToString();
    }

    private void SearchNavigate(int direction)
    {
        if (_historyMode)
        {
            if (_histMatches.Count == 0) return;
            _searchCurrentIndex = (_searchCurrentIndex + direction + _histMatches.Count) % _histMatches.Count;
            ShowCurrentHistoryMatch();
            UpdateSearchCountLabel();
            InvalidateVisual();
            return;
        }
        if (_searchMatches.Count == 0) return;
        _searchCurrentIndex = (_searchCurrentIndex + direction + _searchMatches.Count) % _searchMatches.Count;
        UpdateSearchCountLabel();
        ScrollToCurrentMatch();
        InvalidateVisual();
    }

    private void UpdateSearchCountLabel()
    {
        if (_searchCountLabel == null) return;
        int count = _historyMode ? _histMatches.Count : _searchMatches.Count;
        _searchCountLabel.Text = count > 0
            ? $"{_searchCurrentIndex + 1}/{count}"
            : "0";
    }

    private void ScrollToCurrentMatch()
    {
        if (_searchCurrentIndex < 0 || _searchCurrentIndex >= _searchMatches.Count) return;
        var (absRow, _, _) = _searchMatches[_searchCurrentIndex];
        int scrollbackCount = _buffer.Scrollback.Count;
        int screenRow = absRow - scrollbackCount + _scrollOffset;
        if (screenRow < 0 || screenRow >= _buffer.Rows)
        {
            _scrollOffset = Math.Clamp(scrollbackCount - absRow + _buffer.Rows / 2, 0, scrollbackCount);
        }
    }

    private bool IsCellSearchHighlighted(int absRow, int col, out bool isCurrent)
    {
        isCurrent = false;
        if (_searchMatches.Count == 0) return false;
        for (int i = 0; i < _searchMatches.Count; i++)
        {
            var (mRow, mCol, mLen) = _searchMatches[i];
            if (absRow == mRow && col >= mCol && col < mCol + mLen)
            {
                isCurrent = (i == _searchCurrentIndex);
                return true;
            }
        }
        return false;
    }

    // ── Prompt Navigation ──

    /// <summary>
    /// Scan the buffer for likely user prompt positions.
    /// Detects horizontal rule separators (─, ━, ═, ─── etc.) used by Claude Code CLI
    /// between Q&A turns, then marks the first non-blank line after as a prompt.
    /// Also detects prompt markers (❯, ❱) and Human:/User: labels.
    /// </summary>
    private List<int> ScanForPromptRows()
    {
        var prompts = new List<int>();
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        bool afterSeparator = false;

        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var text = GetRowText(absRow).TrimEnd();
            var trimmed = text.TrimStart();

            // Detect prompt markers (❯ ❱)
            if (trimmed.Length > 0 && (trimmed[0] == '\u276F' || trimmed[0] == '\u2771'))
            {
                prompts.Add(absRow);
                afterSeparator = false;
                continue;
            }

            // Detect horizontal rule separators:
            // Claude Code uses lines made of box-drawing chars (─ ━ ═ ╌ ╍ ┄ ┅ ┈ ┉)
            if (text.Length >= 4)
            {
                bool isSeparator = true;
                int ruleChars = 0;
                foreach (char c in text)
                {
                    if (c == ' ') continue;
                    if (c == '\u2500' || c == '\u2501' || c == '\u2550' ||  // ─ ━ ═
                        c == '\u254C' || c == '\u254D' || c == '\u2504' ||  // ╌ ╍ ┄
                        c == '\u2505' || c == '\u2508' || c == '\u2509' ||  // ┅ ┈ ┉
                        c == '-' || c == '\u2014' || c == '\u2013')          // - — –
                    {
                        ruleChars++;
                    }
                    else
                    {
                        isSeparator = false;
                        break;
                    }
                }
                if (isSeparator && ruleChars >= 4)
                {
                    afterSeparator = true;
                    continue;
                }
            }

            // Blank lines after separator: keep waiting
            if (afterSeparator && string.IsNullOrWhiteSpace(text))
                continue;

            // First non-blank line after separator = start of user prompt
            if (afterSeparator && !string.IsNullOrWhiteSpace(text))
            {
                prompts.Add(absRow);
                afterSeparator = false;
                continue;
            }

            afterSeparator = false;
        }

        System.Diagnostics.Debug.WriteLine($"[PromptNav] ScanForPromptRows found {prompts.Count} prompts in {totalRows} rows");
        return prompts;
    }

    /// <summary>Navigate to the previous (-1) or next (+1) user prompt.</summary>
    private void NavigatePrompt(int direction)
    {
        // Use tracked input rows if available, otherwise scan buffer
        var prompts = _userInputRows.Count > 0 ? _userInputRows : ScanForPromptRows();
        System.Diagnostics.Debug.WriteLine($"[PromptNav] NavigatePrompt({direction}): found {prompts.Count} prompts, currentIdx={_promptNavCurrentIndex}");
        if (prompts.Count == 0) return;

        int scrollbackCount = _buffer.Scrollback.Count;

        if (_promptNavCurrentIndex < 0 || _promptNavCurrentIndex >= prompts.Count)
        {
            // First navigation: find the prompt nearest to current viewport
            int currentAbsRow = scrollbackCount - _scrollOffset;
            _promptNavCurrentIndex = 0;
            for (int i = prompts.Count - 1; i >= 0; i--)
            {
                if (prompts[i] <= currentAbsRow)
                {
                    _promptNavCurrentIndex = i;
                    break;
                }
            }
        }

        // Move index by direction, clamping to valid range
        int newIndex = _promptNavCurrentIndex + direction;
        newIndex = Math.Clamp(newIndex, 0, prompts.Count - 1);
        _promptNavCurrentIndex = newIndex;

        int targetAbsRow = prompts[newIndex];

        // Scroll so the prompt is near the top of the viewport (2 rows margin)
        _scrollOffset = Math.Clamp(scrollbackCount - targetAbsRow + 2, 0, scrollbackCount);

        UpdatePromptNavLabel(newIndex + 1, prompts.Count);
        ShowPromptNavBar();
        InvalidateVisual();
    }

    private void ShowPromptNavBar()
    {
        if (_promptNavBar == null) CreatePromptNavBar();
        _promptNavBar!.IsVisible = true;
    }

    private void HidePromptNavBar()
    {
        if (_promptNavBar != null)
            _promptNavBar.IsVisible = false;
        _promptNavCurrentIndex = -1;
    }

    private void UpdatePromptNavLabel(int current, int total)
    {
        if (_promptNavLabel != null)
            _promptNavLabel.Text = $"Q {current}/{total}";
    }

    private void CreatePromptNavBar()
    {
        _promptNavLabel = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 50,
        };

        var prevBtn = new Button
        {
            Content = "\u25B2", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(prevBtn, "Previous prompt (Ctrl+\u2191)");
        prevBtn.Click += (_, _) => NavigatePrompt(-1);

        var nextBtn = new Button
        {
            Content = "\u25BC", FontSize = 10,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(nextBtn, "Next prompt (Ctrl+\u2193)");
        nextBtn.Click += (_, _) => NavigatePrompt(1);

        var closeBtn = new Button
        {
            Content = "\u00D7", FontSize = 14,
            Padding = new Thickness(6, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 205)),
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeBtn.Click += (_, _) => HidePromptNavBar();

        var label = new TextBlock
        {
            Text = "Prompt",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 160, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        panel.Children.Add(label);
        panel.Children.Add(_promptNavLabel);
        panel.Children.Add(prevBtn);
        panel.Children.Add(nextBtn);
        panel.Children.Add(closeBtn);

        _promptNavBar = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 65, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };

        // Position below search bar if present
        if (_searchBar != null)
            _promptNavBar.Margin = new Thickness(0, _searchBar.IsVisible ? 30 : 0, 0, 0);

        VisualChildren.Add(_promptNavBar);
        LogicalChildren.Add(_promptNavBar);
    }

    // ── Document View Mode ──

    public void SetDocumentViewSession(string? path)
    {
        if (string.Equals(_docViewSessionPath, path, StringComparison.OrdinalIgnoreCase)) return;
        _docViewSessionPath = path;
        if (!_isDocumentView || _docViewPanel == null) return;

        if (path != null)
        {
            _docViewPanel.LoadSession(path);
            _docViewPanel.StartPolling();
        }
        else
        {
            // Polling goes on without a transcript: the background list is tied to the folder
            _docViewPanel.Clear();
        }
    }

    public void ToggleDocumentView()
    {
        _isDocumentView = !_isDocumentView;

        if (_isDocumentView)
        {
            // Create document view panel lazily
            if (_docViewPanel == null)
            {
                _docViewPanel = new Controls.DocumentViewPanel(_isDark, _typeface);
                _docViewPanel.SetFont(_typeface.FontFamily.Name, _fontSize);
                _docViewPanel.AskAnswered += (questions, replies) => AnswerAskUserQuestion(questions, replies);
                _docViewPanel.AskCancelled += () => _pty?.WriteInput("\x1b");
                _docViewPanel.IsAskOpen = IsAskSelectorOnScreen;
                _docViewPanel.RewindRequested += target => RewindToPrompt(target);
                _docViewPanel.SearchClosed += () => _inputTextBox.Focus();
                _docViewPanel.FileOpenRequested += path => FileOpenRequested?.Invoke(path);
                _docViewPanel.QueuedPromptRemoved += index =>
                {
                    if (index < 0 || index >= _sendQueue.Count) return;
                    _sendQueue.RemoveAt(index);
                    _docViewPanel?.SetQueue(_sendQueue.Select(q => q.Shown).ToList());
                };
                VisualChildren.Add(_docViewPanel);
                LogicalChildren.Add(_docViewPanel);
                EnsureSidePane();
            }

            _docViewPanel.IsVisible = true;
            _docViewPanel.ProjectFolder = _workingDirectory;

            if (_docViewSessionPath != null)
            {
                _docViewPanel.LoadSession(_docViewSessionPath);
                _docViewPanel.StartPolling();
            }
            else
            {
                _docViewPanel.Clear();
                _docViewPanel.StartPolling();
            }
            StartSuggestionWatch();
        }
        else
        {
            if (_docViewPanel != null)
            {
                _docViewPanel.IsVisible = false;
                _docViewPanel.StopPolling();
            }
            StopSuggestionWatch();
        }

        ApplyInputChrome();
        // The PTY was left alone while the chat view was up; it may have been resized since
        if (!_isDocumentView)
        {
            _sizeBeforePane = null;
            RecalcTerminalSize();
        }
        else if (TerminalInSidePane)
        {
            OnSidePaneLayoutChanged();
        }

        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
        DocumentViewChanged?.Invoke(_isDocumentView);
    }

    // ── Suggested next prompt (chat view) ──

    private DispatcherTimer? _suggestionTimer;
    private string? _promptSuggestion;

    // The terminal shows the CLI's suggestion as ghost text in its prompt; the chat view hides
    // that prompt, so the box carries the suggestion as its placeholder instead.
    private void StartSuggestionWatch()
    {
        if (_suggestionTimer == null)
        {
            _suggestionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _suggestionTimer.Tick += (_, _) =>
            {
                var status = Services.TerminalInsight.WorkingStatus(GetScreenText(0));
                _docViewPanel?.SetWorking(status != null, status);
                FlushSendQueueWhenIdle();
                var s = ReadPromptSuggestion();
                if (s == _promptSuggestion) return;
                _promptSuggestion = s;
                ApplyChatPlaceholder();
            };
        }
        _suggestionTimer.Start();
    }

    // ── Stop and the send queue (chat view) ──

    private record QueuedPrompt(string Shown, string Text, bool WithImages);
    private readonly List<QueuedPrompt> _sendQueue = new();
    private int _idleTicks;
    // The spinner can drop out for a tick between tool calls; wait this many ticks (x400 ms)
    // of quiet before treating the turn as over
    private const int IdleTicksBeforeSend = 3;

    /// <summary>
    /// Interrupts the running turn. Queued prompts go back into the box, as the CLI does with
    /// its own queue: the reader stopped Claude, so sending them on regardless would be wrong.
    /// </summary>
    private void StopTurn()
    {
        _pty?.WriteInput("\x1b");
        if (_sendQueue.Count == 0) return;
        var back = string.Join("\n", _sendQueue.Select(q => q.Shown).Where(s => !string.IsNullOrWhiteSpace(s)));
        var typed = _inputTextBox.Text ?? "";
        _inputTextBox.Text = string.IsNullOrWhiteSpace(typed) ? back : back + "\n" + typed;
        _inputTextBox.CaretIndex = _inputTextBox.Text.Length;
        _sendQueue.Clear();
        _docViewPanel?.SetQueue(Array.Empty<string>());
        _inputTextBox.Focus();
    }

    // Sends the next queued prompt once the turn has been over for a moment and nothing else
    // (a permission prompt, a question, a menu) is waiting for an answer.
    private void FlushSendQueueWhenIdle()
    {
        if (_sendQueue.Count == 0 || _docViewPanel == null || !_isDocumentView || _rewinding
            || _docViewPanel.IsBusy || IsPermissionPromptOnScreen() || IsAskSelectorOnScreen())
        {
            _idleTicks = 0;
            return;
        }
        if (++_idleTicks < IdleTicksBeforeSend) return;
        _idleTicks = 0;

        var next = _sendQueue[0];
        _sendQueue.RemoveAt(0);
        _docViewPanel.SetQueue(_sendQueue.Select(q => q.Shown).ToList());
        _docViewPanel.ShowPendingPrompt(next.Shown);
        PromptSubmitted?.Invoke(next.Text);
        WriteAndSubmit(next.Text, next.WithImages);
    }

    // ── Rewind / edit (chat view) ──

    private bool _rewinding;
    private const string RewindMenuMarker = "Restore the code and/or conversation";
    private const string RewindConfirmMarker = "Confirm you want to restore";

    /// <summary>
    /// Drives the CLI's /rewind to the point before a prompt. Measured against Claude Code
    /// 2.1.283: the menu lists the prompts one line each (truncated with "…") with the caret on
    /// "(current)" at the bottom, Up walks it, and Enter opens a confirm page of numbered choices
    /// (restore the conversation, code too when it changed, summarize, never mind). That page is
    /// left to the reader through the choice card. A conversation restore puts the prompt's text
    /// back into the CLI's input; that is how a completed rewind is told from a cancelled one.
    /// </summary>
    private async void RewindToPrompt(Controls.RewindTarget target)
    {
        if (_rewinding || _pty == null || _docViewPanel == null) return;
        if (_docViewPanel.IsBusy || IsPermissionPromptOnScreen() || IsAskSelectorOnScreen())
        {
            ShowRewindNotice("ChatRewindBusy");
            return;
        }
        _rewinding = true;
        try
        {
            WriteAndSubmit("/rewind", false);
            if (!await WaitForScreen(s => s.Contains(RewindMenuMarker), 5000))
            {
                ShowRewindNotice("ChatRewindFailed");
                return;
            }

            var want = Controls.DocumentViewPanel.FirstLine(target.Text);
            int skip = target.SameTextLater;
            string? selected = ReadRewindSelection();
            bool found = false;
            for (int step = 0; step < 300 && !found; step++)
            {
                _pty?.WriteInput("\x1b[A");
                var before = selected;
                // The caret row is redrawn a moment after the key; the list scrolls at the top
                if (!await WaitForScreen(_ => (selected = ReadRewindSelection()) != before, 1500))
                    break;   // did not move: the top of the list
                if (selected != null && RewindRowMatches(selected, want) && skip-- == 0)
                    found = true;
            }
            if (!found)
            {
                _pty?.WriteInput("\x1b");
                ShowRewindNotice("ChatRewindNotFound");
                return;
            }

            _pty?.WriteInput("\r");
            await WaitForScreen(s => s.Contains(RewindConfirmMarker), 3000);
            // The reader answers the confirm page from the choice card; wait for both pages to go
            if (!await WaitForScreen(s => !s.Contains(RewindConfirmMarker) && !s.Contains(RewindMenuMarker), 120_000))
                return;
            await Task.Delay(300);

            var restored = ReadCliInput();
            if (restored == null || !RewindRowMatches(Controls.DocumentViewPanel.FirstLine(restored), want[..Math.Min(want.Length, 20)]))
                return;   // "Never mind", or a summarize that keeps the prompt out of the input
            _pty?.WriteInput("\x15");   // Ctrl+U: the box, not the CLI's input, holds the draft
            _docViewPanel?.HideFrom(target.Uuid);
            if (target.Edit)
            {
                _inputTextBox.Text = target.Text;
                _inputTextBox.CaretIndex = target.Text.Length;
                _inputTextBox.Focus();
            }
        }
        finally
        {
            _rewinding = false;
        }
    }

    private async Task<bool> WaitForScreen(Func<string, bool> condition, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition(GetScreenText(0))) return true;
            await Task.Delay(60);
        }
        return false;
    }

    /// <summary>The /rewind row under the caret, without the caret.</summary>
    private string? ReadRewindSelection()
    {
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        for (int i = totalRows - 1; i >= Math.Max(0, totalRows - _buffer.Rows); i--)
        {
            var t = GetRowText(i).Trim(' ', '│', '|');
            if (t.Length > 1 && (t[0] == '❯' || t[0] == '>'))
                return Controls.DocumentViewPanel.FirstLine(t[1..]);
        }
        return null;
    }

    // A listed row is the prompt's first line, cut short with "…" when it does not fit
    private static bool RewindRowMatches(string row, string want)
    {
        row = row.TrimEnd('…', '.', ' ');
        if (row.Length == 0 || want.Length == 0) return false;
        return want.StartsWith(row, StringComparison.Ordinal) || row.StartsWith(want, StringComparison.Ordinal);
    }

    /// <summary>Text in the CLI's own input row, if any (the chat view keeps it empty).</summary>
    private string? ReadCliInput()
    {
        int bottom = _buffer.Rows - 1;
        for (int r = bottom; r >= 0 && bottom - r < 24; r--)
        {
            if (FirstNonBlankCol(r) != 0) continue;
            int marker = GetCellAt(r, 0).Character;
            if (marker != '❯' && marker != '>') continue;
            var text = GetRowText(_buffer.Scrollback.Count + r).Trim();
            text = text.TrimStart('❯', '>').Trim();
            return text.Length == 0 ? null : text;
        }
        return null;
    }

    private void ShowRewindNotice(string key)
    {
        _docViewPanel?.ShowNotice(Services.Loc.Get(key));
    }

    private void StopSuggestionWatch()
    {
        _suggestionTimer?.Stop();
        _promptSuggestion = null;
        _docViewPanel?.SetWorking(false);
    }

    private void ApplyChatPlaceholder()
    {
        if (!_isDocumentView) return;
        _inputTextBox.PlaceholderText = _promptSuggestion is { } s
            ? string.Format(Services.Loc.Get("ChatSuggestionPlaceholder"), s)
            : Services.Loc.Get("ChatInputPlaceholder");
    }

    /// <summary>
    /// The CLI's predicted next prompt: ghost text after the prompt marker with the caret still
    /// in front of it. Null when the prompt holds typed text, or when the only dim text is the first-run
    /// "Try ..." hint, which is an example rather than something to send.
    /// </summary>
    private string? ReadPromptSuggestion()
    {
        int bottom = _buffer.Rows - 1;
        for (int r = bottom; r >= 0 && bottom - r < 24; r--)
        {
            if (FirstNonBlankCol(r) != 0) continue;
            int marker = GetCellAt(r, 0).Character;
            if (marker != '❯' && marker != '>') continue;
            // The ghost text carries no dim attribute through ConPTY; what sets it apart from
            // typed text is that the caret stays at the start of the input instead of after it.
            if (_buffer.CursorRow != r || _buffer.CursorCol != 2) return null;

            var sb = new System.Text.StringBuilder();
            for (int row = r; row < _buffer.Rows && row - r < MaxInputBlockRows; row++)
            {
                // A wrapped suggestion continues at the text column; stop at anything else
                if (row > r && FirstNonBlankCol(row) != 2) break;
                int lastCol = -1;
                for (int col = 2; col < _buffer.Cols; col++)
                {
                    var cell = GetCellAt(row, col);
                    if ((cell.Attributes & CellAttributes.WideCharTrail) != 0) continue;
                    if (cell.Character > ' ') lastCol = col;
                    sb.Append(cell.Text);
                }
                int len = sb.Length;
                while (len > 0 && sb[len - 1] == ' ') len--;
                sb.Length = len;
                sb.Append(' ');
                if (lastCol < _buffer.Cols * 2 / 3) break;   // a short row did not wrap
            }

            var text = sb.ToString().Trim();
            return text.Length == 0 || text.StartsWith("Try \"") ? null : text;
        }
        return null;
    }

    /// <summary>
    /// Runs for the life of the session, so the card appears as soon as the chat view is switched
    /// on over an open prompt, and goes away when the view is switched back to the terminal.
    /// </summary>
    private void StartPermissionWatch()
    {
        if (!EnablePermissionOverlay) return;
        if (_permissionCheckTimer == null)
        {
            _permissionCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _permissionCheckTimer.Tick += OnPermissionCheckTick;
        }
        _permissionCheckTimer.Start();
    }

    private void OnPermissionCheckTick(object? sender, EventArgs e)
    {
        if (!EnablePermissionOverlay) return;

        // The terminal view shows the CLI's own prompt, answerable from the keyboard; the card is
        // for the chat view, which hides the terminal
        var prompt = _isDocumentView ? ReadChoicePrompt() : null;
        // Claude Code 2.1.28x can hold the AskUserQuestion call back from the transcript until
        // it is answered, which leaves the chat view with no question card while the CLI waits.
        // The card is then read off the screen instead.
        if (prompt == null && _isDocumentView && _docViewPanel is { HasOpenAskCard: false })
            prompt = ReadAskPrompt();
        // Panels such as /mcp list their rows without numbers, under headings and notes; the
        // card copies the panel as it stands and passes the keys through
        if (prompt == null && _isDocumentView && _docViewPanel is { HasOpenAskCard: false } && !IsAskSelectorOnScreen())
            prompt = ReadScreenPanel();

        // Rebuilt only when the prompt itself changes, not on every caret move. A card that was
        // just answered stays down until its prompt leaves the screen, rather than popping back up
        // while the CLI is still taking the keys in.
        var signature = prompt?.Signature;
        if (signature == _choiceSignature)
        {
            // The preview box follows the CLI's caret, which the signature leaves out
            if (prompt?.Preview is { } preview && _askPreview != null && _askPreview.Text != preview)
                _askPreview.Text = preview;
            return;
        }
        // A panel redraws on every arrow key: the same page is updated in place, so the card
        // neither flickers nor jumps back to the top
        if (prompt is { Kind: ChoiceKind.Panel } && _panelText != null && _panelTitle == prompt.Title)
        {
            _choiceSignature = signature;
            FillPanelText(prompt.Context);
            return;
        }
        HidePermissionOverlay();
        _choiceSignature = signature;
        if (prompt != null)
            ShowPermissionOverlay(prompt, prompt.Kind == ChoiceKind.Permission ? ReadPermissionPromptText() : null);
    }

    private string? _choiceSignature;

    /// <summary>Panel: a selector the card cannot pick apart, shown as the CLI draws it.</summary>
    private enum ChoiceKind { Permission, Plan, Menu, Ask, Panel }

    /// <param name="Detail">The description rows under an AskUserQuestion option.</param>
    /// <param name="Toggles">A multi-select option: its digit ticks it rather than picking it.</param>
    private sealed record ChoiceOption(int Number, string Label, bool TakesText, string? Detail = null, bool Toggles = false);

    /// <param name="Context">The prompt's rows above its options - the command or file being
    /// asked about - so two prompts with the same question still read as different ones.</param>
    private sealed record ChoicePrompt(ChoiceKind Kind, string Title, string Context,
        IReadOnlyList<ChoiceOption> Options, int CaretNumber, string? Footer, bool Unnumbered = false,
        string? Preview = null)
    {
        public string Signature => Kind + "\n" + Context + "\n"
            + string.Join("\n", Options.Select(o => o.Number + ". " + o.Label));
    }

    // "❯ 1. Yes", "  2. Yes, and don't ask again", "↓ 10. Opus 4.6", inside a box or not. The
    // caret prints as ">" through ConPTY.
    private static readonly System.Text.RegularExpressions.Regex ChoiceRowRegex = new(
        @"^[\s│|]*(?<caret>[❯>])?\s*[↑↓]?\s*(?<n>\d{1,2})[.)]\s+(?<label>\S.*?)\s*[│|]?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the CLI is sitting on a numbered selector of any kind.</summary>
    private bool IsPermissionPromptOnScreen() => ReadChoicePrompt() != null;

    /// <summary>
    /// Reads the numbered selector the CLI is showing, if any. Measured against Claude Code
    /// 2.1.283:
    /// - permission prompts ask "Do you want to …?" and option 1 is always Yes
    /// - plan approval asks "… Would you like to proceed?"; its last option is a text field
    ///   that takes the feedback in place of its label
    /// - menus such as /model end with "Esc to cancel", and the caret starts on the current
    ///   setting rather than on option 1
    ///
    /// The last run of numbered rows is the selector; numbered lines higher up are Claude's
    /// reply. A selector always shows its caret, which is what tells it apart from a list that
    /// merely ends the reply. AskUserQuestion lists are left to the chat view's own card.
    /// </summary>
    private ChoicePrompt? ReadChoicePrompt()
    {
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        var rows = new List<string>();
        for (int i = Math.Max(0, totalRows - 30); i < totalRows; i++)
            rows.Add(GetRowText(i).TrimEnd());

        foreach (var row in rows)
            if (row.Contains("Enter to select") || row.Contains("Type something")) return null;

        int last = -1;
        for (int i = rows.Count - 1; i >= 0 && last < 0; i--)
            if (ChoiceRowRegex.IsMatch(rows[i])) last = i;
        if (last < 0) return ReadUnnumberedChoicePrompt();

        // Walk up through consecutive numbers. A long label wraps onto a row of its own, so a
        // couple of unnumbered rows between two options do not end the run.
        var options = new List<ChoiceOption>();
        int caret = -1, firstRow = last, gap = 0;
        for (int i = last; i >= 0; i--)
        {
            var m = ChoiceRowRegex.Match(rows[i]);
            int n = m.Success ? int.Parse(m.Groups["n"].Value) : -1;
            if (m.Success && (options.Count == 0 || n == options[^1].Number - 1))
            {
                // Menus pad their columns with runs of spaces, which read as holes in a button
                var label = System.Text.RegularExpressions.Regex.Replace(m.Groups["label"].Value, @"\s{2,}", " — ");
                options.Add(new ChoiceOption(n, label, false));
                if (m.Groups["caret"].Success) caret = n;
                firstRow = i;
                gap = 0;
            }
            else if (!m.Success && rows[i].Trim().Length > 0 && ++gap <= 2) continue;
            else break;
        }
        options.Reverse();
        if (options.Count < 2 || caret < 0) return ReadUnnumberedChoicePrompt();

        // The prompt's own rows above its options, up to the rule that opens it
        var context = new List<string>();
        for (int i = firstRow - 1; i >= 0 && context.Count < 12; i--)
        {
            var t = rows[i].Trim(' ', '│', '|');
            if (t.Length > 0 && t.All(c => c is '─' or '━' or '▔' or '-' or '╭' or '╮' or '╌')) break;
            if (t.Length > 0) context.Insert(0, t);
        }
        string? footer = null;
        for (int i = last + 1; i < rows.Count && footer == null; i++)
            if (rows[i].Contains("Esc to cancel")) footer = rows[i].Trim(' ', '│', '|');

        bool yesFirst = options[0].Number == 1 && options[0].Label.StartsWith("Yes");
        var question = context.LastOrDefault(t => t.Contains("Do you want") || t.Contains("Would you like"));
        ChoiceKind kind;
        if (yesFirst && question != null && question.Contains("Would you like to proceed"))
        {
            kind = ChoiceKind.Plan;
            // The feedback row: its label is the placeholder, or whatever has been typed into it
            options[^1] = options[^1] with { TakesText = !options[^1].Label.StartsWith("Yes") };
        }
        else if (yesFirst && question != null && question.Contains("Do you want"))
            kind = ChoiceKind.Permission;
        // /rewind's confirm page is a menu that shows no footer
        else if (footer != null || context.Any(t => t.StartsWith(RewindConfirmMarker)))
            kind = ChoiceKind.Menu;
        else
            return null;

        var title = kind == ChoiceKind.Menu ? context.FirstOrDefault() ?? "" : question ?? "";
        return new ChoicePrompt(kind, title, string.Join("\n", context), options, caret, footer);
    }

    // "> No, exit" - the caret row of a selector whose options carry no numbers
    private static readonly System.Text.RegularExpressions.Regex UnnumberedCaretRegex = new(
        @"^[\s│|]*[❯>]\s+(?<label>\S.*?)\s*[│|]?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The folder trust dialog a new project opens with ("Accessing workspace: …") lists
    /// "> No, exit" / "Yes, I trust this folder" without numbers, over "Enter to confirm · Esc to
    /// cancel". The options are the block of rows just above that footer, one of them carrying
    /// the caret; they are numbered here in screen order so the arrow keys can be counted.
    /// The footer is required: a bare "> " row is also the CLI's own input prompt.
    /// </summary>
    private ChoicePrompt? ReadUnnumberedChoicePrompt()
    {
        // The dialog sits at the top of a tall window with blank rows under it, so the last 30
        // rows the numbered selectors are read from can miss its title
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        var rows = new List<string>();
        for (int i = Math.Max(0, totalRows - 80); i < totalRows; i++)
            rows.Add(GetRowText(i).TrimEnd());

        int f = -1;
        for (int i = rows.Count - 1; i >= 0 && f < 0; i--)
            if (rows[i].Contains("Enter to confirm")) f = i;
        if (f < 0) return null;

        int end = f - 1;
        while (end >= 0 && rows[end].Trim(' ', '│', '|').Length == 0) end--;
        int start = end;
        while (start - 1 >= 0 && rows[start - 1].Trim(' ', '│', '|').Length > 0) start--;
        if (end < 0 || end - start + 1 is < 2 or > 10) return null;

        var options = new List<ChoiceOption>();
        int caret = -1;
        for (int i = start; i <= end; i++)
        {
            var m = UnnumberedCaretRegex.Match(rows[i]);
            var label = m.Success ? m.Groups["label"].Value : rows[i].Trim(' ', '│', '|');
            options.Add(new ChoiceOption(options.Count + 1, label, false));
            if (m.Success)
            {
                if (caret >= 0) return null;
                caret = options.Count;
            }
        }
        if (caret < 0) return null;

        var context = new List<string>();
        for (int i = start - 1; i >= 0 && context.Count < 12; i--)
        {
            var t = rows[i].Trim(' ', '│', '|');
            if (t.Length > 0 && t.All(c => c is '─' or '━' or '▔' or '-' or '╭' or '╮' or '╌')) break;
            if (t.Length > 0) context.Insert(0, t);
        }
        return new ChoicePrompt(ChoiceKind.Menu, context.FirstOrDefault() ?? "", string.Join("\n", context),
            options, caret, rows[f].Trim(' ', '│', '|'), Unnumbered: true);
    }

    // The key hint a panel ends with: "↑/↓ to navigate · Enter to confirm · Esc to cancel", or
    // /config's "Type to filter · Enter/↓ to select · ↑ to tabs · Esc to clear"
    private static readonly System.Text.RegularExpressions.Regex PanelFooterRegex = new(
        @"Esc to (cancel|close|clear|go back|exit)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Any other panel the CLI is showing, such as /mcp (Claude Code 2.x): a title, groups of
    /// rows under headings with notes between them, and the key hint at the bottom. Which rows
    /// the caret can reach is not on the screen, so the panel is taken whole: from the rule that
    /// opens it down to the hint, which must be one of the last rows on screen.
    /// </summary>
    private ChoicePrompt? ReadScreenPanel()
    {
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        var rows = new List<string>();
        for (int i = Math.Max(0, totalRows - 80); i < totalRows; i++)
            rows.Add(GetRowText(i).TrimEnd());

        int f = -1;
        for (int i = rows.Count - 1, seen = 0; i >= 0 && seen < 4 && f < 0; i--)
        {
            if (rows[i].Trim(' ', '│', '|').Length == 0) continue;
            if (PanelFooterRegex.IsMatch(rows[i])) f = i;
            seen++;
        }
        if (f < 0) return null;

        int start = f;
        while (start - 1 >= 0 && f - start < 60)
        {
            // A box inside the panel, such as /config's search field, is not where it opens
            var t = rows[start - 1].Trim(' ', '│', '|');
            if (t.Length >= 10 && t.All(c => c is '─' or '━' or '▔' or '-' or '╌')) break;
            start--;
        }
        // Rows are kept whole: a box inside the panel ends in the same bar a frame would
        var lines = rows.GetRange(start, f - start + 1);
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        if (lines.Count < 2) return null;

        int indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        lines = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();
        return new ChoicePrompt(ChoiceKind.Panel, lines[0].Trim(), string.Join("\n", lines),
            Array.Empty<ChoiceOption>(), -1, lines[^1].Trim());
    }

    /// <summary>
    /// Reads the page of the AskUserQuestion selector on screen (see <see cref="IsAskSelectorOnScreen"/>),
    /// Claude Code 2.1.286: an optional tab row ("← [ ] Q1 [ ] Q2 √ Submit →"), the question, the
    /// numbered options each followed by its description row, "Type something.", a rule,
    /// "Chat about this", then the key hints. The review page lists "Submit answers" / "Cancel".
    /// "Chat about this" is left out: picking it abandons the whole selector.
    /// </summary>
    private ChoicePrompt? ReadAskPrompt()
    {
        if (!IsAskSelectorOnScreen()) return null;
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        var rows = new List<string>();
        var cols = new List<List<int>>();
        for (int i = Math.Max(0, totalRows - 40); i < totalRows; i++)
        {
            rows.Add(GetRowText(i, out var colOf).TrimEnd());
            cols.Add(colOf);
        }
        var preview = CutAskPreviewBox(rows, cols);

        int last = -1;
        for (int i = rows.Count - 1; i >= 0 && last < 0; i--)
            if (ChoiceRowRegex.IsMatch(rows[i])) last = i;
        if (last < 0) return null;

        // Walk up through consecutive numbers; the rows between two options are the upper one's
        // description (and the rule above "Chat about this")
        var options = new List<ChoiceOption>();
        var detail = new List<string>();
        int caret = -1, firstRow = last, gap = 0;
        for (int i = last; i >= 0; i--)
        {
            var m = ChoiceRowRegex.Match(rows[i]);
            int n = m.Success ? int.Parse(m.Groups["n"].Value) : -1;
            if (m.Success && (options.Count == 0 || n == options[^1].Number - 1))
            {
                var label = m.Groups["label"].Value.Trim();
                var text = detail.Count > 0 ? string.Join(" ", detail) : null;
                detail.Clear();
                options.Add(new ChoiceOption(n, label, label.TrimStart('[', ' ', 'x', 'X', '✓', '√', ']').StartsWith("Type something"), text,
                    System.Text.RegularExpressions.Regex.IsMatch(label, @"^\[.\]")));
                if (m.Groups["caret"].Success) caret = n;
                firstRow = i;
                gap = 0;
            }
            // Blank rows do not count: a preview box taller than the options leaves a run of
            // them once it is cut away. Nor does the preview page's "Notes:" hint.
            else if (!m.Success && (rows[i].Trim(' ', '│', '|').Length == 0 || ++gap <= 4))
            {
                var t = rows[i].Trim(' ', '│', '|');
                if (t.Length > 0 && !t.All(c => c is '─' or '━' or '-' or '╌') && !t.StartsWith("Notes:"))
                    detail.Insert(0, t);
            }
            else break;
        }
        options.Reverse();
        options.RemoveAll(o => o.Label.StartsWith("Chat about this"));
        if (options.Count == 0) return null;

        // The question and the tab row above the options, up to the rule that opens the selector
        var context = new List<string>();
        for (int i = firstRow - 1; i >= 0 && context.Count < 8; i--)
        {
            var t = rows[i].Trim(' ', '│', '|');
            if (t.Length > 0 && t.All(c => c is '─' or '━' or '▔' or '-' or '╭' or '╮' or '╌')) break;
            if (t.Length > 0) context.Insert(0, t);
        }
        var title = context.LastOrDefault(t => !t.StartsWith('←') && !t.EndsWith('→')) ?? "";
        return new ChoicePrompt(ChoiceKind.Ask, title, string.Join("\n", context), options, caret, null, Preview: preview);
    }

    /// <summary>
    /// A question whose options carry previews draws the focused option's preview in a box to
    /// the right of the options, its top edge on option 1's row. Left in, the box's rows read as
    /// part of the labels and descriptions, and the labels change with the caret. Cuts every
    /// row the box spans at the box's left column and returns what was inside the box.
    /// </summary>
    private static string? CutAskPreviewBox(List<string> rows, List<List<int>> cols)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            int k = rows[i].IndexOfAny(['┌', '╭']);
            if (k <= 0 || rows[i][k - 1] != ' ') continue;
            int col = cols[i][k];
            if (col < 8) continue;

            int end = -1;
            for (int j = i + 1; j < rows.Count && end < 0; j++)
            {
                int b = rows[j].IndexOfAny(['└', '╰']);
                if (b >= 0 && cols[j][b] == col) end = j;
            }
            if (end < 0) continue;

            // The box's inside, its side bars dropped and its own indent kept
            var inside = new List<string>();
            for (int j = i; j <= end; j++)
            {
                int cut = cols[j].FindIndex(c => c >= col);
                if (cut < 0 || cut >= rows[j].Length) continue;
                var part = rows[j][cut..];
                rows[j] = rows[j][..cut].TrimEnd();
                if (j == i || j == end) continue;
                part = part.TrimEnd();
                if (part.StartsWith('│')) part = part[1..];
                if (part.EndsWith('│')) part = part[..^1];
                inside.Add(part.TrimEnd());
            }
            while (inside.Count > 0 && inside[0].Length == 0) inside.RemoveAt(0);
            while (inside.Count > 0 && inside[^1].Length == 0) inside.RemoveAt(inside.Count - 1);
            if (inside.Count == 0) return null;
            int indent = inside.Where(l => l.Length > 0).Min(l => l.Length - l.TrimStart().Length);
            return string.Join("\n", inside.Select(l => l.Length >= indent ? l[indent..] : l));
        }
        return null;
    }

    /// <summary>Grabs the text of the permission prompt so it can be explained in plain words.</summary>
    private string ReadPermissionPromptText()
    {
        var sb = new System.Text.StringBuilder();
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        for (int i = Math.Max(0, totalRows - 30); i < totalRows; i++)
        {
            var text = GetRowText(i).TrimEnd();
            if (text.Length > 0) sb.AppendLine(text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Keys the chosen option in. Selectors move by arrow from wherever the caret is, so the
    /// screen is read again first: if the prompt has moved on, the keys would pick something in
    /// whatever replaced it.
    /// </summary>
    private async void ChooseOption(ChoicePrompt prompt, ChoiceOption option, string? text)
    {
        HidePermissionOverlay();
        // A preview's caret move still going in would interleave with these keys
        for (int w = 0; w < 40 && _askCaretMoving; w++) await Task.Delay(50);
        var now = prompt.Kind == ChoiceKind.Ask ? ReadAskPrompt() : ReadChoicePrompt();
        if (now == null || now.Signature != prompt.Signature) return;

        // A multi-select option is ticked by its digit and the page stays put
        if (option.Toggles)
        {
            _pty?.WriteInput(option.Number.ToString());
            return;
        }
        if (now.CaretNumber < 0) return;

        int delta = option.Number - now.CaretNumber;
        var arrow = delta > 0 ? "\x1b[B" : "\x1b[A";
        for (int k = 0; k < Math.Abs(delta); k++)
        {
            _pty?.WriteInput(arrow);
            await Task.Delay(AskKeyDelayMs);
        }
        if (!string.IsNullOrEmpty(text))
        {
            _pty?.WriteInput(text.Replace("\r", " ").Replace("\n", " "));
            await Task.Delay(AskKeyDelayMs);
        }
        _pty?.WriteInput("\r");
    }

    /// <summary>
    /// The CLI's own words for the options the card knows, in the user's language. The rest
    /// keep the CLI's text: a menu's options are names, and a new wording is better shown as is
    /// than guessed at.
    /// </summary>
    private static string ChoiceButtonText(ChoiceKind kind, ChoiceOption o)
    {
        var l = o.Label;
        if (kind == ChoiceKind.Permission)
        {
            if (l == "Yes") return Services.Loc.Get("AllowAction", "Yes, allow");
            if (l.StartsWith("No")) return Services.Loc.Get("DenyAction", "No, deny");
            // Edit prompts: option 2 turns on accept-edits mode instead of remembering a rule
            if (l.Contains("accept edits") || l.Contains("all edits"))
                return Services.Loc.Get("AllowAndAcceptEdits", "Allow, and auto-accept edits from now on");
            if (l.StartsWith("Yes, and")) return Services.Loc.Get("AlwaysAllow", "Always allow");
        }
        else if (kind == ChoiceKind.Plan)
        {
            if (l.StartsWith("Yes, auto-accept")) return Services.Loc.Get("PlanAutoAccept", "Approve (auto-accept edits)");
            if (l.StartsWith("Yes, manually")) return Services.Loc.Get("PlanManualApprove", "Approve (confirm each edit)");
            if (l.StartsWith("Yes, and use auto mode")) return Services.Loc.Get("PlanAutoMode", "Approve (run in auto mode)");
        }
        return l.Length > 70 ? l[..67] + "…" : l;
    }

    private Button MakeChoiceButton(string text, string tip, Color? fill, bool stretch)
    {
        var btn = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            Background = new SolidColorBrush(fill ?? (_isDark ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 205))),
            Foreground = fill != null ? Brushes.White
                : new SolidColorBrush(_isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(40, 40, 45)),
            Padding = new Thickness(16, 6),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = stretch ? new Thickness(0, 2) : new Thickness(4, 0),
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            HorizontalContentAlignment = stretch ? HorizontalAlignment.Left : HorizontalAlignment.Center,
        };
        ToolTip.SetTip(btn, tip);
        return btn;
    }

    private void ShowPermissionOverlay(ChoicePrompt prompt, string? promptText)
    {
        if (_permissionOverlay != null) return;
        _cardChoices.Clear();
        _cardChoice = -1;
        _cardPages = false;

        var primary = new SolidColorBrush(_isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30));
        var secondary = new SolidColorBrush(_isDark ? Color.FromRgb(152, 152, 158) : Color.FromRgb(85, 85, 93));

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        content.Children.Add(new TextBlock
        {
            Text = prompt.Kind switch
            {
                ChoiceKind.Permission => Services.Loc.Get("PermissionRequired", "Permission Required"),
                ChoiceKind.Plan => Services.Loc.Get("PlanApproval", "Approve the plan?"),
                _ => prompt.Title,
            },
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            Foreground = primary,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
        });

        // Say in plain words what the CLI is asking permission for, and how risky it is.
        var explanation = promptText != null ? Services.CommandExplainer.Explain(promptText) : null;
        if (explanation != null)
        {
            var riskColor = explanation.Risk switch
            {
                Services.RiskLevel.ReadOnly => Color.FromRgb(48, 209, 88),
                Services.RiskLevel.FileChange => Color.FromRgb(255, 214, 10),
                _ => Color.FromRgb(255, 69, 58),
            };

            var riskBadge = new Border
            {
                Background = new SolidColorBrush(riskColor, 0.18),
                BorderBrush = new SolidColorBrush(riskColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = explanation.RiskLabel,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(riskColor),
                },
            };
            content.Children.Add(riskBadge);

            content.Children.Add(new TextBlock
            {
                Text = explanation.Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                Foreground = primary,
                Margin = new Thickness(0, 0, 0, 2),
            });

            if (!string.IsNullOrWhiteSpace(explanation.Detail))
            {
                content.Children.Add(new TextBlock
                {
                    Text = explanation.Detail,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    Foreground = secondary,
                    Margin = new Thickness(0, 0, 0, 10),
                });
            }
        }

        if (prompt.Kind == ChoiceKind.Ask)
        {
            // The tab row tells which question of several this page is
            var tabs = prompt.Context.Split('\n').FirstOrDefault(t => t.StartsWith('←') || t.EndsWith('→'));
            if (tabs != null)
            {
                content.Children.Insert(0, new TextBlock
                {
                    Text = tabs,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = secondary,
                    Margin = new Thickness(0, 0, 0, 4),
                });
            }

            ChoiceOption? textOption = null;
            bool toggles = false;
            foreach (var o in prompt.Options)
            {
                if (o.TakesText) { textOption = o; continue; }
                toggles |= o.Toggles;
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = $"{o.Number}. {o.Label}", TextWrapping = TextWrapping.Wrap });
                if (o.Detail != null)
                    label.Children.Add(new TextBlock { Text = o.Detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
                var btn = MakeChoiceButton("", o.Label, null, true);
                btn.Content = label;
                AddCardChoice(btn, () => ChooseOption(prompt, o, null), o.Number == prompt.CaretNumber, o.Toggles);
                // The CLI draws only the focused option's preview, so its caret is moved to
                // whichever option the card's outline is on (see SelectCardChoice)
                if (prompt.Preview != null)
                {
                    _askSignature = prompt.Signature;
                    _cardAskOptions[_cardChoices.Count - 1] = o.Number;
                }
                content.Children.Add(btn);
            }

            if (prompt.Preview != null)
            {
                _askPreview = new SelectableTextBlock
                {
                    Text = prompt.Preview,
                    FontFamily = _typeface.FontFamily,
                    FontSize = 12,
                    Foreground = primary,
                };
                content.Children.Add(new Border
                {
                    BorderBrush = secondary,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6),
                    Margin = new Thickness(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = _askPreview,
                    },
                });
            }

            if (textOption != null)
            {
                var box = new TextBox
                {
                    Watermark = Services.Loc.Get("AskTypeSomething", "Or type your own answer"),
                    AcceptsReturn = false,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 320,
                    MaxWidth = 440,
                };
                var send = MakeChoiceButton(Services.Loc.Get("PlanSendFeedback", "Send"), textOption.Label, null, false);
                bool SendText()
                {
                    var text = box.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) return false;
                    ChooseOption(prompt, textOption, text);
                    return true;
                }
                _cardSendText = SendText;
                send.Click += (_, _) => SendText();
                box.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter) { ke.Handled = true; SendText(); }
                };
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                row.Children.Add(box);
                row.Children.Add(send);
                content.Children.Add(row);
            }

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0),
            };
            // Multi-select pages are left with Right once the ticks are in; so is a question
            // skipped among several
            _cardPages = toggles || tabs != null;
            if (toggles || tabs != null)
            {
                var next = MakeChoiceButton(Services.Loc.Get("AskNextQuestion", "Next →"), "→", Color.FromRgb(0, 122, 255), false);
                next.Click += (_, _) => { HidePermissionOverlay(); _choiceSignature = null; _pty?.WriteInput("\x1b[C"); };
                actions.Children.Add(next);
            }
            var cancelAsk = MakeChoiceButton(Services.Loc.Get("MenuCancel", "Cancel (Esc)"), "Esc", null, false);
            cancelAsk.Click += (_, _) => { HidePermissionOverlay(); _pty?.WriteInput("\x1b"); };
            actions.Children.Add(cancelAsk);
            content.Children.Add(actions);
        }
        else if (prompt.Kind == ChoiceKind.Panel)
        {
            // The panel in the terminal's font, so its columns still line up; the title is the
            // card's heading above
            _panelTitle = prompt.Title;
            _panelText = new SelectableTextBlock
            {
                FontFamily = _typeface.FontFamily,
                FontSize = 12,
                // Fixed, so box-drawing glyphs from a fallback font do not spread the rows apart
                LineHeight = PanelLineHeight,
                Foreground = primary,
                TextWrapping = TextWrapping.NoWrap,
            };
            // Fitted to the chat view once laid out (see FillPanelText); the caret's row is kept in
            // sight. Rows wider than the card are cut off: a horizontal bar would sit over the last row.
            _panelScroll = new ScrollViewer
            {
                Content = _panelText,
                MaxHeight = 240,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
            FillPanelText(prompt.Context);
            content.Children.Add(_panelScroll);

            var keys = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0),
            };
            foreach (var (text, seq) in new[] { ("↑", "\x1b[A"), ("↓", "\x1b[B"), ("Enter", "\r"),
                         (Services.Loc.Get("MenuCancel", "Cancel (Esc)"), "\x1b") })
            {
                var key = MakeChoiceButton(text, text, text == "Enter" ? Color.FromRgb(0, 122, 255) : null, false);
                key.Click += (_, _) => SendPanelKey(seq);
                keys.Children.Add(key);
            }
            content.Children.Add(keys);
        }
        else if (prompt.Kind == ChoiceKind.Menu)
        {
            // A menu can run to a dozen names: one per row, the current pick marked, scrolling
            // past a screenful
            // A dialog such as the folder trust check says what it is about under its title
            if (prompt.Unnumbered)
            {
                var body = string.Join("\n", prompt.Context.Split('\n').Skip(1));
                if (body.Length > 0)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = body,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Left,
                        Foreground = secondary,
                        MaxWidth = 560,
                        Margin = new Thickness(0, 0, 0, 10),
                    });
                }
            }
            var list = new StackPanel();
            foreach (var o in prompt.Options)
            {
                var btn = MakeChoiceButton(prompt.Unnumbered ? o.Label : $"{o.Number}. {o.Label}", o.Label, null, true);
                AddCardChoice(btn, () => ChooseOption(prompt, o, null), o.Number == prompt.CaretNumber);
                list.Children.Add(btn);
            }
            content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });

            if (prompt.Footer != null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = prompt.Footer,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    Foreground = secondary,
                    Margin = new Thickness(0, 8, 0, 6),
                });
            }
            var cancel = MakeChoiceButton(Services.Loc.Get("MenuCancel", "Cancel (Esc)"), "Esc", null, false);
            cancel.HorizontalAlignment = HorizontalAlignment.Left;
            cancel.Click += (_, _) => { HidePermissionOverlay(); _pty?.WriteInput("\x1b"); };
            content.Children.Add(cancel);
        }
        else
        {
            // Laid out like a menu: one option per row, the CLI's current pick outlined
            var list = new StackPanel();
            ChoiceOption? textOption = null;
            foreach (var o in prompt.Options)
            {
                if (o.TakesText) { textOption = o; continue; }
                var btn = MakeChoiceButton(ChoiceButtonText(prompt.Kind, o), o.Label, null, true);
                AddCardChoice(btn, () => ChooseOption(prompt, o, null), o.Number == prompt.CaretNumber);
                list.Children.Add(btn);
            }
            content.Children.Add(list);

            if (textOption != null)
            {
                var box = new TextBox
                {
                    Watermark = Services.Loc.Get("PlanFeedbackHint", "Or tell Claude what to change"),
                    AcceptsReturn = false,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 320,
                    MaxWidth = 440,
                };
                var send = MakeChoiceButton(Services.Loc.Get("PlanSendFeedback", "Send"), textOption.Label, null, false);
                bool SendFeedback()
                {
                    var text = box.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) return false;
                    ChooseOption(prompt, textOption, text);
                    return true;
                }
                _cardSendText = SendFeedback;
                send.Click += (_, _) => SendFeedback();
                box.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Key.Enter) { ke.Handled = true; SendFeedback(); }
                };
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                row.Children.Add(box);
                row.Children.Add(send);
                content.Children.Add(row);
            }

            if (prompt.Footer != null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = prompt.Footer,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    Foreground = secondary,
                    Margin = new Thickness(0, 8, 0, 6),
                });
                var cancel = MakeChoiceButton(Services.Loc.Get("MenuCancel", "Cancel (Esc)"), "Esc", null, false);
                cancel.HorizontalAlignment = HorizontalAlignment.Left;
                cancel.Click += (_, _) => { HidePermissionOverlay(); _pty?.WriteInput("\x1b"); };
                content.Children.Add(cancel);
            }
        }

        // A card in the transcript, after the reply it follows, styled like the chat's own
        // question card rather than floating over the text
        _permissionOverlay = new Border
        {
            Background = new SolidColorBrush(Controls.ChatTheme.Surface(_isDark)),
            BorderBrush = new SolidColorBrush(Controls.ChatTheme.Outline(_isDark)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
        _permissionOverlay.KeyDown += (_, ke) =>
        {
            if (ke.Source is not TextBox && HandleCardKey(ke, focus: true)) ke.Handled = true;
        };
        // Typing with a panel card's button focused still reaches the panel's filter
        _permissionOverlay.TextInput += (_, te) =>
        {
            if (_panelText == null || te.Source is TextBox || string.IsNullOrEmpty(te.Text) || te.Text == " ") return;
            SendPanelKey(te.Text);
            te.Handled = true;
        };
        // A tick rebuilds the card; the selection stays on the ticked row, not the CLI's caret
        int keep = _cardKeep;
        bool keepFocus = _cardKeepFocus;
        _cardKeep = -1;
        _cardKeepFocus = false;
        if (keep >= 0 && keep < _cardChoices.Count) _cardChoice = keep;
        SelectCardChoice(Math.Max(0, _cardChoice), focus: false);
        _docViewPanel?.SetLiveCard(_permissionOverlay);
        if (keepFocus && _cardChoices.Count > 0)
        {
            var b = _cardChoices[_cardChoice].Button;
            Dispatcher.UIThread.Post(() => b.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void HidePermissionOverlay()
    {
        if (_permissionOverlay == null) return;
        _docViewPanel?.SetLiveCard(null);
        _permissionOverlay = null;
        _cardChoices.Clear();
        _cardChoice = -1;
        _cardSendText = null;
        _panelText = null;
        _panelScroll = null;
        _panelTitle = null;
        _askPreview = null;
        _askSignature = null;
        _cardAskOptions.Clear();
        _askCaretTarget = 0;
    }

    private SelectableTextBlock? _askPreview;
    private string? _askSignature;
    // Card choice index -> option number, on a page whose options carry previews
    private readonly Dictionary<int, int> _cardAskOptions = new();
    private int _askCaretTarget;
    private bool _askCaretMoving;

    /// <summary>
    /// Arrows the AskUserQuestion selector's caret onto an option so the CLI draws that option's
    /// preview. One key at a time, each waiting for the screen to show it landed: the target can
    /// change while the keys are going in, and a stale read would overshoot.
    /// </summary>
    private async void MoveAskCaret(string signature, int number)
    {
        _askCaretTarget = number;
        if (_askCaretMoving) return;
        _askCaretMoving = true;
        try
        {
            for (int step = 0; step < 20 && _askCaretTarget > 0; step++)
            {
                var now = ReadAskPrompt();
                if (now == null || now.Signature != signature || now.CaretNumber < 0
                    || now.CaretNumber == _askCaretTarget) break;
                int from = now.CaretNumber;
                _pty?.WriteInput(from < _askCaretTarget ? "\x1b[B" : "\x1b[A");
                for (int w = 0; w < 10 && ReadAskPrompt()?.CaretNumber == from; w++)
                    await Task.Delay(50);
            }
        }
        finally { _askCaretMoving = false; }
    }

    // ── Panel card ──

    private const double PanelLineHeight = 17;
    private SelectableTextBlock? _panelText;
    private ScrollViewer? _panelScroll;
    private string? _panelTitle;

    /// <summary>The panel's rows under its title, the caret's row picked out.</summary>
    private void FillPanelText(string panel)
    {
        if (_panelText == null) return;
        var accent = new SolidColorBrush(Color.FromRgb(0, 122, 255));
        var inlines = new Avalonia.Controls.Documents.InlineCollection();
        var lines = panel.Split('\n').Skip(1).ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        int caretLine = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) inlines.Add(new Avalonia.Controls.Documents.LineBreak());
            var run = new Avalonia.Controls.Documents.Run(lines[i]);
            if (lines[i].TrimStart().StartsWith('>') || lines[i].TrimStart().StartsWith('❯'))
            {
                run.Foreground = accent;
                run.FontWeight = FontWeight.SemiBold;
                caretLine = i;
            }
            inlines.Add(run);
        }
        _panelText.Inlines = inlines;

        var scroll = _panelScroll;
        if (scroll == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // Read here: on the card's first fill it is not yet in place
            var card = _panelScroll == scroll ? _permissionOverlay : null;
            // The panel's rows take what the chat view has left once the heading and keys are in
            double room = _docViewPanel?.LiveCardRoom ?? 0;
            if (card != null && room > 0)
            {
                double chrome = card.Bounds.Height - scroll.Bounds.Height;
                double h = Math.Max(4 * PanelLineHeight, room - chrome);
                if (Math.Abs(h - scroll.MaxHeight) > 1)
                {
                    scroll.MaxHeight = h;
                    Dispatcher.UIThread.Post(() => _docViewPanel?.KeepLiveCardInView(), DispatcherPriority.Loaded);
                }
            }
            if (caretLine < 0) return;
            double top = caretLine * PanelLineHeight, view = Math.Min(scroll.Viewport.Height, scroll.MaxHeight);
            if (view <= 0) return;
            if (top < scroll.Offset.Y || top + PanelLineHeight > scroll.Offset.Y + view)
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, top - view / 3));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Keys a panel card passes to the CLI, read back at once rather than on the next tick.</summary>
    private void SendPanelKey(string seq)
    {
        _pty?.WriteInput(seq);
        DispatcherTimer.RunOnce(() => OnPermissionCheckTick(null, EventArgs.Empty), TimeSpan.FromMilliseconds(120));
    }

    // ── Card keyboard ──
    // The options of the open card, in order, and the one Up/Down has moved to. Enter picks
    // it, as in the CLI's own selector, whether the composer or the card has focus.

    private readonly List<(Button Button, Action Choose, bool Toggles)> _cardChoices = new();
    private int _cardChoice = -1;
    private int _cardKeep = -1;          // the row to select again once a tick rebuilds the card
    private bool _cardKeepFocus;
    private bool _cardPages;             // Left/Right move between the questions of an Ask card
    private bool _swallowCardSpace;      // the Space that ticked a row is not typed into the composer
    private Func<bool>? _cardSendText;   // sends what the card's text box holds; false when empty

    private void AddCardChoice(Button btn, Action choose, bool current, bool toggles = false)
    {
        int index = _cardChoices.Count;
        Action act = toggles
            ? () =>
            {
                _cardKeep = index;
                _cardKeepFocus = _permissionOverlay?.IsKeyboardFocusWithin == true;
                choose();
            }
            : choose;
        btn.Click += (_, _) => act();
        // The outline follows the mouse, so the hovered row and the one Enter picks never disagree
        btn.PointerEntered += (_, _) => SelectCardChoice(index, focus: false);
        if (current) _cardChoice = index;
        _cardChoices.Add((btn, act, toggles));
    }

    private void SelectCardChoice(int index, bool focus)
    {
        if (_cardChoices.Count == 0) return;
        _cardChoice = Math.Clamp(index, 0, _cardChoices.Count - 1);
        var outline = new SolidColorBrush(Color.FromRgb(0, 122, 255));
        for (int i = 0; i < _cardChoices.Count; i++)
        {
            var b = _cardChoices[i].Button;
            b.BorderBrush = i == _cardChoice ? outline : null;
            b.BorderThickness = new Thickness(i == _cardChoice ? 1.5 : 0);
        }
        var selected = _cardChoices[_cardChoice].Button;
        selected.BringIntoView();
        if (focus) selected.Focus();
        if (_askSignature != null && _cardAskOptions.TryGetValue(_cardChoice, out var number))
            MoveAskCaret(_askSignature, number);
    }

    /// <summary>Up/Down move through the open card's options and Enter picks one.</summary>
    private bool HandleCardKey(KeyEventArgs e, bool focus)
    {
        // A panel card has no rows of its own: the keys go to the CLI as typed
        if (_panelText != null && e.KeyModifiers == KeyModifiers.None)
        {
            // A focused key button clicks itself
            if (focus && e.Source is Button && e.Key is Key.Enter or Key.Space) return false;
            string? seq = e.Key switch
            {
                Key.Up => "\x1b[A",
                Key.Down => "\x1b[B",
                Key.Right => "\x1b[C",
                Key.Left => "\x1b[D",
                Key.Enter => "\r",
                Key.Escape => "\x1b",
                Key.Space => " ",
                Key.Back => "\x7f",
                Key.Tab => "\t",
                _ => null,
            };
            if (seq == null) return false;
            _swallowCardSpace = e.Key == Key.Space && !focus;
            SendPanelKey(seq);
            return true;
        }
        if (_permissionOverlay == null || _cardChoices.Count == 0 || e.KeyModifiers != KeyModifiers.None)
            return false;
        switch (e.Key)
        {
            case Key.Up:
                SelectCardChoice(_cardChoice - 1, focus);
                return true;
            case Key.Down:
                SelectCardChoice(_cardChoice + 1, focus);
                return true;
            case Key.Enter:
                // A focused button outside the options (Next, Cancel) is clicked by itself
                if (focus && e.Source is Button b && !_cardChoices.Exists(c => c.Button == b)) return false;
                // Feedback typed into the card is what Enter sends, even with the box no longer
                // focused; only an option focused on purpose wins over it
                if (!(focus && e.Source is Button) && _cardSendText?.Invoke() == true) return true;
                _cardChoices[Math.Max(0, _cardChoice)].Choose();
                return true;
            case Key.Space:
                // Space ticks a multi-select row, as in the CLI; it picks nothing else
                if (focus && e.Source is Button) return false;   // the button clicks itself
                var row = _cardChoices[Math.Max(0, _cardChoice)];
                if (!row.Toggles) return false;
                _swallowCardSpace = !focus;
                row.Choose();
                return true;
            case Key.Left:
            case Key.Right:
                if (!_cardPages) return false;
                HidePermissionOverlay();
                _choiceSignature = null;
                _pty?.WriteInput(e.Key == Key.Right ? "\x1b[C" : "\x1b[D");
                return true;
            default:
                return false;
        }
    }

    // ── Diagram Cache ──

    private void AutoCacheNewDiagrams()
    {
        var blocks = _codeBlockDetector.DetectedBlocks;
        if (blocks.Count <= _lastCachedBlockCount) return;

        for (int i = _lastCachedBlockCount; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
            {
                Services.DiagramCache.Save(_workingDirectory ?? "", block);
            }
        }
        _lastCachedBlockCount = blocks.Count;
    }

    /// <summary>
    /// Load cached diagrams from disk for the current project folder.
    /// Called when a session is resumed or when the terminal starts.
    /// </summary>
    public void LoadCachedDiagrams()
    {
        if (string.IsNullOrEmpty(_workingDirectory)) return;
        _cachedDiagrams.Clear();
        _cachedDiagrams.AddRange(Services.DiagramCache.Load(_workingDirectory));
        if (_cachedDiagrams.Count > 0)
            InvalidateVisual();
    }

    /// <summary>
    /// Get all diagrams (detected + cached) for display.
    /// </summary>
    public IReadOnlyList<CodeBlockInfo> GetAllDiagrams()
    {
        var result = new List<CodeBlockInfo>(_cachedDiagrams);
        foreach (var block in _codeBlockDetector.DetectedBlocks)
        {
            if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
                result.Add(block);
        }
        return result;
    }

    // ── Diagram Export ──

    private void ShowDiagramContextMenu(CodeBlockInfo block, Point pos)
    {
        var menu = new Avalonia.Controls.ContextMenu();

        var openItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("OpenInWindow"),
        };
        openItem.Click += (_, _) =>
        {
            var win = new DiagramWindow(block, _isDark, _typeface);
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is Window parentWindow)
                win.Show(parentWindow);
            else
                win.Show();
        };

        var artifactItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("SaveAsArtifact"),
        };
        artifactItem.Click += async (_, _) => await SaveAsArtifact(block);

        var saveItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("SaveImage"),
        };
        saveItem.Click += async (_, _) => await ExportDiagramAsPng(block);

        var copyItem = new Avalonia.Controls.MenuItem
        {
            Header = Services.Loc.Get("CopyImage"),
        };
        copyItem.Click += async (_, _) => await CopyDiagramToClipboard(block);

        menu.Items.Add(openItem);
        menu.Items.Add(new Avalonia.Controls.Separator());
        menu.Items.Add(artifactItem);
        menu.Items.Add(saveItem);
        menu.Items.Add(copyItem);
        menu.Open(this);
    }

    private async Task SaveAsArtifact(CodeBlockInfo block)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Services.Loc.Get("SaveAsArtifact"),
                DefaultExtension = "excalidraw",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Excalidraw") { Patterns = new[] { "*.excalidraw" } },
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("SVG Image") { Patterns = new[] { "*.svg" } },
                },
                SuggestedFileName = $"artifact_{DateTime.Now:yyyyMMdd_HHmmss}"
            });

            if (file == null) return;

            var path = file.Path.LocalPath;
            var ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".excalidraw")
            {
                // Save as Excalidraw native format
                var cleanJson = CleanJsonWhitespace(block.Content);
                var excalidrawDoc = $@"{{
  ""type"": ""excalidraw"",
  ""version"": 2,
  ""source"": ""Snipyard"",
  ""elements"": {cleanJson},
  ""appState"": {{
    ""viewBackgroundColor"": ""{(_isDark ? "#1e1e1e" : "#ffffff")}""
  }}
}}";
                await File.WriteAllTextAsync(path, excalidrawDoc);
            }
            else if (ext == ".svg")
            {
                // Save as SVG
                var svg = RenderDiagramToSvg(block);
                if (svg != null)
                    await File.WriteAllTextAsync(path, svg);
            }
            else
            {
                // Save as PNG
                var pngBytes = RenderDiagramToPng(block, 2400, 1200);
                if (pngBytes != null)
                    await File.WriteAllBytesAsync(path, pngBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveAsArtifact error: {ex.Message}");
        }
    }

    private string? RenderDiagramToSvg(CodeBlockInfo block)
    {
        try
        {
            var cleanJson = CleanJsonWhitespace(block.Content);
            var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
            if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var el in elements.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "cameraUpdate" || type == null) continue;
                if (type == "delete")
                {
                    if (el.TryGetProperty("ids", out var ids))
                        foreach (var id in (ids.GetString() ?? "").Split(','))
                            elementMap.Remove(id.Trim());
                    continue;
                }
                if (el.TryGetProperty("id", out var idProp))
                    elementMap[idProp.GetString() ?? ""] = el;
            }
            var drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var el in drawables)
            {
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                minX = Math.Min(minX, ex); minY = Math.Min(minY, ey);
                maxX = Math.Max(maxX, ex + Math.Max(ew, 10));
                maxY = Math.Max(maxY, ey + Math.Max(eh, 10));
            }
            if (!double.IsFinite(minX)) return null;

            double pad = 20;
            double w = maxX - minX + pad * 2;
            double h = maxY - minY + pad * 2;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w:F0}"" height=""{h:F0}"" viewBox=""{minX - pad:F0} {minY - pad:F0} {w:F0} {h:F0}"">");
            sb.AppendLine($@"<rect x=""{minX - pad:F0}"" y=""{minY - pad:F0}"" width=""{w:F0}"" height=""{h:F0}"" fill=""{(_isDark ? "#1e1e1e" : "#ffffff")}""/>");

            foreach (var el in drawables)
            {
                var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                var stroke = el.TryGetProperty("strokeColor", out var sc) ? sc.GetString() ?? "#1e1e1e" : "#1e1e1e";
                var fill = el.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() ?? "none" : "none";
                if (fill == "transparent") fill = "none";
                double sw = el.TryGetProperty("strokeWidth", out var swp) ? swp.GetDouble() : 1;
                double opacity = el.TryGetProperty("opacity", out var op) ? op.GetDouble() / 100.0 : 1.0;
                bool rounded = el.TryGetProperty("roundness", out _);
                string rx = rounded ? @" rx=""6"" ry=""6""" : "";
                string opAttr = opacity < 1 ? $@" opacity=""{opacity:F2}""" : "";

                if (type == "rectangle")
                {
                    sb.AppendLine($@"<rect x=""{ex:F1}"" y=""{ey:F1}"" width=""{ew:F1}"" height=""{eh:F1}"" fill=""{fill}"" stroke=""{stroke}"" stroke-width=""{sw}""{rx}{opAttr}/>");
                    if (el.TryGetProperty("label", out var lbl) && lbl.TryGetProperty("text", out var lt))
                    {
                        double fs = lbl.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16;
                        sb.AppendLine($@"<text x=""{ex + ew / 2:F1}"" y=""{ey + eh / 2:F1}"" text-anchor=""middle"" dominant-baseline=""central"" font-size=""{fs}"" fill=""{stroke}"">{EscapeXml(lt.GetString() ?? "")}</text>");
                    }
                }
                else if (type == "text")
                {
                    var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                    double fs = el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16;
                    double ty = ey + fs;
                    foreach (var line in text.Split('\n'))
                    {
                        sb.AppendLine($@"<text x=""{ex:F1}"" y=""{ty:F1}"" font-size=""{fs}"" fill=""{stroke}""{opAttr}>{EscapeXml(line)}</text>");
                        ty += fs * 1.3;
                    }
                }
                else if (type == "arrow" || type == "line")
                {
                    if (el.TryGetProperty("points", out var pts))
                    {
                        var points = new List<(double x, double y)>();
                        foreach (var pt in pts.EnumerateArray())
                        {
                            int idx = 0; double px = 0, py = 0;
                            foreach (var v in pt.EnumerateArray()) { if (idx == 0) px = v.GetDouble(); else if (idx == 1) py = v.GetDouble(); idx++; }
                            if (idx >= 2) points.Add((ex + px, ey + py));
                        }
                        if (points.Count >= 2)
                        {
                            var d = $"M {points[0].x:F1} {points[0].y:F1}";
                            for (int i = 1; i < points.Count; i++)
                                d += $" L {points[i].x:F1} {points[i].y:F1}";
                            string marker = "";
                            if (type == "arrow" && el.TryGetProperty("endArrowhead", out var ea) && ea.GetString() != null)
                                marker = @" marker-end=""url(#arrowhead)""";
                            sb.AppendLine($@"<path d=""{d}"" fill=""none"" stroke=""{stroke}"" stroke-width=""{sw}""{marker}{opAttr}/>");
                        }
                    }
                }
                else if (type == "ellipse")
                {
                    sb.AppendLine($@"<ellipse cx=""{ex + ew / 2:F1}"" cy=""{ey + eh / 2:F1}"" rx=""{ew / 2:F1}"" ry=""{eh / 2:F1}"" fill=""{fill}"" stroke=""{stroke}"" stroke-width=""{sw}""{opAttr}/>");
                }
            }

            sb.AppendLine(@"<defs><marker id=""arrowhead"" markerWidth=""10"" markerHeight=""7"" refX=""10"" refY=""3.5"" orient=""auto""><polygon points=""0 0, 10 3.5, 0 7"" fill=""#1e1e1e""/></marker></defs>");
            sb.AppendLine("</svg>");
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private CodeBlockInfo? HitTestDiagram(Point pos)
    {
        if (!EnableChartRendering) return null;
        int viewStart = ScreenRowToAbsolute(0);
        int viewEnd = ScreenRowToAbsolute(_buffer.Rows - 1);
        foreach (var block in _codeBlockDetector.GetVisibleBlocks(viewStart, viewEnd))
        {
            if (block.Type != CodeBlockType.Excalidraw || block.Content.Length <= 50) continue;
            int startScreen = AbsoluteToScreenRow(block.StartAbsRow);
            double drawY = Math.Max(0, startScreen * _cellHeight);
            double drawH = 300;
            if (pos.Y >= drawY && pos.Y <= drawY + drawH)
                return block;
        }
        return null;
    }

    private async Task ExportDiagramAsPng(CodeBlockInfo block)
    {
        try
        {
            var pngBytes = RenderDiagramToPng(block, 1200, 600);
            if (pngBytes == null) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Services.Loc.Get("SaveImage"),
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                },
                SuggestedFileName = $"diagram_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            });

            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                await stream.WriteAsync(pngBytes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExportDiagramAsPng error: {ex.Message}");
        }
    }

    private async Task CopyDiagramToClipboard(CodeBlockInfo block)
    {
        try
        {
            var pngBytes = RenderDiagramToPng(block, 1200, 600);
            if (pngBytes == null) return;

            var tempPath = Path.Combine(Services.AppPaths.Temp, $"diagram_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await File.WriteAllBytesAsync(tempPath, pngBytes);

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(tempPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyDiagramToClipboard error: {ex.Message}");
        }
    }

    private byte[]? RenderDiagramToPng(CodeBlockInfo block, int width, int height)
    {
        try
        {
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                var bgDefault = _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
                // Create a temporary block with adjusted coordinates for full-size render
                var fakeBlock = block with { StartAbsRow = 0, EndAbsRow = 0 };
                // Draw directly using the same method but with adjusted dimensions
                var bg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(252, 252, 255);
                ctx.FillRectangle(new SolidColorBrush(bg), new Rect(0, 0, width, height));

                var cleanJson = CleanJsonWhitespace(block.Content);
                var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
                if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

                var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
                foreach (var el in elements.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "cameraUpdate" || type == null) continue;
                    if (type == "delete")
                    {
                        if (el.TryGetProperty("ids", out var ids))
                            foreach (var id in (ids.GetString() ?? "").Split(','))
                                elementMap.Remove(id.Trim());
                        continue;
                    }
                    if (el.TryGetProperty("id", out var idProp))
                        elementMap[idProp.GetString() ?? ""] = el;
                }
                var drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var el in drawables)
                {
                    double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                    double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                    double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                    double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                    double textW = 0;
                    if ((el.TryGetProperty("type", out var tp2) ? tp2.GetString() : "") == "text" && el.TryGetProperty("text", out var txt))
                        textW = (txt.GetString()?.Length ?? 0) * 8;
                    minX = Math.Min(minX, ex);
                    minY = Math.Min(minY, ey);
                    maxX = Math.Max(maxX, ex + Math.Max(ew, textW));
                    maxY = Math.Max(maxY, ey + Math.Max(eh, 20));
                }
                if (!double.IsFinite(minX)) return null;

                double contentW = maxX - minX + 40;
                double contentH = maxY - minY + 40;
                double scale = Math.Min((width - 40) / contentW, (height - 40) / contentH);
                double offsetX = 20 + ((width - 40) - contentW * scale) / 2 - minX * scale;
                double offsetY = 20 + ((height - 40) - contentH * scale) / 2 - minY * scale;

                foreach (var el in drawables)
                {
                    var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                    double ex2 = (el.TryGetProperty("x", out var xp2) ? xp2.GetDouble() : 0) * scale + offsetX;
                    double ey2 = (el.TryGetProperty("y", out var yp2) ? yp2.GetDouble() : 0) * scale + offsetY;
                    double ew2 = (el.TryGetProperty("width", out var wp2) ? wp2.GetDouble() : 0) * scale;
                    double eh2 = (el.TryGetProperty("height", out var hp2) ? hp2.GetDouble() : 0) * scale;
                    var strokeStr = el.TryGetProperty("strokeColor", out var sc2) ? sc2.GetString() : "#1e1e1e";
                    var fillStr = el.TryGetProperty("backgroundColor", out var bc2) ? bc2.GetString() : "transparent";
                    double opacity = el.TryGetProperty("opacity", out var op2) ? op2.GetDouble() / 100.0 : 1.0;
                    double sw = (el.TryGetProperty("strokeWidth", out var swp2) ? swp2.GetDouble() : 1) * Math.Min(scale, 1.5);

                    Color strokeColor = ParseColor(strokeStr, Color.FromRgb(30, 30, 30));
                    Color fillColor = ParseColor(fillStr, Colors.Transparent);
                    if (opacity < 1)
                    {
                        strokeColor = Color.FromArgb((byte)(opacity * 255), strokeColor.R, strokeColor.G, strokeColor.B);
                        fillColor = Color.FromArgb((byte)(opacity * 255), fillColor.R, fillColor.G, fillColor.B);
                    }

                    if (type == "rectangle")
                    {
                        var rect = new Rect(ex2, ey2, Math.Max(1, ew2), Math.Max(1, eh2));
                        bool hasRoundness = el.TryGetProperty("roundness", out _);
                        if (fillColor.A > 0 && fillStr != "transparent")
                            ctx.FillRectangle(new SolidColorBrush(fillColor), rect, (float)(hasRoundness ? 6 : 0));
                        if (strokeStr != "transparent" && sw > 0)
                            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(strokeColor), sw), rect, (float)(hasRoundness ? 6 : 0), (float)(hasRoundness ? 6 : 0));
                        if (el.TryGetProperty("label", out var label) && label.TryGetProperty("text", out var lt))
                        {
                            double lfs = (label.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16) * scale;
                            lfs = Math.Max(10, Math.Min(lfs, 36));
                            var ft = new FormattedText(lt.GetString() ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, lfs, new SolidColorBrush(strokeColor));
                            ctx.DrawText(ft, new Point(ex2 + (ew2 - ft.Width) / 2, ey2 + (eh2 - ft.Height) / 2));
                        }
                    }
                    else if (type == "text")
                    {
                        var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                        double fs = (el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16) * scale;
                        fs = Math.Max(10, Math.Min(fs, 42));
                        double ty = ey2;
                        foreach (var line in text.Split('\n'))
                        {
                            var ft = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, fs, new SolidColorBrush(strokeColor));
                            ctx.DrawText(ft, new Point(ex2, ty));
                            ty += fs * 1.3;
                        }
                    }
                    else if (type == "arrow" || type == "line")
                    {
                        var pen = new Pen(new SolidColorBrush(strokeColor), sw);
                        if (el.TryGetProperty("points", out var pts) && pts.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var pointList = new List<Point>();
                            foreach (var pt in pts.EnumerateArray())
                            {
                                int idx = 0; double px = 0, py = 0;
                                foreach (var v in pt.EnumerateArray()) { if (idx == 0) px = v.GetDouble(); else if (idx == 1) py = v.GetDouble(); idx++; }
                                if (idx >= 2) pointList.Add(new Point(ex2 + px * scale, ey2 + py * scale));
                            }
                            for (int i = 0; i < pointList.Count - 1; i++) ctx.DrawLine(pen, pointList[i], pointList[i + 1]);
                            if (type == "arrow" && pointList.Count >= 2 && el.TryGetProperty("endArrowhead", out var ea2) && ea2.GetString() != null)
                            {
                                var last = pointList[^1]; var prev = pointList[^2];
                                double angle = Math.Atan2(last.Y - prev.Y, last.X - prev.X);
                                double arrLen = 10 * scale;
                                ctx.DrawLine(pen, last, new Point(last.X - arrLen * Math.Cos(angle - 0.4), last.Y - arrLen * Math.Sin(angle - 0.4)));
                                ctx.DrawLine(pen, last, new Point(last.X - arrLen * Math.Cos(angle + 0.4), last.Y - arrLen * Math.Sin(angle + 0.4)));
                            }
                        }
                    }
                    else if (type == "ellipse")
                    {
                        var geo = new EllipseGeometry(new Rect(ex2, ey2, Math.Max(1, ew2), Math.Max(1, eh2)));
                        if (fillColor.A > 0 && fillStr != "transparent") ctx.DrawGeometry(new SolidColorBrush(fillColor), null, geo);
                        if (strokeStr != "transparent" && sw > 0) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(strokeColor), sw), geo);
                    }
                }
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, PngBitmapEncoderOptions.Default);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RenderDiagramToPng error: {ex.Message}");
            return null;
        }
    }

    // ── Export ──

    /// <summary>
    /// Raw text of what is on screen right now, plus a little scrollback, so the status
    /// detectors can read the mode line, the spinner and any error the CLI just printed.
    /// </summary>
    public string GetScreenText(int extraScrollbackLines = 40)
    {
        var sb = new System.Text.StringBuilder();
        int scrollbackCount = _buffer.Scrollback.Count;
        int start = Math.Max(0, scrollbackCount - extraScrollbackLines);
        int totalRows = scrollbackCount + _buffer.Rows;
        for (int absRow = start; absRow < totalRows; absRow++)
            sb.AppendLine(GetRowText(absRow).TrimEnd());
        return sb.ToString();
    }

    public string GetPreviewText(int maxLines = 10)
    {
        int scrollbackCount = _buffer.Scrollback.Count;

        // Include scrollback + screen buffer, but exclude bottom rows
        // (status line, prompt, empty lines at bottom)
        // Find last meaningful content row in screen buffer by scanning upward from cursor
        int lastContentRow = _buffer.CursorRow - 1; // exclude cursor/prompt row
        // Skip status-like rows from bottom (typically contain | or are very short prompts)
        for (; lastContentRow >= 0; lastContentRow--)
        {
            var rowText = GetRowText(scrollbackCount + lastContentRow).TrimEnd();
            // Stop skipping if we find a substantial content line (not status/prompt)
            if (!string.IsNullOrWhiteSpace(rowText) && rowText.Length > 2
                && !rowText.StartsWith(">") && !rowText.Contains(" | "))
                break;
        }

        int totalRows = scrollbackCount + lastContentRow + 1;

        var lines = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var rowText = GetRowText(absRow).TrimEnd();
            bool isWrapped = absRow < scrollbackCount
                ? _buffer.IsScrollbackLineWrapped(absRow)
                : _buffer.IsLineWrapped(absRow - scrollbackCount);
            current.Append(rowText);
            if (!isWrapped)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) lines.Add(current.ToString());

        // Take last N non-empty lines, excluding user input lines
        var result = new List<string>();
        for (int i = lines.Count - 1; i >= 0 && result.Count < maxLines; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Skip user input prompts (Claude Code uses > or ❯ prefix)
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(">") || trimmed.StartsWith("❯") || trimmed.StartsWith("$"))
                continue;
            result.Add(line);
        }
        result.Reverse();
        return string.Join("\n", result);
    }

    public string ExportAsText()
    {
        var sb = new System.Text.StringBuilder();
        int totalRows = _buffer.Scrollback.Count + _buffer.Rows;
        for (int absRow = 0; absRow < totalRows; absRow++)
        {
            var rowText = GetRowText(absRow).TrimEnd();
            int scrollbackCount = _buffer.Scrollback.Count;
            bool isWrapped = absRow < scrollbackCount
                ? _buffer.IsScrollbackLineWrapped(absRow)
                : _buffer.IsLineWrapped(absRow - scrollbackCount);
            sb.Append(rowText);
            if (!isWrapped) sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ── Scroll & Zoom ──

    // Ctrl+Scroll: font zoom (works in both modes)
    private void OnZoomWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
        double newSize = Math.Clamp(_fontSize + (e.Delta.Y > 0 ? 1 : -1), 8, 32);
        if (newSize != _fontSize)
        {
            SetFont(_typeface.FontFamily.Name, newSize);
            FontSizeChanged?.Invoke(newSize);
        }
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (ScrollHistory(e.Delta.Y)) { e.Handled = true; return; }

        // Document view: let ScrollViewer inside DocumentViewPanel handle scrolling
        // (the side pane's terminal still scrolls here)
        if (_isDocumentView && !(TerminalInSidePane && SideTerminalRect.Contains(e.GetPosition(this))))
        {
            // Don't handle - let the event bubble to the ScrollViewer
            return;
        }

        if (_buffer.IsAltBuffer)
        {
            if (e.Delta.Y > 0)
                _pty?.WriteInput("\x1b[5~");
            else
                _pty?.WriteInput("\x1b[6~");
            e.Handled = true;
            return;
        }

        int scrollLines = 3;
        int maxOffset = _buffer.Scrollback.Count;

        if (e.Delta.Y > 0)
            _scrollOffset = Math.Min(_scrollOffset + scrollLines, maxOffset);
        else
            _scrollOffset = Math.Max(_scrollOffset - scrollLines, 0);

        InvalidateVisual();
        e.Handled = true;
    }

    private TerminalCell GetCellAt(int screenRow, int col)
    {
        if (_scrollOffset == 0)
        {
            return _buffer.GetCell(screenRow, col);
        }

        int scrollbackCount = _buffer.Scrollback.Count;
        int historyRow = scrollbackCount - _scrollOffset + screenRow;

        if (historyRow < 0)
            return TerminalCell.Empty;
        if (historyRow < scrollbackCount)
        {
            var line = _buffer.GetScrollbackLine(historyRow);
            if (line != null && col < line.Length)
                return line[col];
            return TerminalCell.Empty;
        }

        int bufferRow = historyRow - scrollbackCount;
        return _buffer.GetCell(bufferRow, col);
    }

    public override void Render(DrawingContext context)
    {
        var bgDefault = _isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255);
        var fgDefault = _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(38, 40, 44);
        double termH = TerminalAreaHeight;

        // Draw entire control background
        var inputBg = _isDark ? Color.FromRgb(44, 44, 46) : Color.FromRgb(242, 242, 242);
        context.FillRectangle(new SolidColorBrush(inputBg), new Rect(0, 0, Bounds.Width, Bounds.Height));

        // Document view mode: skip all terminal cell rendering
        if (_isDocumentView)
        {
            // Draw background for document view area
            var docBg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(250, 250, 252);
            context.FillRectangle(new SolidColorBrush(docBg), new Rect(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - InputAreaHeight - ExpandedPanelHeight)));
            if (!TerminalInSidePane) return;

            // The side pane's Terminal tab: the same grid, drawn into the pane's content area
            var side = SideTerminalRect;
            using (context.PushClip(side))
            using (context.PushTransform(Matrix.CreateTranslation(side.X, side.Y)))
                RenderGrid(context, bgDefault, fgDefault, termH, drawSeparator: false);
            return;
        }

        if (_historyShown)
        {
            RenderHistory(context, bgDefault, fgDefault, termH);
            return;
        }

        RenderGrid(context, bgDefault, fgDefault, termH, drawSeparator: true);
    }

    private void RenderGrid(DrawingContext context, Color bgDefault, Color fgDefault, double termH, bool drawSeparator)
    {
        double viewW = TermViewWidth;

        // Draw terminal background
        context.FillRectangle(new SolidColorBrush(bgDefault), new Rect(0, 0, viewW, termH));

        // Draw separator line above input box
        if (drawSeparator)
        {
            var sepPen = new Pen(new SolidColorBrush(_isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200)), 0.5);
            context.DrawLine(sepPen, new Point(0, termH), new Point(viewW, termH));
        }

        // Draw scrollbar, even with nothing scrolled off yet (the thumb then fills the track),
        // so the view always shows where it sits. Not on the alternate screen: a full-screen app
        // such as the Claude CLI scrolls inside itself and never tells us where it is
        if (!_buffer.IsAltBuffer)
        {
            var (thumbY, thumbH) = GetScrollbarThumb();
            double barX = viewW - ScrollbarWidth;

            byte scrollbarBase = _isDark ? (byte)255 : (byte)0;
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(30, scrollbarBase, scrollbarBase, scrollbarBase)),
                new Rect(barX, 0, ScrollbarWidth, termH));

            byte thumbAlpha = _isScrollbarDragging ? (byte)160 : (_scrollOffset > 0 ? (byte)100 : (byte)70);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(thumbAlpha, scrollbarBase, scrollbarBase, scrollbarBase)),
                new Rect(barX + 2, thumbY, ScrollbarWidth - 4, thumbH));
        }

        bool focused = _inputTextBox.IsFocused;

        // A cursor that has moved since the last frame restarts the blink cycle, so the
        // caret stays lit while text is being typed or the caret walked along a line.
        if (_buffer.CursorRow != _lastCaretRow || _buffer.CursorCol != _lastCaretCol)
        {
            _lastCaretRow = _buffer.CursorRow;
            _lastCaretCol = _buffer.CursorCol;
            if (focused) RestartCaretBlink();
        }

        // Pre-compute screen rows covered by Excalidraw diagrams (to skip cell drawing there)
        var diagramRowRanges = new List<(int start, int end)>();
        if (EnableChartRendering)
        {
            int vStart = ScreenRowToAbsolute(0);
            int vEnd = ScreenRowToAbsolute(_buffer.Rows - 1);
            foreach (var block in _codeBlockDetector.GetVisibleBlocks(vStart, vEnd))
            {
                if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
                {
                    int s = Math.Max(0, AbsoluteToScreenRow(block.StartAbsRow));
                    // Cover from block start to block end (includes checkpointId response)
                    int blockEnd = AbsoluteToScreenRow(block.EndAbsRow);
                    int diagRows = (int)(300 / _cellHeight);
                    int e = Math.Min(_buffer.Rows - 1, Math.Max(blockEnd, s + diagRows));
                    diagramRowRanges.Add((s, e));
                }
            }
        }

        // Draw cells
        for (int row = 0; row < _buffer.Rows; row++)
        {
            double y = row * _cellHeight;
            if (y + _cellHeight > termH) break; // Don't render beyond terminal area

            // Skip rows covered by inline diagrams
            bool skipRow = false;
            foreach (var (ds, de) in diagramRowRanges)
            {
                if (row >= ds && row <= de) { skipRow = true; break; }
            }
            if (skipRow) continue;

            double x = 0;
            for (int col = 0; col < _buffer.Cols; col++)
            {
                var cell = GetCellAt(row, col);

                // Skip wide-char trail cells (the lead cell already covers this space)
                if (cell.Attributes.HasFlag(CellAttributes.WideCharTrail))
                {
                    // Orphaned trail (no preceding wide lead) — treat as empty cell
                    if (col == 0 || !TerminalBuffer.IsWideChar(GetCellAt(row, col - 1).Character))
                        x += _cellWidth;
                    continue;
                }

                // Determine cell display width: wide chars use 2 cell widths
                bool isWide = TerminalBuffer.IsWideChar(cell.Character);
                double cellW = isWide ? _cellWidth * 2 : _cellWidth;

                var fg = ResolveColor(cell.Foreground, fgDefault, true);
                var bg = ResolveColor(cell.Background, bgDefault, false);

                if (cell.Attributes.HasFlag(CellAttributes.Bold) && cell.Foreground >= 0 && cell.Foreground < 8)
                {
                    // Straight from the table, so the light-mode clamp ResolveColor applies
                    // has to be repeated here - bright green and bright cyan are otherwise
                    // laid down on white at under 2:1.
                    var boldFg = GetAnsiColor(cell.Foreground + 8, true);
                    fg = _isDark ? boldFg : ClampFgForLightBg(boldFg);
                }

                if (cell.Attributes.HasFlag(CellAttributes.Dim))
                {
                    // Scale the colour itself down rather than laying a translucent fg over
                    // the background: alpha blending over a light-mode white background wipes
                    // out saturation (the colour reads as grey), while blending over the dark
                    // background barely changes it. A flat RGB scale dims consistently in both.
                    fg = Color.FromRgb((byte)(fg.R * 0.7), (byte)(fg.G * 0.7), (byte)(fg.B * 0.7));
                }

                if (cell.Attributes.HasFlag(CellAttributes.Inverse))
                    (fg, bg) = (bg, fg);

                if (bg != bgDefault)
                    context.FillRectangle(new SolidColorBrush(bg), new Rect(x, y, cellW, _cellHeight));

                // The caret is an insert-mode bar sitting on the cell's leading edge.
                // It is drawn after the glyph (see below) so the character underneath
                // stays readable rather than being inverted out by a block.
                bool isCaretCell = _scrollOffset == 0 && row == _buffer.CursorRow && col == _buffer.CursorCol
                                   && _buffer.CursorVisible && focused && _caretOn;

                // Draw selection highlight
                if (IsCellSelected(row, col))
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(90, 50, 120, 220)),
                        new Rect(x, y, cellW, _cellHeight));

                // Draw search match highlight
                if (_searchMatches.Count > 0)
                {
                    int absRowForSearch = ScreenRowToAbsolute(row);
                    if (IsCellSearchHighlighted(absRowForSearch, col, out bool isCurrent))
                    {
                        var hlColor = isCurrent
                            ? Color.FromArgb(180, 230, 160, 0)   // current match: orange
                            : Color.FromArgb(100, 200, 200, 50); // other matches: yellow
                        context.FillRectangle(new SolidColorBrush(hlColor), new Rect(x, y, cellW, _cellHeight));
                    }
                }

                // Draw character
                if (cell.Character > ' ')
                {
                    // Render block element characters (U+2580-U+259F) programmatically
                    // to avoid font-dependent rendering issues in status line graphs
                    if (cell.Character >= '\u2580' && cell.Character <= '\u259F')
                    {
                        var fgBrush = new SolidColorBrush(fg);
                        DrawBlockElement(context, (char)cell.Character, x, y, cellW, _cellHeight, fg, fgBrush);
                    }
                    else
                    {
                        var ft = new FormattedText(cell.Text, CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight, _typeface, _fontSize, new SolidColorBrush(fg));

                        // A variation selector belongs to the glyph before it rather than
                        // being a character of its own, so the pair may use both columns.
                        double glyphW = cellW;
                        if (!isWide && col + 1 < _buffer.Cols)
                        {
                            int next = GetCellAt(row, col + 1).Character;
                            if (next >= 0xFE00 && next <= 0xFE0F) glyphW += _cellWidth;
                        }

                        DrawGlyph(context, ft, x, y, glyphW);
                    }
                }

                // Draw underline
                if (cell.Attributes.HasFlag(CellAttributes.Underline))
                {
                    var pen = new Pen(new SolidColorBrush(fg), 1);
                    context.DrawLine(pen, new Point(x, y + _cellHeight - 1), new Point(x + cellW, y + _cellHeight - 1));
                }

                // Draw the caret last so it sits on top of the glyph. The default
                // foreground is used rather than the cell's own, which an inverse-video
                // run would have swapped to the background colour and made invisible.
                if (isCaretCell)
                {
                    double caretW = Math.Max(1.5, _cellWidth * 0.18);
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(230, fgDefault.R, fgDefault.G, fgDefault.B)),
                        new Rect(x, y, caretW, _cellHeight));
                }

                x += cellW;
            }
        }

        // Draw code block cards (overlay on detected renderable blocks)
        DrawCodeBlockCards(context, bgDefault, termH);
    }

    private void DrawCodeBlockCards(DrawingContext context, Color bgDefault, double termH)
    {
        if (!EnableChartRendering) return;

        int viewStart = ScreenRowToAbsolute(0);
        int viewEnd = ScreenRowToAbsolute(_buffer.Rows - 1);
        var visibleBlocks = _codeBlockDetector.GetVisibleBlocks(viewStart, viewEnd);

        foreach (var block in visibleBlocks)
        {
            if (block.Type == CodeBlockType.Excalidraw && block.Content.Length > 50)
            {
                DrawExcalidrawInline(context, block, bgDefault, termH);
            }
        }
    }

    private void DrawExcalidrawInline(DrawingContext context, CodeBlockInfo block, Color bgDefault, double termH)
    {
        // Calculate where to draw: at the start of the code block
        int startScreen = AbsoluteToScreenRow(block.StartAbsRow);
        if (startScreen >= _buffer.Rows) return;

        double drawY = Math.Max(0, startScreen * _cellHeight);
        double drawW = Math.Min(_buffer.Cols * _cellWidth, TermViewWidth - ScrollbarWidth) - 20;
        double drawH = Math.Min(300, termH - drawY);
        if (drawH < 50) return;

        var drawRect = new Rect(10, drawY, drawW, drawH);

        // Background
        var bg = _isDark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(252, 252, 255);
        context.FillRectangle(new SolidColorBrush(bg), drawRect);

        // Border
        var borderPen = new Pen(new SolidColorBrush(_isDark
            ? Color.FromRgb(60, 60, 65) : Color.FromRgb(200, 200, 210)), 1);
        context.DrawRectangle(null, borderPen, drawRect);

        // Parse and render Excalidraw elements (with cache fallback for resize tolerance)
        List<System.Text.Json.JsonElement> drawables;
        double minX, minY, maxX, maxY;
        try
        {
            // Pre-process JSON: collapse whitespace and strip non-ASCII outside strings
            var cleanJson = CleanJsonWhitespace(block.Content);
            var elements = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(cleanJson);
            if (elements.ValueKind != System.Text.Json.JsonValueKind.Array) return;

            // Process delete operations and collect drawable elements
            var elementMap = new Dictionary<string, System.Text.Json.JsonElement>();
            foreach (var el in elements.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "cameraUpdate" || type == null) continue;
                if (type == "delete")
                {
                    if (el.TryGetProperty("ids", out var ids))
                        foreach (var id in (ids.GetString() ?? "").Split(','))
                            elementMap.Remove(id.Trim());
                    continue;
                }
                if (el.TryGetProperty("id", out var idProp))
                    elementMap[idProp.GetString() ?? ""] = el;
            }

            drawables = new List<System.Text.Json.JsonElement>(elementMap.Values);

            // Find bounding box
            minX = double.MaxValue; minY = double.MaxValue;
            maxX = double.MinValue; maxY = double.MinValue;
            foreach (var el in drawables)
            {
                double ex = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                double ey = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                double ew = el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0;
                double eh = el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0;
                var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                double textW = 0;
                if (type == "text" && el.TryGetProperty("text", out var txt))
                    textW = (txt.GetString()?.Length ?? 0) * 8;
                minX = Math.Min(minX, ex);
                minY = Math.Min(minY, ey);
                maxX = Math.Max(maxX, ex + Math.Max(ew, textW));
                maxY = Math.Max(maxY, ey + Math.Max(eh, 20));
            }

            if (!double.IsFinite(minX) || drawables.Count == 0) return;

            // Cache successful parse for fallback after terminal reflow
            _excalidrawCacheDrawables = drawables;
            _excalidrawCacheMinX = minX; _excalidrawCacheMinY = minY;
            _excalidrawCacheMaxX = maxX; _excalidrawCacheMaxY = maxY;
        }
        catch
        {
            // Parse failed (e.g., after terminal reflow corrupted JSON) — use cached result
            if (_excalidrawCacheDrawables != null)
            {
                drawables = _excalidrawCacheDrawables;
                minX = _excalidrawCacheMinX; minY = _excalidrawCacheMinY;
                maxX = _excalidrawCacheMaxX; maxY = _excalidrawCacheMaxY;
            }
            else
                return; // No cache available, skip rendering
        }
        try
        {

            double contentW = maxX - minX + 40;
            double contentH = maxY - minY + 40;
            double scale = Math.Min((drawW - 20) / contentW, (drawH - 10) / contentH);
            scale = Math.Min(scale, 2);
            double offsetX = drawRect.X + 10 + ((drawW - 20) - contentW * scale) / 2 - minX * scale;
            double offsetY = drawRect.Y + 5 + ((drawH - 10) - contentH * scale) / 2 - minY * scale;

            // Clip to draw area
            using (context.PushClip(drawRect))
            {
                foreach (var el in drawables)
                {
                    var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "";
                    double ex = (el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0) * scale + offsetX;
                    double ey = (el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0) * scale + offsetY;
                    double ew = (el.TryGetProperty("width", out var wp) ? wp.GetDouble() : 0) * scale;
                    double eh = (el.TryGetProperty("height", out var hp) ? hp.GetDouble() : 0) * scale;
                    var strokeStr = el.TryGetProperty("strokeColor", out var sc) ? sc.GetString() : "#1e1e1e";
                    var fillStr = el.TryGetProperty("backgroundColor", out var bc) ? bc.GetString() : "transparent";
                    double opacity = el.TryGetProperty("opacity", out var op) ? op.GetDouble() / 100.0 : 1.0;
                    double sw = (el.TryGetProperty("strokeWidth", out var swp) ? swp.GetDouble() : 1) * Math.Min(scale, 1);

                    Color strokeColor = ParseColor(strokeStr, _isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(30, 30, 30));
                    Color fillColor = ParseColor(fillStr, Colors.Transparent);

                    // Adjust text/stroke contrast for readability on diagram background
                    strokeColor = AdjustColorForContrast(strokeColor, _isDark);

                    if (opacity < 1)
                    {
                        strokeColor = Color.FromArgb((byte)(opacity * 255), strokeColor.R, strokeColor.G, strokeColor.B);
                        fillColor = Color.FromArgb((byte)(opacity * 255), fillColor.R, fillColor.G, fillColor.B);
                    }

                    if (type == "rectangle")
                    {
                        var rect = new Rect(ex, ey, Math.Max(1, ew), Math.Max(1, eh));
                        bool hasRoundness = el.TryGetProperty("roundness", out _);
                        if (fillColor.A > 0 && fillStr != "transparent")
                            context.FillRectangle(new SolidColorBrush(fillColor), rect, (float)(hasRoundness ? 6 : 0));
                        if (strokeStr != "transparent" && sw > 0)
                            context.DrawRectangle(null, new Pen(new SolidColorBrush(strokeColor), sw), rect, (float)(hasRoundness ? 6 : 0), (float)(hasRoundness ? 6 : 0));

                        // Label
                        if (el.TryGetProperty("label", out var label) && label.TryGetProperty("text", out var lt))
                        {
                            double lfs = (label.TryGetProperty("fontSize", out var lf) ? lf.GetDouble() : 16) * scale;
                            lfs = Math.Max(8, Math.Min(lfs, 24));
                            var ft = new FormattedText(lt.GetString() ?? "", CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, _typeface, lfs, new SolidColorBrush(strokeColor));
                            context.DrawText(ft, new Point(ex + (ew - ft.Width) / 2, ey + (eh - ft.Height) / 2));
                        }
                    }
                    else if (type == "text")
                    {
                        var text = el.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : "";
                        double fs = (el.TryGetProperty("fontSize", out var fsp) ? fsp.GetDouble() : 16) * scale;
                        fs = Math.Max(8, Math.Min(fs, 28));
                        foreach (var line in text.Split('\n'))
                        {
                            var ft = new FormattedText(line, CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, _typeface, fs, new SolidColorBrush(strokeColor));
                            context.DrawText(ft, new Point(ex, ey));
                            ey += fs * 1.3;
                        }
                    }
                    else if (type == "arrow" || type == "line")
                    {
                        var pen = new Pen(new SolidColorBrush(strokeColor), sw);
                        if (el.TryGetProperty("points", out var pts) && pts.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            var pointList = new List<Point>();
                            foreach (var pt in pts.EnumerateArray())
                            {
                                int idx = 0;
                                double px = 0, py = 0;
                                foreach (var v in pt.EnumerateArray())
                                {
                                    if (idx == 0) px = v.GetDouble();
                                    else if (idx == 1) py = v.GetDouble();
                                    idx++;
                                }
                                if (idx >= 2)
                                    pointList.Add(new Point(ex + px * scale, ey + py * scale));
                            }
                            for (int i = 0; i < pointList.Count - 1; i++)
                                context.DrawLine(pen, pointList[i], pointList[i + 1]);

                            // Arrowhead
                            if (type == "arrow" && pointList.Count >= 2 &&
                                el.TryGetProperty("endArrowhead", out var ea) && ea.GetString() != null)
                            {
                                var last = pointList[^1];
                                var prev = pointList[^2];
                                double angle = Math.Atan2(last.Y - prev.Y, last.X - prev.X);
                                double arrLen = 8 * scale;
                                context.DrawLine(pen, last,
                                    new Point(last.X - arrLen * Math.Cos(angle - 0.4), last.Y - arrLen * Math.Sin(angle - 0.4)));
                                context.DrawLine(pen, last,
                                    new Point(last.X - arrLen * Math.Cos(angle + 0.4), last.Y - arrLen * Math.Sin(angle + 0.4)));
                            }

                            // Arrow label
                            if (el.TryGetProperty("label", out var al) && al.TryGetProperty("text", out var alt) && pointList.Count >= 2)
                            {
                                var mid = pointList[pointList.Count / 2];
                                double lfs = (al.TryGetProperty("fontSize", out var alf) ? alf.GetDouble() : 14) * scale;
                                lfs = Math.Max(8, Math.Min(lfs, 20));
                                var ft = new FormattedText(alt.GetString() ?? "", CultureInfo.CurrentCulture,
                                    FlowDirection.LeftToRight, _typeface, lfs, new SolidColorBrush(strokeColor));
                                context.DrawText(ft, new Point(mid.X - ft.Width / 2, mid.Y - ft.Height - 4));
                            }
                        }
                    }
                    else if (type == "ellipse")
                    {
                        var center = new Point(ex + ew / 2, ey + eh / 2);
                        var geo = new EllipseGeometry(new Rect(ex, ey, Math.Max(1, ew), Math.Max(1, eh)));
                        if (fillColor.A > 0 && fillStr != "transparent")
                            context.DrawGeometry(new SolidColorBrush(fillColor), null, geo);
                        if (strokeStr != "transparent" && sw > 0)
                            context.DrawGeometry(null, new Pen(new SolidColorBrush(strokeColor), sw), geo);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DrawExcalidraw] Error: {ex.Message}");
            // Show error visually in the draw area (visible in Release mode too)
            var errText = new FormattedText($"Render Error: {ex.Message}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                _typeface, 11, new SolidColorBrush(Color.FromRgb(255, 100, 100)));
            context.DrawText(errText, new Point(drawRect.X + 10, drawRect.Y + 10));
        }
    }

    private static Color ParseColor(string? hex, Color defaultColor)
    {
        if (string.IsNullOrEmpty(hex) || hex == "transparent") return Colors.Transparent;
        try { return Color.Parse(hex); } catch { return defaultColor; }
    }

    /// <summary>
    /// Adjust stroke/text colors for readability on the diagram background.
    /// Dark mode bg ≈ #1e1e22, Light mode bg ≈ #fcfcff.
    /// Colors too close to the background are shifted for contrast.
    /// </summary>
    private static Color AdjustColorForContrast(Color c, bool isDark)
    {
        double brightness = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255.0;
        if (isDark)
        {
            // Dark background: colors with brightness < 0.3 are too dark to read
            if (brightness < 0.3)
                return Color.FromRgb(
                    (byte)Math.Min(255, 255 - c.R + 40),
                    (byte)Math.Min(255, 255 - c.G + 40),
                    (byte)Math.Min(255, 255 - c.B + 40));
        }
        else
        {
            // Light background: colors with brightness > 0.7 are too light to read
            if (brightness > 0.7)
                return Color.FromRgb(
                    (byte)Math.Max(0, c.R - 180),
                    (byte)Math.Max(0, c.G - 180),
                    (byte)Math.Max(0, c.B - 180));
        }
        return c;
    }

    /// <summary>
    /// Clean JSON that has been extracted from terminal output.
    /// Terminal line wrapping inserts extra whitespace (spaces, newlines)
    /// into the JSON content, potentially breaking parsing.
    /// This method collapses runs of whitespace outside string values.
    /// </summary>
    /// <summary>
    /// Minify JSON by removing ALL whitespace outside string values.
    /// This fixes terminal line-wrapping artifacts where numbers get split
    /// across rows (e.g., "800" becomes "80 0" due to wrapped indentation).
    /// </summary>
    private static string CleanJsonWhitespace(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (escape) { sb.Append(c); escape = false; continue; }
            if (c == '\\' && inString) { sb.Append(c); escape = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }

            if (inString)
            {
                sb.Append(c);
            }
            else if (c > ' ' && c <= '~')
            {
                // Outside strings: only keep printable ASCII (0x21-0x7E).
                // Strips whitespace AND non-ASCII characters (e.g., ⎿ ● from terminal formatting)
                // that can leak into JSON during terminal reflow after resize.
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Draws one cell's glyph, squeezed horizontally when the font draws it wider than the
    /// columns the terminal gave that character.
    ///
    /// Japanese monospace fonts (BIZ UDGothic, MS Gothic, Meiryo) draw every East Asian
    /// "Ambiguous" character at full width - twice the ASCII advance. That set is large and
    /// turns up constantly in CLI output: Greek letters, the multiplication sign, the box
    /// drawing range, and the geometric shapes used as bullets. Terminals, and the CLI that
    /// lays its output out for one, all count those as a single column. Drawn at its natural
    /// width the glyph spills past its cell and collides with the next character.
    /// </summary>
    private static void DrawGlyph(DrawingContext ctx, FormattedText ft, double x, double y, double maxWidth)
    {
        // Half a pixel of slack: side bearings can push an otherwise fitting glyph a
        // fraction past the boundary, and scaling those would be visible churn for nothing.
        if (ft.Width <= maxWidth + 0.5 || ft.Width <= 0)
        {
            ctx.DrawText(ft, new Point(x, y));
            return;
        }

        // Horizontal only. Scaling both axes would break the grid - box-drawing runs would
        // stop meeting their neighbours and vertical rules would no longer reach the next row.
        using (ctx.PushTransform(Matrix.CreateScale(maxWidth / ft.Width, 1) * Matrix.CreateTranslation(x, y)))
            ctx.DrawText(ft, new Point(0, 0));
    }

    private static void DrawBlockElement(DrawingContext ctx, char c, double x, double y, double w, double h, Color fg, IBrush brush)
    {
        switch (c)
        {
            case '\u2580': ctx.FillRectangle(brush, new Rect(x, y, w, h / 2)); break;
            case '\u2581': ctx.FillRectangle(brush, new Rect(x, y + h * 7 / 8, w, h / 8)); break;
            case '\u2582': ctx.FillRectangle(brush, new Rect(x, y + h * 3 / 4, w, h / 4)); break;
            case '\u2583': ctx.FillRectangle(brush, new Rect(x, y + h * 5 / 8, w, h * 3 / 8)); break;
            case '\u2584': ctx.FillRectangle(brush, new Rect(x, y + h / 2, w, h / 2)); break;
            case '\u2585': ctx.FillRectangle(brush, new Rect(x, y + h * 3 / 8, w, h * 5 / 8)); break;
            case '\u2586': ctx.FillRectangle(brush, new Rect(x, y + h / 4, w, h * 3 / 4)); break;
            case '\u2587': ctx.FillRectangle(brush, new Rect(x, y + h / 8, w, h * 7 / 8)); break;
            case '\u2588': ctx.FillRectangle(brush, new Rect(x, y, w, h)); break;
            case '\u2589': ctx.FillRectangle(brush, new Rect(x, y, w * 7 / 8, h)); break;
            case '\u258A': ctx.FillRectangle(brush, new Rect(x, y, w * 3 / 4, h)); break;
            case '\u258B': ctx.FillRectangle(brush, new Rect(x, y, w * 5 / 8, h)); break;
            case '\u258C': ctx.FillRectangle(brush, new Rect(x, y, w / 2, h)); break;
            case '\u258D': ctx.FillRectangle(brush, new Rect(x, y, w * 3 / 8, h)); break;
            case '\u258E': ctx.FillRectangle(brush, new Rect(x, y, w / 4, h)); break;
            case '\u258F': ctx.FillRectangle(brush, new Rect(x, y, w / 8, h)); break;
            case '\u2590': ctx.FillRectangle(brush, new Rect(x + w / 2, y, w / 2, h)); break;
            case '\u2591': // ░ Light shade (25%)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(64, fg.R, fg.G, fg.B)), new Rect(x, y, w, h)); break;
            case '\u2592': // ▒ Medium shade (50%)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(128, fg.R, fg.G, fg.B)), new Rect(x, y, w, h)); break;
            case '\u2593': // ▓ Dark shade (75%)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(192, fg.R, fg.G, fg.B)), new Rect(x, y, w, h)); break;
            case '\u2594': ctx.FillRectangle(brush, new Rect(x, y, w, h / 8)); break;
            case '\u2595': ctx.FillRectangle(brush, new Rect(x + w * 7 / 8, y, w / 8, h)); break;
            default: ctx.FillRectangle(brush, new Rect(x, y, w, h)); break;
        }
    }

    private Color ResolveColor(int colorIndex, Color defaultColor, bool isFg)
    {
        if (colorIndex == -1) return defaultColor;
        Color c;
        if ((colorIndex & 0x01000000) != 0)
        {
            c = Color.FromRgb(
                (byte)((colorIndex >> 16) & 0xFF),
                (byte)((colorIndex >> 8) & 0xFF),
                (byte)(colorIndex & 0xFF));
        }
        else if (colorIndex >= 0 && colorIndex < 256)
        {
            c = GetAnsiColor(colorIndex, isFg);
        }
        else
        {
            return defaultColor;
        }

        if (!_isDark)
        {
            double brightness = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255.0;
            if (isFg)
            {
                // Light mode foreground: pull the colour down until it clears the contrast
                // floor against the white background. The flat brightness cut this replaces
                // only fired above 0.6, which left the mid-range Tango entries - green,
                // cyan, bright blue - sitting on white at 3:1 or less.
                c = ClampFgForLightBg(c);
            }
            else
            {
                // Light mode background: lighten dark backgrounds
                if (brightness < 0.4)
                {
                    c = Color.FromRgb(
                        (byte)(c.R + (255 - c.R) * 0.80),
                        (byte)(c.G + (255 - c.G) * 0.80),
                        (byte)(c.B + (255 - c.B) * 0.80));
                }
            }
        }
        return c;
    }

    // 1.05 / (L + 0.05) = 4.0 solves to L = 0.2125; relaxed from the original 5:1 (L =
    // 0.16) so colours aren't crushed almost to black to hit the ratio. Legibility now
    // leans on the saturation boost below (hue/chroma difference from the white
    // background) rather than on lightness contrast alone.
    private const double LightFgMaxLuminance = 0.22;

    // Uniform RGB scaling toward black keeps hue and even keeps the HSL saturation
    // ratio intact (min/max shrink together), but a very dark shade of a colour still
    // *reads* as muted grey - real chroma capacity shrinks toward zero as lightness
    // approaches zero. Boosting HSL saturation after the scale compensates so text
    // stays visibly colourful instead of just dark.
    private const double LightFgSaturationBoost = 1.5;

    /// <summary>
    /// Scales a foreground colour down, hue intact, until it clears
    /// <see cref="LightFgMaxLuminance"/>, then boosts saturation to keep it vivid.
    /// </summary>
    private static Color ClampFgForLightBg(Color c)
    {
        c = ClampLuminance(c);

        var (h, s, l) = RgbToHsl(c);
        if (s <= 0) return c; // grey/white/black: nothing to boost
        s = Math.Min(1.0, s * LightFgSaturationBoost);
        c = HslToRgb(h, s, l);

        // The boost can raise a channel-weighted luminance back above the target even
        // though HSL lightness stayed fixed - green's 0.7152 weight dominates, so a
        // saturation gain that grows the green channel outweighs shrinking red/blue.
        return ClampLuminance(c);
    }

    private static Color ClampLuminance(Color c)
    {
        double lum = RelativeLuminance(c);
        if (lum <= LightFgMaxLuminance) return c;
        // Luminance runs roughly as the 2.4th power of the channel values, so one
        // factor lands on the target instead of iterating towards it.
        double f = Math.Pow(LightFgMaxLuminance / lum, 1.0 / 2.4);
        return Color.FromRgb(
            (byte)Math.Round(c.R * f),
            (byte)Math.Round(c.G * f),
            (byte)Math.Round(c.B * f));
    }

    /// <summary>WCAG relative luminance, 0 for black through 1 for white.</summary>
    private static double RelativeLuminance(Color c) =>
        0.2126 * ToLinear(c.R) + 0.7152 * ToLinear(c.G) + 0.0722 * ToLinear(c.B);

    private static (double h, double s, double l) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        if (max == min) return (0, 0, l);

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h / 6.0, s, l);
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        double r, g, b;
        if (s <= 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    private static double ToLinear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    // Blue and red carry too little luminance to be read on a near-black background at
    // full saturation: the standard ANSI blue (0,0,187) lands at 1.4:1 against the
    // #1C1C1E terminal background and red (187,0,0) at 2.5:1, and neither hue clears
    // 4.5:1 even at full intensity (pure blue peaks near 2:1, pure red at 4.3:1). So
    // dark-mode *text* uses these lightened variants instead of palette entries 1, 4, 9
    // and 12. The bright pair is lifted along with the normal one to keep bold text
    // visibly brighter than plain text. Backgrounds keep the dark palette entries, so
    // white-on-blue and white-on-red bars stay readable.
    private static readonly Color DarkRedFg = Color.FromRgb(238, 75, 75);           // 4.7:1
    private static readonly Color DarkBrightRedFg = Color.FromRgb(255, 145, 140);   // 7.8:1
    private static readonly Color DarkBlueFg = Color.FromRgb(80, 150, 255);         // 5.8:1
    private static readonly Color DarkBrightBlueFg = Color.FromRgb(130, 190, 255);  // 8.7:1

    private static readonly Color[] DarkColors16 =
    {
        Color.FromRgb(0, 0, 0),
        Color.FromRgb(187, 0, 0),
        Color.FromRgb(0, 187, 0),
        Color.FromRgb(187, 187, 0),
        Color.FromRgb(0, 0, 187),
        Color.FromRgb(187, 0, 187),
        Color.FromRgb(0, 187, 187),
        Color.FromRgb(187, 187, 187),
        Color.FromRgb(85, 85, 85),
        Color.FromRgb(255, 85, 85),
        Color.FromRgb(85, 255, 85),
        Color.FromRgb(255, 255, 85),
        Color.FromRgb(85, 85, 255),
        Color.FromRgb(255, 85, 255),
        Color.FromRgb(85, 255, 255),
        Color.FromRgb(255, 255, 255),
    };

    // Tango Light color scheme (matches Windows Terminal)
    private static readonly Color[] LightColors16 =
    {
        Color.FromRgb(0, 0, 0),          // 0 Black
        Color.FromRgb(204, 0, 0),        // 1 Red
        Color.FromRgb(78, 154, 6),       // 2 Green
        Color.FromRgb(196, 160, 0),      // 3 Yellow
        Color.FromRgb(52, 101, 164),     // 4 Blue
        Color.FromRgb(117, 80, 123),     // 5 Magenta
        Color.FromRgb(6, 152, 154),      // 6 Cyan
        Color.FromRgb(211, 215, 207),    // 7 White
        Color.FromRgb(85, 87, 83),       // 8 Bright Black
        Color.FromRgb(239, 41, 41),      // 9 Bright Red
        Color.FromRgb(138, 226, 52),     // 10 Bright Green
        Color.FromRgb(252, 233, 79),     // 11 Bright Yellow
        Color.FromRgb(114, 159, 207),    // 12 Bright Blue
        Color.FromRgb(173, 127, 168),    // 13 Bright Magenta
        Color.FromRgb(52, 226, 226),     // 14 Bright Cyan
        Color.FromRgb(238, 238, 236),    // 15 Bright White
    };

    private Color GetAnsiColor(int index, bool isFg)
    {
        var colors16 = _isDark ? DarkColors16 : LightColors16;

        if (index < 16)
        {
            if (isFg && _isDark)
            {
                switch (index)
                {
                    case 1: return DarkRedFg;
                    case 4: return DarkBlueFg;
                    case 9: return DarkBrightRedFg;
                    case 12: return DarkBrightBlueFg;
                }
            }
            return colors16[index];
        }

        if (index < 232)
        {
            int i = index - 16;
            int r = (i / 36) * 51;
            int g = ((i / 6) % 6) * 51;
            int b = (i % 6) * 51;
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        int gray = (index - 232) * 10 + 8;
        return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }

    public bool IsExpanded => _isExpanded;

    public void AppendToExpandedInput(string text)
    {
        _expandedTextBox.Text = (_expandedTextBox.Text ?? "") + text;
        _expandedTextBox.CaretIndex = _expandedTextBox.Text.Length;
        _expandedTextBox.Focus();
    }

    /// <summary>
    /// Opens the expanded input panel if needed and puts <paramref name="text"/> in it, replacing
    /// anything already there. For long text that the user must be able to read and edit before
    /// it goes anywhere - nothing is written to the PTY until they send it themselves.
    /// </summary>
    public void ShowInExpandedInput(string text)
    {
        if (!_isExpanded) ExpandInputPanel();
        _expandedTextBox.Text = text;
        _expandedTextBox.CaretIndex = _expandedTextBox.Text.Length;
        _expandedTextBox.Focus();
    }

    public void SendText(string text) => _pty?.WriteInput(text);

    private async Task PasteToInputBoxAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        if (await TryAttachClipboardImagesAsync(clipboard)) return;

        // The box's own paste: over the selection, as one undo step. Assigning Text instead
        // would wipe the undo history.
        _inputTextBox.Paste();
    }

    /// <summary>
    /// Notepad's editing keys for the chat view's prompt box. Returns false for a key that
    /// keeps its terminal meaning: Ctrl+C with nothing selected in the box still copies the
    /// terminal selection or interrupts the CLI, and Ctrl+V goes to the image-aware paste.
    /// </summary>
    private bool HandleChatEditKey(KeyEventArgs e)
    {
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);
        bool boxSelection = _inputTextBox.SelectionStart != _inputTextBox.SelectionEnd;
        switch (e.Key)
        {
            case Key.C when mods == KeyModifiers.Control && boxSelection:
                _inputTextBox.Copy();
                return true;
            case Key.X when mods == KeyModifiers.Control && (boxSelection || !_hasSelection):
                _inputTextBox.Cut();    // nothing selected: nothing to cut, as in Notepad
                return true;
            case Key.Z when mods == KeyModifiers.Control:
                _inputTextBox.Undo();
                return true;
            case Key.Z when mods == (KeyModifiers.Control | KeyModifiers.Shift):
            case Key.Y when mods == KeyModifiers.Control:
                _inputTextBox.Redo();
                return true;
            case Key.A when mods == KeyModifiers.Control:
                _inputTextBox.SelectAll();
                return true;
        }
        return false;
    }

    /// <summary>
    /// Set text in the IME input box (for document view mode where direct PTY send is hidden).
    /// </summary>
    public void SetInputText(string text)
    {
        _inputTextBox.Text = text;
        _inputTextBox.CaretIndex = text.Length;
        _inputTextBox.Focus();
    }

    /// <summary>
    /// Chat view counterpart of typing a snippet into the console: every CR submits the text
    /// before it as a prompt, and whatever follows the last CR is left in the box to edit. An LF
    /// is a line break inside one prompt. The snippet goes in at the caret, replacing any
    /// selection, so what was already typed stays: the text before the caret leads the first
    /// prompt submitted and the text after it follows what is left in the box.
    /// </summary>
    public async void SubmitSnippet(string text)
    {
        var current = _inputTextBox.Text ?? "";
        int start = Math.Clamp(Math.Min(_inputTextBox.SelectionStart, _inputTextBox.SelectionEnd), 0, current.Length);
        int end = Math.Clamp(Math.Max(_inputTextBox.SelectionStart, _inputTextBox.SelectionEnd), start, current.Length);
        if (start == end) start = end = Math.Clamp(_inputTextBox.CaretIndex, 0, current.Length);
        var before = current[..start];
        var after = current[end..];

        var parts = text.Split('\r');
        parts[0] = before + parts[0];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            _inputTextBox.Text = parts[i];
            if (!SubmitChatInput()) _pty?.WriteInput("\r");
            // Let the previous submit's delayed CR land before the next prompt is typed
            await Task.Delay(AttachmentSubmitDelayMs + 100);
        }
        var last = parts[^1];
        _inputTextBox.Text = last + after;
        _inputTextBox.SelectionStart = _inputTextBox.SelectionEnd = last.Length;
        _inputTextBox.CaretIndex = last.Length;
        _inputTextBox.Focus();
    }

    /// <summary>
    /// Types a snippet into the console: every CR presses Enter, and an LF is a line break
    /// inside the prompt being typed, sent as a paste so the CLI does not take it for Enter.
    /// </summary>
    public async void SendSnippet(string text)
    {
        var parts = text.Split('\r');
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (_buffer.BracketedPasteMode && part.Contains('\n'))
                _pty?.WriteInput("\x1b[200~" + part + "\x1b[201~");
            else if (part.Length > 0)
                _pty?.WriteInput(part);
            if (i == parts.Length - 1) break;
            // Text arriving in one burst with its CR reads as a paste, where a CR is no submit
            if (part.Length > 0) await Task.Delay(SubmitDelayMs);
            _pty?.WriteInput("\r");
            if (i < parts.Length - 2) await Task.Delay(AttachmentSubmitDelayMs + 100);
        }
    }

    /// <summary>
    /// Drag payload format used by the in-app Explorer tree: one full path per line.
    /// </summary>
    public static readonly DataFormat<string> ExplorerPathFormat =
        DataFormat.CreateStringApplicationFormat("Snipyard.FilePaths");

    private static bool HasDroppablePaths(IDataTransfer data)
        => data.Contains(DataFormat.File)
        || data.Contains(ExplorerPathFormat)
        || data.Contains(DataFormat.Text);

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasDroppablePaths(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        var text = BuildDroppedText(e.DataTransfer, out bool attached);
        if (string.IsNullOrEmpty(text) && !attached) return;

        // Dropping onto an inactive MDI child should bring it to the front
        Clicked?.Invoke();
        if (!string.IsNullOrEmpty(text))
            ShowInInputArea(text);
        else
            _inputTextBox.Focus();
        e.Handled = true;
    }

    private string BuildDroppedText(IDataTransfer data, out bool attachedImages)
    {
        attachedImages = false;
        var paths = new List<string>();

        var files = data.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                var path = file.Path?.LocalPath;
                if (!string.IsNullOrEmpty(path))
                    paths.Add(TrimTrailingSeparator(path));
            }
        }

        // Explorer-tree payload (also the fallback when the storage lookup failed)
        if (paths.Count == 0 && data.TryGetValue(ExplorerPathFormat) is string raw)
        {
            foreach (var line in raw.Split('\n'))
            {
                var path = line.Trim();
                if (path.Length > 0)
                    paths.Add(TrimTrailingSeparator(path));
            }
        }

        if (paths.Count == 0)
            return (data.TryGetText() ?? "").Trim();

        // Images become thumbnails and go out with the next submit; the rest are typed in
        var others = new List<string>();
        foreach (var path in paths)
        {
            if (Controls.ImageAttachmentStrip.IsImageFile(path))
            {
                _attachStrip.Add(path, FileReference(path));
                attachedImages = true;
            }
            else
                others.Add(path);
        }
        return string.Join(" ", others.Select(FileReference));
    }

    /// <summary>
    /// "@" matches the CLI's own file-reference syntax, and the path is made relative
    /// to the working directory the same way the @ completion popup names its matches.
    /// </summary>
    private string FileReference(string path)
    {
        var relative = RelativeToWorkingDirectory(path);
        return relative.Contains(' ') ? $"@\"{relative}\"" : "@" + relative;
    }

    /// <summary>
    /// Drop sources report folders with a trailing separator; it would escape
    /// the closing quote once the path gets quoted. Roots keep theirs.
    /// </summary>
    private static string TrimTrailingSeparator(string path)
        => path.Length > 3 && (path[^1] == '\\' || path[^1] == '/')
            ? path.TrimEnd('\\', '/')
            : path;

    /// <summary>
    /// Put dropped text into whichever input area is live: the IME box in
    /// document view, the CLI prompt itself otherwise.
    /// </summary>
    private void ShowInInputArea(string text)
    {
        if (_isDocumentView)
        {
            var current = _inputTextBox.Text ?? "";
            var caret = Math.Clamp(_inputTextBox.CaretIndex, 0, current.Length);
            var insert = (caret > 0 && current[caret - 1] != ' ' ? " " + text : text) + " ";
            _inputTextBox.Text = current.Insert(caret, insert);
            _inputTextBox.CaretIndex = caret + insert.Length;
        }
        else
        {
            // Trailing space keeps back-to-back drops from gluing together
            _pty?.WriteInput(text + " ");
        }

        _inputTextBox.Focus();
    }

    public void FocusTerminal()
    {
        _inputTextBox.Focus();
    }

    /// <summary>
    /// Send /exit command and wait for the process to exit gracefully.
    /// Returns true if process exited within timeout.
    /// </summary>
    public async Task<bool> SendExitAndWaitAsync(int timeoutMs = 3000)
    {
        if (_pty == null || !_pty.IsRunning) return true;

        // CLIs without a quit command are torn down by disposing the pseudo console
        if (string.IsNullOrEmpty(ExitCommand)) return false;

        _pty.WriteInput(ExitCommand);
        return await Task.Run(() => _pty.WaitForExitTimeout(timeoutMs));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _marquee.Stop();
        _permissionCheckTimer?.Stop();
        _caretBlinkTimer?.Stop();
        _pty?.Dispose();
    }
}
