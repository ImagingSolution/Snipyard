using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Snipyard.Services;

namespace Snipyard.Terminal;

/// <summary>
/// Find over the whole conversation while the Claude CLI holds the alternate screen.
/// The CLI keeps its own history and only ever shows us one screen of it, so the find bar
/// searches the session transcript instead (the same text the Chat View searches) and shows
/// a hit by drawing the transcript in the terminal's own look, scrolled to it, until the
/// find bar closes.
/// </summary>
public partial class TerminalControl
{
    /// <summary>The transcript of the session running in this window, if it is known yet.</summary>
    public Func<string?>? SessionPathProvider { get; set; }

    private enum HistoryKind : byte { Assistant, User, Tool }

    private readonly record struct HistoryRow(int Logical, int Start, string Text, HistoryKind Kind);

    private bool _historyMode;           // the find bar is searching the transcript
    private bool _historyShown;          // the transcript is drawn in place of the screen
    private readonly List<string> _histLogical = new();
    private readonly List<HistoryKind> _histKind = new();
    private readonly List<HistoryRow> _histRows = new();
    private readonly List<int> _histFirstRow = new();
    private readonly List<(int logical, int start, int length)> _histMatches = new();
    private int _histTop;
    private int _histCols;

    private int HistoryVisibleRows => Math.Max(1, (int)(TerminalAreaHeight / _cellHeight) - 1);

    /// <summary>Reads the transcript when the find bar opens over the CLI screen.</summary>
    private void PrepareHistorySearch()
    {
        _historyMode = false;
        _histLogical.Clear();
        _histKind.Clear();
        if (!_buffer.IsAltBuffer || _isDocumentView) return;

        string? path = null;
        try { path = SessionPathProvider?.Invoke(); } catch { }
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        List<ConversationMessage> messages;
        try { messages = SessionMessageReader.ReadSession(path); }
        catch { return; }

        foreach (var m in messages)
        {
            if (m.IsThinking) continue;
            var text = Controls.DocumentViewPanel.SearchableText(m).Replace("\r", "").Replace("\t", "    ");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var kind = m.Role == MessageRole.User ? HistoryKind.User
                : m.IsToolUse ? HistoryKind.Tool : HistoryKind.Assistant;
            string lead = kind == HistoryKind.User ? "> " : "● ";
            bool first = true;
            foreach (var line in text.Split('\n'))
            {
                _histLogical.Add((first ? lead : "  ") + line);
                _histKind.Add(kind);
                first = false;
            }
            _histLogical.Add("");
            _histKind.Add(HistoryKind.Assistant);
        }
        _historyMode = _histLogical.Count > 0;
        LayoutHistory();
    }

    /// <summary>Wraps the transcript lines to the terminal width, counting wide chars as two cells.</summary>
    private void LayoutHistory()
    {
        _histRows.Clear();
        _histFirstRow.Clear();
        _histCols = _buffer.Cols;
        int cols = Math.Max(10, _buffer.Cols);
        for (int li = 0; li < _histLogical.Count; li++)
        {
            var s = _histLogical[li];
            _histFirstRow.Add(_histRows.Count);
            int start = 0, width = 0, i = 0;
            while (i < s.Length)
            {
                int cp = CodePointAt(s, i, out int n);
                int w = TerminalBuffer.IsWideChar(cp) ? 2 : 1;
                if (width + w > cols)
                {
                    _histRows.Add(new HistoryRow(li, start, s.Substring(start, i - start), _histKind[li]));
                    start = i;
                    width = 0;
                }
                width += w;
                i += n;
            }
            _histRows.Add(new HistoryRow(li, start, s.Substring(start), _histKind[li]));
        }
    }

