using Wic64Server.Activity;
using Wic64Server.Content;

namespace Wic64Server.Programs;

/// <summary>
/// Files for the C64's LOAD helper (c64/loadhelper.asm) and file helper (c64/filehelper.asm): programs started from
/// the browser can LOAD more files, and read SEQ files with OPEN, from the disk image or folder they came from, as if
/// it was in drive 8.
/// </summary>
public sealed class LoadService(Catalog catalog, ServerOptions options, ActivityLog activity, ILogger<LoadService> logger)
{
    const int LowPartSize = 0x0300 - 0x02a7;
    const int HighPartSize = 0x03fc - 0x0334;

    /// <summary>Bytes per request of the file helper (CHUNK in c64/filehelper.asm).</summary>
    public const int ChunkSize = 64;

    /// <summary>What the starter gets when there is no file helper or LOAD guard: an RTS at $03fc, its "entry".</summary>
    static readonly byte[] NoModule = [0xfc, 0x03, 0x60, 0x00, 0x00];

    static readonly string[] FolderFileExtensions = [".prg", ".seq", ".usr"];

    readonly Lock startedLock = new();
    (string Name, byte[] Program)? started;
    (string Key, byte[] Data)? openFile;
    (int Page, int Pages)? fileHelperPlace; // where the file helper went, so the LOAD guard goes elsewhere

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

    /// <summary>The program the browser starts: the file helper and LOAD guard must not go where it lives.</summary>
    public void Started(string name, byte[] program)
    {
        lock (startedLock)
            started = (name, program);
    }

    /// <summary>
    /// The file helper as a .prg, for the starter (file name $00 from the LOAD helper): relocated to free pages when
    /// the disk has SEQ or USR files, otherwise only an RTS. Only this disk counts, not the other images next to it:
    /// those are often unrelated.
    /// </summary>
    public byte[] FileHelper(int folder)
    {
        lock (startedLock)
            fileHelperPlace = null;

        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        var hasDataFiles = isDiskImage
            ? DiskImage.Load(path).Files().Any(f => f.Type != FileType.Prg)
            : System.IO.Directory.Exists(path) && System.IO.Directory.EnumerateFiles(path)
                .Any(f => Path.GetExtension(f).ToLowerInvariant() is ".seq" or ".usr");
        if (!hasDataFiles)
            return NoModule;

        var (programs, _) = ProgramsAndStarted(path, isDiskImage);
        if (Relocate("filehelper", Scan(programs)) is not { } relocated)
        {
            logger.LogInformation("No free memory for the file helper in {Folder}", path);
            activity.Add("load", "No free memory for the file helper: the program can't read SEQ files from the server");
            return NoModule;
        }

        var (module, place) = relocated;
        lock (startedLock)
            fileHelperPlace = place;
        activity.Add("load", $"File helper for SEQ files at {Range(place)}");
        return module;
    }

    /// <summary>
    /// The LOAD guard as a .prg, for the starter (file name $01, right after the file helper): for BASIC programs that
    /// use INPUT or INPUT# and LOAD, which would overwrite the LOAD helper's request in $0200. Otherwise only an RTS.
    /// On a disk image all its BASIC programs count (one may INPUT, the next LOAD); in a folder only the program itself.
    /// </summary>
    public byte[] LoadGuard(int folder)
    {
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        var (programs, startedProgram) = ProgramsAndStarted(path, isDiskImage);
        if (startedProgram is null || !MemoryScan.IsBasic(startedProgram))
            return NoModule;

        var tokens = MemoryScan.BasicTokens(startedProgram);
        if (isDiskImage)
        {
            foreach (var (_, program) in programs)
                tokens.UnionWith(MemoryScan.BasicTokens(program));
        }

        if (!tokens.Contains(MemoryScan.TokenLoad) || !tokens.Overlaps([MemoryScan.TokenInput, MemoryScan.TokenInputFile]))
            return NoModule;

        var scan = Scan(programs);
        lock (startedLock)
        {
            if (fileHelperPlace is { } fileHelper)
                scan.Use(fileHelper.Page, fileHelper.Pages);
            fileHelperPlace = null;
        }

        if (Relocate("loadguard", scan) is not { } relocated)
        {
            activity.Add("load", "No free memory for the LOAD guard: LOADs after an INPUT may fail");
            return NoModule;
        }

        var (module, place) = relocated;
        activity.Add("load", $"LOAD guard for a BASIC program with INPUT at {Range(place)}");
        return module;
    }

