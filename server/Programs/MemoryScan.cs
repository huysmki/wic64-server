namespace Wic64Server.Programs;

/// <summary>
/// What the programs of a disk (or folder) do with memory, guessed from their bytes: where they load, which pages
/// their instructions address, whether they write into the LOAD helper and whether they LOAD more files. Every byte
/// of a file that looks like code is read as if an instruction could start there, so data inside it counts too: the
/// result errs on the side of "used". Packed programs are unpacked first (see Unpacker).
/// </summary>
public sealed class MemoryScan
{
    /// <summary>The LOAD helper (c64/loadhelper.asm) in the tape buffer and the unused area below it.</summary>
    static readonly (int From, int To)[] HelperAreas = [(0x02a7, 0x02ff), (0x0334, 0x03fb)];

    /// <summary>Opcodes with a 16-bit address: absolute, absolute,X, absolute,Y and JMP (indirect).</summary>
    static readonly HashSet<byte> AddressOpcodes =
    [
        0x0d, 0x0e, 0x20, 0x2c, 0x2d, 0x2e, 0x4c, 0x4d, 0x4e, 0x6d, 0x6e, 0x8c, 0x8d, 0x8e, 0xac, 0xad, 0xae, 0xcc,
        0xcd, 0xce, 0xec, 0xed, 0xee, // absolute
        0x1d, 0x1e, 0x3d, 0x3e, 0x5d, 0x5e, 0x7d, 0x7e, 0x9d, 0xbc, 0xbd, 0xdd, 0xde, 0xfd, 0xfe, // absolute,X
        0x19, 0x39, 0x59, 0x79, 0x99, 0xb9, 0xbe, 0xd9, 0xf9, // absolute,Y
        0x6c, // JMP (indirect)
    ];

    /// <summary>The ones of them that write: STA/STX/STY, INC/DEC and the shifts.</summary>
    static readonly HashSet<byte> WriteOpcodes =
    [
        0x8c, 0x8d, 0x8e, 0x9d, 0x99, 0x0e, 0x1e, 0x2e, 0x3e, 0x4e, 0x5e, 0x6e, 0x7e, 0xce, 0xde, 0xee, 0xfe,
    ];

    readonly bool[] used = new bool[256];

    /// <summary>Names of the programs that write into the LOAD helper (or load over it).</summary>
    public List<string> OverwriteHelper { get; } = [];

    /// <summary>A program calls the KERNAL LOAD ($ffd5 or $f49e), so it loads more files.</summary>
    public bool LoadsFiles { get; private set; }

    /// <summary>A program calls the KERNAL SAVE ($ffd8 or $f5dd).</summary>
    public bool SavesFiles { get; private set; }

    /// <summary>A program calls the KERNAL OPEN ($ffc0 or $f34a).</summary>
    public bool OpensFiles { get; private set; }

    static readonly byte[][] DriveCode = ["M-W"u8.ToArray(), "M-E"u8.ToArray(), "W-M"u8.ToArray(), "E-M"u8.ToArray()];

    /// <summary>A program sends code into the drive ("M-W", "M-E"): its own loader, which needs a real drive.</summary>
    public bool SendsDriveCode { get; private set; }

    /// <summary>Programs that LOAD more files and overwrite the LOAD helper: loading may hang.</summary>
    public bool HelperAtRisk => LoadsFiles && OverwriteHelper.Count > 0;

    /// <summary>Adds a program: a .prg file with its load address.</summary>
    public void Add(string name, byte[] prg)
    {
        if (prg.Length < 3)
            return;

        var load = prg[0] | prg[1] << 8;
        var end = Math.Min(load + prg.Length - 2, 0x10000);
        for (var page = load >> 8; page <= (end - 1) >> 8; page++)
            used[page] = true;

        // Follow the code from where it starts: the SYS address of a BASIC line, or the load address
        var entry = Unpacker.SysAddress(prg) ?? (IsBasic(prg) ? (ushort?)null : (ushort)load);
        var memory = new byte[0x10000];
        prg.AsSpan(2, end - load).CopyTo(memory.AsSpan(load));
        var followed = entry is { } start && Follow(memory, start);
        ScanCode(name, prg, load <= 0x03fb && end > 0x02a7, references: !followed);
    }

