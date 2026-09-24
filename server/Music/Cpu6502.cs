namespace Wic64Server.Music;

/// <summary>
/// A small 6502 emulator (official opcodes plus the common undocumented ones), used to find out
/// which memory a SID tune really writes to. It has no video or timing: I/O reads return
/// harmless values and I/O writes are ignored.
/// </summary>
public sealed class Cpu6502
{
    const ushort ReturnSentinel = 0xfff0;

    public byte[] Memory { get; } = new byte[0x10000];

    /// <summary>RAM addresses written by the program (I/O writes are not included).</summary>
    public bool[] Written { get; } = new bool[0x10000];

    byte a, x, y, sp = 0xfd;
    ushort pc;
    bool carry, zero, interrupt, decimalMode, overflow, negative;
    int raster;

    enum Op
    {
        Adc, And, Asl, Bcc, Bcs, Beq, Bit, Bmi, Bne, Bpl, Brk, Bvc, Bvs, Clc, Cld, Cli, Clv, Cmp, Cpx, Cpy,
        Dec, Dex, Dey, Eor, Inc, Inx, Iny, Jmp, Jsr, Lda, Ldx, Ldy, Lsr, Nop, Ora, Pha, Php, Pla, Plp,
        Rol, Ror, Rti, Rts, Sbc, Sec, Sed, Sei, Sta, Stx, Sty, Tax, Tay, Tsx, Txa, Txs, Tya,
        Lax, Sax, Dcp, Isc, Slo, Rla, Sre, Rra, Anc, Alr, Unknown,
    }

    enum Mode { Implied, Accumulator, Immediate, ZeroPage, ZeroPageX, ZeroPageY, Absolute, AbsoluteX, AbsoluteY, Indirect, IndirectX, IndirectY, Relative }

    static readonly (Op Op, Mode Mode)[] Opcodes = BuildOpcodeTable();

    public Cpu6502()
    {
        // KERNAL calls just return, and the CPU port starts in its default configuration
        Array.Fill(Memory, (byte)0x60, 0xe000, 0x2000);
        Memory[0] = 0x2f;
        Memory[1] = 0x37;
    }

    /// <summary>Calls the subroutine at address like JSR. Returns false if it does not return in time.</summary>
    public bool Call(ushort address, byte accumulator, int maxInstructions)
    {
        Push((ReturnSentinel - 1) >> 8);
        Push((ReturnSentinel - 1) & 0xff);
        pc = address;
        a = accumulator;
        x = 0;
        y = 0;

        for (var i = 0; i < maxInstructions; i++)
        {
            if (pc == ReturnSentinel)
                return true;
            if (!Step())
                return false;
        }

        return false;
    }

    bool IoVisible => (Memory[1] & 0x03) != 0;

    byte Read(int address)
    {
        address &= 0xffff;
        if (address is >= 0xd000 and < 0xe000 && IoVisible)
        {
            return address switch
            {
                0xd011 => (byte)(++raster >> 1 & 0x80 | 0x1b),  // let "wait for raster" loops finish
                0xd012 => (byte)++raster,
                0xd41b or 0xd41c => (byte)Random.Shared.Next(256),
                _ => 0,
            };
        }

        return Memory[address];
    }

    void Write(int address, byte value)
    {
        address &= 0xffff;
        if (address is >= 0xd000 and < 0xe000 && IoVisible)
            return;

        Memory[address] = value;
        if (address is < 0x0100 or >= 0x0200) // ignore the stack
            Written[address] = true;
    }

    void Push(int value)
    {
        Memory[0x100 + sp] = (byte)value;
        sp--;
    }

    byte Pull()
    {
        sp++;
        return Memory[0x100 + sp];
    }

    ushort ReadWord(int address) => (ushort)(Read(address) | Read(address + 1) << 8);

    // Zeropage pointer with page wrap, like the real CPU
    ushort ReadZeroPageWord(int address) => (ushort)(Read(address & 0xff) | Read((address + 1) & 0xff) << 8);

    byte SetNz(byte value)
    {
        zero = value == 0;
        negative = (value & 0x80) != 0;
        return value;
    }

    byte Flags() => (byte)((carry ? 0x01 : 0) | (zero ? 0x02 : 0) | (interrupt ? 0x04 : 0) | (decimalMode ? 0x08 : 0)
                           | 0x30 | (overflow ? 0x40 : 0) | (negative ? 0x80 : 0));