    static string Range((int Page, int Pages) place) => $"${place.Page << 8:x4}-${(place.Page + place.Pages << 8) - 1:x4}";

    /// <summary>The programs on the disk or folder, plus the program that is starting (which may come from elsewhere).</summary>
    (List<(string Name, byte[] Program)> Programs, byte[]? Started) ProgramsAndStarted(string path, bool isDiskImage)
    {
        var programs = Programs(path, isDiskImage).ToList();
        lock (startedLock)
        {
            if (started is not { } program)
                return (programs, null);
            programs.Add(program);
            return (programs, program.Program);
        }
    }

    /// <summary>
    /// A module (build/{name}.bin) as a .prg on the first free pages, or null when there is no room. The build at $1100
    /// has the high bytes of the module's own addresses one higher than the build at $1000.
    /// </summary>
    (byte[] Module, (int Page, int Pages) Place)? Relocate(string name, MemoryScan scan)
    {
        var code = Build($"{name}.bin");
        var relocations = Build($"{name}-1100.bin");
        var pages = (code.Length + 0xff) >> 8;
        if (scan.FreePages(pages) is not { } page)
            return null;

        var result = new byte[code.Length + 2];
        result[0] = 0;
        result[1] = (byte)page;
        for (var i = 0; i < code.Length; i++)
        {
            result[i + 2] = relocations[i] == code[i] ? code[i]
                : relocations[i] == (byte)(code[i] + 1) ? (byte)(code[i] - 0x10 + page)
                : throw new InvalidDataException($"the {name} builds differ in more than addresses, run make");
        }

        logger.LogInformation("{Name} at ${Start:x4}", name, page << 8);
        return (result, (page, pages));
    }

