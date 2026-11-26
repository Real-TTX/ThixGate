using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using ThixGate.Core.Terminal;

namespace ThixGate.Sessions;

/// <summary>
/// ThixGate's own terminal for SSH sessions: draws a <see cref="TerminalBuffer"/> (monospace cells, colours of the remote programs, the
/// cursor), sends keys and text (<see cref="TerminalKeys"/>), selects with the mouse and copies (Strg+Umschalt+C / Cmd+C, or the context
/// menu), pastes (Strg+Umschalt+V / Cmd+V / Umschalt+Einfg, bracketed when the program wants it), scrolls back with the wheel or
/// Umschalt+Bild auf/ab and passes mouse clicks to programs that ask for them. Its size in cells follows the control.
/// </summary>
public sealed class TerminalView : Control
{
    private const double FontSize = 13.5;

    private static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas, Menlo, SF Mono, DejaVu Sans Mono, Liberation Mono, monospace");

    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private int _drainPosted;
    private double _cellWidth;
    private double _cellHeight;
    private int _scrollOffset;
    private (int Row, int Column)? _selectionStart;
    private (int Row, int Column)? _selectionEnd;
    private bool _selecting;

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Buffer = new TerminalBuffer(80, 24);
        Buffer.Reply += text => Input?.Invoke(Encoding.UTF8.GetBytes(text));
        MeasureCell();
        AutomationProperties.SetName(this, "Terminal");

