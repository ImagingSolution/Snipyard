using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// The commit list: lanes and edges down the left, then the commit's refs, subject, author and
/// date. Drawn straight onto the <see cref="DrawingContext"/> rather than composed from controls,
/// so several hundred rows scroll without a visual per cell; only rows inside the viewport are
/// painted. Read-only -- it selects and reports, and never touches the repository.
/// </summary>
public sealed partial class CommitGraphView : Control
{
    public const double RowHeight = 24;

    /// <summary>Width of the author column; the window's header uses it to line up.</summary>
    public const double AuthorWidth = 140;

    /// <summary>Width of the date column; the window's header uses it to line up.</summary>
    public const double DateWidth = 120;

    /// <summary>
    /// Drops the author and date columns and gives the whole width to the subject. Set for the
    /// graph inside the source-control panel, where 260 columns of author and date would leave
    /// nothing to read; the full listing is a double-click away in the window.
    /// </summary>
    public bool CompactColumns
    {
        get => _compactColumns;
        set
        {
            if (_compactColumns == value) return;
            _compactColumns = value;
            InvalidateVisual();
        }
    }

    private bool _compactColumns;

    private double AuthorColumn => _compactColumns ? 0 : AuthorWidth;
    private double DateColumn => _compactColumns ? 0 : DateWidth;

    private const double LaneWidth = 14;

    /// <summary>
    /// How narrow lanes may be squeezed before the graph gives up on fitting them all. Lanes
    /// shrink rather than pile up at the right edge, so a branchy repository stays readable.
    /// </summary>
    private const double MinLaneWidth = 6;

    private const double DotRadius = 4;
    private const double GraphPad = 10;
    private const double MaxGraphWidth = 200;
    private const double ColumnGap = 14;
    private const double FontSize = 12;

    /// <summary>
    /// The colours branches are drawn in, handed out in turn (see <see cref="AssignColors"/>).
    /// Chosen to stay legible on both the light and the dark background, since the window
    /// follows whichever theme the app is in.
    /// </summary>
    private static readonly Color[] LaneColors =
    {
        Color.FromRgb(0x3B, 0x8E, 0xEA), // blue
        Color.FromRgb(0xE0, 0x55, 0x61), // red
        Color.FromRgb(0x3F, 0xB9, 0x50), // green
        Color.FromRgb(0xD2, 0x99, 0x22), // amber
        Color.FromRgb(0xA3, 0x71, 0xF7), // purple
        Color.FromRgb(0x26, 0xA5, 0xA5), // teal
        Color.FromRgb(0xE0, 0x6C, 0x9F), // pink
    };

    // Scrolling repaints the whole viewport, so every brush and pen the rows need is built once
    // here rather than per edge and per dot on each frame.
    private static readonly IBrush[] LaneBrushes = MakeBrushes();
    private static readonly Pen[] EdgePens = MakePens(1.6, null);
    private static readonly Pen[] StemPens = MakePens(1.6, new DashStyle(new double[] { 2, 2 }, 0));
    private static readonly Pen[] DotPens = MakePens(1.8, null);
    private static readonly Pen[] RingPens = MakePens(2, null);
    private static readonly Pen[] ChipPens = MakePens(1, null);

    private readonly bool _isDark;
    private readonly Typeface _ui;
    private readonly Typeface _uiBold;

    private readonly IBrush _selectionBrush;
    private readonly IBrush _stripeBrush;
    private readonly IBrush _dotFill;
    private readonly IBrush _textBrush;
    private readonly IBrush _dimBrush;
    private readonly IBrush _headChipText;
    private readonly IBrush _tagBrush;
    private readonly Pen _tagPen;

    private CommitGraph _graph = new();

    /// <summary>Colour index of each row's dot, aligned with <see cref="CommitGraph.Rows"/>.</summary>
    private int[] _rowColors = Array.Empty<int>();

    /// <summary>Colour index of each line, aligned with <see cref="CommitGraph.Edges"/>.</summary>
    private int[] _edgeColors = Array.Empty<int>();

    private bool _showUncommitted;
    private int _selected = -1;
    private ScrollViewer? _scroller;
    private readonly ContextMenu _rowContextMenu;

