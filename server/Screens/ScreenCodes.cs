using System.Globalization;
using System.Text;

namespace Wic64Server.Screens;

/// <summary>
/// Converts .NET text to C64 screen codes for the upper/lowercase character set
/// ($d018 = $17), so the C64 can copy the bytes straight into screen memory.
/// </summary>
public static class ScreenCodes
{
    public const byte Space = 0x20;

    public static byte FromChar(char c)
    {
        switch (c)
        {
            case '@': return 0x00;
            case >= 'a' and <= 'z': return (byte)(c - 'a' + 1);
            case >= 'A' and <= 'Z': return (byte)(c - 'A' + 0x41);
            case >= ' ' and <= '?': return (byte)c;
            case '[': return 0x1b;
            case '£': return 0x1c;
            case ']': return 0x1d;
            case '^' or '↑': return 0x1e;
            case '←': return 0x1f;
            case '_': return 0x2d;
        }

        // Try to strip accents: "é" -> "e"
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length > 1 && CharUnicodeInfo.GetUnicodeCategory(decomposed[1]) == UnicodeCategory.NonSpacingMark)
            return FromChar(decomposed[0]);

        return (byte)'?';
    }

    /// <summary>Writes text into target, truncated or padded with spaces to the target length.</summary>
    public static void Write(Span<byte> target, string text, bool reverse = false)
    {
        for (var i = 0; i < target.Length; i++)
        {
            var code = i < text.Length ? FromChar(text[i]) : Space;
            target[i] = reverse ? (byte)(code | 0x80) : code;
        }
    }

    public static byte[] Line(string text, int width = 40)
    {
        var line = new byte[width];
        Write(line, text);
        return line;
    }
}
