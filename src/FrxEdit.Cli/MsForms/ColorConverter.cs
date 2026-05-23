using System.Globalization;

namespace FrxEdit.Cli.MsForms;

public static class OleColorConverter
{
    private static readonly Dictionary<uint, string> SysColorNames = new()
    {
        { 0x8000_0000, "systemScrollbar" },
        { 0x8000_0001, "systemBackground" },
        { 0x8000_0002, "systemActiveCaption" },
        { 0x8000_0003, "systemInactiveCaption" },
        { 0x8000_0004, "systemMenu" },
        { 0x8000_0005, "systemWindow" },
        { 0x8000_0006, "systemWindowFrame" },
        { 0x8000_0007, "systemMenuText" },
        { 0x8000_0008, "systemWindowText" },
        { 0x8000_0009, "systemCaptionText" },
        { 0x8000_000A, "systemActiveBorder" },
        { 0x8000_000B, "systemInactiveBorder" },
        { 0x8000_000C, "systemAppWorkspace" },
        { 0x8000_000D, "systemHighlight" },
        { 0x8000_000E, "systemHighlightText" },
        { 0x8000_000F, "systemButtonFace" },
        { 0x8000_0010, "systemButtonShadow" },
        { 0x8000_0011, "systemGrayText" },
        { 0x8000_0012, "systemButtonText" },
        { 0x8000_0013, "systemInactiveCaptionText" },
        { 0x8000_0014, "systemButtonHighlight" },
        { 0x8000_0015, "system3DDarkShadow" },
        { 0x8000_0016, "system3DLight" },
        { 0x8000_0017, "systemInfoText" },
        { 0x8000_0018, "systemInfoBackground" }
    };

    private static readonly Dictionary<string, uint> SysColorValues =
        SysColorNames.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

    public static string ToHuman(uint color)
    {
        if (SysColorNames.TryGetValue(color, out var name))
        {
            return name;
        }

        // Standard OLE_COLOR has format 0x00BBGGRR (Blue, Green, Red).
        // Web Hex is #RRGGBB.
        if ((color & 0xFF000000) == 0)
        {
            var r = color & 0xFF;
            var g = (color >> 8) & 0xFF;
            var b = (color >> 16) & 0xFF;
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        return $"&H{color:X8}&"; // fallback
    }

    public static bool TryParse(string text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();

        // 1. Check System Color Dictionary
        if (SysColorValues.TryGetValue(text, out var sysColor))
        {
            value = sysColor;
            return true;
        }

        // 2. Check Web Hex (#RRGGBB)
        if (text.StartsWith('#') && text.Length == 7)
        {
            if (byte.TryParse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                // Pack as 0x00BBGGRR
                value = (uint)((b << 16) | (g << 8) | r);
                return true;
            }
            return false;
        }

        // 3. Legacy VBA Format (&H8000000F&)
        if (text.StartsWith("&H", StringComparison.OrdinalIgnoreCase) && text.EndsWith('&'))
        {
            return uint.TryParse(text[2..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        // 4. Raw integer format
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