    /// <summary>Lane pitch in use, narrowed from <see cref="LaneWidth"/> when lanes are many.</summary>
    private double _laneWidth = LaneWidth;

    private double _dotRadius = DotRadius;

    public CommitGraphView(bool isDark)
    {
        _isDark = isDark;

        // Commit messages are often Japanese here, so the UI face needs a CJK fallback behind it.
        var uiFamily = new FontFamily("Segoe UI, Yu Gothic UI, Meiryo, sans-serif");
        _ui = new Typeface(uiFamily);
        _uiBold = new Typeface(uiFamily, FontStyle.Normal, FontWeight.SemiBold);

        _selectionBrush = new SolidColorBrush(isDark
            ? Color.FromRgb(0x2F, 0x3A, 0x4B)
            : Color.FromRgb(0xD8, 0xE6, 0xF8));
        _stripeBrush = new SolidColorBrush(isDark
            ? Color.FromArgb(10, 255, 255, 255)
            : Color.FromArgb(10, 0, 0, 0));
        _dotFill = new SolidColorBrush(isDark
            ? Color.FromRgb(0x1C, 0x1C, 0x1E)
            : Color.FromRgb(0xFF, 0xFF, 0xFF));
        _textBrush = new SolidColorBrush(isDark
            ? Color.FromRgb(0xDC, 0xDC, 0xE1)
            : Color.FromRgb(0x1C, 0x1C, 0x1E));
        _dimBrush = new SolidColorBrush(isDark
            ? Color.FromRgb(0x8C, 0x8C, 0x94)
            : Color.FromRgb(0x54, 0x54, 0x5C));
        _headChipText = _dotFill;
        _tagBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
        _tagPen = new Pen(_tagBrush, 1);

        Focusable = true;
        ClipToBounds = true;

        var createTag = new MenuItem { Header = Loc.Get("CreateTagAction", "Create Tag...") };
        createTag.Click += (_, _) =>
        {
            if (SelectedCommit is { } commit) CreateTagRequested?.Invoke(this, commit);
        };
        var checkout = new MenuItem { Header = Loc.Get("CheckoutCommitAction", "Checkout") };
        checkout.Click += (_, _) =>
        {
            if (SelectedCommit is { } commit) CheckoutRequested?.Invoke(this, commit);
        };
        var copyHash = new MenuItem { Header = Loc.Get("CopyCommitHashAction", "Copy Commit Hash") };
        copyHash.Click += async (_, _) =>
        {
            if (SelectedCommit is not { } commit) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(commit.Hash);
        };
        _rowContextMenu = new ContextMenu
        {
            ItemsSource = new object[] { checkout, createTag, new Separator(), copyHash },
        };
        InitOperations();
    }

    private static IBrush[] MakeBrushes()
    {
        var brushes = new IBrush[LaneColors.Length];
        for (int i = 0; i < brushes.Length; i++)
            brushes[i] = new SolidColorBrush(LaneColors[i]);
        return brushes;
    }

    private static Pen[] MakePens(double thickness, IDashStyle? dash)
    {
        var pens = new Pen[LaneColors.Length];
        for (int i = 0; i < pens.Length; i++)
            pens[i] = new Pen(LaneBrushes[i], thickness, dash);
        return pens;
    }

    /// <summary>Raised when the highlighted row changes, however it was changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when a row is double-clicked or Enter is pressed on it.</summary>
    public event EventHandler? RowActivated;

    /// <summary>Raised when "Create Tag..." is picked from a commit row's right-click menu.</summary>
    public event EventHandler<GitCommit>? CreateTagRequested;

    /// <summary>Raised when "Checkout" is picked from a commit row's right-click menu.</summary>
    public event EventHandler<GitCommit>? CheckoutRequested;

    /// <summary>True when the selected row is the working tree rather than a commit.</summary>
    public bool IsUncommittedSelected => _showUncommitted && _selected == 0;

    /// <summary>The selected commit, or null when nothing or the working-tree row is selected.</summary>
    public GitCommit? SelectedCommit
    {
        get
        {
            int i = _selected - RowOffset;
            return i >= 0 && i < _graph.Rows.Count ? _graph.Rows[i].Node as GitCommit : null;
        }
    }