        var copy = new MenuItem { Header = "Kopieren", InputGesture = CopyGesture };
        copy.Click += (_, _) => _ = CopyAsync();
        var paste = new MenuItem { Header = "Einfügen", InputGesture = PasteGesture };
        paste.Click += (_, _) => _ = PasteAsync();
        var selectAll = new MenuItem { Header = "Alles auswählen" };
        selectAll.Click += (_, _) => SelectAll();
        ContextMenu = new ContextMenu { Items = { copy, paste, new Separator(), selectAll } };
        ContextMenu.Opening += (_, _) => copy.IsEnabled = HasSelection;
    }

    /// <summary>Keys, text and answers for the server.</summary>
    public event Action<byte[]>? Input;

    /// <summary>The size in cells changed (the server's programs follow).</summary>
    public event Action<int, int>? GridChanged;

    public TerminalBuffer Buffer { get; }

    public int Columns => Buffer.Columns;

    public int Rows => Buffer.Rows;

    public bool HasSelection => _selectionStart is { } start && _selectionEnd is { } end && start != end;

    private static KeyGesture CopyGesture => OperatingSystem.IsMacOS() ? new KeyGesture(Key.C, KeyModifiers.Meta) : new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift);

    private static KeyGesture PasteGesture => OperatingSystem.IsMacOS() ? new KeyGesture(Key.V, KeyModifiers.Meta) : new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift);

    /// <summary>What the server sent (any thread): read on the UI thread, drawn once for everything that arrived in between.</summary>
    public void Receive(byte[] bytes)
    {
        _incoming.Enqueue(bytes);
        if (Interlocked.Exchange(ref _drainPosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(Drain, DispatcherPriority.Render);
        }
    }

    /// <summary>Feeds text as if the server had sent it (tests, messages of ThixGate itself).</summary>
    public void Show(string text)
    {
        Buffer.Feed(text);
        InvalidateVisual();
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _drainPosted, 0);
        var any = false;
        while (_incoming.TryDequeue(out var bytes))
        {
            Buffer.Feed(bytes);
            any = true;
        }

        if (any)
        {
            InvalidateVisual();
        }
    }

    private void Send(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        ClearSelection();
        _scrollOffset = 0;
        Input?.Invoke(Encoding.UTF8.GetBytes(text));
        InvalidateVisual();
    }

    // ================================================================= size

    private void MeasureCell()
    {
        var text = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Mono), FontSize, null);
        _cellWidth = Math.Max(4, Math.Round(text.WidthIncludingTrailingWhitespace / 10, 2));
        _cellHeight = Math.Max(8, Math.Ceiling(text.Height * 1.08));
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 * _cellWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 24 * _cellHeight : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(20, (int)((finalSize.Width - 2 * Padding) / _cellWidth));
        var rows = Math.Max(5, (int)((finalSize.Height - 2 * Padding) / _cellHeight));
        if (columns != Buffer.Columns || rows != Buffer.Rows)
        {
            Buffer.Resize(columns, rows);
            ClearSelection();
            GridChanged?.Invoke(columns, rows);
            InvalidateVisual();
        }

        return finalSize;
    }

    private const double Padding = 6;

    // ================================================================= drawing

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush(TerminalPalette.Background), new Rect(Bounds.Size));
        var buffer = Buffer;
        var first = -Math.Min(_scrollOffset, buffer.ScrollbackCount);
        for (var screenRow = 0; screenRow < buffer.Rows; screenRow++)
        {
            var row = first + screenRow;
            DrawRow(context, buffer.Row(row), row, Padding + screenRow * _cellHeight);
        }

        if (buffer.CursorVisible && _scrollOffset == 0)
        {
            var x = Padding + Math.Min(buffer.CursorColumn, buffer.Columns - 1) * _cellWidth;
            var y = Padding + buffer.CursorRow * _cellHeight;
            var rect = new Rect(x, y, _cellWidth, _cellHeight);
            if (IsFocused)
            {
                context.FillRectangle(Brush(TerminalPalette.Cursor, 0.75), rect);
            }
            else
            {
                context.DrawRectangle(new Pen(Brush(TerminalPalette.Cursor), 1), rect.Deflate(0.5));
            }
        }
    }

    private void DrawRow(DrawingContext context, TerminalCell[] cells, int row, double y)
    {
        var count = Math.Min(cells.Length, Buffer.Columns);
        var column = 0;
        while (column < count)
        {
            var cell = cells[column];
            var (foreground, background) = Colors(cell, IsSelected(row, column));
            var start = column;
            var text = new StringBuilder();
            while (column < count)
            {
                var next = cells[column];
                var (nextForeground, nextBackground) = Colors(next, IsSelected(row, column));
                if (nextForeground != foreground || nextBackground != background || next.Style != cell.Style)
                {
                    break;
                }

                text.Append(next.Char == '\0' ? "" : (next.Style & CellStyle.Invisible) != 0 ? " " : next.Char.ToString());
                column++;
            }

            var x = Padding + start * _cellWidth;
            var width = (column - start) * _cellWidth;
            if (background != TerminalPalette.Background)
            {
                context.FillRectangle(Brush(background), new Rect(x, y, width, _cellHeight));
            }

            var content = text.ToString();
            if (content.Trim().Length == 0 && (cell.Style & (CellStyle.Underline | CellStyle.Strikethrough)) == 0)
            {
                continue;
            }

            DrawText(context, content, cells, start, column, x, y, foreground, cell.Style);
        }
    }

    /// <summary>
    /// Draws a run cell by cell where the font's width differs from the grid (a fallback font, wide characters); else in one go.
    /// </summary>
    private void DrawText(DrawingContext context, string content, TerminalCell[] cells, int start, int end, double x, double y, uint color, CellStyle style)
    {
        var typeface = new Typeface(Mono, (style & CellStyle.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal, (style & CellStyle.Bold) != 0 ? FontWeight.Bold : FontWeight.Normal);
        var brush = Brush(color, (style & CellStyle.Faint) != 0 ? 0.6 : 1);
        var formatted = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
        if ((style & CellStyle.Underline) != 0)
        {
            formatted.SetTextDecorations(TextDecorations.Underline);
        }
        else if ((style & CellStyle.Strikethrough) != 0)
        {
            formatted.SetTextDecorations(TextDecorations.Strikethrough);
        }

        var textTop = y + (_cellHeight - formatted.Height) / 2;
        if (Math.Abs(formatted.WidthIncludingTrailingWhitespace - (end - start) * _cellWidth) < 0.5)
        {
            context.DrawText(formatted, new Point(x, textTop));
            return;
        }

        for (var column = start; column < end; column++)
        {
            var c = cells[column].Char;
            if (c is '\0' or ' ')
            {
                continue;
            }

            var single = new FormattedText(c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, brush);
            context.DrawText(single, new Point(Padding + column * _cellWidth, textTop));
        }
    }

    private (uint Foreground, uint Background) Colors(TerminalCell cell, bool selected)
    {
        var bold = (cell.Style & CellStyle.Bold) != 0;
        var foreground = TerminalPalette.Rgb(cell.Foreground, isForeground: true, bold);
        var background = TerminalPalette.Rgb(cell.Background, isForeground: false);
        if (((cell.Style & CellStyle.Inverse) != 0) ^ Buffer.ReverseVideo)
        {
            (foreground, background) = (background, foreground);
        }

        return selected ? (0xFFFFFF, TerminalPalette.Selection) : (foreground, background);
    }

    private static IBrush Brush(uint rgb, double opacity = 1) =>
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(new Color((byte)(opacity * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // ================================================================= keys and text

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var mac = OperatingSystem.IsMacOS();
        var command = mac ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (command && e.Key == Key.C)
        {
            e.Handled = true;
            _ = CopyAsync();
            return;
        }

        if ((command && e.Key == Key.V) || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = PasteAsync();
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            Scroll(e.Key == Key.PageUp ? Buffer.Rows - 1 : -(Buffer.Rows - 1));
            return;
        }

        if (mac && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            return; // Cmd+… belongs to the app (Cmd+W, Cmd+Q)
        }

        if (TerminalKeys.Sequence(e.Key, e.KeyModifiers, e.KeySymbol, Buffer.ApplicationCursorKeys) is { } sequence)
        {
            e.Handled = true;
            Send(sequence);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl))
        {
            return;
        }

        e.Handled = true;
        Send(e.Text);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    // ================================================================= clipboard

    public string SelectedText()
    {
        if (!HasSelection)
        {
            return "";
        }

        var (from, to) = Ordered();
        var builder = new StringBuilder();
        for (var row = from.Row; row <= to.Row; row++)
        {
            var cells = Buffer.Row(row);
            var start = row == from.Row ? from.Column : 0;
            var end = row == to.Row ? to.Column : cells.Length;
            var line = new StringBuilder();
            for (var column = start; column < Math.Min(end, cells.Length); column++)
            {
                if (cells[column].Char != '\0')
                {
                    line.Append(cells[column].Char);
                }
            }

            builder.Append(Buffer.IsWrapped(row) && row != to.Row ? line.ToString() : line.ToString().TrimEnd());
            if (row != to.Row && !Buffer.IsWrapped(row))
            {
                builder.Append(Environment.NewLine);
            }
        }

        return builder.ToString();
    }

    private async Task CopyAsync()
    {
        if (HasSelection && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(SelectedText());
        }
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || await clipboard.TryGetTextAsync() is not { Length: > 0 } text)
        {
            return;
        }

        Paste(text);
    }

    /// <summary>Text as if pasted: line ends become Enter; bracketed when the program asked for it (it then does not run line by line).</summary>
    public void Paste(string text)
    {
        text = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        Send(Buffer.BracketedPaste ? $"\x1b[200~{text.Replace("\x1b[201~", "", StringComparison.Ordinal)}\x1b[201~" : text);
    }

    // ================================================================= mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (Buffer.MouseTracking != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && MouseButton(point.Properties.PointerUpdateKind) is { } button)
        {
            ReportMouse(button, point.Position, pressed: true);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var cell = CellAt(point.Position);
            if (e.ClickCount == 2)
            {
                SelectWord(cell);
            }
            else if (e.ClickCount >= 3)
            {
                _selectionStart = (cell.Row, 0);
                _selectionEnd = (cell.Row, Buffer.Columns);
            }
            else
            {
                _selectionStart = _selectionEnd = cell;
                _selecting = true;
                e.Pointer.Capture(this);
            }

            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting)
        {
            _selectionEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Buffer.MouseTracking != 0 && !_selecting && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && MouseButton(e.InitialPressMouseButton) is { } button)
        {
            ReportMouse(button, e.GetPosition(this), pressed: false);
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (!HasSelection)
            {
                ClearSelection();
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0)
        {
            return;
        }

        e.Handled = true;
        if (Buffer.MouseTracking != 0 && Buffer.SgrMouse)
        {
            ReportMouse(lines > 0 ? 64 : 65, e.GetPosition(this), pressed: true);
        }
        else if (Buffer.IsAlternateScreen)
        {
            // Full screen programs (less, man) scroll with the cursor keys.
            var key = lines > 0 ? (Buffer.ApplicationCursorKeys ? "\x1bOA" : "\x1b[A") : (Buffer.ApplicationCursorKeys ? "\x1bOB" : "\x1b[B");
            Input?.Invoke(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(key, Math.Abs(lines)))));
        }
        else
        {
            Scroll(lines);
        }
    }

    private void Scroll(int lines)
    {
        _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, Buffer.ScrollbackCount);
        InvalidateVisual();
    }

    private static int? MouseButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 0,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        _ => null,
    };

    private static int? MouseButton(Avalonia.Input.MouseButton button) => button switch
    {
        Avalonia.Input.MouseButton.Left => 0,
        Avalonia.Input.MouseButton.Middle => 1,
        Avalonia.Input.MouseButton.Right => 2,
        _ => null,
    };

    /// <summary>A click for a program that asked for the mouse (mc, htop, vim with mouse=a): SGR form or the classic one.</summary>
    private void ReportMouse(int button, Point position, bool pressed)
    {
        var column = Math.Clamp((int)((position.X - Padding) / _cellWidth), 0, Buffer.Columns - 1) + 1;
        var row = Math.Clamp((int)((position.Y - Padding) / _cellHeight), 0, Buffer.Rows - 1) + 1;
        if (Buffer.SgrMouse)
        {
            Input?.Invoke(Encoding.ASCII.GetBytes($"\x1b[<{button};{column};{row}{(pressed ? 'M' : 'm')}"));
        }
        else if (column <= 223 && row <= 223)
        {
            var code = pressed ? button : 3;
            Input?.Invoke([0x1b, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + column), (byte)(32 + row)]);
        }
    }

    // ================================================================= selection

    private (int Row, int Column) CellAt(Point position)
    {
        var row = Math.Clamp((int)((position.Y - Padding) / _cellHeight), 0, Buffer.Rows - 1) - Math.Min(_scrollOffset, Buffer.ScrollbackCount);
        var column = Math.Clamp((int)Math.Round((position.X - Padding) / _cellWidth), 0, Buffer.Columns);
        return (row, column);
    }

    private ((int Row, int Column) From, (int Row, int Column) To) Ordered()
    {
        var (start, end) = (_selectionStart!.Value, _selectionEnd!.Value);
        return start.Row < end.Row || (start.Row == end.Row && start.Column <= end.Column) ? (start, end) : (end, start);
    }

    private bool IsSelected(int row, int column)
    {
        if (!HasSelection)
        {
            return false;
        }

        var (from, to) = Ordered();
        return (row > from.Row || (row == from.Row && column >= from.Column)) && (row < to.Row || (row == to.Row && column < to.Column));
    }

    private void SelectWord((int Row, int Column) cell)
    {
        var cells = Buffer.Row(cell.Row);
        var column = Math.Min(cell.Column, cells.Length - 1);
        static bool IsWord(char c) => !char.IsWhiteSpace(c) && c != '\0' && "\"'()[]{}<>|;,`".IndexOf(c) < 0;
        if (!IsWord(cells[column].Char))
        {
            return;
        }

        var start = column;
        while (start > 0 && IsWord(cells[start - 1].Char))
        {
            start--;
        }

        var end = column;
        while (end < cells.Length && IsWord(cells[end].Char))
        {
            end++;
        }

        _selectionStart = (cell.Row, start);
        _selectionEnd = (cell.Row, end);
    }

    private void SelectAll()
    {
        _selectionStart = (-Buffer.ScrollbackCount, 0);
        _selectionEnd = (Buffer.Rows - 1, Buffer.Columns);
        InvalidateVisual();
    }

    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        _selecting = false;
    }
}