    /// <summary>
    /// Matches the query against each transcript line. A new query lands on the hit nearest
    /// the end, where the reader is, as the Chat View's find does.
    /// </summary>
    private void UpdateHistoryMatches(System.Text.RegularExpressions.Regex? regex, StringComparison comparison)
    {
        _histMatches.Clear();
        for (int li = 0; li < _histLogical.Count; li++)
        {
            var s = _histLogical[li];
            if (regex != null)
            {
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(s))
                    if (m.Length > 0) _histMatches.Add((li, m.Index, m.Length));
            }
            else
            {
                int idx = 0;
                while ((idx = s.IndexOf(_searchTerm, idx, comparison)) >= 0)
                {
                    _histMatches.Add((li, idx, _searchTerm.Length));
                    idx += _searchTerm.Length;
                }
            }
        }
        _searchCurrentIndex = _histMatches.Count > 0 ? _histMatches.Count - 1 : -1;
        ShowCurrentHistoryMatch();
    }

    private void ShowCurrentHistoryMatch()
    {
        if (_searchCurrentIndex < 0 || _searchCurrentIndex >= _histMatches.Count)
        {
            _historyShown = false;
            return;
        }
        var (li, start, _) = _histMatches[_searchCurrentIndex];
        int row = _histFirstRow[li];
        while (row + 1 < _histRows.Count && _histRows[row + 1].Logical == li && _histRows[row + 1].Start <= start)
            row++;

        int visible = HistoryVisibleRows;
        if (!_historyShown || row < _histTop || row >= _histTop + visible)
            _histTop = row - visible / 3;
        ClampHistoryTop();
        _historyShown = true;
    }

    private void ClampHistoryTop()
    {
        _histTop = Math.Clamp(_histTop, 0, Math.Max(0, _histRows.Count - HistoryVisibleRows));
    }

    private void EndHistorySearch()
    {
        _historyMode = false;
        _historyShown = false;
        _histMatches.Clear();
        _histLogical.Clear();
        _histKind.Clear();
        _histRows.Clear();
        _histFirstRow.Clear();
    }

    /// <summary>Wheel over the transcript scrolls it rather than the CLI underneath.</summary>
    private bool ScrollHistory(double deltaY)
    {
        if (!_historyShown) return false;
        _histTop -= (int)Math.Round(deltaY * 3);
        ClampHistoryTop();
        InvalidateVisual();
        return true;
    }

    /// <summary>Cell column at which char <paramref name="index"/> of <paramref name="s"/> is drawn.</summary>
    private static int HistoryColumnAt(string s, int index)
    {
        int col = 0, i = 0;
        while (i < index && i < s.Length)
        {
            int cp = CodePointAt(s, i, out int n);
            col += TerminalBuffer.IsWideChar(cp) ? 2 : 1;
            i += n;
        }
        return col;
    }

    private void RenderHistory(DrawingContext context, Color bgDefault, Color fgDefault, double termH)
    {
        if (_histCols != _buffer.Cols)
        {
            // The window was resized: wrap again and keep the hit in view
            LayoutHistory();
            _historyShown = false;
            ShowCurrentHistoryMatch();
            if (!_historyShown) return;
        }

        double viewW = TermViewWidth;
        context.FillRectangle(new SolidColorBrush(bgDefault), new Rect(0, 0, viewW, termH));
        var sepPen = new Pen(new SolidColorBrush(_isDark ? Color.FromRgb(56, 56, 58) : Color.FromRgb(198, 198, 200)), 0.5);
        context.DrawLine(sepPen, new Point(0, termH), new Point(viewW, termH));

        var userBg = new SolidColorBrush(_isDark ? Color.FromRgb(48, 48, 52) : Color.FromRgb(238, 238, 242));
        var fgBrush = new SolidColorBrush(fgDefault);
        var toolBrush = new SolidColorBrush(_isDark ? Color.FromRgb(140, 140, 148) : Color.FromRgb(110, 112, 118));
        var hlOther = new SolidColorBrush(Color.FromArgb(100, 200, 200, 50));
        var hlCurrent = new SolidColorBrush(Color.FromArgb(180, 230, 160, 0));

        int visible = HistoryVisibleRows;
        for (int k = 0; k < visible; k++)
        {
            int r = _histTop + k;
            if (r >= _histRows.Count) break;
            var row = _histRows[r];
            double y = k * _cellHeight;

            if (row.Kind == HistoryKind.User)
                context.FillRectangle(userBg, new Rect(0, y, viewW, _cellHeight));

            int rowEnd = row.Start + row.Text.Length;
            for (int m = 0; m < _histMatches.Count; m++)
            {
                var (li, start, length) = _histMatches[m];
                if (li != row.Logical) continue;
                int a = Math.Max(start, row.Start), b = Math.Min(start + length, rowEnd);
                if (a >= b) continue;
                int c0 = HistoryColumnAt(row.Text, a - row.Start);
                int c1 = HistoryColumnAt(row.Text, b - row.Start);
                context.FillRectangle(m == _searchCurrentIndex ? hlCurrent : hlOther,
                    new Rect(c0 * _cellWidth, y, (c1 - c0) * _cellWidth, _cellHeight));
            }

            var brush = row.Kind == HistoryKind.Tool ? toolBrush : fgBrush;
            int col = 0, i = 0;
            var s = row.Text;
            while (i < s.Length)
            {
                int cp = CodePointAt(s, i, out int n);
                bool wide = TerminalBuffer.IsWideChar(cp);
                if (cp > ' ')
                {
                    var ft = new FormattedText(s.Substring(i, n), CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, _typeface, _fontSize, brush);
                    DrawGlyph(context, ft, col * _cellWidth, y, (wide ? 2 : 1) * _cellWidth);
                }
                col += wide ? 2 : 1;
                i += n;
            }
        }

        // The last row says this is the transcript, not the live screen
        double by = visible * _cellHeight;
        var banner = new FormattedText(Loc.Get("TermHistoryBanner"), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize, toolBrush);
        context.DrawText(banner, new Point(Math.Max(0, (viewW - banner.Width) / 2), by + (_cellHeight - banner.Height) / 2));
    }

    /// <summary>The code point at <paramref name="i"/> and how many chars it takes.</summary>
    private static int CodePointAt(string s, int i, out int length)
    {
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(s[i], s[i + 1]);
        }
        length = 1;
        return s[i];
    }
}
