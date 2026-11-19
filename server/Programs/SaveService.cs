using System.Text;
using Wic64Server.Activity;
using Wic64Server.Content;

namespace Wic64Server.Programs;

/// <summary>
/// Writing to the folder or disk image a program came from, like a real drive would:
/// <list type="bullet">
/// <item>SAVEs from the save helper (c64/savehelper.asm): memory in parts of up to 32 KB, written as a PRG file.</item>
/// <item>Files the file helper (c64/filehelper.asm) OPENs for writing ("NAME,S,W", ",A" appends): 64 bytes at a time,
/// written at CLOSE.</item>
/// <item>Commands on channel 15: S (scratch), R (rename), I/V/UI (only answered), with the drive status.</item>
/// </list>
/// A file with the same name is replaced (games usually scratch the old one first). Before the first change to a disk
/// image, the original is copied to the hidden .backups folder next to it; files scratched in a folder go to the hidden
/// .scratched folder instead of being deleted.
/// </summary>
public sealed class SaveService(Catalog catalog, ActivityLog activity, ILogger<SaveService> logger)
{
    readonly Lock saveLock = new();
    (string Key, int NextPart, List<byte> Data)? pendingSave;
    (string Key, byte[] Name, FileType Type, int NextPart, List<byte> Data)? openWrite;

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
                pendingSave = (key, 0, []);
            if (pendingSave is not { } current || current.Key != key || current.NextPart != number)
            {
                pendingSave = null;
                return "a part of the file is missing";
            }

            current.Data.AddRange(data);
            pendingSave = current with { NextPart = number + 1 };
            if ((part & 0x80) == 0)
                return null;

            pendingSave = null;
            var text = Petscii.ToText(petsciiName);
            if (CbmFileName.Parse(petsciiName) is not { HasWildcards: false } name)
            {
                activity.Add("save", $"SAVE \"{text}\": not a file name the server can save, the C64 tries its own drive");
                return "bad file name";
            }

