using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Snipyard.Services;

namespace Snipyard.Controls;

/// <summary>
/// Standalone window that shows unified-diff-style text (as produced by
/// <see cref="GitChangeService.GetDiffAsync"/>) with basic +/-/@@ syntax coloring.
/// Renders each line as a virtualized row so large diffs stay responsive.
///
/// Lines can be selected, and a selection can be sent to the session as
/// "@path:12-20 &lt;comment&gt;" - the whole point of reading a diff here is usually to say
/// something about it, and that used to mean copying the text out by hand.
/// </summary>
public class DiffWindow : Window
{
    /// <summary>
    /// One rendered line. <see cref="File"/> and <see cref="Line"/> are where the line sits in
    /// the file, which is what a comment has to name; headers and hunk markers belong to no
    /// line of their own and leave it at zero.
    /// </summary>
    private sealed record DiffRow(string Text, string? File, int Line);

    /// <summary>Only the new-file side matters: a comment is about the file as it stands.</summary>
    private static readonly Regex HunkHeader =
        new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    private static readonly Thickness LinePadding = new(8, 0, 24, 0);

    private readonly bool _isDark;
    private readonly Typeface _typeface;
    private readonly string _diffText;
    private readonly string? _fallbackPath;
    private readonly Action<string>? _sendComment;
    private readonly List<DiffRow> _rows;
    private readonly double _widestLine;

    private readonly ListBox _list;
    private Button? _commentBtn;
    private Border? _commentPanel;
    private TextBlock? _referenceLabel;
    private TextBox? _commentBox;

