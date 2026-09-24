namespace Wic64Server.Content;

/// <summary>Conversions between PETSCII (as used in C64 file names) and text.</summary>
public static class Petscii
{
    /// <summary>Unshifted PETSCII letters show as lowercase in the browser's character set.</summary>
    public static string ToText(IEnumerable<byte> petscii) => new(petscii.Select(b => b switch
    {
        >= 0x41 and <= 0x5a => (char)(b + 0x20),
        >= 0xc1 and <= 0xda => (char)(b - 0x80),
        >= 0x20 and <= 0x3f => (char)b,
        _ => '?',
    }).ToArray());

    /// <summary>A C64 file name (max 16 characters) for a file name on this computer.</summary>
    public static byte[] FileName(string name)
    {
        var bytes = new List<byte>();
        foreach (var c in Path.GetFileNameWithoutExtension(name).ToUpperInvariant())
        {
            if (bytes.Count == 16)
                break;
            bytes.Add(c switch
            {
                >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' or '.' or '-' or '+' or '!' or '&' or '(' or ')' => (byte)c,
                _ => (byte)'-',
            });
        }

        return bytes.Count == 0 ? "PROGRAM"u8.ToArray() : bytes.ToArray();
    }
}
