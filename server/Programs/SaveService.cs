using Wic64Server.Activity;
using Wic64Server.Content;

namespace Wic64Server.Programs;

/// <summary>
/// SAVEs from the C64's save helper (c64/savehelper.asm): the memory arrives in parts of up to 32 KB and is written as
/// a .prg into the folder or disk image the program came from, like a real drive would. A file with the same name is
/// replaced (games usually scratch the old one through the command channel, which doesn't reach the server). Before the
/// first save into a disk image, the original is copied to the hidden .backups folder next to it.
/// </summary>
public sealed class SaveService(Catalog catalog, ActivityLog activity, ILogger<SaveService> logger)
{
    readonly Lock saveLock = new();
    (string Key, int NextPart, List<byte> Data)? pending;

    /// <summary>
    /// A part of a SAVE (bit 7 of <paramref name="part"/> marks the last one). Null when the part is taken or the file
    /// is saved; otherwise why not: the C64 then saves to its real drive.
    /// </summary>
    public string? Part(int folder, int start, int part, byte[] petsciiName, byte[] data)
    {
        lock (saveLock)
        {
            var key = $"{folder}:{start}:{Convert.ToHexString(petsciiName)}";
            var number = part & 0x7f;
            if (number == 0)
                pending = (key, 0, []);
            if (pending is not { } current || current.Key != key || current.NextPart != number)
            {
                pending = null;
                return "a part of the file is missing";
            }

            current.Data.AddRange(data);
            pending = current with { NextPart = number + 1 };
            if ((part & 0x80) == 0)
                return null;

            pending = null;
            return Save(folder, petsciiName, [(byte)start, (byte)(start >> 8), .. current.Data]);
        }
    }

    string? Save(int folder, byte[] petsciiName, byte[] program)
    {
        var text = Petscii.ToText(petsciiName);
        if (FileName(petsciiName) is not { } name)
        {
            activity.Add("save", $"SAVE \"{text}\": not a file name the server can save, the C64 tries its own drive");
            return "bad file name";
        }

        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        var blocks = (program.Length + 253) / 254;
        try
        {
            string target;
            if (isDiskImage)
            {
                var image = DiskImage.Load(path);
                image.WriteProgram(name, program);
                Backup(path);
                var temporary = path + ".saving";
                File.WriteAllBytes(temporary, image.Bytes);
                File.Move(temporary, path, overwrite: true);
                target = Path.GetFileName(path);
            }
            else
            {
                if (path == catalog.PluginFolder || !Directory.Exists(path))
                    throw new InvalidOperationException("can't save in this folder");
                var file = Directory.EnumerateFiles(path, "*.prg").FirstOrDefault(f => Petscii.FileName(Path.GetFileName(f)).SequenceEqual(name))
                           ?? Path.Combine(path, HostName(name) + ".prg");
                File.WriteAllBytes(file, program);
                target = Path.Combine(catalog.NameOf(Section.Programs, folder), Path.GetFileName(file));
            }

            logger.LogInformation("SAVE \"{Name}\" to {Target}, {Blocks} blocks", text, target, blocks);
            activity.Add("save", $"SAVE \"{Petscii.ToText(name)}\" to {target} ({blocks} block{(blocks == 1 ? "" : "s")})");
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "SAVE \"{Name}\" failed", text);
            activity.Add("error", $"SAVE \"{text}\" failed: {e.Message}; the C64 tries its own drive");
            return e.Message;
        }
    }

    /// <summary>"@0:NAME", "0:NAME" and "NAME,P,W" mean NAME; null for an empty name or wildcards.</summary>
    static byte[]? FileName(byte[] petsciiName)
    {
        var name = petsciiName.AsSpan();
        if (name is [(byte)'@', ..])
            name = name[1..];
        var colon = name.IndexOf((byte)':');
        if (colon is 0 or 1)
            name = name[(colon + 1)..];
        var comma = name.IndexOf((byte)',');
        if (comma >= 0)
            name = name[..comma];
        return name.Length is 0 or > 16 || name.IndexOfAny((byte)'*', (byte)'?') >= 0 ? null : name.ToArray();
    }

    /// <summary>A file name on the computer for a PETSCII name: lower case, without characters file systems refuse.</summary>
    static string HostName(byte[] name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var text = new string(Petscii.ToText(name).Select(c => invalid.Contains(c) || c < ' ' ? '-' : c).ToArray()).Trim(' ', '.');
        return text.Length == 0 ? "saved" : text;
    }

    /// <summary>The image as it was before the server first saved into it, in .backups next to it.</summary>
    static void Backup(string imagePath)
    {
        var folder = Path.Combine(Path.GetDirectoryName(imagePath)!, ".backups");
        var backup = Path.Combine(folder, Path.GetFileName(imagePath));
        if (File.Exists(backup))
            return;
        Directory.CreateDirectory(folder);
        File.Copy(imagePath, backup);
    }
}
