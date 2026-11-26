namespace ThixGate.Core.Terminal;

/// <summary>
/// The colours of the terminal as 0xRRGGBB: the remote programs choose them (like the picture of a remote desktop), so they are the
/// terminal's own and not the theme's. A dark terminal in light and dark mode alike (as Windows Terminal, Royal TS): ANSI colours are made
/// for a dark background. The 16 base colours are calm (close to Windows Terminal's "Campbell"), 16…255 are xterm's cube and greys.
/// </summary>
public static class TerminalPalette
{
    public const uint Background = 0x0C0C14;
    public const uint Foreground = 0xD8D8E0;
    public const uint Cursor = 0xB8B2FF;
    public const uint Selection = 0x4B3FCF;

    private static readonly uint[] Base =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x3B78FF, 0x9A50C8, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x6E9BFF, 0xC77DFF, 0x61D6D6, 0xF2F2F2,
    ];

    /// <summary>A cell's colour (<see cref="TerminalCell.Default"/>, 0…255 or true colour) as 0xRRGGBB.</summary>
    public static uint Rgb(int color, bool isForeground, bool bold = false)
    {
        if (color == TerminalCell.Default)
        {
            return isForeground ? Foreground : Background;
        }

        if ((color & TerminalCell.TrueColor) != 0)
        {
            return (uint)color & 0xFFFFFF;
        }

        if (color < 16)
        {
            // Bold text in one of the 8 base colours shows the bright one (as xterm does).
            return Base[bold && isForeground && color < 8 ? color + 8 : color];
        }

        if (color < 232)
        {
            var index = color - 16;
            static uint Level(int value) => value == 0 ? 0u : (uint)(55 + value * 40);
            return (Level(index / 36) << 16) | (Level(index / 6 % 6) << 8) | Level(index % 6);
        }

        var grey = (uint)(8 + (color - 232) * 10);
        return (grey << 16) | (grey << 8) | grey;
    }
}