    /// <summary>
    /// Adds a program unpacked on the server: the C64's memory from $0800 when the real program starts. Pages that are
    /// all zero count as free, wherever they are.
    /// </summary>
    public void AddMemory(string name, byte[] memory, ushort? entry)
    {
        var followed = entry is { } start && Follow(memory, start);
        for (var page = 0x08; page < 0x100; page++)
        {
            if (NonZero(memory, page))
                used[page] = true;
        }

        // Each stretch of used pages on its own: what an unpacker leaves behind is not code
        for (var page = 0x08; page < 0x100; page++)
        {
            if (!NonZero(memory, page))
                continue;
            var first = page;
            while (page + 1 < 0x100 && NonZero(memory, page + 1))
                page++;
            ScanCode(name, [0x00, (byte)first, .. memory.AsSpan(first << 8, (page - first + 1) << 8)], false, references: !followed);
        }
    }

    static bool NonZero(byte[] memory, int page) => memory.AsSpan(page << 8, 0x100).IndexOfAnyExcept((byte)0) >= 0;

    /// <summary>
    /// Code, not data (music, pictures, packed bytes): it has at least 3 calls into itself or the KERNAL's jump table.
    /// Data read as instructions would "address" every page.
    /// </summary>
    static bool LooksLikeCode(byte[] prg)
    {
        var load = prg[0] | prg[1] << 8;
        var end = load + prg.Length - 2;
        var calls = 0;
        for (var i = 2; i + 2 < prg.Length && calls < 3; i++)
        {
            if (prg[i] != 0x20)
                continue;
            var address = prg[i + 1] | prg[i + 2] << 8;
            if ((address >= load && address < end) || address is >= 0xff81 and <= 0xfff3)
                calls++;
        }

        return calls >= 3;
    }

    /// <summary>Opcodes of one byte; the others without a 16-bit address have two.</summary>
    static readonly HashSet<byte> OneByteOpcodes =
    [
        0x00, 0x08, 0x18, 0x28, 0x38, 0x40, 0x48, 0x58, 0x60, 0x68, 0x78, 0x88, 0x8a, 0x98, 0x9a, 0xa8, 0xaa, 0xb8, 0xba,
        0xc8, 0xca, 0xd8, 0xe8, 0xea, 0xf8, 0x0a, 0x2a, 0x4a, 0x6a, 0x1a, 0x3a, 0x5a, 0x7a, 0xda, 0xfa,
    ];

    const int MinFollowed = 40;

    /// <summary>
    /// Follows the code from <paramref name="entry"/> through branches, jumps and calls, and marks the pages its
    /// instructions address. False when that reaches too little code to rely on (the program jumps through vectors or
    /// tables, or the entry is a guess): the caller then reads every byte as an instruction.
    /// </summary>
    bool Follow(byte[] memory, ushort entry)
    {
        var pages = new HashSet<int>();
        var visited = new HashSet<int>();
        var todo = new Stack<int>();
        todo.Push(entry);
        while (todo.Count > 0)
        {
            var pc = todo.Pop();
            while (pc is >= 0x0200 and < 0xfffe && visited.Add(pc))
            {
                var opcode = memory[pc];
                if (opcode is 0x00 or 0x40 or 0x60 || (opcode & 0x0f) == 0x02 && opcode is not (0x82 or 0xa2 or 0xc2 or 0xe2))
                    break; // BRK, RTI, RTS or a halt

                if ((opcode & 0x1f) == 0x10) // a branch: both ways
                {
                    todo.Push(pc + 2 + (sbyte)memory[pc + 1]);
                    pc += 2;
                }
                else if (AddressOpcodes.Contains(opcode))
                {
                    var address = memory[pc + 1] | memory[pc + 2] << 8;
                    pages.Add(address >> 8);
                    if (opcode is 0x20 or 0x4c && address < 0xe000 && memory[address] != 0)
                        todo.Push(address);
                    if (opcode is 0x4c or 0x6c)
                        break;
                    pc += 3;
                }
                else
                {
                    pc += OneByteOpcodes.Contains(opcode) ? 1 : 2;
                }
            }
        }

        if (visited.Count < MinFollowed)
            return false;
        foreach (var page in pages)
            used[page] = true;
        return true;
    }