    byte[] Build(string name)
    {
        var path = Path.Combine(options.BuildFolder, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : throw new FileNotFoundException($"{name} missing, run make", path);
    }

    /// <summary>
    /// A warning for the menu of a disk image whose programs LOAD more files but overwrite the LOAD helper: loading
    /// then hangs. Only for disk images, where the programs belong together; not for folders of unrelated programs.
    /// </summary>
    public string? Warning(int folder)
    {
        try
        {
            var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
            return isDiskImage && Scan(Programs(path, isDiskImage)).HelperAtRisk ? "! Overwrites the LOAD helper: may hang" : null;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not scan folder {Folder}", folder);
            return null;
        }
    }

    /// <summary>A single program that LOADs more files but overwrites the LOAD helper.</summary>
    public static bool OverwritesHelper(string name, byte[] program) => Scan([(name, program)]).HelperAtRisk;

    static MemoryScan Scan(IEnumerable<(string Name, byte[] Program)> programs)
    {
        var scan = new MemoryScan();
        foreach (var (name, program) in programs)
            scan.Add(name, program);
        return scan;
    }

    /// <summary>The programs on a disk image, or in a folder.</summary>
    static IEnumerable<(string Name, byte[] Program)> Programs(string path, bool isDiskImage)
    {
        if (isDiskImage)
        {
            var image = DiskImage.Load(path);
            foreach (var file in image.Programs())
                yield return (file.DisplayName, image.Read(file));
        }
        else if (System.IO.Directory.Exists(path))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(path, "*.prg"))
                yield return (Path.GetFileName(file), File.ReadAllBytes(file));
        }
    }

    /// <summary>The image a program came from, then the other images next to it (other disk sides).</summary>
    static IEnumerable<string> ImagesFrom(string path) =>
        new[] { path }.Concat(DiskImage.In(Path.GetDirectoryName(path)!)
            .Where(other => other != path)
            .OrderBy(other => other, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Chunk <paramref name="chunk"/> of a file the file helper OPENs, and whether it is the last one; null when the
    /// server does not have the file (or it is opened for writing): the C64 then tries its own drive.
    /// </summary>
    public (byte[] Data, bool Last)? ReadChunk(int folder, int chunk, byte[] petsciiName)
    {
        var key = $"{folder}:{Convert.ToHexString(petsciiName)}";
        byte[]? data;
        lock (startedLock)
            data = openFile is { } open && open.Key == key && chunk > 0 ? open.Data : null;

        if (data is null)
        {
            data = Open(folder, petsciiName);
            if (data is null)
                return null;
            lock (startedLock)
                openFile = (key, data);
        }

        var start = Math.Min(chunk * ChunkSize, data.Length);
        var end = Math.Min(start + ChunkSize, data.Length);
        return (data[start..end], end == data.Length);
    }

    /// <summary>The contents of a file for OPEN, by CBM DOS rules: "0:NAME,S,R", wildcards, "$" for the directory.</summary>
    byte[]? Open(int folder, byte[] petsciiName)
    {
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        var text = Petscii.ToText(petsciiName);

        if (petsciiName is [(byte)'$', ..])
        {
            activity.Add("load", $"OPEN \"{text}\" (directory)");
            return Directory(path, isDiskImage);
        }

        if (ParseOpenName(petsciiName) is not { } parsed)
        {
            activity.Add("load", $"OPEN \"{text}\": not for reading, the C64 tries its own drive");
            return null;
        }

        var (name, type) = parsed;

        if (isDiskImage)
        {
            foreach (var imagePath in ImagesFrom(path))
            {
                var image = DiskImage.Load(imagePath);
                if (image.Files().FirstOrDefault(f => (type is null || f.Type == type) && Matches(name, f.Name)) is { } found)
                {
                    activity.Add("load", $"OPEN \"{text}\" from {Path.GetFileName(imagePath)}");
                    return image.Read(found);
                }
            }
        }
        else if (System.IO.Directory.Exists(path))
        {
            var file = System.IO.Directory.EnumerateFiles(path)
                .Where(f => FolderFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => type is null || TypeOf(f) == type)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => Matches(name, Petscii.FileName(Path.GetFileName(f))));
            if (file is not null)
            {
                activity.Add("load", $"OPEN \"{text}\" from {Path.GetFileName(file)}");
                return File.ReadAllBytes(file);
            }
        }

        activity.Add("load", $"OPEN \"{text}\": not on the server, the C64 tries its own drive");
        return null;
    }

    static FileType TypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".seq" => FileType.Seq,
        ".usr" => FileType.Usr,
        _ => FileType.Prg,
    };

    /// <summary>"0:NAME,S,R" → NAME and SEQ; null for writing (",W", ",A", "@0:"), REL files (",L") and channels ("#").</summary>
    static (byte[] Name, FileType? Type)? ParseOpenName(byte[] petsciiName)
    {
        var parts = Split(petsciiName, (byte)',');
        var name = StripDrive(parts[0]);
        if (name.Length == 0 || petsciiName[0] is (byte)'@' or (byte)'#')
            return null;

        FileType? type = null;
        foreach (var part in parts.Skip(1))
        {
            switch (part is [] ? (byte)0 : part[0])
            {
                case (byte)'S': type = FileType.Seq; break;
                case (byte)'P': type = FileType.Prg; break;
                case (byte)'U': type = FileType.Usr; break;
                case (byte)'R' or 0: break;
                default: return null;
            }
        }

        return (name, type);
    }

    static List<byte[]> Split(byte[] bytes, byte separator)
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

    /// <summary>The .prg file for a LOAD, or null when the server does not have it.</summary>
    public byte[]? Load(int folder, byte[] petsciiName)
    {
        if (petsciiName is [0])
            return FileHelper(folder);
        if (petsciiName is [1])
            return LoadGuard(folder);

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
            foreach (var imagePath in ImagesFrom(path))
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
        IEnumerable<(byte[] Name, int Blocks, FileType Type)> files;
        byte[] title;
        if (isDiskImage)
        {
            var image = DiskImage.Load(path);
            files = image.Files().Select(f => (f.Name, f.Blocks, f.Type));
            title = image.Title();
        }
        else
        {
            files = System.IO.Directory.Exists(path)
                ? System.IO.Directory.EnumerateFiles(path)
                    .Where(f => FolderFileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (Petscii.FileName(Path.GetFileName(f)), (int)((new FileInfo(f).Length + 253) / 254), TypeOf(f)))
                : [];
            var name = Petscii.FileName(Path.GetFileName(path) + ".dir"); // FileName drops the "extension"
            title = [.. name, .. Enumerable.Repeat((byte)' ', 16 - name.Length), .. " WC 2A"u8];
        }

        var lines = new List<(int Number, byte[] Text)> { (0, [0x12, (byte)'"', .. title[..16], (byte)'"', .. title[16..]]) };
        foreach (var (name, blocks, type) in files)
        {
            var text = new List<byte>();
            text.AddRange(Enumerable.Repeat((byte)' ', blocks < 10 ? 3 : blocks < 100 ? 2 : 1));
            text.Add((byte)'"');
            text.AddRange(name);
            text.Add((byte)'"');
            text.AddRange(Enumerable.Repeat((byte)' ', 17 - name.Length));
            text.AddRange(System.Text.Encoding.ASCII.GetBytes(type.ToString().ToUpperInvariant()));
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