    /// <param name="filePath">
    /// The file the diff is of, used when the text carries no "+++ b/&lt;path&gt;" header of its
    /// own. Without it a comment would have nothing to point at.
    /// </param>
    /// <param name="sendComment">
    /// Where a comment goes. Null leaves the window read-only, as it was.
    /// </param>
    public DiffWindow(string title, string diffText, bool isDark, Typeface typeface,
        string? filePath = null, Action<string>? sendComment = null)
    {
        _isDark = isDark;
        _typeface = typeface;
        _diffText = diffText ?? "";
        _fallbackPath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Replace('\\', '/');
        _sendComment = sendComment;

        Title = title;
        Width = 900;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(isDark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(255, 255, 255));

        var lines = _diffText.Replace("\r\n", "\n").Split('\n');
        _rows = ParseRows(lines);
        _widestLine = MeasureWidestLine(lines);

        var copyBtn = CreateToolButton(Loc.Get("Copy", "Copy"));
        copyBtn.Click += async (_, _) => await CopyToClipboard();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 6),
        };
        toolbar.Children.Add(copyBtn);

        if (_sendComment != null)
        {
            _commentBtn = CreateToolButton(Loc.Get("DiffComment", "Comment"));
            _commentBtn.IsEnabled = false;
            ToolTip.SetTip(_commentBtn, Loc.Get("DiffCommentSelectLines", "Select lines in the diff first"));
            _commentBtn.Click += (_, _) => ToggleCommentPanel();
            toolbar.Children.Add(_commentBtn);
        }

        var toolbarBorder = new Border
        {
            Child = toolbar,
            BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        // A ListBox rather than a bare ItemsControl: it brings the ScrollViewer its template
        // needs, keyboard navigation, and click / Shift+click / Ctrl+click range selection,
        // none of which is worth hand-rolling here.
        _list = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = new FuncDataTemplate<DiffRow>((row, _) => BuildLineControl(row), true),
            SelectionMode = SelectionMode.Multiple,
            Background = new SolidColorBrush(isDark ? Color.FromRgb(24, 24, 26) : Color.FromRgb(250, 250, 252)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        StyleRows(_list);
        _list.SelectionChanged += (_, _) => OnSelectionChanged();

        var dock = new DockPanel();
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        dock.Children.Add(toolbarBorder);

        if (_sendComment != null)
        {
            _commentPanel = BuildCommentPanel();
            DockPanel.SetDock(_commentPanel, Dock.Bottom);
            dock.Children.Add(_commentPanel);
        }

        dock.Children.Add(_list);
        Content = dock;

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            // Escape backs out of the comment first: closing the whole window would throw away
            // text that has just been typed.
            if (_commentPanel is { IsVisible: true })
            {
                CloseCommentPanel();
                e.Handled = true;
                return;
            }
            Close();
        };
    }

    // ── Diff model ──

    /// <summary>
    /// Walks the diff once, carrying the current file and new-file line number forward, so
    /// every row knows what it would be called in an "@path:line" reference.
    /// </summary>
    private List<DiffRow> ParseRows(string[] lines)
    {
        var rows = new List<DiffRow>(lines.Length);
        string? file = _fallbackPath;
        string? oldPath = null;
        int newLine = 0;
        bool inHunk = false;

        foreach (var raw in lines)
        {
            if (raw.StartsWith("--- "))
            {
                var p = StripSidePrefix(raw[4..].Trim());
                oldPath = p;
                inHunk = false;
                rows.Add(new DiffRow(raw, file, 0));
                continue;
            }

            if (raw.StartsWith("+++ "))
            {
                var p = StripSidePrefix(raw[4..].Trim());
                // A deletion has no new side; the file is still the one that was removed.
                file = p ?? oldPath ?? file;
                inHunk = false;
                rows.Add(new DiffRow(raw, file, 0));
                continue;
            }

            if (raw.StartsWith("diff ") || raw.StartsWith("index ")
                || raw.StartsWith("new file") || raw.StartsWith("deleted file")
                || raw.StartsWith("similarity ") || raw.StartsWith("rename "))
            {
                inHunk = false;
                rows.Add(new DiffRow(raw, file, 0));
                continue;
            }

            var hunk = HunkHeader.Match(raw);
            if (hunk.Success)
            {
                newLine = int.Parse(hunk.Groups[1].Value);
                inHunk = true;
                rows.Add(new DiffRow(raw, file, 0));
                continue;
            }

            // "\ No newline at end of file" and anything outside a hunk occupy no line.
            if (!inHunk || raw.Length == 0 || raw[0] == '\\')
            {
                rows.Add(new DiffRow(raw, file, 0));
                continue;
            }

            rows.Add(new DiffRow(raw, file, newLine));

            // A removed line is anchored to where it used to be but does not advance the new
            // side; context and additions both do.
            if (raw[0] != '-') newLine++;
        }

        return rows;
    }

    /// <summary>"b/Services/GitCli.cs" as the file it names, or null for /dev/null.</summary>
    private static string? StripSidePrefix(string path)
    {
        if (path.Length == 0 || path == "/dev/null") return null;
        if (path.StartsWith("a/") || path.StartsWith("b/")) return path[2..];
        return path;
    }

    // ── Selection ──

    private void OnSelectionChanged()
    {
        bool any = HasSelection();
        if (_commentBtn != null) _commentBtn.IsEnabled = any;

        if (_commentPanel is { IsVisible: true })
        {
            if (any) UpdateReferenceLabel();
            else CloseCommentPanel();
        }
    }

    private bool HasSelection()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_list.Selection.IsSelected(i)) return true;
        return false;
    }

    /// <summary>
    /// The selected lines as one range per file, in the order they appear. A selection that
    /// crosses files in a commit-wide diff therefore names each of them.
    /// </summary>
    private List<(string File, int First, int Last)> SelectedRanges()
    {
        var ranges = new List<(string File, int First, int Last)>();

        for (int i = 0; i < _rows.Count; i++)
        {
            if (!_list.Selection.IsSelected(i)) continue;

            var row = _rows[i];
            var file = row.File ?? _fallbackPath;
            if (string.IsNullOrEmpty(file)) continue;

            if (ranges.Count > 0 && ranges[^1].File == file)
            {
                if (row.Line == 0) continue;
                var last = ranges[^1];
                ranges[^1] = (file,
                    last.First == 0 ? row.Line : Math.Min(last.First, row.Line),
                    Math.Max(last.Last, row.Line));
            }
            else
            {
                ranges.Add((file, row.Line, row.Line));
            }
        }

        return ranges;
    }

    /// <summary>"@Services/GitCli.cs:120-134", or just the file when only a header is selected.</summary>
    private string BuildReference()
    {
        var parts = new List<string>();
        foreach (var (file, first, last) in SelectedRanges())
        {
            if (first == 0) parts.Add("@" + file);
            else if (first == last) parts.Add($"@{file}:{first}");
            else parts.Add($"@{file}:{first}-{last}");
        }
        return string.Join(" ", parts);
    }

    private string SelectedText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _rows.Count; i++)
        {
            if (!_list.Selection.IsSelected(i)) continue;
            sb.Append(_rows[i].Text).Append('\n');
        }
        return sb.ToString();
    }

    // ── Comment ──

    private Border BuildCommentPanel()
    {
        _referenceLabel = new TextBlock
        {
            FontFamily = _typeface.FontFamily,
            FontSize = 11,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 5),
        };

        _commentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
            MaxHeight = 140,
            FontSize = 12,
            PlaceholderText = Loc.Get("DiffCommentHint", "What should change in the selected lines?"),
        };
        _commentBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Shortcut.HasPrimary(e.KeyModifiers))
            {
                SendComment();
                e.Handled = true;
            }
        };

        var sendBtn = CreateToolButton(Loc.Get("SendToConsole"));
        sendBtn.Click += (_, _) => SendComment();

        var cancelBtn = CreateToolButton(Loc.Get("Cancel", "Cancel"));
        cancelBtn.Click += (_, _) => CloseCommentPanel();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.Add(sendBtn);
        buttons.Children.Add(cancelBtn);

        var stack = new StackPanel { Margin = new Thickness(10, 8) };
        stack.Children.Add(_referenceLabel);
        stack.Children.Add(_commentBox);
        stack.Children.Add(buttons);

        return new Border
        {
            Child = stack,
            IsVisible = false,
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(32, 32, 34) : Color.FromRgb(244, 244, 247)),
            BorderBrush = new SolidColorBrush(_isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(210, 210, 215)),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
    }

    private void ToggleCommentPanel()
    {
        if (_commentPanel == null) return;

        if (_commentPanel.IsVisible)
        {
            CloseCommentPanel();
            return;
        }

        UpdateReferenceLabel();
        _commentPanel.IsVisible = true;
        _commentBox?.Focus();
    }

    private void CloseCommentPanel()
    {
        if (_commentPanel == null) return;
        _commentPanel.IsVisible = false;
        _list.Focus();
    }

    private void UpdateReferenceLabel()
    {
        if (_referenceLabel == null) return;
        var reference = BuildReference();
        _referenceLabel.Text = reference.Length > 0
            ? reference
            : Loc.Get("DiffCommentSelectLines", "Select lines in the diff first");
    }

    /// <summary>
    /// Hands the session the reference and the comment as one line. One line because the raw
    /// terminal reads a newline as "submit", and half a sentence is worse than none.
    /// </summary>
    private void SendComment()
    {
        if (_sendComment == null) return;

        var reference = BuildReference();
        if (reference.Length == 0) return;

        var comment = (_commentBox?.Text ?? "").Trim();
        comment = Regex.Replace(comment, @"\s*\r?\n\s*", " ");

        _sendComment(comment.Length > 0 ? reference + " " + comment : reference + " ");

        if (_commentBox != null) _commentBox.Text = "";
        CloseCommentPanel();
    }

    // ── Rendering ──

    /// <summary>
    /// How wide the longest line renders, plus the padding a row carries. The font is
    /// monospaced, so the longest line by character count is also the widest one, and only that
    /// one has to be measured.
    /// </summary>
    private double MeasureWidestLine(string[] lines)
    {
        var widest = "";
        foreach (var line in lines)
        {
            if (line.Length > widest.Length) widest = line;
        }
        if (widest.Length == 0) return 0;

        var text = new FormattedText(
            widest,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            _typeface,
            12,
            Brushes.White);

        return text.WidthIncludingTrailingWhitespace + LinePadding.Left + LinePadding.Right;
    }

    private Control BuildLineControl(DiffRow? row)
    {
        var line = row?.Text ?? "";
        return new TextBlock
        {
            Text = line.Length == 0 ? " " : line,
            FontFamily = _typeface.FontFamily,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(LineColor(line)),
            Padding = LinePadding,
            // Rows are virtualized, so the panel only knows how wide the lines it has realized
            // are: left alone, the horizontal extent would grow and shrink as the user scrolls
            // past long lines and the view would jump sideways. Giving every row the width of
            // the longest one pins it, and lets a selection highlight span the full width.
            MinWidth = _widestLine,
        };
    }

    /// <summary>
    /// Strips the padding and rounding a list row normally carries - a diff is a block of text,
    /// not a list of cards - and states the selection colours, which have to read against the
    /// diff's own green and red in both themes.
    /// </summary>
    private void StyleRows(ListBox list)
    {
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(0)),
            },
        });

        var hover = new SolidColorBrush(_isDark
            ? Color.FromArgb(22, 255, 255, 255)
            : Color.FromArgb(16, 0, 0, 0));
        var selected = new SolidColorBrush(_isDark
            ? Color.FromArgb(90, 10, 132, 255)
            : Color.FromArgb(60, 10, 132, 255));

        // The theme states each of these separately, so each has to be answered separately or
        // the highlight would disappear the moment the pointer moved over a selected row.
        list.Styles.Add(RowBackground(x => x.OfType<ListBoxItem>().Class(":pointerover"), hover));
        list.Styles.Add(RowBackground(x => x.OfType<ListBoxItem>().Class(":selected"), selected));
        list.Styles.Add(RowBackground(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pointerover"), selected));
        list.Styles.Add(RowBackground(x => x.OfType<ListBoxItem>().Class(":selected").Class(":focus"), selected));
    }

    private static Style RowBackground(Func<Selector?, Selector> match, IBrush brush)
        => new(x => match(x).Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, brush) },
        };

    private Color LineColor(string line)
    {
        if (line.StartsWith("diff ") || line.StartsWith("index ") ||
            line.StartsWith("---") || line.StartsWith("+++"))
            return _isDark ? Color.FromRgb(140, 140, 145) : Color.FromRgb(108, 108, 116);

        if (line.StartsWith("@@"))
            return _isDark ? Color.FromRgb(100, 180, 255) : Color.FromRgb(0, 110, 220);

        if (line.StartsWith("+"))
            return Color.FromRgb(0x30, 0xD1, 0x58);

        if (line.StartsWith("-"))
            return Color.FromRgb(0xFF, 0x45, 0x3A);

        return _isDark ? Color.FromRgb(220, 220, 225) : Color.FromRgb(28, 28, 30);
    }

    /// <summary>Copies the selected lines, or the whole diff when nothing is selected.</summary>
    private async System.Threading.Tasks.Task CopyToClipboard()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            var selected = SelectedText();
            await clipboard.SetTextAsync(selected.Length > 0 ? selected : _diffText);
        }
        catch { }
    }

    private Button CreateToolButton(string text)
    {
        return new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4),
            Background = new SolidColorBrush(_isDark ? Color.FromRgb(50, 50, 52) : Color.FromRgb(230, 230, 235)),
            Foreground = new SolidColorBrush(_isDark ? Color.FromRgb(210, 210, 215) : Color.FromRgb(28, 28, 30)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 6, 0),
        };
    }
}
