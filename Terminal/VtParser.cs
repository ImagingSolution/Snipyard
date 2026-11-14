using System;
using System.Collections.Generic;
using System.Text;

namespace Snipyard.Terminal;

public class VtParser
{
    private readonly TerminalBuffer _buffer;
    private ParserState _state = ParserState.Normal;
    private readonly StringBuilder _escBuffer = new();
    private readonly StringBuilder _oscBuffer = new();
    private string _title = "";

    // UTF-8 decoding state
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

    // Lead half of a surrogate pair waiting for its trail (see ProcessChar). 0 = none.
    private int _pendingHighSurrogate;

    public string Title => _title;
    public event Action<string>? TitleChanged;

    private enum ParserState
    {
        Normal,
        Escape,
        Csi,
        Osc,
        OscEsc,
        Dcs,
        DcsEsc
    }

    public VtParser(TerminalBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Process(ReadOnlySpan<byte> data)
    {
        // Decode UTF-8 bytes to chars, then process each char
        var charBuf = new char[data.Length * 2];
        int charCount = _utf8Decoder.GetChars(data, charBuf, flush: false);
        for (int i = 0; i < charCount; i++)
        {
            ProcessChar(charBuf[i]);
        }
        _buffer.NotifyChanged();
    }

    public void Process(string text)
    {
        foreach (char c in text)
        {
            ProcessChar(c);
        }
        _buffer.NotifyChanged();
    }

    /// <summary>
    /// Recombines UTF-16 surrogate pairs before dispatching. A code point above the BMP —
    /// every emoji — decodes to two UTF-16 units, and the grid stores whole code points:
    /// feeding the halves through separately would leave two lone surrogates in the grid,
    /// each of which renders as U+FFFD (the "&#xFFFD;&#xFFFD;" mojibake).
    /// </summary>
    private void ProcessChar(int b)
    {
        if (_pendingHighSurrogate != 0)
        {
            int high = _pendingHighSurrogate;
            _pendingHighSurrogate = 0;
            if (b >= 0xDC00 && b <= 0xDFFF)
            {
                Dispatch(char.ConvertToUtf32((char)high, (char)b));
                return;
            }
            Dispatch(high);   // unpaired lead: pass it through, then handle b normally
        }

        // Only hold a lead surrogate back in the printable path; escape/OSC/DCS payloads
        // are collected as raw UTF-16 units and must not be merged.
        if (b >= 0xD800 && b <= 0xDBFF && _state == ParserState.Normal)
        {
            _pendingHighSurrogate = b;
            return;
        }

        Dispatch(b);
    }

    private void Dispatch(int b)
    {
        switch (_state)
        {
            case ParserState.Normal:
                ProcessNormal(b);
                break;
            case ParserState.Escape:
                ProcessEscape(b);
                break;
            case ParserState.Csi:
                ProcessCsi(b);
                break;
            case ParserState.Osc:
                ProcessOsc(b);
                break;
            case ParserState.OscEsc:
                if (b == '\\')
                {
                    HandleOsc(_oscBuffer.ToString());
                    _state = ParserState.Normal;
                }
                else
                {
                    _state = ParserState.Normal;
                }
                break;
            case ParserState.Dcs:
                if (b == 0x1B)
                    _state = ParserState.DcsEsc;
                break;
            case ParserState.DcsEsc:
                _state = ParserState.Normal;
                break;
        }
    }

    private void ProcessNormal(int b)
    {
        switch (b)
        {
            case 0x1B: // ESC
                _state = ParserState.Escape;
                _escBuffer.Clear();
                break;
            case '\n':
                _buffer.LineFeed();
                break;
            case '\r':
                _buffer.CarriageReturn();
                break;
            case '\b':
                _buffer.Backspace();
                break;
            case '\t':
                _buffer.Tab();
                break;
            case 0x07: // BEL
                break;
            default:
                if (b >= 0x20)
                    _buffer.WriteChar(b);
                break;
        }
    }

    private void ProcessEscape(int b)
    {
        switch (b)
        {
            case '[': // CSI
                _state = ParserState.Csi;
                _escBuffer.Clear();
                break;
            case ']': // OSC
                _state = ParserState.Osc;
                _oscBuffer.Clear();
                break;
            case 'P': // DCS
                _state = ParserState.Dcs;
                break;
            case '7': // Save cursor (DECSC)
                _buffer.SaveCursor();
                _state = ParserState.Normal;
                break;
            case '8': // Restore cursor (DECRC)
                _buffer.RestoreCursor();
                _state = ParserState.Normal;
                break;
            case 'M': // Reverse index
                _buffer.ReverseLineFeed();
                _state = ParserState.Normal;
                break;
            case 'D': // Index (linefeed)
                _buffer.LineFeed();
                _state = ParserState.Normal;
                break;
            case 'E': // Next line
                _buffer.CarriageReturn();
                _buffer.LineFeed();
                _state = ParserState.Normal;
                break;
            case 'c': // Full reset
                _buffer.ClearAll();
                _buffer.CursorRow = 0;
                _buffer.CursorCol = 0;
                _buffer.CurrentFg = -1;
                _buffer.CurrentBg = -1;
                _buffer.CurrentAttrs = CellAttributes.None;
                _state = ParserState.Normal;
                break;
            case '(': case ')': case '*': case '+': // Character set designation - consume next byte
                _state = ParserState.Normal; // simplified: skip
                break;
            case '=': case '>': // Keypad modes
                _state = ParserState.Normal;
                break;
            default:
                _state = ParserState.Normal;
                break;
        }
    }

    private void ProcessCsi(int b)
    {
        if (b >= 0x30 && b < 0x40 || b == ';' || b == '?' || b == '>' || b == '!' || b == ' ')
        {
            _escBuffer.Append((char)b);
            return;
        }

        // Final byte
        char final_ = (char)b;
        string paramStr = _escBuffer.ToString();
        _state = ParserState.Normal;

        bool isPrivate = paramStr.StartsWith('?');
        if (isPrivate) paramStr = paramStr[1..];

        // '>', '<' and '=' prefixed sequences are keyboard-protocol and query controls
        // (xterm modifyOtherKeys "CSI > 4;2 m", kitty "CSI > 1 u" / "CSI < u", DA2/DA3,
        // XTVERSION), none of which touch the screen. Running them through the plain
        // dispatch would turn "CSI > 4;2 m" into SGR underline+dim and "CSI > 1 u" into a
        // cursor restore. ConPTY strips them on Windows; a Unix pty passes them through.
        if (paramStr.Length > 0 && paramStr[0] is '>' or '<' or '=')
            return;

        // Handle space-prefixed sequences (e.g., "0 q" for cursor style)
        if (paramStr.EndsWith(' '))
        {
            // Cursor style etc., ignore for now
            return;
        }

        var ps = ParseParams(paramStr);

        if (isPrivate)
        {
            HandlePrivateCsi(final_, ps);
            return;
        }

        switch (final_)
        {
            case 'A': // Cursor up
                _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'B': // Cursor down
                _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'C': // Cursor forward
                _buffer.CursorCol = Math.Min(_buffer.Cols - 1, _buffer.CursorCol + Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'D': // Cursor back
                _buffer.CursorCol = Math.Max(0, _buffer.CursorCol - Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'E': // Cursor next line
                _buffer.CursorCol = 0;
                _buffer.CursorRow = Math.Min(_buffer.Rows - 1, _buffer.CursorRow + Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'F': // Cursor previous line
                _buffer.CursorCol = 0;
                _buffer.CursorRow = Math.Max(0, _buffer.CursorRow - Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'G': // Cursor horizontal absolute
                _buffer.CursorCol = Math.Clamp(Param(ps, 0, 1) - 1, 0, _buffer.Cols - 1);
                break;
            case 'H': // Cursor position
            case 'f':
                _buffer.CursorRow = Math.Clamp(Param(ps, 0, 1) - 1, 0, _buffer.Rows - 1);
                _buffer.CursorCol = Math.Clamp(Param(ps, 1, 1) - 1, 0, _buffer.Cols - 1);
                break;
            case 'J': // Erase in display
                _buffer.EraseInDisplay(Param(ps, 0, 0));
                break;
            case 'K': // Erase in line
                _buffer.EraseInLine(Param(ps, 0, 0));
                break;
            case 'L': // Insert lines
                _buffer.InsertLines(Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'M': // Delete lines
                _buffer.DeleteLines(Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'P': // Delete characters
                _buffer.DeleteChars(Math.Max(1, Param(ps, 0, 1)));
                break;
            case '@': // Insert characters
                _buffer.InsertChars(Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'X': // Erase characters
                _buffer.EraseChars(Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'S': // Scroll up
                _buffer.ScrollUp(Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'T': // Scroll down
                _buffer.ScrollDown(Math.Max(1, Param(ps, 0, 1)));
                break;
            case 'd': // Cursor vertical absolute
                _buffer.CursorRow = Math.Clamp(Param(ps, 0, 1) - 1, 0, _buffer.Rows - 1);
                break;
            case 'm': // SGR
                HandleSgr(ps);
                break;
            case 'r': // Set scroll region
                _buffer.SetScrollRegion(Param(ps, 0, 1) - 1, Param(ps, 1, _buffer.Rows) - 1);
                _buffer.CursorRow = 0;
                _buffer.CursorCol = 0;
                break;
            case 's': // Save cursor
                _buffer.SaveCursor();
                break;
            case 'u': // Restore cursor
                _buffer.RestoreCursor();
                break;
            case 'n': // Device status report
                // We'd need to write back to the PTY - skip for now
                break;
            case 'c': // Device attributes
                break;
            case 't': // Window manipulation - ignore
                break;
            case 'q': // Cursor style (with space prefix, already handled)
                break;
        }
    }

    private void HandlePrivateCsi(char final_, List<int> ps)
    {
        switch (final_)
        {
            case 'h': // Set mode
                foreach (var p in ps)
                {
                    switch (p)
                    {
                        case 25: _buffer.CursorVisible = true; break;
                        case 2004: _buffer.BracketedPasteMode = true; break;
                        case 1049: // Alt screen buffer
                        case 47:
                        case 1047:
                            _buffer.SwitchToAltBuffer();
                            break;
                        case 7: // Auto-wrap mode - always on
                            break;
                    }
                }
                break;
            case 'l': // Reset mode
                foreach (var p in ps)
                {
                    switch (p)
                    {
                        case 25: _buffer.CursorVisible = false; break;
                        case 2004: _buffer.BracketedPasteMode = false; break;
                        case 1049:
                        case 47:
                        case 1047:
                            _buffer.SwitchToMainBuffer();
                            break;
                    }
                }
                break;
        }
    }

    private void HandleSgr(List<int> ps)
    {
        if (ps.Count == 0) ps.Add(0);

        for (int i = 0; i < ps.Count; i++)
        {
            int p = ps[i];
            switch (p)
            {
                case 0: // Reset
                    _buffer.CurrentFg = -1;
                    _buffer.CurrentBg = -1;
                    _buffer.CurrentAttrs = CellAttributes.None;
                    break;
                case 1: _buffer.CurrentAttrs |= CellAttributes.Bold; break;
                case 2: _buffer.CurrentAttrs |= CellAttributes.Dim; break;
                case 3: _buffer.CurrentAttrs |= CellAttributes.Italic; break;
                case 4: _buffer.CurrentAttrs |= CellAttributes.Underline; break;
                case 7: _buffer.CurrentAttrs |= CellAttributes.Inverse; break;
                case 21: case 22: _buffer.CurrentAttrs &= ~(CellAttributes.Bold | CellAttributes.Dim); break;
                case 23: _buffer.CurrentAttrs &= ~CellAttributes.Italic; break;
                case 24: _buffer.CurrentAttrs &= ~CellAttributes.Underline; break;
                case 27: _buffer.CurrentAttrs &= ~CellAttributes.Inverse; break;
                case >= 30 and <= 37:
                    _buffer.CurrentFg = p - 30;
                    break;
                case 38: // Extended foreground
                    i = ParseExtendedColor(ps, i, out int fg);
                    _buffer.CurrentFg = fg;
                    break;
                case 39: _buffer.CurrentFg = -1; break;
                case >= 40 and <= 47:
                    _buffer.CurrentBg = p - 40;
                    break;
                case 48: // Extended background
                    i = ParseExtendedColor(ps, i, out int bg);
                    _buffer.CurrentBg = bg;
                    break;
                case 49: _buffer.CurrentBg = -1; break;
                case >= 90 and <= 97:
                    _buffer.CurrentFg = p - 90 + 8; // bright colors
                    break;
                case >= 100 and <= 107:
                    _buffer.CurrentBg = p - 100 + 8; // bright colors
                    break;
            }
        }
    }

    private int ParseExtendedColor(List<int> ps, int i, out int color)
    {
        color = -1;
        if (i + 1 < ps.Count)
        {
            if (ps[i + 1] == 5 && i + 2 < ps.Count)
            {
                // 256-color: 38;5;n
                color = ps[i + 2];
                return i + 2;
            }
            else if (ps[i + 1] == 2 && i + 4 < ps.Count)
            {
                // True color: 38;2;r;g;b → encode as 0x01RRGGBB (flag bit 24)
                int r = ps[i + 2], g = ps[i + 3], b = ps[i + 4];
                color = 0x01000000 | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
                return i + 4;
            }
        }
        return i;
    }

    private void ProcessOsc(int b)
    {
        if (b == 0x07) // BEL terminates OSC
        {
            HandleOsc(_oscBuffer.ToString());
            _state = ParserState.Normal;
        }
        else if (b == 0x1B) // ESC might start ST
        {
            _state = ParserState.OscEsc;
        }
        else
        {
            _oscBuffer.Append((char)b);
        }
    }

    private void HandleOsc(string data)
    {
        // OSC 0/2: Set title
        int semi = data.IndexOf(';');
        if (semi >= 0)
        {
            string numStr = data[..semi];
            string value = data[(semi + 1)..];
            if (int.TryParse(numStr, out int num) && (num == 0 || num == 2))
            {
                _title = value;
                TitleChanged?.Invoke(value);
            }
        }
    }

    private static List<int> ParseParams(string s)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(s))
            return result;
        foreach (var part in s.Split(';'))
        {
            result.Add(int.TryParse(part, out int v) ? v : 0);
        }
        return result;
    }

    private static int Param(List<int> ps, int index, int defaultValue)
    {
        if (index < ps.Count && ps[index] != 0)
            return ps[index];
        return defaultValue;
    }
}