    void SetFlags(byte p)
    {
        carry = (p & 0x01) != 0;
        zero = (p & 0x02) != 0;
        interrupt = (p & 0x04) != 0;
        decimalMode = (p & 0x08) != 0;
        overflow = (p & 0x40) != 0;
        negative = (p & 0x80) != 0;
    }

    int EffectiveAddress(Mode mode)
    {
        switch (mode)
        {
            case Mode.Immediate: return pc++;
            case Mode.ZeroPage: return Read(pc++);
            case Mode.ZeroPageX: return (Read(pc++) + x) & 0xff;
            case Mode.ZeroPageY: return (Read(pc++) + y) & 0xff;
            case Mode.Absolute: { var address = ReadWord(pc); pc += 2; return address; }
            case Mode.AbsoluteX: { var address = ReadWord(pc); pc += 2; return (address + x) & 0xffff; }
            case Mode.AbsoluteY: { var address = ReadWord(pc); pc += 2; return (address + y) & 0xffff; }
            case Mode.Indirect:
            {
                // JMP ($xxff) reads the high byte from $xx00 on the real CPU
                var pointer = ReadWord(pc);
                pc += 2;
                return Read(pointer) | Read((pointer & 0xff00) | ((pointer + 1) & 0xff)) << 8;
            }
            case Mode.IndirectX: return ReadZeroPageWord(Read(pc++) + x);
            case Mode.IndirectY: return (ReadZeroPageWord(Read(pc++)) + y) & 0xffff;
            case Mode.Relative: return pc++;
            default: return 0;
        }
    }

