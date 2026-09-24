using Wic64Server.Activity;
using Wic64Server.Content;

namespace Wic64Server.Programs;

/// <summary>
/// Files for the C64's LOAD helper (c64/loadhelper.asm): programs started from the browser can
/// LOAD more files from the .d64 image or folder they came from, as if it was in drive 8.
/// </summary>
public sealed class LoadService(Catalog catalog, ServerOptions options, ActivityLog activity, ILogger<LoadService> logger)
{
    const int LowPartSize = 0x0300 - 0x02a7;
    const int HighPartSize = 0x03fc - 0x0334;

    /// <summary>
    /// The helper code for $02a7 and $0334. The browser writes the request header and the URL
    /// up to the file name to $0200 itself, and the URL's length into prefix_length ($0337).
    /// </summary>
    public byte[] Helper()
    {
        var path = Path.Combine(options.BuildFolder, "loadhelper.bin");
        if (!File.Exists(path))
            throw new FileNotFoundException("LOAD helper missing, run make", path);

        // The helper is assembled from $02a7 to $03fb, with the KERNAL vectors in between
        var code = File.ReadAllBytes(path);
        var low = new byte[LowPartSize];
        var high = new byte[HighPartSize];
        code.AsSpan(0, Math.Min(LowPartSize, code.Length)).CopyTo(low);
        code.AsSpan(0x0334 - 0x02a7, Math.Min(HighPartSize, code.Length - (0x0334 - 0x02a7))).CopyTo(high);
        return [.. low, .. high];
    }

    /// <summary>
    /// For browsers built before the browser wrote the URL itself: the request block for $0200
    /// (header + URL up to the file name) in front of the helper code, with prefix_length filled in.
    /// </summary>
    public byte[] LegacyHelper(string host, int folder)
    {
        var prefix = System.Text.Encoding.ASCII.GetBytes($"http://{host}/l/{folder:x4}/");
        var request = new byte[0x0259 - 0x0200];
        request[0] = (byte)'R';
        request[1] = 0x01; // WIC64_HTTP_GET
        prefix.CopyTo(request, 4);

        var code = Helper();
        code[LowPartSize + 3] = (byte)prefix.Length;
        return [.. request, .. code];
    }

    /// <summary>The .prg file for a LOAD, or null when the server does not have it.</summary>
    public byte[]? Load(int folder, byte[] petsciiName)
    {
        var name = StripDrive(petsciiName);
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);

        if (name is [(byte)'$'])
        {
            activity.Add("load", "LOAD \"$\" (directory)");
            return Directory(path, isDiskImage);
        }

        // The image the program came from, then the other images next to it (other disk sides)
        if (isDiskImage)
        {
            var images = new[] { path }.Concat(
                System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.d64")
                    .Where(other => other != path)
                    .OrderBy(other => other, StringComparer.OrdinalIgnoreCase));

            foreach (var imagePath in images)
            {
                var image = DiskImage.Load(imagePath);
                if (image.Programs().FirstOrDefault(file => Matches(name, file.Name)) is { } found)
                {
                    logger.LogInformation("LOAD \"{Name}\" from {Image}", Petscii.ToText(name), Path.GetFileName(imagePath));
                    activity.Add("load", $"LOAD \"{Petscii.ToText(name)}\" from {Path.GetFileName(imagePath)}");
                    return image.Read(found);
                }
            }
        }
        else if (System.IO.Directory.Exists(path))
        {
            var file = System.IO.Directory.EnumerateFiles(path, "*.prg")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => Matches(name, Petscii.FileName(Path.GetFileName(f))));
            if (file is not null)
            {
                logger.LogInformation("LOAD \"{Name}\" from {File}", Petscii.ToText(name), Path.GetFileName(file));
                activity.Add("load", $"LOAD \"{Petscii.ToText(name)}\" from {Path.GetFileName(file)}");
                return File.ReadAllBytes(file);
            }
        }

        logger.LogInformation("LOAD \"{Name}\": not on the server, the C64 tries its own drive", Petscii.ToText(name));
        activity.Add("load", $"LOAD \"{Petscii.ToText(name)}\": not on the server, the C64 tries its own drive");
        return null;
    }

    /// <summary>"0:NAME" and ":NAME" mean NAME.</summary>
    static byte[] StripDrive(byte[] name)
    {
        var colon = Array.IndexOf(name, (byte)':');
        return colon is 0 or 1 ? name[(colon + 1)..] : name;
    }

    /// <summary>CBM DOS wildcards: * matches the rest of the name, ? any single character.</summary>
    static bool Matches(byte[] pattern, byte[] name)
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

    /// <summary>LOAD"$",8: the directory as a BASIC program, like a real drive sends it.</summary>
    static byte[] Directory(string path, bool isDiskImage)
    {
        IEnumerable<(byte[] Name, int Blocks)> files;
        byte[] title;
        if (isDiskImage)
        {
            var image = DiskImage.Load(path);
            files = image.Programs().Select(f => (f.Name, f.Blocks));
            title = image.Title();
        }
        else
        {
            files = System.IO.Directory.Exists(path)
                ? System.IO.Directory.EnumerateFiles(path, "*.prg")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (Petscii.FileName(Path.GetFileName(f)), (int)((new FileInfo(f).Length + 253) / 254)))
                : [];
            var name = Petscii.FileName(Path.GetFileName(path) + ".dir"); // FileName drops the "extension"
            title = [.. name, .. Enumerable.Repeat((byte)' ', 16 - name.Length), .. " WC 2A"u8];
        }

        var lines = new List<(int Number, byte[] Text)> { (0, [0x12, (byte)'"', .. title[..16], (byte)'"', .. title[16..]]) };
        foreach (var (name, blocks) in files)
        {
            var text = new List<byte>();
            text.AddRange(Enumerable.Repeat((byte)' ', blocks < 10 ? 3 : blocks < 100 ? 2 : 1));
            text.Add((byte)'"');
            text.AddRange(name);
            text.Add((byte)'"');
            text.AddRange(Enumerable.Repeat((byte)' ', 17 - name.Length));
            text.AddRange("PRG"u8.ToArray());
            lines.Add((blocks, text.ToArray()));
        }
        lines.Add((0, "BLOCKS FREE."u8.ToArray()));

        // BASIC program at $0801: link to the next line, line number, text, 0
        var program = new List<byte> { 0x01, 0x08 };
        var address = 0x0801;
        foreach (var (number, text) in lines)
        {
            address += 4 + text.Length + 1;
            program.AddRange([(byte)address, (byte)(address >> 8), (byte)number, (byte)(number >> 8)]);
            program.AddRange(text);
            program.Add(0);
        }
        program.AddRange([0, 0]);
        return program.ToArray();
    }
}
