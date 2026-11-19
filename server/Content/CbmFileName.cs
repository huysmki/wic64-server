namespace Wic64Server.Content;

/// <summary>
/// A file name as programs pass it to OPEN, by CBM DOS rules: "@0:NAME,S,W" is NAME, a SEQ file, opened for
/// writing, replacing an existing file. Mode R (read, the default), W (write), A (append) or M.
/// </summary>
public sealed record CbmFileName(byte[] Name, FileType? Type, char Mode, bool Replace)
{
    public bool HasWildcards => Name.AsSpan().IndexOfAny((byte)'*', (byte)'?') >= 0;

    /// <summary>Null for names DOS would refuse here: empty, REL files (",L"), buffers ("#") or unknown parts.</summary>
    public static CbmFileName? Parse(byte[] petsciiName)
    {
        var text = petsciiName.AsSpan();
        var replace = text is [(byte)'@', ..];
        if (replace)
            text = text[1..];
        if (text is [(byte)'#', ..])
            return null;
        var colon = text.IndexOf((byte)':');
        if (colon is 0 or 1)
            text = text[(colon + 1)..];

        var parts = text.ToArray().Split((byte)',');
        if (parts[0].Length is 0 or > 16)
            return null;

        FileType? type = null;
        var mode = 'R';
        foreach (var part in parts.Skip(1))
        {
            switch (part is [] ? (byte)0 : part[0])
            {
                case (byte)'S': type = FileType.Seq; break;
                case (byte)'P': type = FileType.Prg; break;
                case (byte)'U': type = FileType.Usr; break;
                case (byte)'R' or (byte)'W' or (byte)'A' or (byte)'M': mode = (char)part[0]; break;
                case 0: break;
                default: return null; // REL (",L") and anything else
            }
        }

        return new CbmFileName(parts[0], type, mode, replace);
    }

    /// <summary>CBM DOS wildcards: * matches the rest of the name, ? any single character.</summary>
    public static bool Matches(byte[] pattern, byte[] name)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
                return true;
            if (i >= name.Length || (pattern[i] != '?' && pattern[i] != name[i]))
                return false;
        }

        return pattern.Length == name.Length;
    }
}

static class ByteArraySplit
{
    public static List<byte[]> Split(this byte[] bytes, byte separator)
    {
        var parts = new List<byte[]>();
        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i == bytes.Length || bytes[i] == separator)
            {
                parts.Add(bytes[start..i]);
                start = i + 1;
            }
        }

        return parts;
    }
}
