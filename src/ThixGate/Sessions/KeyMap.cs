using Avalonia.Input;

namespace ThixGate.Sessions;

/// <summary>
/// A key of this keyboard as the engine wants it (the same message Matgate's page sends): the PC/AT scancode (set 1) with the extended
/// flag for RDP, and the X11 keysym for VNC. The physical key decides the scancode - so the server's layout types the character, as on
/// a real keyboard; the keysym comes from the character the key types here, or from the key itself for keys without one.
/// </summary>
public static class KeyMap
{
    private static readonly Dictionary<PhysicalKey, (int Scancode, bool Extended)> Scancodes = new()
    {
        [PhysicalKey.Escape] = (0x01, false),
        [PhysicalKey.Digit1] = (0x02, false), [PhysicalKey.Digit2] = (0x03, false), [PhysicalKey.Digit3] = (0x04, false),
        [PhysicalKey.Digit4] = (0x05, false), [PhysicalKey.Digit5] = (0x06, false), [PhysicalKey.Digit6] = (0x07, false),
        [PhysicalKey.Digit7] = (0x08, false), [PhysicalKey.Digit8] = (0x09, false), [PhysicalKey.Digit9] = (0x0A, false),
        [PhysicalKey.Digit0] = (0x0B, false), [PhysicalKey.Minus] = (0x0C, false), [PhysicalKey.Equal] = (0x0D, false),
        [PhysicalKey.Backspace] = (0x0E, false), [PhysicalKey.Tab] = (0x0F, false),
        [PhysicalKey.Q] = (0x10, false), [PhysicalKey.W] = (0x11, false), [PhysicalKey.E] = (0x12, false), [PhysicalKey.R] = (0x13, false),
        [PhysicalKey.T] = (0x14, false), [PhysicalKey.Y] = (0x15, false), [PhysicalKey.U] = (0x16, false), [PhysicalKey.I] = (0x17, false),
        [PhysicalKey.O] = (0x18, false), [PhysicalKey.P] = (0x19, false), [PhysicalKey.BracketLeft] = (0x1A, false),
        [PhysicalKey.BracketRight] = (0x1B, false), [PhysicalKey.Enter] = (0x1C, false), [PhysicalKey.ControlLeft] = (0x1D, false),
        [PhysicalKey.A] = (0x1E, false), [PhysicalKey.S] = (0x1F, false), [PhysicalKey.D] = (0x20, false), [PhysicalKey.F] = (0x21, false),
        [PhysicalKey.G] = (0x22, false), [PhysicalKey.H] = (0x23, false), [PhysicalKey.J] = (0x24, false), [PhysicalKey.K] = (0x25, false),
        [PhysicalKey.L] = (0x26, false), [PhysicalKey.Semicolon] = (0x27, false), [PhysicalKey.Quote] = (0x28, false),
        [PhysicalKey.Backquote] = (0x29, false), [PhysicalKey.ShiftLeft] = (0x2A, false), [PhysicalKey.Backslash] = (0x2B, false),
        [PhysicalKey.Z] = (0x2C, false), [PhysicalKey.X] = (0x2D, false), [PhysicalKey.C] = (0x2E, false), [PhysicalKey.V] = (0x2F, false),
        [PhysicalKey.B] = (0x30, false), [PhysicalKey.N] = (0x31, false), [PhysicalKey.M] = (0x32, false), [PhysicalKey.Comma] = (0x33, false),
        [PhysicalKey.Period] = (0x34, false), [PhysicalKey.Slash] = (0x35, false), [PhysicalKey.ShiftRight] = (0x36, false),
        [PhysicalKey.NumPadMultiply] = (0x37, false), [PhysicalKey.AltLeft] = (0x38, false), [PhysicalKey.Space] = (0x39, false),
        [PhysicalKey.CapsLock] = (0x3A, false),
        [PhysicalKey.F1] = (0x3B, false), [PhysicalKey.F2] = (0x3C, false), [PhysicalKey.F3] = (0x3D, false), [PhysicalKey.F4] = (0x3E, false),
        [PhysicalKey.F5] = (0x3F, false), [PhysicalKey.F6] = (0x40, false), [PhysicalKey.F7] = (0x41, false), [PhysicalKey.F8] = (0x42, false),
        [PhysicalKey.F9] = (0x43, false), [PhysicalKey.F10] = (0x44, false), [PhysicalKey.NumLock] = (0x45, false),
        [PhysicalKey.ScrollLock] = (0x46, false), [PhysicalKey.NumPad7] = (0x47, false), [PhysicalKey.NumPad8] = (0x48, false),
        [PhysicalKey.NumPad9] = (0x49, false), [PhysicalKey.NumPadSubtract] = (0x4A, false), [PhysicalKey.NumPad4] = (0x4B, false),
        [PhysicalKey.NumPad5] = (0x4C, false), [PhysicalKey.NumPad6] = (0x4D, false), [PhysicalKey.NumPadAdd] = (0x4E, false),
        [PhysicalKey.NumPad1] = (0x4F, false), [PhysicalKey.NumPad2] = (0x50, false), [PhysicalKey.NumPad3] = (0x51, false),
        [PhysicalKey.NumPad0] = (0x52, false), [PhysicalKey.NumPadDecimal] = (0x53, false), [PhysicalKey.IntlBackslash] = (0x56, false),
        [PhysicalKey.F11] = (0x57, false), [PhysicalKey.F12] = (0x58, false),
        [PhysicalKey.NumPadEnter] = (0x1C, true), [PhysicalKey.ControlRight] = (0x1D, true), [PhysicalKey.NumPadDivide] = (0x35, true),
        [PhysicalKey.PrintScreen] = (0x37, true), [PhysicalKey.AltRight] = (0x38, true), [PhysicalKey.Home] = (0x47, true),
        [PhysicalKey.ArrowUp] = (0x48, true), [PhysicalKey.PageUp] = (0x49, true), [PhysicalKey.ArrowLeft] = (0x4B, true),
        [PhysicalKey.ArrowRight] = (0x4D, true), [PhysicalKey.End] = (0x4F, true), [PhysicalKey.ArrowDown] = (0x50, true),
        [PhysicalKey.PageDown] = (0x51, true), [PhysicalKey.Insert] = (0x52, true), [PhysicalKey.Delete] = (0x53, true),
        [PhysicalKey.MetaLeft] = (0x5B, true), [PhysicalKey.MetaRight] = (0x5C, true), [PhysicalKey.ContextMenu] = (0x5D, true),
    };

