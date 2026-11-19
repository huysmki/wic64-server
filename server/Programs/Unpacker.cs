using System.Collections.Concurrent;
using System.Security.Cryptography;
using Wic64Server.Music;

namespace Wic64Server.Programs;

/// <summary>
/// A packed program, unpacked on the server: the memory as its unpacker leaves it, and where the real program starts.
/// </summary>
/// <param name="Memory">The C64's 64 KB when the unpacker hands over.</param>
/// <param name="Parts">The memory the unpacker wrote, as .prg files (load address first): below the I/O area, and above it.
/// The starter only loads them (the other modules it also calls).</param>
/// <param name="Launch">Builds the launcher: a .prg for the screen ($0400) that sets the CPU and the hardware as the
/// unpacker left them and starts the program; its last 3 bytes are its entry, like the other modules of the starter.
/// With a staging page (see <see cref="UnderIo"/>) it first puts the RAM under the I/O area in place.</param>
/// <param name="Entry">Where the program starts; null when the unpacker ends with BASIC's RUN.</param>
/// <param name="Code">Where its code starts, for following it: the entry, or the SYS address of the BASIC line RUN runs.</param>
/// <param name="Instructions">How many instructions the unpacker ran.</param>
/// <param name="UnderIo">The unpacker left data in the RAM under the I/O area ($d000-$dfff), where the C64 can't receive
/// it. The launcher then LOADs it (file $13) to 16 pages elsewhere, copies it with the I/O area switched off, and
/// LOADs what belongs in those 16 pages back (file $14).</param>
public sealed record Unpacked(
    byte[] Memory, IReadOnlyList<byte[]> Parts, Func<int?, byte[]> Launch, ushort? Entry, ushort? Code, int Instructions, bool UnderIo)
{
    /// <summary>16 pages of the unpacked memory as a .prg: the RAM under the I/O area for <paramref name="page"/>
    /// (staged there), or what belongs at <paramref name="page"/> itself.</summary>
    public byte[] Pages(int page, bool underIo) => [0x00, (byte)page, .. Memory.AsSpan(underIo ? 0xd000 : page << 8, 0x1000)];

    public string Start => Entry is { } entry ? $"starts at ${entry:x4}" : "starts with RUN";
}

/// <summary>
/// Runs the unpacker of a packed program in the 6502 emulator, so the C64 gets the program unpacked. An unpacker
/// overwrites most of the memory, including the helpers the browser installs (LOAD helper, file helper, ...); unpacked
/// on the server, the helpers go in after it, where the real program leaves room.
///
/// The program is started at the SYS address of its BASIC line. An unpacker ends a stage with a far jump into memory
/// it wrote itself, after writing a lot, or with BASIC's CLR and RUN (then a SYS line in what it unpacked starts the
/// next stage). The run goes on through further stages until the program calls the KERNAL, waits (no writes for a
/// long time) or the budget is used up; the last stage's end is where the C64 starts. Hardware registers the unpacker
/// set are set again by the launcher. A program that never ends a stage is not packed and left as it is.
/// </summary>
public static class Unpacker
{
    const int MaxInstructions = 6_000_000;
    const int MaxAfterStage = 1_500_000; // looking for another stage
    const int MaxIdle = 400_000;        // instructions without a write: the program waits for something
    const int FarJump = 0x100;          // an unpacker's own loops and calls stay closer
    const int MinWrittenPerStage = 0x100;
    const int MinWritten = 0x400;
    const int LauncherAddress = 0x0400; // the screen: free while the program starts
    const int MaxZeroPage = 254;        // an unpacker may live in the zero page
    const int MaxRegisters = 64;
    const int BasicClr = 0xa659, BasicRelink = 0xa533, BasicRun = 0xa7ae;

    static readonly ConcurrentDictionary<string, (Unpacked? Unpacked, string Reason)> cache = new();

    /// <summary>The program unpacked, or null when it is not packed (or not in a way this understands).</summary>
    public static Unpacked? Unpack(byte[] prg) => Cached(prg).Unpacked;

    /// <summary>How the run ended (for testing and the log).</summary>
    public static string Explain(byte[] prg) => Cached(prg).Reason;

    static (Unpacked? Unpacked, string Reason) Cached(byte[] prg) =>
        cache.GetOrAdd(Convert.ToHexString(SHA256.HashData(prg)), _ =>
        {
            try
            {
                var unpacked = Run(prg, out var reason);
                return (unpacked, reason);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or InvalidOperationException or ArgumentException)
            {
                return (null, e.Message);
            }
        });