    bool Step()
    {
        var (op, mode) = Opcodes[Read(pc++)];
        var address = EffectiveAddress(mode);

        switch (op)
        {
            case Op.Lda: a = SetNz(Read(address)); break;
            case Op.Ldx: x = SetNz(Read(address)); break;
            case Op.Ldy: y = SetNz(Read(address)); break;
            case Op.Lax: a = x = SetNz(Read(address)); break;
            case Op.Sta: Write(address, a); break;
            case Op.Stx: Write(address, x); break;
            case Op.Sty: Write(address, y); break;
            case Op.Sax: Write(address, (byte)(a & x)); break;
            case Op.Tax: x = SetNz(a); break;
            case Op.Tay: y = SetNz(a); break;
            case Op.Txa: a = SetNz(x); break;
            case Op.Tya: a = SetNz(y); break;
            case Op.Tsx: x = SetNz(sp); break;
            case Op.Txs: sp = x; break;
            case Op.Pha: Push(a); break;
            case Op.Php: Push(Flags()); break;
            case Op.Pla: a = SetNz(Pull()); break;
            case Op.Plp: SetFlags(Pull()); break;

            case Op.Adc: Add(Read(address)); break;
            case Op.Sbc: Subtract(Read(address)); break;
            case Op.And: a = SetNz((byte)(a & Read(address))); break;
            case Op.Ora: a = SetNz((byte)(a | Read(address))); break;
            case Op.Eor: a = SetNz((byte)(a ^ Read(address))); break;
            case Op.Cmp: Compare(a, Read(address)); break;
            case Op.Cpx: Compare(x, Read(address)); break;
            case Op.Cpy: Compare(y, Read(address)); break;
            case Op.Bit:
            {
                var value = Read(address);
                zero = (a & value) == 0;
                overflow = (value & 0x40) != 0;
                negative = (value & 0x80) != 0;
                break;
            }
            case Op.Anc: a = SetNz((byte)(a & Read(address))); carry = negative; break;
            case Op.Alr: a = (byte)(a & Read(address)); carry = (a & 1) != 0; a = SetNz((byte)(a >> 1)); break;

            case Op.Inc: Write(address, SetNz((byte)(Read(address) + 1))); break;
            case Op.Dec: Write(address, SetNz((byte)(Read(address) - 1))); break;
            case Op.Inx: x = SetNz((byte)(x + 1)); break;
            case Op.Iny: y = SetNz((byte)(y + 1)); break;
            case Op.Dex: x = SetNz((byte)(x - 1)); break;
            case Op.Dey: y = SetNz((byte)(y - 1)); break;

            case Op.Asl: Modify(mode, address, v => { carry = (v & 0x80) != 0; return (byte)(v << 1); }); break;
            case Op.Lsr: Modify(mode, address, v => { carry = (v & 0x01) != 0; return (byte)(v >> 1); }); break;
            case Op.Rol: Modify(mode, address, v => { var c = carry ? 1 : 0; carry = (v & 0x80) != 0; return (byte)(v << 1 | c); }); break;
            case Op.Ror: Modify(mode, address, v => { var c = carry ? 0x80 : 0; carry = (v & 0x01) != 0; return (byte)(v >> 1 | c); }); break;

            // Undocumented read-modify-write combinations
            case Op.Dcp: { var v = (byte)(Read(address) - 1); Write(address, v); Compare(a, v); break; }
            case Op.Isc: { var v = (byte)(Read(address) + 1); Write(address, v); Subtract(v); break; }
            case Op.Slo: { var v = Read(address); carry = (v & 0x80) != 0; v <<= 1; Write(address, v); a = SetNz((byte)(a | v)); break; }
            case Op.Sre: { var v = Read(address); carry = (v & 0x01) != 0; v >>= 1; Write(address, v); a = SetNz((byte)(a ^ v)); break; }
            case Op.Rla:
            {
                var v = Read(address);
                var c = carry ? 1 : 0;
                carry = (v & 0x80) != 0;
                v = (byte)(v << 1 | c);
                Write(address, v);
                a = SetNz((byte)(a & v));
                break;
            }
            case Op.Rra:
            {
                var v = Read(address);
                var c = carry ? 0x80 : 0;
                carry = (v & 0x01) != 0;
                v = (byte)(v >> 1 | c);
                Write(address, v);
                Add(v);
                break;
            }

            case Op.Bpl: Branch(address, !negative); break;
            case Op.Bmi: Branch(address, negative); break;
            case Op.Bvc: Branch(address, !overflow); break;
            case Op.Bvs: Branch(address, overflow); break;
            case Op.Bcc: Branch(address, !carry); break;
            case Op.Bcs: Branch(address, carry); break;
            case Op.Bne: Branch(address, !zero); break;
            case Op.Beq: Branch(address, zero); break;

            case Op.Jmp: pc = (ushort)address; break;
            case Op.Jsr:
                Push((pc - 1) >> 8);
                Push((pc - 1) & 0xff);
                pc = (ushort)address;
                break;
            case Op.Rts: pc = (ushort)((Pull() | Pull() << 8) + 1); break;
            case Op.Rti: SetFlags(Pull()); pc = (ushort)(Pull() | Pull() << 8); break;

            case Op.Clc: carry = false; break;
            case Op.Sec: carry = true; break;
            case Op.Cli: interrupt = false; break;
            case Op.Sei: interrupt = true; break;
            case Op.Clv: overflow = false; break;
            case Op.Cld: decimalMode = false; break;
            case Op.Sed: decimalMode = true; break;
            case Op.Nop: break;

            default: return false; // BRK, jams and unsupported undocumented opcodes
        }

        return true;
    }

    void Modify(Mode mode, int address, Func<byte, byte> operation)
    {
        if (mode == Mode.Accumulator)
            a = SetNz(operation(a));
        else
            Write(address, SetNz(operation(Read(address))));
    }

    void Branch(int offsetAddress, bool condition)
    {
        if (condition)
            pc = (ushort)(pc + (sbyte)Read(offsetAddress));
    }

    void Compare(byte register, byte value)
    {
        carry = register >= value;
        SetNz((byte)(register - value));
    }

    void Add(byte value)
    {
        var c = carry ? 1 : 0;
        if (decimalMode)
        {
            var low = (a & 0x0f) + (value & 0x0f) + c;
            var high = (a >> 4) + (value >> 4);
            if (low > 9) { low += 6; high++; }
            if (high > 9) high += 6;
            carry = high > 15;
            a = SetNz((byte)(high << 4 | low & 0x0f));
            return;
        }

        var sum = a + value + c;
        overflow = (~(a ^ value) & (a ^ sum) & 0x80) != 0;
        carry = sum > 0xff;
        a = SetNz((byte)sum);
    }

    void Subtract(byte value)
    {
        if (decimalMode)
        {
            var borrow = carry ? 0 : 1;
            var low = (a & 0x0f) - (value & 0x0f) - borrow;
            var high = (a >> 4) - (value >> 4);
            if (low < 0) { low -= 6; high--; }
            if (high < 0) high -= 6;
            carry = a - value - borrow >= 0;
            a = SetNz((byte)(high << 4 | low & 0x0f));
            return;
        }

        Add((byte)~value);
    }

