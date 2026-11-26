using System.Globalization;
using System.Text;

namespace ThixGate.Core.Terminal;

/// <summary>One cell of the terminal: a character (or a wide character's right half: '\0') with its colours and style.</summary>
public readonly record struct TerminalCell(char Char, int Foreground, int Background, CellStyle Style)
{
    /// <summary>A colour: -1 = the default; 0…255 = the xterm palette; 0x1000000 | rgb = true colour.</summary>
    public const int Default = -1;

    public const int TrueColor = 0x1000000;

    public static TerminalCell Blank(int background = Default) => new(' ', Default, background, CellStyle.None);
}

[Flags]
public enum CellStyle : byte
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Strikethrough = 32,
    Invisible = 64,
}

/// <summary>
/// The screen of a terminal as an xterm ("xterm-256color") sees it: it reads what the server sends (UTF-8 text, control characters and
/// escape sequences: cursor movement, erasing, colours with 16/256/true colour, scroll regions, insert/delete, the alternate screen of full
/// screen programs, modes) and keeps the cells, the cursor and the lines scrolled off the top. It answers questions of the server (cursor
/// position, device attributes) through <see cref="Reply"/>. No UI: the view draws <see cref="Row"/>.
/// </summary>
public sealed class TerminalBuffer
{
    /// <summary>How many lines scrolled off the top are kept.</summary>
    public const int ScrollbackLimit = 5000;

    private readonly List<TerminalCell[]> _scrollback = [];
    private readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _sequence = new();
    private readonly char[] _chars = new char[8192];
    private TerminalCell[][] _main;
    private TerminalCell[][]? _alternate;
    private bool[] _wrapped;
    private State _state = State.Text;
    private int _foreground = TerminalCell.Default;
    private int _background = TerminalCell.Default;
    private CellStyle _style;
    private int _top;
    private int _bottom;
    private bool _wrapPending;
    private (int Column, int Row, int Foreground, int Background, CellStyle Style, bool Origin) _saved;
    private bool[] _tabStops;
    private bool _lineDrawing;

    public TerminalBuffer(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        _main = NewScreen(Columns, Rows);
        _wrapped = new bool[Rows];
        _tabStops = DefaultTabs(Columns);
        _bottom = Rows - 1;
    }

    private enum State
    {
        Text,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        Ignore,
        IgnoreEscape,
    }

    /// <summary>Text for the server: answers to its questions (cursor position, device attributes).</summary>
    public event Action<string>? Reply;

    /// <summary>The window title the server set (OSC 0/2).</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The server rang the bell.</summary>
    public event Action? Bell;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int CursorColumn { get; private set; }

