namespace Wic64Server.Music;

public enum SidPlayMode
{
    /// <summary>Plays from the browser's interrupt while you keep browsing.</summary>
    Background,

    /// <summary>Needs memory of the browser, or runs its own interrupts: the browser hands over the machine.</summary>
    Standalone,
}

public sealed record SidPlan(SidPlayMode Mode, bool[] Used, bool Emulated, string? Problem)
{
    public bool Uses(int start, int end)
    {
        for (var address = start; address < end; address++)
            if (Used[address])
                return true;
        return false;
    }
}

/// <summary>
/// Runs a tune's init and play routines in the 6502 emulator to see which memory it really uses.
/// Many tunes unpack data or keep buffers outside the range they are loaded to.
/// </summary>
public static class SidAnalyzer
{
    // Memory the C64 browser uses (see c64/browser.asm)
    public const int BrowserStart = 0xc000, BrowserEnd = 0xd000;
    public const int ScreenStart = 0x0400, ScreenEnd = 0x0800;
    public const int StandalonePlayerStart = 0x0334, StandalonePlayerEnd = 0x0400;

    const int PlayCallsPerSong = 250;   // 5 seconds of music
    const int MaxSongsToAnalyze = 32;

    public static SidPlan Plan(SidFile sid)
    {
        // Below $0400 are the system variables and the standalone player, above $cfff the I/O area
        if (sid.LoadAddress < ScreenStart || sid.EndAddress > BrowserEnd)
            return Reject($"tune is at ${sid.LoadAddress:x4}-${sid.EndAddress - 1:x4}, outside $0400-$cfff");
        if (sid.IsRsid && sid.IsBasic)
            return Reject("basic rsid tunes are not supported");

        var used = new bool[0x10000];
        Array.Fill(used, true, sid.LoadAddress, sid.Data.Length);

        var emulated = Emulate(sid, used);

        // RSID tunes and tunes without play address install their own interrupts
        var ownInterrupts = sid.IsRsid || sid.PlayAddress == 0
            || Uses(used, 0x0314, 0x031a) || Uses(used, 0xfffa, 0x10000);
        var needsBrowserMemory = Uses(used, BrowserStart, BrowserEnd) || Uses(used, ScreenStart, ScreenEnd);

        if (!ownInterrupts && !needsBrowserMemory)
            return new SidPlan(SidPlayMode.Background, used, emulated, null);

        if (Uses(used, StandalonePlayerStart, StandalonePlayerEnd))
            return Reject("tune overwrites the player at $0334");

        return new SidPlan(SidPlayMode.Standalone, used, emulated, null);

        SidPlan Reject(string problem) => new(SidPlayMode.Background, new bool[0x10000], false, problem);
    }

    /// <summary>Marks every address the tune writes. Returns false if the emulation did not complete.</summary>
    static bool Emulate(SidFile sid, bool[] used)
    {
        var songs = Math.Min(sid.Songs, MaxSongsToAnalyze);
        var complete = true;

        for (var song = 0; song < songs; song++)
        {
            var cpu = new Cpu6502();
            sid.Data.CopyTo(cpu.Memory, sid.LoadAddress);

            // RSID init routines may never return (they run their own main loop), so only look at what they did so far
            var initialized = cpu.Call(sid.InitAddress, (byte)song, 5_000_000);
            if (initialized && sid.PlayAddress != 0 && !sid.IsRsid)
            {
                for (var i = 0; i < PlayCallsPerSong; i++)
                {
                    if (!cpu.Call(sid.PlayAddress, 0, 200_000))
                    {
                        complete = false;
                        break;
                    }
                }
            }
            else if (!initialized && !sid.IsRsid)
            {
                complete = false;
            }

            for (var address = 0; address < 0x10000; address++)
                used[address] |= cpu.Written[address];
        }

        // Zeropage use is normal for tunes and does not bother the browser
        Array.Fill(used, false, 0, 0x100);
        return complete;
    }

    static bool Uses(bool[] used, int start, int end)
    {
        for (var address = start; address < end; address++)
            if (used[address])
                return true;
        return false;
    }
}
