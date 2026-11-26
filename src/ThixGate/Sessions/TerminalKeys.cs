using Avalonia.Input;

namespace ThixGate.Sessions;

/// <summary>
/// What a key sends to the server in an xterm: cursor and function keys as escape sequences (with Shift/Alt/Ctrl as xterm's modifier
/// parameter, cursor keys in the application mode as SS3), Ctrl+letter as a control character, Alt+key with ESC before it. Printable text
/// comes from the text input instead (null here), so every keyboard layout works.
/// </summary>
public static class TerminalKeys
{
    public static string? Sequence(Key key, KeyModifiers modifiers, string? symbol, bool applicationCursorKeys)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        var control = modifiers.HasFlag(KeyModifiers.Control);
        var modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (control ? 4 : 0);

        string Cursor(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : applicationCursorKeys ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int number) => modifier > 1 ? $"\x1b[{number};{modifier}~" : $"\x1b[{number}~";
        string Function(char final) => modifier > 1 ? $"\x1b[1;{modifier}{final}" : $"\x1bO{final}";

        var sequence = key switch
        {
            Key.Up => Cursor('A'),
            Key.Down => Cursor('B'),
            Key.Right => Cursor('C'),
            Key.Left => Cursor('D'),
            Key.Home => Cursor('H'),
            Key.End => Cursor('F'),
            Key.Insert => Tilde(2),
            Key.Delete => Tilde(3),
            Key.PageUp => Tilde(5),
            Key.PageDown => Tilde(6),
            Key.F1 => Function('P'),
            Key.F2 => Function('Q'),
            Key.F3 => Function('R'),
            Key.F4 => Function('S'),
            Key.F5 => Tilde(15),
            Key.F6 => Tilde(17),
            Key.F7 => Tilde(18),
            Key.F8 => Tilde(19),
            Key.F9 => Tilde(20),
            Key.F10 => Tilde(21),
            Key.F11 => Tilde(23),
            Key.F12 => Tilde(24),
            Key.Enter => "\r",
            Key.Back => control ? "\b" : "\x7f",
            Key.Tab => shift ? "\x1b[Z" : "\t",
            Key.Escape => "\x1b",
            _ => null,
        };

        if (sequence is not null)
        {
            return alt && sequence.Length == 1 ? "\x1b" + sequence : sequence;
        }

        if (control && !alt && ControlCharacter(key, symbol) is { } controlCharacter)
        {
            return controlCharacter.ToString();
        }

        if (alt && !control && symbol is { Length: 1 } && !char.IsControl(symbol[0]))
        {
            // Alt+key (Meta): ESC before the character, as bash and emacs expect.
            return "\x1b" + symbol;
        }

        return null;
    }

    /// <summary>Ctrl+A … Ctrl+Z, Ctrl+Space, Ctrl+[ \ ] ^ _ as the control characters 0…31.</summary>
    private static char? ControlCharacter(Key key, string? symbol)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return (char)(key - Key.A + 1);
        }

        if (key == Key.Space || symbol == "@" || symbol == "2")
        {
            return '\0';
        }

        return symbol switch
        {
            "[" or "3" => '\x1b',
            "\\" or "4" => '\x1c',
            "]" or "5" => '\x1d',
            "^" or "6" => '\x1e',
            "_" or "-" or "7" => '\x1f',
            "?" or "8" => '\x7f',
            _ => null,
        };
    }
}