    public int CursorRow { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    /// <summary>The alternate screen of a full screen program (vim, less, htop) is shown: no scrollback then.</summary>
    public bool IsAlternateScreen => _alternate is not null;

    /// <summary>Cursor keys send SS3 (ESC O A) instead of CSI (ESC [ A) (DECCKM).</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>Pasted text is wrapped in ESC [200~ … ESC [201~.</summary>
    public bool BracketedPaste { get; private set; }

    public bool AutoWrap { get; private set; } = true;

    public bool OriginMode { get; private set; }

    public bool ReverseVideo { get; private set; }

    /// <summary>Which mouse reports the server wants (0: none, 1000: clicks, 1002: + drags, 1003: every move).</summary>
    public int MouseTracking { get; private set; }

    /// <summary>Mouse reports in SGR form (1006).</summary>
    public bool SgrMouse { get; private set; }

    public string Title { get; private set; } = "";

    /// <summary>Lines scrolled off the top (oldest first).</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Changes every time something visible changes (the view redraws then).</summary>
    public long Version { get; private set; }

    private TerminalCell[][] Screen => _alternate ?? _main;

    /// <summary>A row: 0…Rows-1 on the screen; negative rows are the scrollback (-1 = the newest line above the screen).</summary>
    public TerminalCell[] Row(int row) => row >= 0 ? Screen[row] : _scrollback[_scrollback.Count + row];

    /// <summary>The text of a screen row (without the trailing blanks) - tests, copying.</summary>
    public string Text(int row)
    {
        var builder = new StringBuilder();
        foreach (var cell in Row(row))
        {
            if (cell.Char != '\0')
            {
                builder.Append(cell.Char);
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>The screen as text, one line per row (tests).</summary>
    public string ScreenText() => string.Join('\n', Enumerable.Range(0, Rows).Select(Text)).TrimEnd('\n');

    /// <summary>Whether a row continues on the next one (it was wrapped, not ended): copying joins them.</summary>
    public bool IsWrapped(int row) => row >= 0 && row < Rows && _wrapped[row];

    // ================================================================= input from the server

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            var take = Math.Min(bytes.Length, 4096);
            var count = _utf8.GetChars(bytes[..take], _chars, flush: false);
            Feed(_chars.AsSpan(0, count));
            bytes = bytes[take..];
        }
    }

    public void Feed(string text) => Feed(text.AsSpan());

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            Step(c);
        }

        Version++;
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case State.Text:
                Text(c);
                break;
            case State.Escape:
                Escape(c);
                break;
            case State.EscapeIntermediate:
                EscapeIntermediate(c);
                break;
            case State.Csi:
                if (c is >= '@' and <= '~')
                {
                    Csi(_sequence.ToString(), c);
                    _state = State.Text;
                }
                else if (c == '\x1b')
                {
                    _state = State.Escape;
                }
                else if (c is '\x18' or '\x1a')
                {
                    _state = State.Text;
                }
                else if (c < ' ')
                {
                    Control(c);
                }
                else
                {
                    _sequence.Append(c);
                }

                break;
            case State.Osc:
                if (c == '\x07')
                {
                    Osc(_sequence.ToString());
                    _state = State.Text;
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEscape;
                }
                else if (_sequence.Length < 4096)
                {
                    _sequence.Append(c);
                }

                break;
            case State.OscEscape:
                Osc(_sequence.ToString());
                _state = State.Text;
                if (c != '\\')
                {
                    Step(c);
                }

                break;
            case State.Ignore:
                if (c == '\x1b')
                {
                    _state = State.IgnoreEscape;
                }
                else if (c == '\x07')
                {
                    _state = State.Text;
                }

                break;
            case State.IgnoreEscape:
                _state = c == '\\' ? State.Text : State.Ignore;
                break;
        }
    }

    private void Text(char c)
    {
        if (c < ' ' || c == '\x7f')
        {
            Control(c);
            return;
        }

        if (char.IsHighSurrogate(c))
        {
            return;
        }

        if (char.IsLowSurrogate(c))
        {
            // Outside the BMP (emoji): one cell can hold one UTF-16 unit; a wide placeholder keeps the columns right.
            Print('�', wide: true);
            return;
        }

        if (_lineDrawing && c is >= '_' and <= '~')
        {
            c = LineDrawing[c - '_'];
        }

        if (IsZeroWidth(c))
        {
            return;
        }

        Print(c, IsWide(c));
    }

    private void Print(char c, bool wide)
    {
        if (_wrapPending)
        {
            _wrapPending = false;
            if (AutoWrap)
            {
                _wrapped[CursorRow] = true;
                CursorColumn = 0;
                LineFeed();
            }
        }

        if (wide && CursorColumn == Columns - 1)
        {
            // A wide character does not fit in the last column: it goes to the next line.
            Screen[CursorRow][CursorColumn] = Cell(' ');
            if (AutoWrap)
            {
                _wrapped[CursorRow] = true;
                CursorColumn = 0;
                LineFeed();
            }
        }

        var line = Screen[CursorRow];
        line[CursorColumn] = Cell(c);
        if (wide && CursorColumn + 1 < Columns)
        {
            line[CursorColumn + 1] = Cell('\0');
            CursorColumn++;
        }

        if (CursorColumn == Columns - 1)
        {
            _wrapPending = true;
        }
        else
        {
            CursorColumn++;
        }
    }

    private TerminalCell Cell(char c) => new(c, _foreground, _background, _style);

    private TerminalCell BlankCell => TerminalCell.Blank(_background);

    private void Control(char c)
    {
        switch (c)
        {
            case '\x07':
                Bell?.Invoke();
                break;
            case '\b':
                _wrapPending = false;
                CursorColumn = Math.Max(0, CursorColumn - 1);
                break;
            case '\t':
                CursorColumn = NextTab(CursorColumn);
                break;
            case '\n' or '\v' or '\f':
                _wrapPending = false;
                LineFeed();
                break;
            case '\r':
                _wrapPending = false;
                CursorColumn = 0;
                break;
            case '\x0e': // SO: G1 (not used by xterm-256color programs) - ignored
            case '\x0f':
                break;
            case '\x1b':
                _sequence.Clear();
                _state = State.Escape;
                break;
        }
    }

    private void Escape(char c)
    {
        _state = State.Text;
        switch (c)
        {
            case '[':
                _sequence.Clear();
                _state = State.Csi;
                break;
            case ']':
                _sequence.Clear();
                _state = State.Osc;
                break;
            case 'P' or 'X' or '^' or '_': // DCS, SOS, PM, APC: read and ignored
                _state = State.Ignore;
                break;
            case '(' or ')' or '*' or '+' or '#' or '%':
                _sequence.Clear();
                _sequence.Append(c);
                _state = State.EscapeIntermediate;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                CursorColumn = 0;
                LineFeed();
                break;
            case 'H':
                _tabStops[CursorColumn] = true;
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
            case '=' or '>': // keypad modes: the keypad sends digits either way here
                break;
        }
    }

    private void EscapeIntermediate(char c)
    {
        _state = State.Text;
        var intermediate = _sequence.Length > 0 ? _sequence[0] : ' ';
        if (intermediate == '(')
        {
            _lineDrawing = c == '0';
        }
        else if (intermediate == '#' && c == '8')
        {
            // DECALN: the screen full of E (tests of terminals)
            foreach (var line in Screen)
            {
                Array.Fill(line, new TerminalCell('E', TerminalCell.Default, TerminalCell.Default, CellStyle.None));
            }
        }
    }

    private void Csi(string body, char final)
    {
        var prefix = body.Length > 0 && body[0] is '?' or '>' or '<' or '=' ? body[0] : '\0';
        var intermediate = body.Length > 0 && body[^1] is >= ' ' and <= '/' ? body[^1] : '\0';
        var parameterText = body[(prefix == '\0' ? 0 : 1)..^(intermediate == '\0' ? 0 : 1)];
        var p = Parameters(parameterText);
        int Arg(int index, int fallback = 1) => index < p.Length && p[index] > 0 ? p[index] : fallback;

        if (intermediate == ' ' && final == 'q')
        {
            return; // cursor shape: the view draws a block
        }

        if (intermediate == '!' && final == 'p')
        {
            SoftReset();
            return;
        }

        switch (final)
        {
            case '@':
                InsertCharacters(Arg(0));
                break;
            case 'A':
                MoveTo(CursorColumn, Math.Max(CursorRow - Arg(0), CursorRow >= _top ? _top : 0));
                break;
            case 'B' or 'e':
                MoveTo(CursorColumn, Math.Min(CursorRow + Arg(0), CursorRow <= _bottom ? _bottom : Rows - 1));
                break;
            case 'C' or 'a':
                MoveTo(CursorColumn + Arg(0), CursorRow);
                break;
            case 'D':
                MoveTo(CursorColumn - Arg(0), CursorRow);
                break;
            case 'E':
                MoveTo(0, Math.Min(CursorRow + Arg(0), _bottom));
                break;
            case 'F':
                MoveTo(0, Math.Max(CursorRow - Arg(0), _top));
                break;
            case 'G' or '`':
                MoveTo(Arg(0) - 1, CursorRow);
                break;
            case 'H' or 'f':
                MoveTo(Arg(1) - 1, (OriginMode ? _top : 0) + Arg(0) - 1, clampToRegion: OriginMode);
                break;
            case 'I':
                for (var i = 0; i < Arg(0); i++)
                {
                    CursorColumn = NextTab(CursorColumn);
                }

                break;
            case 'J' when prefix is '\0' or '?':
                EraseInDisplay(Arg(0, 0));
                break;
            case 'K' when prefix is '\0' or '?':
                EraseInLine(Arg(0, 0));
                break;
            case 'L':
                InsertLines(Arg(0));
                break;
            case 'M':
                DeleteLines(Arg(0));
                break;
            case 'P':
                DeleteCharacters(Arg(0));
                break;
            case 'S' when prefix == '\0':
                ScrollUp(_top, _bottom, Arg(0));
                break;
            case 'T' when prefix == '\0':
                ScrollDown(_top, _bottom, Arg(0));
                break;
            case 'X':
                EraseCharacters(Arg(0));
                break;
            case 'Z':
                for (var i = 0; i < Arg(0); i++)
                {
                    CursorColumn = PreviousTab(CursorColumn);
                }

                break;
            case 'b':
                // REP: the last character again
                if (CursorColumn > 0 || _wrapPending)
                {
                    var last = Screen[CursorRow][Math.Max(0, _wrapPending ? CursorColumn : CursorColumn - 1)].Char;
                    for (var i = 0; i < Math.Min(Arg(0), Columns * Rows); i++)
                    {
                        Print(last, false);
                    }
                }

                break;
            case 'c' when prefix == '\0':
                Reply?.Invoke("\x1b[?62;22c");
                break;
            case 'c' when prefix == '>':
                Reply?.Invoke("\x1b[>0;10;1c");
                break;
            case 'd':
                MoveTo(CursorColumn, (OriginMode ? _top : 0) + Arg(0) - 1, clampToRegion: OriginMode);
                break;
            case 'g':
                if (Arg(0, 0) == 0)
                {
                    _tabStops[CursorColumn] = false;
                }
                else if (Arg(0, 0) == 3)
                {
                    Array.Clear(_tabStops);
                }

                break;
            case 'h' or 'l':
                foreach (var mode in p)
                {
                    SetMode(prefix == '?', mode, final == 'h');
                }

                break;
            case 'm' when prefix == '\0':
                Graphics(p.Length == 0 ? [0] : p, parameterText);
                break;
            case 'n' when Arg(0, 0) == 6:
                Reply?.Invoke($"\x1b[{CursorRow - (OriginMode ? _top : 0) + 1};{Math.Min(CursorColumn, Columns - 1) + 1}R");
                break;
            case 'n' when Arg(0, 0) == 5:
                Reply?.Invoke("\x1b[0n");
                break;
            case 'r' when prefix == '\0':
                var top = Arg(0) - 1;
                var bottom = Arg(1, Rows) - 1;
                if (top < bottom && bottom < Rows)
                {
                    _top = top;
                    _bottom = bottom;
                    MoveTo(0, OriginMode ? _top : 0);
                }

                break;
            case 's' when prefix == '\0':
                SaveCursor();
                break;
            case 'u' when prefix == '\0':
                RestoreCursor();
                break;
            case 't':
                if (Arg(0, 0) == 18)
                {
                    Reply?.Invoke($"\x1b[8;{Rows};{Columns}t");
                }

                break;
        }
    }

    private static int[] Parameters(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var parts = text.Split(';', ':');
        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Min(value, 65535) : 0;
        }

        return values;
    }

    private void SetMode(bool isPrivate, int mode, bool on)
    {
        if (!isPrivate)
        {
            return; // insert mode (4) and line feed mode (20) are not used by today's programs
        }

        switch (mode)
        {
            case 1:
                ApplicationCursorKeys = on;
                break;
            case 5:
                ReverseVideo = on;
                break;
            case 6:
                OriginMode = on;
                MoveTo(0, on ? _top : 0);
                break;
            case 7:
                AutoWrap = on;
                break;
            case 25:
                CursorVisible = on;
                break;
            case 9 or 1000 or 1002 or 1003:
                MouseTracking = on ? mode : 0;
                break;
            case 1006:
                SgrMouse = on;
                break;
            case 47 or 1047:
                AlternateScreen(on, saveCursor: false);
                break;
            case 1048:
                if (on)
                {
                    SaveCursor();
                }
                else
                {
                    RestoreCursor();
                }

                break;
            case 1049:
                AlternateScreen(on, saveCursor: true);
                break;
            case 2004:
                BracketedPaste = on;
                break;
        }
    }

    private void Graphics(int[] p, string text)
    {
        for (var i = 0; i < p.Length; i++)
        {
            var code = p[i];
            switch (code)
            {
                case 0:
                    _foreground = TerminalCell.Default;
                    _background = TerminalCell.Default;
                    _style = CellStyle.None;
                    break;
                case 1:
                    _style |= CellStyle.Bold;
                    break;
                case 2:
                    _style |= CellStyle.Faint;
                    break;
                case 3:
                    _style |= CellStyle.Italic;
                    break;
                case 4:
                    _style |= CellStyle.Underline;
                    break;
                case 7:
                    _style |= CellStyle.Inverse;
                    break;
                case 8:
                    _style |= CellStyle.Invisible;
                    break;
                case 9:
                    _style |= CellStyle.Strikethrough;
                    break;
                case 21 or 22:
                    _style &= ~(CellStyle.Bold | CellStyle.Faint);
                    break;
                case 23:
                    _style &= ~CellStyle.Italic;
                    break;
                case 24:
                    _style &= ~CellStyle.Underline;
                    break;
                case 27:
                    _style &= ~CellStyle.Inverse;
                    break;
                case 28:
                    _style &= ~CellStyle.Invisible;
                    break;
                case 29:
                    _style &= ~CellStyle.Strikethrough;
                    break;
                case >= 30 and <= 37:
                    _foreground = code - 30;
                    break;
                case 39:
                    _foreground = TerminalCell.Default;
                    break;
                case >= 40 and <= 47:
                    _background = code - 40;
                    break;
                case 49:
                    _background = TerminalCell.Default;
                    break;
                case >= 90 and <= 97:
                    _foreground = code - 90 + 8;
                    break;
                case >= 100 and <= 107:
                    _background = code - 100 + 8;
                    break;
                case 38 or 48:
                    var (color, used) = ExtendedColor(p, i + 1);
                    i += used;
                    if (color is { } value)
                    {
                        if (code == 38)
                        {
                            _foreground = value;
                        }
                        else
                        {
                            _background = value;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>38;5;n (palette) or 38;2;r;g;b (true colour); the parameters it used.</summary>
    private static (int? Color, int Used) ExtendedColor(int[] p, int at)
    {
        if (at >= p.Length)
        {
            return (null, 0);
        }

        if (p[at] == 5 && at + 1 < p.Length)
        {
            return (Math.Clamp(p[at + 1], 0, 255), 2);
        }

        if (p[at] == 2 && at + 3 < p.Length)
        {
            var r = Math.Clamp(p[at + 1], 0, 255);
            var g = Math.Clamp(p[at + 2], 0, 255);
            var b = Math.Clamp(p[at + 3], 0, 255);
            return (TerminalCell.TrueColor | (r << 16) | (g << 8) | b, 4);
        }

        return (null, 1);
    }

    private void Osc(string body)
    {
        var separator = body.IndexOf(';');
        if (separator <= 0)
        {
            return;
        }

        var code = body[..separator];
        if (code is "0" or "2")
        {
            Title = body[(separator + 1)..];
            TitleChanged?.Invoke(Title);
        }
    }

    // ================================================================= cursor and lines

    private void MoveTo(int column, int row, bool clampToRegion = false)
    {
        _wrapPending = false;
        CursorColumn = Math.Clamp(column, 0, Columns - 1);
        CursorRow = clampToRegion ? Math.Clamp(row, _top, _bottom) : Math.Clamp(row, 0, Rows - 1);
    }

    private void LineFeed()
    {
        if (CursorRow == _bottom)
        {
            ScrollUp(_top, _bottom, 1);
        }
        else if (CursorRow < Rows - 1)
        {
            CursorRow++;
        }
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _top)
        {
            ScrollDown(_top, _bottom, 1);
        }
        else if (CursorRow > 0)
        {
            CursorRow--;
        }
    }

    private void ScrollUp(int top, int bottom, int count)
    {
        var screen = Screen;
        count = Math.Min(count, bottom - top + 1);
        for (var i = 0; i < count; i++)
        {
            var leaving = screen[top];
            if (top == 0 && _alternate is null)
            {
                _scrollback.Add(leaving);
                if (_scrollback.Count > ScrollbackLimit)
                {
                    _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
                }

                leaving = new TerminalCell[Columns];
            }

            Array.Copy(screen, top + 1, screen, top, bottom - top);
            Array.Copy(_wrapped, top + 1, _wrapped, top, bottom - top);
            Array.Fill(leaving, BlankCell);
            screen[bottom] = leaving;
            _wrapped[bottom] = false;
        }
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        var screen = Screen;
        count = Math.Min(count, bottom - top + 1);
        for (var i = 0; i < count; i++)
        {
            var reused = screen[bottom];
            Array.Copy(screen, top, screen, top + 1, bottom - top);
            Array.Copy(_wrapped, top, _wrapped, top + 1, bottom - top);
            Array.Fill(reused, BlankCell);
            screen[top] = reused;
            _wrapped[top] = false;
        }
    }

    private void InsertLines(int count)
    {
        if (CursorRow >= _top && CursorRow <= _bottom)
        {
            ScrollDown(CursorRow, _bottom, count);
            CursorColumn = 0;
            _wrapPending = false;
        }
    }

    private void DeleteLines(int count)
    {
        if (CursorRow >= _top && CursorRow <= _bottom)
        {
            var screen = Screen;
            count = Math.Min(count, _bottom - CursorRow + 1);
            for (var i = 0; i < count; i++)
            {
                var reused = screen[CursorRow];
                Array.Copy(screen, CursorRow + 1, screen, CursorRow, _bottom - CursorRow);
                Array.Fill(reused, BlankCell);
                screen[_bottom] = reused;
            }

            CursorColumn = 0;
            _wrapPending = false;
        }
    }

    private void InsertCharacters(int count)
    {
        var line = Screen[CursorRow];
        count = Math.Min(count, Columns - CursorColumn);
        Array.Copy(line, CursorColumn, line, CursorColumn + count, Columns - CursorColumn - count);
        Array.Fill(line, BlankCell, CursorColumn, count);
        _wrapPending = false;
    }

    private void DeleteCharacters(int count)
    {
        var line = Screen[CursorRow];
        count = Math.Min(count, Columns - CursorColumn);
        Array.Copy(line, CursorColumn + count, line, CursorColumn, Columns - CursorColumn - count);
        Array.Fill(line, BlankCell, Columns - count, count);
        _wrapPending = false;
    }

    private void EraseCharacters(int count)
    {
        count = Math.Min(count, Columns - CursorColumn);
        Array.Fill(Screen[CursorRow], BlankCell, CursorColumn, count);
        _wrapPending = false;
    }

    private void EraseInLine(int mode)
    {
        var line = Screen[CursorRow];
        switch (mode)
        {
            case 0:
                Array.Fill(line, BlankCell, CursorColumn, Columns - CursorColumn);
                _wrapped[CursorRow] = false;
                break;
            case 1:
                Array.Fill(line, BlankCell, 0, Math.Min(CursorColumn + 1, Columns));
                break;
            case 2:
                Array.Fill(line, BlankCell);
                _wrapped[CursorRow] = false;
                break;
        }
    }

    private void EraseInDisplay(int mode)
    {
        var screen = Screen;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (var row = CursorRow + 1; row < Rows; row++)
                {
                    Array.Fill(screen[row], BlankCell);
                    _wrapped[row] = false;
                }

                break;
            case 1:
                EraseInLine(1);
                for (var row = 0; row < CursorRow; row++)
                {
                    Array.Fill(screen[row], BlankCell);
                    _wrapped[row] = false;
                }

                break;
            case 2:
                foreach (var line in screen)
                {
                    Array.Fill(line, BlankCell);
                }

                Array.Clear(_wrapped);
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void SaveCursor() => _saved = (CursorColumn, CursorRow, _foreground, _background, _style, OriginMode);

    private void RestoreCursor()
    {
        OriginMode = _saved.Origin;
        _foreground = _saved.Foreground;
        _background = _saved.Background;
        _style = _saved.Style;
        MoveTo(_saved.Column, _saved.Row);
    }

    private void AlternateScreen(bool on, bool saveCursor)
    {
        if (on == _alternate is not null)
        {
            return;
        }

        if (on)
        {
            if (saveCursor)
            {
                SaveCursor();
            }

            _alternate = NewScreen(Columns, Rows);
        }
        else
        {
            _alternate = null;
            if (saveCursor)
            {
                RestoreCursor();
            }
        }

        Array.Clear(_wrapped);
    }

    private void SoftReset()
    {
        CursorVisible = true;
        OriginMode = false;
        AutoWrap = true;
        ApplicationCursorKeys = false;
        _top = 0;
        _bottom = Rows - 1;
        _foreground = TerminalCell.Default;
        _background = TerminalCell.Default;
        _style = CellStyle.None;
        _lineDrawing = false;
    }

    private void Reset()
    {
        SoftReset();
        _alternate = null;
        _main = NewScreen(Columns, Rows);
        _wrapped = new bool[Rows];
        _tabStops = DefaultTabs(Columns);
        BracketedPaste = false;
        MouseTracking = 0;
        SgrMouse = false;
        ReverseVideo = false;
        MoveTo(0, 0);
    }

    private int NextTab(int column)
    {
        for (var next = column + 1; next < Columns; next++)
        {
            if (_tabStops[next])
            {
                return next;
            }
        }

        return Columns - 1;
    }

    private int PreviousTab(int column)
    {
        for (var previous = column - 1; previous > 0; previous--)
        {
            if (_tabStops[previous])
            {
                return previous;
            }
        }

        return 0;
    }

    // ================================================================= size

    /// <summary>The view changed its size: the screen keeps what fits; lines that no longer fit go into the scrollback (main screen).</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
        {
            return;
        }

        // When the screen gets lower, the lines above the cursor go to the scrollback so the cursor stays on screen.
        var shift = Math.Max(0, CursorRow - (rows - 1));
        if (_alternate is null)
        {
            for (var i = 0; i < shift; i++)
            {
                _scrollback.Add(_main[i]);
            }
        }

        _main = Fit(_main, shift, columns, rows);
        if (_alternate is not null)
        {
            _alternate = Fit(_alternate, shift, columns, rows);
        }

        for (var i = 0; i < _scrollback.Count; i++)
        {
            if (_scrollback[i].Length != columns)
            {
                _scrollback[i] = FitRow(_scrollback[i], columns);
            }
        }

        if (_scrollback.Count > ScrollbackLimit)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }

        var tabs = DefaultTabs(columns);
        Array.Copy(_tabStops, tabs, Math.Min(_tabStops.Length, columns));
        _tabStops = tabs;
        _wrapped = new bool[rows];
        Columns = columns;
        Rows = rows;
        _top = 0;
        _bottom = rows - 1;
        MoveTo(CursorColumn, CursorRow - shift);
        _saved = (Math.Min(_saved.Column, columns - 1), Math.Clamp(_saved.Row - shift, 0, rows - 1), _saved.Foreground, _saved.Background, _saved.Style, _saved.Origin);
        Version++;
    }

    private static TerminalCell[][] Fit(TerminalCell[][] screen, int shift, int columns, int rows)
    {
        var fitted = new TerminalCell[rows][];
        for (var row = 0; row < rows; row++)
        {
            fitted[row] = row + shift < screen.Length ? FitRow(screen[row + shift], columns) : BlankRow(columns);
        }

        return fitted;
    }

    private static TerminalCell[] FitRow(TerminalCell[] row, int columns)
    {
        if (row.Length == columns)
        {
            return row;
        }

        var fitted = BlankRow(columns);
        Array.Copy(row, fitted, Math.Min(row.Length, columns));
        return fitted;
    }

    private static TerminalCell[][] NewScreen(int columns, int rows) => Enumerable.Range(0, rows).Select(_ => BlankRow(columns)).ToArray();

    private static TerminalCell[] BlankRow(int columns)
    {
        var row = new TerminalCell[columns];
        Array.Fill(row, TerminalCell.Blank());
        return row;
    }

    private static bool[] DefaultTabs(int columns)
    {
        var tabs = new bool[columns];
        for (var column = 8; column < columns; column += 8)
        {
            tabs[column] = true;
        }

        return tabs;
    }

    // ================================================================= characters

    /// <summary>DEC special graphics (ESC ( 0): the line drawing of ncurses programs, '_' … '~'.</summary>
    private const string LineDrawing = " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private static bool IsZeroWidth(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format;

    /// <summary>East Asian wide and full width characters take two cells.</summary>
    public static bool IsWide(char c) =>
        c >= 0x1100 && (c <= 0x115f || c == 0x2329 || c == 0x232a
                        || (c >= 0x2e80 && c <= 0xa4cf && c != 0x303f)
                        || (c >= 0xac00 && c <= 0xd7a3)
                        || (c >= 0xf900 && c <= 0xfaff)
                        || (c >= 0xfe30 && c <= 0xfe6f)
                        || (c >= 0xff00 && c <= 0xff60)
                        || (c >= 0xffe0 && c <= 0xffe6));
}