    /// <summary>
    /// What a program does, from its bytes read as instructions; with <paramref name="references"/> also the pages
    /// those address, when the file looks like code.
    /// </summary>
    void ScanCode(string name, byte[] prg, bool overwrites, bool references)
    {
        // The drive's memory write and execute commands; loaders often keep them backwards
        if (DriveCode.Any(command => prg.AsSpan().IndexOf(command) >= 0))
            SendsDriveCode = true;
        references = references && LooksLikeCode(prg);
        for (var i = 2; i + 2 < prg.Length; i++)
        {
            if (!AddressOpcodes.Contains(prg[i]))
                continue;

            var address = prg[i + 1] | prg[i + 2] << 8;
            if (references)
                used[address >> 8] = true;
            if (prg[i] is 0x20 or 0x4c && address is 0xffd5 or 0xf49e)
                LoadsFiles = true;
            if (prg[i] is 0x20 or 0x4c && address is 0xffd8 or 0xf5dd)
                SavesFiles = true;
            if (prg[i] is 0x20 or 0x4c && address is 0xffc0 or 0xf34a)
                OpensFiles = true;
            if (WriteOpcodes.Contains(prg[i]) && HelperAreas.Any(area => address >= area.From && address <= area.To))
                overwrites = true;
        }

        if (overwrites)
            OverwriteHelper.Add(name);
    }

    /// <summary>Marks pages as used, e.g. by another module.</summary>
    public void Use(int first, int pages)
    {
        for (var page = first; page < first + pages; page++)
            used[page] = true;
    }

    public const byte TokenInputFile = 0x84, TokenInput = 0x85, TokenLoad = 0x93, TokenSave = 0x94, TokenOpen = 0x9f;
    const byte TokenRem = 0x8f;

    /// <summary>A BASIC program: loaded at $0801, starting with a valid line link.</summary>
    public static bool IsBasic(byte[] prg) =>
        prg.Length > 6 && prg[0] == 0x01 && prg[1] == 0x08 && (prg[2] | prg[3] << 8) is var link && link > 0x0801 && link <= 0x0801 + prg.Length;

    /// <summary>The BASIC keywords (tokens) a BASIC program uses, outside strings and REMs; none for other programs.</summary>
    public static HashSet<byte> BasicTokens(byte[] prg)
    {
        var tokens = new HashSet<byte>();
        if (!IsBasic(prg))
            return tokens;

        var line = 2;
        while (line + 4 < prg.Length)
        {
            var link = prg[line] | prg[line + 1] << 8;
            if (link == 0)
                break;

            var quoted = false;
            for (var i = line + 4; i < prg.Length && prg[i] != 0; i++)
            {
                if (prg[i] == '"')
                    quoted = !quoted;
                else if (!quoted && prg[i] >= 0x80)
                {
                    if (prg[i] == TokenRem)
                        break;
                    tokens.Add(prg[i]);
                }
            }

            var next = link - 0x0801 + 2;
            if (next <= line)
                break;
            line = next;
        }

        return tokens;
    }

    /// <summary>
    /// The first of <paramref name="pages"/> pages no program uses: in $c000-$cfff if possible (the classic free
    /// area, above BASIC), otherwise as high as possible in $0800-$9fff (BASIC memory then ends below it).
    /// </summary>
    public int? FreePages(int pages)
    {
        foreach (var (low, high) in new[] { (0xc0, 0xcf), (0x08, 0x9f) })
        {
            for (var first = high - pages + 1; first >= low; first--)
            {
                if (Enumerable.Range(first, pages).All(page => !used[page]))
                    return first;
            }
        }

        return null;
    }
}