    static Unpacked? Run(byte[] prg, out string reason)
    {
        reason = "no SYS line";
        if (SysAddress(prg) is not { } start)
            return null;

        var cpu = new Cpu6502();
        var memory = cpu.Memory;
        Array.Clear(memory, 0xe000, 0x2000); // no ROM here: a call into it ends the run
        prg.AsSpan(2).CopyTo(memory.AsSpan(0x0801));
        var end = 0x0801 + prg.Length - 2;

        // What BASIC leaves after LOAD and RUN: unpackers read the end of the program
        (memory[0x2b], memory[0x2c]) = (0x01, 0x08);
        foreach (var pointer in new[] { 0x2d, 0x2f, 0x31, 0xae })
            (memory[pointer], memory[pointer + 1]) = ((byte)end, (byte)(end >> 8));
        (memory[0x33], memory[0x34], memory[0x37], memory[0x38]) = (0x00, 0xa0, 0x00, 0xa0);
        memory[0xba] = 8;
        cpu.Sp = 0xf6;
        cpu.Pc = start;

        Unpacked? result = null;
        var failed = "";
        var writtenBefore = new bool[0x10000]; // by earlier stages (and the hop of a stub to where it runs)
        var stages = 0;
        long writesAtLastStage = 0, lastWrites = 0;
        var idle = 0;
        var limit = MaxInstructions;
        for (var i = 0; i < limit; i++)
        {
            if (result is not null)
                limit = Math.Min(limit, result.Instructions + MaxAfterStage);
            var from = cpu.Pc;
            if (!cpu.StepOne())
            {
                reason = $"{failed}{stages} stages, then an unknown opcode ${memory[from]:x2} at ${from:x4}";
                return result;
            }

            if (cpu.ReadCharRom)
            {
                reason = $"{failed}{stages} stages, then it read the character ROM at ${from:x4}";
                return result;
            }

            idle = cpu.Writes == lastWrites ? idle + 1 : 0;
            lastWrites = cpu.Writes;
            if (idle > MaxIdle)
            {
                reason = $"{failed}{stages} stages, then waiting at ${from:x4}";
                return result;
            }

            var pc = cpu.Pc;
            var port = memory[1] & 7;
            var written = cpu.Writes - writesAtLastStage;
            var inRom = (pc >= 0xe000 && (port & 2) != 0) || (pc is >= 0xa000 and < 0xc000 && (port & 3) == 3);
            if (inRom)
            {
                // BASIC's CLR and RUN end a stage: "JSR $a659, (JSR $a533,) JMP $a7ae" or a jump to RUN itself
                var back = ((memory[0x101 + cpu.Sp] | memory[0x102 + cpu.Sp] << 8) + 1) & 0xffff;
                var next = memory.AsSpan(back, 6);
                var runsBasic = pc == BasicRun
                                || (pc == BasicClr && (next[..3].SequenceEqual<byte>([0x4c, 0xae, 0xa7])
                                                       || next.SequenceEqual<byte>([0x20, 0x33, 0xa5, 0x4c, 0xae, 0xa7])));
                reason = $"{stages} stages, then a call of ${pc:x4} in the ROM from ${from:x4} ({written} written; then {Convert.ToHexString(memory.AsSpan(back, 6))})";
                if (!runsBasic || written < MinWrittenPerStage || cpu.Writes < MinWritten)
                    return result;

                stages++;
                writesAtLastStage = cpu.Writes;
                cpu.Written.CopyTo(writtenBefore, 0);

                // RUN: a SYS line starts the next stage; any other BASIC program runs on the C64
                var unpackedEnd = memory[0x2d] | memory[0x2e] << 8;
                var sys = unpackedEnd > 0x0801
                    ? SysAddress([0x01, 0x08, .. memory.AsSpan(0x0801, Math.Min(unpackedEnd, 0xd000) - 0x0801)])
                    : null;
                if (Result(cpu, null, sys, i + 1) is not { } basic)
                    return result;
                result = basic;
                if (sys is null)
                {
                    reason = $"{stages} stages, the last ends with RUN of a BASIC program";
                    return result;
                }

                cpu.Sp = 0xf6;
                cpu.Pc = sys.Value;
                continue;
            }

            if (Math.Abs(pc - from) < FarJump || !cpu.Written[pc] || writtenBefore[pc])
                continue;

            // A far jump into memory written since the last stage: a stub hopping to where it runs, or, after a lot
            // was written and with the stack back at the top (a handover, not a call), the end of a stage
            writesAtLastStage = cpu.Writes;
            cpu.Written.CopyTo(writtenBefore, 0);
            if (written < MinWrittenPerStage || cpu.Writes < MinWritten || pc is < 0x0800 or >= 0xd000 || cpu.Sp < 0xf6)
                continue;
            stages++;
            if (Result(cpu, pc, pc, i + 1) is { } stage)
                result = stage;
            else
                failed = $"stage {stages} can't be sent: {Problem(cpu)}; ";
        }

        reason = $"{failed}{stages} stages in {limit} instructions";
        return result;
    }