    // X11 keysyms of the keys that type no character.
    private static readonly Dictionary<PhysicalKey, uint> Keysyms = new()
    {
        [PhysicalKey.Escape] = 0xFF1B, [PhysicalKey.Backspace] = 0xFF08, [PhysicalKey.Tab] = 0xFF09, [PhysicalKey.Enter] = 0xFF0D,
        [PhysicalKey.NumPadEnter] = 0xFF8D, [PhysicalKey.ControlLeft] = 0xFFE3, [PhysicalKey.ControlRight] = 0xFFE4,
        [PhysicalKey.ShiftLeft] = 0xFFE1, [PhysicalKey.ShiftRight] = 0xFFE2, [PhysicalKey.AltLeft] = 0xFFE9, [PhysicalKey.AltRight] = 0xFE03,
        [PhysicalKey.MetaLeft] = 0xFFEB, [PhysicalKey.MetaRight] = 0xFFEC, [PhysicalKey.CapsLock] = 0xFFE5, [PhysicalKey.NumLock] = 0xFF7F,
        [PhysicalKey.ScrollLock] = 0xFF14, [PhysicalKey.ContextMenu] = 0xFF67, [PhysicalKey.PrintScreen] = 0xFF61,
        [PhysicalKey.Home] = 0xFF50, [PhysicalKey.ArrowLeft] = 0xFF51, [PhysicalKey.ArrowUp] = 0xFF52, [PhysicalKey.ArrowRight] = 0xFF53,
        [PhysicalKey.ArrowDown] = 0xFF54, [PhysicalKey.PageUp] = 0xFF55, [PhysicalKey.PageDown] = 0xFF56, [PhysicalKey.End] = 0xFF57,
        [PhysicalKey.Insert] = 0xFF63, [PhysicalKey.Delete] = 0xFFFF, [PhysicalKey.Space] = 0x0020,
        [PhysicalKey.F1] = 0xFFBE, [PhysicalKey.F2] = 0xFFBF, [PhysicalKey.F3] = 0xFFC0, [PhysicalKey.F4] = 0xFFC1, [PhysicalKey.F5] = 0xFFC2,
        [PhysicalKey.F6] = 0xFFC3, [PhysicalKey.F7] = 0xFFC4, [PhysicalKey.F8] = 0xFFC5, [PhysicalKey.F9] = 0xFFC6, [PhysicalKey.F10] = 0xFFC7,
        [PhysicalKey.F11] = 0xFFC8, [PhysicalKey.F12] = 0xFFC9,
    };

    /// <summary>The scancode of a physical key (0 when RDP has none for it).</summary>
    public static (int Scancode, bool Extended) ScancodeOf(PhysicalKey key) => Scancodes.GetValueOrDefault(key);

    /// <summary>
    /// The keysym: for keys without a character the key's own, otherwise the character it types (Latin-1 directly, everything else as
    /// 0x01000000 + code point - the X11 rule). 0: none.
    /// </summary>
    public static uint KeysymOf(PhysicalKey key, string? symbol)
    {
        if (Keysyms.TryGetValue(key, out var keysym))
        {
            return keysym;
        }

        if (string.IsNullOrEmpty(symbol) || char.IsControl(symbol[0]))
        {
            return 0;
        }

        var codePoint = char.ConvertToUtf32(symbol, 0);
        return codePoint is >= 0x20 and <= 0xFF ? (uint)codePoint : 0x01000000u + (uint)codePoint;
    }
}
