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

    public static readonly string[] FolderFileExtensions = [".prg", ".seq", ".usr"];

    readonly Lock startedLock = new();
    (string Name, byte[] Program)? started;
    (string Key, byte[] Data)? openFile;
    readonly List<(int Page, int Pages)> placed = []; // where the starter's modules went, so the next ones go elsewhere
    int? stagingPage; // where the launcher of an unpacked program stages the RAM under the I/O area
    byte[]? loadModule; // the LOAD module for the program that is starting: placed first, asked for last

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
        if (Unpacker.Unpack(program) is { } unpacked)
            activity.Add("load", $"{name} is packed: unpacked on the server, {unpacked.Start}");
    }

    /// <summary>
    /// For the program that is starting, when it is packed (see <see cref="Unpacker"/>): its unpacked memory below
    /// (part 0) and above (part 1) the I/O area, which the starter loads over the packed program before the helpers
    /// (file names $10 and $11), and the launcher that starts it, after them ($12); the launcher itself LOADs the RAM
    /// under the I/O area through 16 staging pages ($13, then $14 for those pages' own contents). Otherwise only an RTS; but as
    /// the last module ($12), code that sets the end of the LOAD ($ae/$af) to the program's end, like the KERNAL's
    /// LOAD leaves it: unpackers read it, and loading the modules changed it.
    /// </summary>
    public byte[] UnpackedPart(int part)
    {
        lock (startedLock)
        {
            if (started is not { } program)
                return NoModule;
            if (Unpacker.Unpack(program.Program) is { } unpacked)
            {
                switch (part)
                {
                    case 0 or 1:
                        return part < unpacked.Parts.Count ? unpacked.Parts[part] : NoModule;
                    case 2:
                        // 16 pages below the I/O area that no helper sits on, to pass the RAM under the I/O area through
                        stagingPage = unpacked.UnderIo
                            ? Enumerable.Range(0x08, 0xc0 - 0x08 + 1).Cast<int?>().FirstOrDefault(first =>
                                !placed.Any(p => p.Page < first + 16 && first < p.Page + p.Pages))
                            : null;
                        return unpacked.Launch(stagingPage);
                    case 3 or 4 when stagingPage is { } page:
                        return unpacked.Pages(page, underIo: part == 3);
                    default:
                        return NoModule;
                }
            }

            if (part != 2 || program.Program.Length < 3)
                return NoModule;

            // In the stack page between the starter (to $01c3) and the stack: lda #<end, sta $ae, lda #>end, sta $af,
            // rts; the entry: jmp $01d0
            var end = (program.Program[0] | program.Program[1] << 8) + program.Program.Length - 2;
            return [0xd0, 0x01, 0xa9, (byte)end, 0x85, 0xae, 0xa9, (byte)(end >> 8), 0x85, 0xaf, 0x60, 0x4c, 0xd0, 0x01];
        }
    }

    /// <summary>
    /// The file helper as a .prg, for the starter (file name $00 from the LOAD helper): relocated to free pages when
    /// the disk has SEQ or USR files or its programs OPEN files (the BASIC keyword or a call of the KERNAL's OPEN),
    /// otherwise only an RTS. Only this disk counts, not the other images next to it:
    /// those are often unrelated.
    /// </summary>
    public byte[] FileHelper(int folder)
    {
        lock (startedLock)
            placed.Clear(); // the first module the starter asks for

        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        PlaceLoadModule(path, isDiskImage);
        var hasDataFiles = isDiskImage
            ? DiskImage.Load(path).Files().Any(f => f.Type != FileType.Prg)
            : System.IO.Directory.Exists(path) && System.IO.Directory.EnumerateFiles(path)
                .Any(f => Path.GetExtension(f).ToLowerInvariant() is ".seq" or ".usr");
        var (programs, startedProgram) = ProgramsAndStarted(path, isDiskImage);
        var opensFiles = (isDiskImage ? programs.Select(p => p.Program) : startedProgram is null ? [] : [startedProgram])
            .Any(program => MemoryScan.BasicTokens(program).Contains(MemoryScan.TokenOpen) || Scan([("", program)]).OpensFiles);
        if (!hasDataFiles && !opensFiles)
            return NoModule;

        if (Relocate("filehelper", ScanBesidesPlaced(programs)) is not { } relocated)
        {
            logger.LogInformation("No free memory for the file helper in {Folder}", path);
            activity.Add("load", "No free memory for the file helper: OPEN goes to the real drive");
            return NoModule;
        }

        var (module, place) = relocated;
        activity.Add("load", $"File helper (OPEN, SEQ files, channel 15) at {Range(place)}");
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
        lock (startedLock)
        {
            if (loadModule is not null)
                return NoModule; // the LOAD module has its own copy of the request
        }

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

        if (Relocate("loadguard", ScanBesidesPlaced(programs)) is not { } relocated)
        {
            activity.Add("load", "No free memory for the LOAD guard: LOADs after an INPUT may fail");
            return NoModule;
        }

        var (module, place) = relocated;
        activity.Add("load", $"LOAD guard for a BASIC program with INPUT at {Range(place)}");
        return module;
    }

    /// <summary>
    /// The save helper as a .prg, for the starter (file name $02): for programs that SAVE (the BASIC keyword, or a
    /// call of the KERNAL's SAVE), so they save to the server. Otherwise only an RTS. On a disk image all its programs
    /// count; in a folder only the program itself.
    /// </summary>
    public byte[] SaveHelper(int folder)
    {
        var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
        var (programs, startedProgram) = ProgramsAndStarted(path, isDiskImage);
        if (startedProgram is null)
            return NoModule;

        var candidates = isDiskImage ? programs.Select(p => p.Program) : [startedProgram];
        if (!candidates.Any(program => MemoryScan.BasicTokens(program).Contains(MemoryScan.TokenSave) || Scan([("", program)]).SavesFiles))
            return NoModule;

        if (Relocate("savehelper", ScanBesidesPlaced(programs)) is not { } relocated)
        {
            activity.Add("load", "No free memory for the save helper: SAVEs go to the real drive");
            return NoModule;
        }

        var (module, place) = relocated;
        activity.Add("load", $"Save helper at {Range(place)}: SAVE goes to {Path.GetFileName(path)}");
        return module;
    }

    /// <summary>
    /// The LOAD module (c64/loadmodule.asm) for programs that LOAD more files but overwrite the LOAD helper in the tape
    /// buffer: the same LOAD from free pages. It is placed before the other modules, as LOAD matters most, and handed
    /// out last (file name $03). On a disk image all its programs count; in a folder only the program itself.
    /// </summary>
    void PlaceLoadModule(string path, bool isDiskImage)
    {
        lock (startedLock)
            loadModule = null;

        var (programs, startedProgram) = ProgramsAndStarted(path, isDiskImage);
        var atRisk = isDiskImage ? Scan(programs).HelperAtRisk
            : startedProgram is not null && Scan([("", startedProgram)]).HelperAtRisk;
        if (!atRisk)
            return;

        if (Relocate("loadmodule", ScanBesidesPlaced(programs)) is not var (module, place))
        {
            activity.Add("error", "No free memory to move the LOAD helper: loading more files may hang (use a real drive)");
            return;
        }

        lock (startedLock)
            loadModule = module;
        activity.Add("load", $"The program overwrites the LOAD helper: LOAD module at {Range(place)} instead");
    }

    /// <summary>The LOAD module as a .prg, for the starter (file name $03); only an RTS when the LOAD helper can stay.</summary>
    public byte[] LoadModule()
    {
        lock (startedLock)
            return loadModule ?? NoModule;
    }

    /// <summary>Programs that overwrite the LOAD helper and leave no room for the LOAD module: loading may hang.</summary>
    static bool CantMoveHelper(IEnumerable<(string Name, byte[] Program)> programs)
    {
        var scan = Scan(programs);
        return scan.HelperAtRisk && scan.FreePages(LoadModulePages) is null;
    }

    /// <summary>Pages of the LOAD module (c64/loadmodule.asm is under 512 bytes).</summary>
    const int LoadModulePages = 2;

    /// <summary>What the programs use, plus the pages of the modules placed before; marks the new module's pages.</summary>
    MemoryScan ScanBesidesPlaced(IEnumerable<(string Name, byte[] Program)> programs)
    {
        var scan = Scan(programs);
        lock (startedLock)
        {
            foreach (var (page, pages) in placed)
                scan.Use(page, pages);
        }

        return scan;
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
        lock (startedLock)
            placed.Add((page, pages));
        return (result, (page, pages));
    }

    byte[] Build(string name)
    {
        var path = Path.Combine(options.BuildFolder, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : throw new FileNotFoundException($"{name} missing, run make", path);
    }

    /// <summary>Blocks outside any file above which a disk needs its own loader (and so a real drive).</summary>
    const int OwnLoaderBlocks = 16;

    /// <summary>
    /// A warning for the menu of a disk image: it has its own loader (trackmo demos, original games), seen from data
    /// outside its files or code sent into the drive, so it needs a real drive; or its programs LOAD more files but
    /// overwrite the LOAD helper and leave no room to move it (the LOAD module), so loading hangs. Only for disk
    /// images, where the programs belong together; not for folders of unrelated programs.
    /// </summary>
    public string? Warning(int folder)
    {
        try
        {
            var (path, isDiskImage) = catalog.PathOf(Section.Programs, folder);
            if (!isDiskImage)
                return null;
            var programs = Programs(path, isDiskImage).ToList();
            if (Scan(programs).SendsDriveCode || DiskImage.Load(path).HiddenBlocks() > OwnLoaderBlocks)
                return "! Own loader: write it to a real disk";
            return CantMoveHelper(programs) ? "! Overwrites the LOAD helper: may hang" : null;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not scan folder {Folder}", folder);
            return null;
        }
    }

    /// <summary>A single program that LOADs more files, overwrites the LOAD helper and leaves no room to move it.</summary>
    public static bool OverwritesHelper(string name, byte[] program) => CantMoveHelper([(name, program)]);

    static MemoryScan Scan(IEnumerable<(string Name, byte[] Program)> programs)
    {
        var scan = new MemoryScan();
        foreach (var (name, program) in programs)
        {
            if (Unpacker.Unpack(program) is { } unpacked)
                scan.AddMemory(name, unpacked.Memory, unpacked.Code); // what the real program uses, not what its packed bytes look like
            else
                scan.Add(name, program);
        }

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

        if (CbmFileName.Parse(petsciiName) is not { Mode: 'R' } parsed)
        {
            activity.Add("load", $"OPEN \"{text}\": not for reading, the C64 tries its own drive");
            return null;
        }

        var (name, type) = (parsed.Name, parsed.Type);

        if (isDiskImage)
        {
            foreach (var imagePath in ImagesFrom(path))
            {
                var image = DiskImage.Load(imagePath);
                if (image.Files().FirstOrDefault(f => (type is null || f.Type == type) && CbmFileName.Matches(name, f.Name)) is { } found)
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
                .FirstOrDefault(f => CbmFileName.Matches(name, Petscii.FileName(Path.GetFileName(f))));
            if (file is not null)
            {
                activity.Add("load", $"OPEN \"{text}\" from {Path.GetFileName(file)}");
                return File.ReadAllBytes(file);
            }
        }

        activity.Add("load", $"OPEN \"{text}\": not on the server, the C64 tries its own drive");
        return null;
    }

    public static FileType TypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".seq" => FileType.Seq,
        ".usr" => FileType.Usr,
        _ => FileType.Prg,
    };

    /// <summary>The .prg file for a LOAD, or null when the server does not have it.</summary>
    public byte[]? Load(int folder, byte[] petsciiName)
    {
        if (petsciiName is [0])
            return FileHelper(folder);
        if (petsciiName is [1])
            return LoadGuard(folder);
        if (petsciiName is [2])
            return SaveHelper(folder);
        if (petsciiName is [3])
            return LoadModule();
        if (petsciiName is [>= 0x10 and <= 0x14])
            return UnpackedPart(petsciiName[0] - 0x10);

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
                if (image.Programs().FirstOrDefault(file => CbmFileName.Matches(name, file.Name)) is { } found)
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
                .FirstOrDefault(f => CbmFileName.Matches(name, Petscii.FileName(Path.GetFileName(f))));
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