    /// <summary>
    /// How much width the lanes take. The column header lines up with the text columns by
    /// starting here.
    /// </summary>
    public double GraphWidth =>
        Math.Min(MaxGraphWidth, GraphPad * 2 + Math.Max(1, _graph.LaneCount) * _laneWidth);

    private int RowOffset => _showUncommitted ? 1 : 0;

    private int DisplayCount => _graph.Rows.Count + RowOffset;

    /// <summary>
    /// Replaces the contents. The selection moves to the top row unless
    /// <paramref name="keepSelection"/> asks for the commit that was selected to be found again,
    /// which is what stops a "load more" throwing the reader back to the newest commit.
    /// </summary>
    public void SetGraph(CommitGraph graph, bool showUncommitted, bool keepSelection = false)
    {
        var previousHash = keepSelection ? SelectedCommit?.Hash : null;
        bool wasUncommitted = keepSelection && IsUncommittedSelected;

        _graph = graph;
        (_rowColors, _edgeColors) = AssignColors(graph);
        _showUncommitted = showUncommitted;

        // Lanes give up width rather than piling up at the right edge once there are more of
        // them than the graph column can hold at full pitch.
        _laneWidth = Math.Clamp(
            (MaxGraphWidth - GraphPad * 2) / Math.Max(1, _graph.LaneCount), MinLaneWidth, LaneWidth);
        _dotRadius = Math.Min(DotRadius, _laneWidth * 0.36);

        int restored = -1;
        if (wasUncommitted && _showUncommitted) restored = 0;
        else if (previousHash != null)
        {
            int i = _graph.Rows.FindIndex(r => r.Node.Hash == previousHash);
            if (i >= 0) restored = i + RowOffset;
        }

        bool sameSelection = restored >= 0 && restored == _selected;
        _selected = restored >= 0 ? restored : (DisplayCount > 0 ? 0 : -1);

        InvalidateMeasure();
        InvalidateVisual();

        // Re-announcing a selection that has not moved would only make the detail pane reload
        // the commit it is already showing.
        if (!sameSelection) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Colours the graph by branch rather than by lane. A commit carries on the colour of the
    /// child whose first parent it is along the same lane, and a commit a branch points at starts
    /// a fresh colour -- so where history runs straight on from one branch into another, the dots
    /// and the line change colour at the branch's commit and its label matches them.
    /// </summary>
    private static (int[] Rows, int[] Edges) AssignColors(CommitGraph graph)
    {
        var rowColors = new int[graph.Rows.Count];
        var edgeColors = new int[graph.Edges.Count];

        var arrivals = new List<int>?[graph.Rows.Count];
        for (int e = 0; e < graph.Edges.Count; e++)
        {
            int to = graph.Edges[e].ToRow;
            if (to >= 0) (arrivals[to] ??= new List<int>()).Add(e);
        }

        int next = 0;
        for (int row = 0; row < graph.Rows.Count; row++)
        {
            var graphRow = graph.Rows[row];

            int inherited = -1;
            foreach (int e in arrivals[row] ?? Enumerable.Empty<int>())
            {
                var edge = graph.Edges[e];
                if (edge.FromLane != graphRow.Lane || edge.TravelLane != graphRow.Lane) continue;
                var parents = graph.Rows[edge.FromRow].Node.Parents;
                if (parents.Count == 0 || parents[0] != graphRow.Node.Hash) continue;
                inherited = rowColors[edge.FromRow];
                break;
            }

            bool startsBranch = graphRow.Node is GitCommit commit && commit.Refs.Any(r =>
                r.Kind is GitRefKind.LocalBranch or GitRefKind.RemoteBranch);

            if (inherited >= 0 && !startsBranch)
            {
                rowColors[row] = inherited;
                continue;
            }

            // A new branch must not look like a continuation of the one it follows on from.
            int color = next++ % LaneColors.Length;
            if (color == inherited) color = next++ % LaneColors.Length;
            rowColors[row] = color;
        }

        // A line belongs to the branch on its outer side: the child's where a branch forks off
        // or runs straight down, the parent's where a merge reaches out to it.
        for (int e = 0; e < graph.Edges.Count; e++)
        {
            var edge = graph.Edges[e];
            bool childSide = edge.FromLane >= Math.Max(edge.TravelLane, edge.ToLane);
            edgeColors[e] = childSide || edge.ToRow < 0 ? rowColors[edge.FromRow] : rowColors[edge.ToRow];
        }

        return (rowColors, edgeColors);
    }

    /// <summary>
    /// Points the view at the scroll viewer holding it, so it can paint just the visible rows
    /// and keep the selection in sight.
    /// </summary>
    public void AttachScroller(ScrollViewer scroller)
    {
        _scroller = scroller;
        scroller.ScrollChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        return new Size(width, Math.Max(DisplayCount * RowHeight, 1));
    }

    // -- Input ----------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        int row = (int)(e.GetPosition(this).Y / RowHeight);
        if (row >= 0 && row < DisplayCount) Select(row, scrollIntoView: false);

        // The context menu is only offered over a real commit - not blank space below the last
        // row, and not the synthetic "Uncommitted Changes" row, which has nothing to tag.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            ContextMenu = SelectedCommit != null ? _rowContextMenu : null;
    }

    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (_selected >= 0) RowActivated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DisplayCount == 0) { base.OnKeyDown(e); return; }

        int page = Math.Max(1, (int)((_scroller?.Viewport.Height ?? RowHeight * 10) / RowHeight) - 1);
        int target = _selected;

        switch (e.Key)
        {
            case Key.Up: target = _selected - 1; break;
            case Key.Down: target = _selected + 1; break;
            case Key.PageUp: target = _selected - page; break;
            case Key.PageDown: target = _selected + page; break;
            case Key.Home: target = 0; break;
            case Key.End: target = DisplayCount - 1; break;
            case Key.Enter:
                if (_selected >= 0) RowActivated?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }

        Select(Math.Clamp(target, 0, DisplayCount - 1), scrollIntoView: true);
        e.Handled = true;
    }

    private void Select(int row, bool scrollIntoView)
    {
        if (row == _selected) return;
        _selected = row;

        if (scrollIntoView && _scroller != null)
        {
            double top = row * RowHeight;
            double offset = _scroller.Offset.Y;
            double viewport = _scroller.Viewport.Height;

            if (top < offset)
                _scroller.Offset = _scroller.Offset.WithY(top);
            else if (top + RowHeight > offset + viewport)
                _scroller.Offset = _scroller.Offset.WithY(top + RowHeight - viewport);
        }

        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // -- Rendering ------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DisplayCount == 0) return;

        double width = Bounds.Width;
        double totalHeight = DisplayCount * RowHeight;

        // Only the rows on screen are worth painting, plus one either side so a partially
        // scrolled row and the edges crossing it are still drawn.
        double viewTop = _scroller?.Offset.Y ?? 0;
        double viewHeight = _scroller?.Viewport.Height ?? Bounds.Height;
        int firstRow = Math.Max(0, (int)(viewTop / RowHeight) - 1);
        int lastRow = Math.Min(DisplayCount - 1, (int)((viewTop + viewHeight) / RowHeight) + 1);

        double graphWidth = GraphWidth;

        DrawRowBackgrounds(context, firstRow, lastRow, width);
        DrawEdges(context, firstRow, lastRow, totalHeight);
        DrawUncommittedStem(context, firstRow, lastRow);
        DrawDots(context, firstRow, lastRow);
        DrawText(context, firstRow, lastRow, graphWidth, width);
    }

    private void DrawRowBackgrounds(DrawingContext context, int firstRow, int lastRow, double width)
    {
        for (int row = firstRow; row <= lastRow; row++)
        {
            var rect = new Rect(0, row * RowHeight, width, RowHeight);
            if (row == _selected) context.FillRectangle(_selectionBrush, rect);
            else if (row % 2 == 1) context.FillRectangle(_stripeBrush, rect);
        }
    }

    private void DrawEdges(DrawingContext context, int firstRow, int lastRow, double totalHeight)
    {
        if (_hideLines) return;
        for (int e = 0; e < _graph.Edges.Count; e++)
        {
            var edge = _graph.Edges[e];
            // A parent outside the loaded range has no row; run its line off the bottom.
            int toRow = edge.ToRow >= 0 ? edge.ToRow + RowOffset : DisplayCount;
            int fromRow = edge.FromRow + RowOffset;
            if (toRow < firstRow || fromRow > lastRow) continue;

            double x1 = LaneX(edge.FromLane);
            double y1 = RowCenter(fromRow);
            double xt = LaneX(edge.TravelLane);
            double x2 = LaneX(edge.ToLane);
            double y2 = edge.ToRow >= 0 ? RowCenter(toRow) : totalHeight;

            context.DrawGeometry(null, ColorPen(EdgePens, _edgeColors[e]),
                BuildEdge(x1, y1, xt, x2, y2));
        }
    }

    /// <summary>
    /// A line from a commit to one of its parents. It turns into its travel lane over the row
    /// below the commit and out of it over the row above the parent, so a fork and a merge read
    /// differently at a glance and a line that had to step around a chain is seen doing it.
    /// </summary>
    private static StreamGeometry BuildEdge(double x1, double y1, double xt, double x2, double y2)
    {
        bool turnsOut = Math.Abs(x1 - xt) >= 0.5;
        bool turnsIn = Math.Abs(xt - x2) >= 0.5;

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(x1, y1), false);

        double top = y1;
        if (turnsOut)
        {
            top = Math.Min(y1 + RowHeight, y2);
            CurveTo(ctx, x1, y1, xt, top);
        }

        if (turnsIn)
        {
            double bottom = Math.Max(y2 - RowHeight, top);
            if (bottom > top) ctx.LineTo(new Point(xt, bottom));
            CurveTo(ctx, xt, bottom, x2, y2);
        }
        else
        {
            ctx.LineTo(new Point(x2, y2));
        }

        ctx.EndFigure(false);
        return geometry;
    }

    /// <summary>An S-bend from one lane into another, flat at both ends so it meets the runs.</summary>
    private static void CurveTo(StreamGeometryContext ctx, double x1, double y1, double x2, double y2)
    {
        ctx.CubicBezierTo(
            new Point(x1, y1 + (y2 - y1) * 0.55),
            new Point(x2, y2 - (y2 - y1) * 0.45),
            new Point(x2, y2));
    }

    /// <summary>Joins the working-tree row to the commit it sits on top of, with a dashed stem.</summary>
    private void DrawUncommittedStem(DrawingContext context, int firstRow, int lastRow)
    {
        if (!_showUncommitted || _graph.Rows.Count == 0) return;

        int headRow = Math.Max(HeadRow(), 0);
        int lane = _graph.Rows[headRow].Lane;
        int bottom = headRow + RowOffset;
        if (firstRow > bottom || lastRow < 0) return;

        double x = LaneX(lane);
        context.DrawLine(ColorPen(StemPens, _rowColors[headRow]),
            new Point(x, RowCenter(0)), new Point(x, RowCenter(bottom)));
    }

    private void DrawDots(DrawingContext context, int firstRow, int lastRow)
    {
        if (_showUncommitted && firstRow == 0)
        {
            int headRow = HeadRow();
            int lane = headRow >= 0 ? _graph.Rows[headRow].Lane : 0;
            int color = headRow >= 0 ? _rowColors[headRow] : 0;
            context.DrawEllipse(_dotFill, ColorPen(DotPens, color),
                new Point(LaneX(lane), RowCenter(0)), _dotRadius, _dotRadius);
        }

        for (int row = Math.Max(firstRow, RowOffset); row <= lastRow; row++)
        {
            var graphRow = _graph.Rows[row - RowOffset];
            int color = _rowColors[row - RowOffset];
            var center = new Point(LaneX(graphRow.Lane), RowCenter(row));

            // A merge gets a ring rather than a disc, so the joins stand out when scanning.
            if (graphRow.Node is GitCommit { IsMerge: true })
                context.DrawEllipse(_dotFill, ColorPen(RingPens, color), center,
                    _dotRadius, _dotRadius);
            else
                context.DrawEllipse(ColorBrush(color), null, center, _dotRadius, _dotRadius);
        }
    }

    private void DrawText(DrawingContext context, int firstRow, int lastRow, double graphWidth, double width)
    {
        double authorW = AuthorColumn;
        double dateW = DateColumn;
        double authorX = Math.Max(graphWidth, width - authorW - dateW - ColumnGap);
        double dateX = authorX + authorW;
        double subjectWidth = Math.Max(40, authorX - graphWidth - ColumnGap);

        for (int row = firstRow; row <= lastRow; row++)
        {
            double top = row * RowHeight;

            if (_showUncommitted && row == 0)
            {
                Draw(context, Loc.Get("GraphUncommitted", "Uncommitted Changes"), _uiBold, _dimBrush,
                    graphWidth, top, subjectWidth);
                continue;
            }

            if (_graph.Rows[row - RowOffset].Node is not GitCommit commit) continue;

            double x = graphWidth;
            double remaining = subjectWidth;

            foreach (var reference in commit.Refs)
            {
                if (remaining < 40) break;
                double used = DrawRefChip(context, reference, _rowColors[row - RowOffset], x, top, remaining);
                x += used;
                remaining -= used;
            }

            Draw(context, commit.Subject, _ui, _textBrush, x, top, remaining);
            if (_compactColumns) continue;

            Draw(context, commit.Author, _ui, _dimBrush, authorX, top, authorW - ColumnGap);
            // The listing is walked in commit-date order, so that is the date the column shows;
            // an author date would be out of sequence after any rebase or cherry-pick.
            Draw(context, FormatDate(commit.CommitDate), _ui, _dimBrush, dateX, top, dateW);
        }
    }

    /// <summary>Draws one branch, remote, or tag label and reports how much width it took.</summary>
    private double DrawRefChip(DrawingContext context, GitRef reference, int color, double x, double top, double maxWidth)
    {
        // Branches, local and remote alike, wear the colour of the line they start.
        var (brush, pen) = reference.Kind switch
        {
            GitRefKind.Tag => (_tagBrush, _tagPen),
            _ => (ColorBrush(color), ColorPen(ChipPens, color)),
        };

        var text = new FormattedText(reference.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _uiBold, FontSize - 1, Brushes.Black)
        {
            MaxTextWidth = Math.Max(20, maxWidth - 16),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        double chipWidth = text.WidthIncludingTrailingWhitespace + 10;
        double chipHeight = RowHeight - 8;
        var rect = new Rect(x, top + 4, chipWidth, chipHeight);

        // The checked-out branch is filled so HEAD is findable without reading every label.
        if (reference.IsHead)
        {
            context.DrawRectangle(brush, null, rect, 4, 4);
            text.SetForegroundBrush(_headChipText);
        }
        else
        {
            context.DrawRectangle(null, pen, rect, 4, 4);
            text.SetForegroundBrush(brush);
        }

        context.DrawText(text, new Point(x + 5, top + (RowHeight - text.Height) / 2));
        return chipWidth + 5;
    }

    private static void Draw(DrawingContext context, string value, Typeface typeface, IBrush brush,
                             double x, double top, double maxWidth)
    {
        if (string.IsNullOrEmpty(value) || maxWidth <= 0) return;

        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, FontSize, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        context.DrawText(text, new Point(x, top + (RowHeight - text.Height) / 2));
    }

    // -- Geometry helpers -----------------------------------------------

    private int HeadRow() => _graph.Rows.FindIndex(r =>
        r.Node is GitCommit commit && commit.Refs.Any(x => x.IsHead));

    private double LaneX(int lane) =>
        Math.Min(GraphPad + lane * _laneWidth + _laneWidth / 2, MaxGraphWidth - GraphPad);

    private static double RowCenter(int row) => row * RowHeight + RowHeight / 2;

    private static IBrush ColorBrush(int color) => LaneBrushes[color];

    private static Pen ColorPen(Pen[] pens, int color) => pens[color];

    private static string FormatDate(DateTimeOffset date) =>
        date == default ? "" : date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}