    static (Op, Mode)[] BuildOpcodeTable()
    {
        var table = Enumerable.Repeat((Op.Unknown, Mode.Implied), 256).ToArray();

        void Set(Op op, Mode mode, params int[] codes)
        {
            foreach (var code in codes)
                table[code] = (op, mode);
        }

        // The eight addressing modes of the ALU instructions, in order:
        // (zp,x) zp #imm abs (zp),y zp,x abs,y abs,x
        void Alu(Op op, int baseCode)
        {
            Set(op, Mode.IndirectX, baseCode + 0x01);
            Set(op, Mode.ZeroPage, baseCode + 0x05);
            Set(op, Mode.Immediate, baseCode + 0x09);
            Set(op, Mode.Absolute, baseCode + 0x0d);
            Set(op, Mode.IndirectY, baseCode + 0x11);
            Set(op, Mode.ZeroPageX, baseCode + 0x15);
            Set(op, Mode.AbsoluteY, baseCode + 0x19);
            Set(op, Mode.AbsoluteX, baseCode + 0x1d);
        }

        Alu(Op.Ora, 0x00);
        Alu(Op.And, 0x20);
        Alu(Op.Eor, 0x40);
        Alu(Op.Adc, 0x60);
        Alu(Op.Sta, 0x80);
        table[0x89] = (Op.Nop, Mode.Immediate); // there is no STA #imm
        Alu(Op.Lda, 0xa0);
        Alu(Op.Cmp, 0xc0);
        Alu(Op.Sbc, 0xe0);
        Set(Op.Sbc, Mode.Immediate, 0xeb);

        // Shifts, INC and DEC
        foreach (var (op, baseCode) in new[] { (Op.Asl, 0x00), (Op.Rol, 0x20), (Op.Lsr, 0x40), (Op.Ror, 0x60), (Op.Dec, 0xc0), (Op.Inc, 0xe0) })
        {
            Set(op, Mode.ZeroPage, baseCode + 0x06);
            Set(op, Mode.Absolute, baseCode + 0x0e);
            Set(op, Mode.ZeroPageX, baseCode + 0x16);
            Set(op, Mode.AbsoluteX, baseCode + 0x1e);
            if (op is Op.Asl or Op.Rol or Op.Lsr or Op.Ror)
                Set(op, Mode.Accumulator, baseCode + 0x0a);
        }

        // Undocumented read-modify-write combinations use the same pattern as the ALU instructions
        foreach (var (op, baseCode) in new[] { (Op.Slo, 0x00), (Op.Rla, 0x20), (Op.Sre, 0x40), (Op.Rra, 0x60), (Op.Dcp, 0xc0), (Op.Isc, 0xe0) })
        {
            Set(op, Mode.IndirectX, baseCode + 0x03);
            Set(op, Mode.ZeroPage, baseCode + 0x07);
            Set(op, Mode.Absolute, baseCode + 0x0f);
            Set(op, Mode.IndirectY, baseCode + 0x13);
            Set(op, Mode.ZeroPageX, baseCode + 0x17);
            Set(op, Mode.AbsoluteY, baseCode + 0x1b);
            Set(op, Mode.AbsoluteX, baseCode + 0x1f);
        }

        Set(Op.Lax, Mode.IndirectX, 0xa3); Set(Op.Lax, Mode.ZeroPage, 0xa7); Set(Op.Lax, Mode.Absolute, 0xaf);
        Set(Op.Lax, Mode.IndirectY, 0xb3); Set(Op.Lax, Mode.ZeroPageY, 0xb7); Set(Op.Lax, Mode.AbsoluteY, 0xbf);
        Set(Op.Sax, Mode.IndirectX, 0x83); Set(Op.Sax, Mode.ZeroPage, 0x87); Set(Op.Sax, Mode.Absolute, 0x8f); Set(Op.Sax, Mode.ZeroPageY, 0x97);
        Set(Op.Anc, Mode.Immediate, 0x0b, 0x2b);
        Set(Op.Alr, Mode.Immediate, 0x4b);

        Set(Op.Ldx, Mode.Immediate, 0xa2); Set(Op.Ldx, Mode.ZeroPage, 0xa6); Set(Op.Ldx, Mode.Absolute, 0xae);
        Set(Op.Ldx, Mode.ZeroPageY, 0xb6); Set(Op.Ldx, Mode.AbsoluteY, 0xbe);
        Set(Op.Ldy, Mode.Immediate, 0xa0); Set(Op.Ldy, Mode.ZeroPage, 0xa4); Set(Op.Ldy, Mode.Absolute, 0xac);
        Set(Op.Ldy, Mode.ZeroPageX, 0xb4); Set(Op.Ldy, Mode.AbsoluteX, 0xbc);
        Set(Op.Stx, Mode.ZeroPage, 0x86); Set(Op.Stx, Mode.Absolute, 0x8e); Set(Op.Stx, Mode.ZeroPageY, 0x96);
        Set(Op.Sty, Mode.ZeroPage, 0x84); Set(Op.Sty, Mode.Absolute, 0x8c); Set(Op.Sty, Mode.ZeroPageX, 0x94);
        Set(Op.Cpx, Mode.Immediate, 0xe0); Set(Op.Cpx, Mode.ZeroPage, 0xe4); Set(Op.Cpx, Mode.Absolute, 0xec);
        Set(Op.Cpy, Mode.Immediate, 0xc0); Set(Op.Cpy, Mode.ZeroPage, 0xc4); Set(Op.Cpy, Mode.Absolute, 0xcc);
        Set(Op.Bit, Mode.ZeroPage, 0x24); Set(Op.Bit, Mode.Absolute, 0x2c);

        Set(Op.Bpl, Mode.Relative, 0x10); Set(Op.Bmi, Mode.Relative, 0x30); Set(Op.Bvc, Mode.Relative, 0x50); Set(Op.Bvs, Mode.Relative, 0x70);
        Set(Op.Bcc, Mode.Relative, 0x90); Set(Op.Bcs, Mode.Relative, 0xb0); Set(Op.Bne, Mode.Relative, 0xd0); Set(Op.Beq, Mode.Relative, 0xf0);

        Set(Op.Brk, Mode.Implied, 0x00);
        Set(Op.Jsr, Mode.Absolute, 0x20);
        Set(Op.Rti, Mode.Implied, 0x40);
        Set(Op.Rts, Mode.Implied, 0x60);
        Set(Op.Jmp, Mode.Absolute, 0x4c);
        Set(Op.Jmp, Mode.Indirect, 0x6c);

        Set(Op.Php, Mode.Implied, 0x08); Set(Op.Plp, Mode.Implied, 0x28); Set(Op.Pha, Mode.Implied, 0x48); Set(Op.Pla, Mode.Implied, 0x68);
        Set(Op.Clc, Mode.Implied, 0x18); Set(Op.Sec, Mode.Implied, 0x38); Set(Op.Cli, Mode.Implied, 0x58); Set(Op.Sei, Mode.Implied, 0x78);
        Set(Op.Clv, Mode.Implied, 0xb8); Set(Op.Cld, Mode.Implied, 0xd8); Set(Op.Sed, Mode.Implied, 0xf8);
        Set(Op.Dey, Mode.Implied, 0x88); Set(Op.Txa, Mode.Implied, 0x8a); Set(Op.Tya, Mode.Implied, 0x98); Set(Op.Txs, Mode.Implied, 0x9a);
        Set(Op.Tay, Mode.Implied, 0xa8); Set(Op.Tax, Mode.Implied, 0xaa); Set(Op.Tsx, Mode.Implied, 0xba);
        Set(Op.Iny, Mode.Implied, 0xc8); Set(Op.Dex, Mode.Implied, 0xca); Set(Op.Inx, Mode.Implied, 0xe8);

        // NOPs of all sizes
        Set(Op.Nop, Mode.Implied, 0xea, 0x1a, 0x3a, 0x5a, 0x7a, 0xda, 0xfa);
        Set(Op.Nop, Mode.Immediate, 0x80, 0x82, 0xc2, 0xe2);
        Set(Op.Nop, Mode.ZeroPage, 0x04, 0x44, 0x64);
        Set(Op.Nop, Mode.ZeroPageX, 0x14, 0x34, 0x54, 0x74, 0xd4, 0xf4);
        Set(Op.Nop, Mode.Absolute, 0x0c);
        Set(Op.Nop, Mode.AbsoluteX, 0x1c, 0x3c, 0x5c, 0x7c, 0xdc, 0xfc);

        return table;
    }
}