    /// <summary>The address of "SYS 2061" in the first BASIC line of a program at $0801.</summary>
    public static ushort? SysAddress(byte[] prg)
    {
        if (prg.Length < 12 || prg[0] != 0x01 || prg[1] != 0x08)
            return null;

        var i = 6; // after the load address, the line link and the line number
        while (i < prg.Length && prg[i] == ' ')
            i++;
        if (i >= prg.Length || prg[i] != 0x9e)
            return null;
        i++;
        while (i < prg.Length && prg[i] is (byte)' ' or (byte)'(')
            i++;

        var address = 0;
        var digits = 0;
        for (; i < prg.Length && prg[i] is >= (byte)'0' and <= (byte)'9' && digits < 5; i++, digits++)
            address = address * 10 + prg[i] - '0';
        return digits > 0 && address is >= 0x0801 and < 0xd000 && address < 0x0801 + prg.Length ? (ushort)address : null;
    }

    static string Problem(Cpu6502 cpu)
    {
        var underIo = Enumerable.Range(0xd000, 0x1000).Count(a => cpu.Written[a] && cpu.Memory[a] != 0);
        var system = Enumerable.Range(0x0300, 0x0334 - 0x0300).Where(a => cpu.Written[a]).ToList();
        var zeroPage = Enumerable.Range(2, 0xfe).Count(a => cpu.Written[a]);
        return $"{underIo} bytes under the I/O area, {system.Count} in system memory{(system.Count > 0 ? $" (${system[0]:x4}-${system[^1]:x4})" : "")}, {zeroPage} zero page values, {cpu.IoWrites.Count} registers";
    }

    /// <summary>The state now as something the C64 can take over, or null when it can't.</summary>
    static Unpacked? Result(Cpu6502 cpu, ushort? entry, ushort? code, int instructions)
    {
        var memory = cpu.Memory;
        var underIo = Enumerable.Range(0xd000, 0x1000).Any(a => cpu.Written[a] && memory[a] != 0);
        if (Enumerable.Range(0x0300, 0x0334 - 0x0300).Any(a => cpu.Written[a]))
            return null; // the KERNAL's vectors: more than an unpacker does (its tables elsewhere below $0800 are scratch)

        var parts = new List<byte[]>();
        foreach (var (low, high) in new[] { (0x0800, 0xd000), (0xe000, 0x10000) })
        {
            var first = Array.IndexOf(cpu.Written, true, low, high - low);
            if (first < 0)
                continue;
            var last = Array.LastIndexOf(cpu.Written, true, high - 1, high - first);
            parts.Add([(byte)first, (byte)(first >> 8), .. memory.AsSpan(first, last - first + 1)]);
        }

        // $37/$38: the top of BASIC memory, which a helper may have lowered
        var zeroPage = Enumerable.Range(2, 0xfe).Where(a => cpu.Written[a] && a is not (0x37 or 0x38)).Select(a => (byte)a).ToArray();
        if (parts.Count == 0 || zeroPage.Length > MaxZeroPage || cpu.IoWrites.Count > MaxRegisters)
            return null;

        // The CPU moves on: keep what the launcher needs from this moment
        var (sp, port, a, x, y, interruptsOff) = (cpu.Sp, memory[1], cpu.A, cpu.X, cpu.Y, cpu.InterruptsOff);
        var registers = cpu.IoWrites.ToList();
        var values = zeroPage.Select(address => memory[address]).ToArray();
        return new Unpacked((byte[])memory.Clone(), parts,
            staging => Launcher(registers, zeroPage, values, sp, port, a, x, y, interruptsOff, entry, staging),
            entry, code, instructions, underIo);
    }