            return Write(folder, name.Name, [(byte)start, (byte)(start >> 8), .. current.Data], FileType.Prg, "SAVE");
        }
    }

    /// <summary>
    /// OPEN of a file for writing ("NAME,S,W", or ",A" to append): true when the server takes it, false when it can't
    /// (the C64 then tries its real drive), null when it is not opened for writing.
    /// </summary>
    public bool? BeginWrite(int folder, byte[] petsciiName)
    {
        if (CbmFileName.Parse(petsciiName) is not { Mode: 'W' or 'A' } name)
            return null;

        var text = Petscii.ToText(petsciiName);
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        if (name.HasWildcards || (!isDiskImage && (path == catalog.PluginFolder || !Directory.Exists(path))))
        {
            activity.Add("save", $"OPEN \"{text}\": can't write here, the C64 tries its own drive");
            return false;
        }

        var type = name.Type ?? FileType.Seq;
        List<byte> data = name.Mode == 'A' && Read(path, isDiskImage, name.Name) is { } existing ? [.. existing] : [];
        lock (saveLock)
            openWrite = ($"{folder}:{Convert.ToHexString(petsciiName)}", name.Name, type, 0, data);
        activity.Add("save", $"OPEN \"{text}\" for {(name.Mode == 'A' ? "appending" : "writing")}");
        return true;
    }

    /// <summary>
    /// Bytes written to the file opened with <see cref="BeginWrite"/>; bit 15 of <paramref name="part"/> marks the
    /// last part (CLOSE), which writes the file. Null when fine, otherwise why not.
    /// </summary>
    public string? WritePart(int folder, int part, byte[] petsciiName, byte[] data)
    {
        lock (saveLock)
        {
            var number = part & 0x7fff;
            if (openWrite is not { } current || current.Key != $"{folder}:{Convert.ToHexString(petsciiName)}" || current.NextPart != number)
            {
                openWrite = null;
                return "file not open";
            }

            current.Data.AddRange(data);
            openWrite = current with { NextPart = number + 1 };
            if ((part & 0x8000) == 0)
                return null;

            openWrite = null;
            return Write(folder, current.Name, current.Data.ToArray(), current.Type, "CLOSE");
        }
    }

    /// <summary>A command on channel 15; the drive status it leaves, with its RETURN.</summary>
    public byte[] Command(int folder, byte[] command)
    {
        var text = Encoding.ASCII.GetString(command).TrimEnd('\r', ' ');
        var status = RunCommand(folder, Encoding.ASCII.GetBytes(text)) ?? "00, OK,00,00";
        activity.Add("save", $"Command \"{Petscii.ToText(Encoding.ASCII.GetBytes(text))}\": {status}");
        return Encoding.ASCII.GetBytes(status + "\r");
    }

    string? RunCommand(int folder, byte[] command)
    {
        if (command.Length == 0)
            return null;

        var text = Encoding.ASCII.GetString(command);
        var colon = Array.IndexOf(command, (byte)':');
        var arguments = colon < 0 ? [] : command[(colon + 1)..];
        switch ((char)command[0])
        {
            case 'S' when colon > 0:
            {
                var patterns = arguments.Split((byte)',').Select(p => CbmFileName.Parse(p)?.Name).OfType<byte[]>().ToList();
                lock (saveLock)
                    return $"01, FILES SCRATCHED,{Scratch(folder, patterns):00},00";
            }
            case 'R' when colon > 0 && Array.IndexOf(arguments, (byte)'=') is > 0 and var equals:
            {
                if (CbmFileName.Parse(arguments[..equals]) is not { HasWildcards: false } newName
                    || CbmFileName.Parse(arguments[(equals + 1)..]) is not { HasWildcards: false } oldName)
                    return "31,SYNTAX ERROR,00,00";
                lock (saveLock)
                    return Rename(folder, newName.Name, oldName.Name);
            }
            case 'I' or 'V':
                return null;
            case 'U' when text is "UI" or "UJ" or "U:" or "U;" || text.StartsWith("UI") || text.StartsWith("UJ"):
                return "73,CBM DOS V2.6 1541,00,00";
            case 'N':
                return "26,WRITE PROTECT ON,00,00"; // the server never formats
            default:
                return "31,SYNTAX ERROR,00,00";
        }
    }

    int Scratch(int folder, List<byte[]> patterns)
    {
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        if (isDiskImage)
        {
            var count = 0;
            UpdateImage(path, image => count = image.Scratch(patterns));
            return count;
        }

        var files = HostFiles(path).Where(f => patterns.Any(p => CbmFileName.Matches(p, Petscii.FileName(Path.GetFileName(f))))).ToList();
        if (files.Count > 0)
        {
            var trash = Path.Combine(path, ".scratched");
            Directory.CreateDirectory(trash);
            foreach (var file in files)
                File.Move(file, Path.Combine(trash, Path.GetFileName(file)), overwrite: true);
        }

        return files.Count;
    }

    string? Rename(int folder, byte[] newName, byte[] oldName)
    {
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        try
        {
            if (isDiskImage)
            {
                var found = false;
                UpdateImage(path, image => found = image.Rename(newName, oldName));
                return found ? null : "62,FILE NOT FOUND,00,00";
            }

            if (HostFile(path, newName) is not null)
                return "63,FILE EXISTS,00,00";
            if (HostFile(path, oldName) is not { } file)
                return "62,FILE NOT FOUND,00,00";
            File.Move(file, Path.Combine(path, HostName(newName) + Path.GetExtension(file)));
            return null;
        }
        catch (InvalidOperationException)
        {
            return "63,FILE EXISTS,00,00";
        }
    }

    /// <summary>Writes a file into the folder or disk image; null when fine, otherwise why not.</summary>
    string? Write(int folder, byte[] name, byte[] data, FileType type, string what)
    {
        var text = Petscii.ToText(name);
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        var blocks = Math.Max(1, (data.Length + 253) / 254);
        try
        {
            string target;
            if (isDiskImage)
            {
                UpdateImage(path, image => image.WriteFile(name, data, type));
                target = Path.GetFileName(path);
            }
            else
            {
                if (path == catalog.PluginFolder || !Directory.Exists(path))
                    throw new InvalidOperationException("can't save in this folder");
                var existing = HostFile(path, name);
                var file = Path.Combine(path, (existing is null ? HostName(name) : Path.GetFileNameWithoutExtension(existing)) + Extension(type));
                File.WriteAllBytes(file, data);
                if (existing is not null && existing != file)
                    File.Delete(existing); // same name, other type
                target = Path.Combine(catalog.NameOf(Section.Programs, folder), Path.GetFileName(file));
            }

            logger.LogInformation("{What} \"{Name}\" to {Target}, {Blocks} blocks", what, text, target, blocks);
            activity.Add("save", $"{what} \"{text}\" to {target} ({type.ToString().ToUpperInvariant()}, {blocks} block{(blocks == 1 ? "" : "s")})");
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "{What} \"{Name}\" failed", what, text);
            activity.Add("error", $"{what} \"{text}\" failed: {e.Message}");
            return e.Message;
        }
    }

    /// <summary>The contents of an existing file, for appending.</summary>
    static byte[]? Read(string path, bool isDiskImage, byte[] name)
    {
        if (isDiskImage)
        {
            var image = DiskImage.Load(path);
            return image.Files().FirstOrDefault(f => f.Name.SequenceEqual(name)) is { } file ? image.Read(file) : null;
        }

        return HostFile(path, name) is { } existing ? File.ReadAllBytes(existing) : null;
    }

    /// <summary>Changes a disk image: the original goes to .backups first; the new image replaces it in one step.</summary>
    static void UpdateImage(string path, Action<DiskImage> change)
    {
        var image = DiskImage.Load(path);
        change(image);
        Backup(path);
        var temporary = path + ".saving";
        File.WriteAllBytes(temporary, image.Bytes);
        File.Move(temporary, path, overwrite: true);
    }

    static IEnumerable<string> HostFiles(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => LoadService.FolderFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : [];

    /// <summary>The file on the computer with this PETSCII name, of any type.</summary>
    static string? HostFile(string folder, byte[] name) =>
        HostFiles(folder).FirstOrDefault(f => Petscii.FileName(Path.GetFileName(f)).SequenceEqual(name));

    static string Extension(FileType type) => type switch
    {
        FileType.Seq => ".seq",
        FileType.Usr => ".usr",
        _ => ".prg",
    };

    /// <summary>A file name on the computer for a PETSCII name: lower case, without characters file systems refuse.</summary>
    static string HostName(byte[] name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var text = new string(Petscii.ToText(name).Select(c => invalid.Contains(c) || c < ' ' ? '-' : c).ToArray()).Trim(' ', '.');
        return text.Length == 0 ? "saved" : text;
    }

    /// <summary>The image as it was before the server first changed it, in .backups next to it.</summary>
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
