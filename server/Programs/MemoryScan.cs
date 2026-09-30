namespace Wic64Server.Programs;

/// <summary>
/// What the programs of a disk (or folder) do with memory, guessed from their bytes: where they load, which pages
/// their instructions address, whether they write into the LOAD helper and whether they LOAD more files. Every byte
/// is read as if an instruction could start there, so data counts too: the result errs on the side of "used".
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

        var overwrites = load <= 0x03fb && end > 0x02a7;
        for (var i = 2; i + 2 < prg.Length; i++)
        {
            if (!AddressOpcodes.Contains(prg[i]))
                continue;

            var address = prg[i + 1] | prg[i + 2] << 8;
            used[address >> 8] = true;
            if (prg[i] is 0x20 or 0x4c && address is 0xffd5 or 0xf49e)
                LoadsFiles = true;
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

    public const byte TokenInputFile = 0x84, TokenInput = 0x85, TokenLoad = 0x93;
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