    /// <summary>
    /// SEI, the RAM under the I/O area (through the staging page, when given), the hardware registers and zero page
    /// values the unpacker left, the stack pointer, the CPU port, A/X/Y, CLI if interrupts were on, then JMP to the
    /// program; or, after an unpacker that ends with RUN: CLR and RUN.
    /// </summary>
    static byte[] Launcher(List<KeyValuePair<int, byte>> registers, byte[] zeroPage, byte[] zeroPageValues, byte sp, byte port,
        byte a, byte x, byte y, bool interruptsOff, ushort? entry, int? staging)
    {
        var code = new List<byte> { 0x78 };
        if (staging is { } page)
        {
            // LOAD file $13 (the RAM under the I/O area, sent to the staging pages), copy 16 pages to $d000 with the
            // I/O area off, then LOAD file $14 (what belongs in the staging pages)
            const int Load = LauncherAddress + 43, Name = LauncherAddress + 66, Next = LauncherAddress + 67;
            code.AddRange([
                0x20, Load & 0xff, Load >> 8,                                   //  1: jsr load
                0xa9, 0x34, 0x85, 0x01,                                         //  4: lda #$34, sta $01
                0xa2, 0x10, 0xa0, 0x00,                                         //  8: ldx #16, ldy #0
                0xb9, 0x00, (byte)page,                                         // 12: lda staging,y
                0x99, 0x00, 0xd0,                                               // 15: sta $d000,y
                0xc8, 0xd0, 0xf7,                                               // 18: iny, bne 12
                0xee, (LauncherAddress + 14) & 0xff, (LauncherAddress + 14) >> 8, // 21: inc the page to read
                0xee, (LauncherAddress + 17) & 0xff, (LauncherAddress + 17) >> 8, // 24: inc the page to write
                0xca, 0xd0, 0xee,                                               // 27: dex, bne 12
                0xa9, 0x37, 0x85, 0x01,                                         // 30: lda #$37, sta $01
                0xee, Name & 0xff, Name >> 8,                                   // 34: inc name
                0x20, Load & 0xff, Load >> 8,                                   // 37: jsr load
                0x4c, Next & 0xff, Next >> 8,                                   // 40: jmp next
                0xa9, 0x01, 0x85, 0xb7, 0x85, 0xb9,                             // 43: load: lda #1, sta $b7 (name length), $b9
                0xa9, Name & 0xff, 0x85, 0xbb,                                  //     lda #<name, sta $bb
                0xa9, Name >> 8, 0x85, 0xbc,                                    //     lda #>name, sta $bc
                0xa9, 0x08, 0x85, 0xba,                                         //     lda #8, sta $ba (device)
                0xa9, 0x00, 0x4c, 0x34, 0x03,                                   //     lda #0, jmp $0334 (the LOAD helper)
                0x13,                                                           // 66: name
            ]);
            if (LauncherAddress + code.Count != Next)
                throw new InvalidOperationException("launcher layout");
        }

        foreach (var (register, value) in registers)
            code.AddRange([0xa9, value, 0x8d, (byte)register, (byte)(register >> 8)]); // lda #value, sta register

        var tail = entry is null ? 4 + 1 + 3 + 3 + 3 : 3 + 4 + 6 + 1 + 3;
        var tables = LauncherAddress + code.Count + (zeroPage.Length > 0 ? 14 : 0) + tail;
        if (zeroPage.Length > 0)
        {
            var values = tables;
            var addresses = tables + zeroPage.Length;
            code.AddRange([
                0xa2, (byte)(zeroPage.Length - 1),                  // ldx #count-1
                0xbd, (byte)values, (byte)(values >> 8),            // lda values,x
                0xbc, (byte)addresses, (byte)(addresses >> 8),      // ldy addresses,x
                0x99, 0x00, 0x00,                                   // sta $0000,y
                0xca,                                               // dex
                0x10, 0xf4,                                         // bpl
            ]);
        }

        if (entry is { } pc)
        {
            code.AddRange([0xa2, sp, 0x9a]);                                // ldx #sp, txs
            code.AddRange([0xa9, port, 0x85, 0x01]);                        // lda #port, sta $01
            code.AddRange([0xa9, a, 0xa2, x, 0xa0, y]);                     // lda, ldx, ldy
            code.Add(interruptsOff ? (byte)0xea : (byte)0x58);              // nop or cli
            code.AddRange([0x4c, (byte)pc, (byte)(pc >> 8)]);               // jmp program
        }
        else
        {
            code.AddRange([0xa9, 0x37, 0x85, 0x01]);                        // lda #$37, sta $01
            code.Add(0x58);                                                 // cli
            code.AddRange([0x20, BasicClr & 0xff, BasicClr >> 8]);          // jsr CLR
            code.AddRange([0x20, BasicRelink & 0xff, BasicRelink >> 8]);    // jsr relink the lines
            code.AddRange([0x4c, BasicRun & 0xff, BasicRun >> 8]);          // jmp RUN
        }

        code.AddRange(zeroPageValues);
        code.AddRange(zeroPage);
        code.AddRange([0x4c, LauncherAddress & 0xff, LauncherAddress >> 8]); // the entry: jmp start
        return [LauncherAddress & 0xff, LauncherAddress >> 8, .. code];
    }
}
