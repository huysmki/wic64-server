using System.Collections.Concurrent;
using Wic64Server.Content;
using Wic64Server.Screens;

namespace Wic64Server.Music;

/// <summary>Everything the C64 needs to play a tune: analysis, header, standalone player and info screen.</summary>
public sealed class SidService(Catalog catalog, SongLengths songLengths, ServerOptions options, ILogger<SidService> logger)
{
    public const int SubtuneLengths = 9; // keys 1-9
    const int StandalonePlayerSize = 0x0400 - 0x0334;

    readonly ConcurrentDictionary<(string Path, DateTime Modified), (SidFile Sid, byte[] File, SidPlan Plan)> cache = new();

    public (SidFile Sid, byte[] File, SidPlan Plan) Load(FileEntry entry)
    {
        var info = new FileInfo(entry.Path);
        return cache.GetOrAdd((info.FullName, info.LastWriteTimeUtc), key =>
        {
            var file = File.ReadAllBytes(key.Path);
            var sid = SidFile.Parse(file);
            var started = DateTime.UtcNow;
            var plan = SidAnalyzer.Plan(sid);
            logger.LogInformation("Analyzed {File} in {Ms} ms: {Mode}, uses {Ranges}{Incomplete}",
                info.Name, (int)(DateTime.UtcNow - started).TotalMilliseconds,
                plan.Problem ?? plan.Mode.ToString(), Ranges(plan.Used), plan.Emulated ? "" : " (emulation incomplete)");
            return (sid, file, plan);
        });
    }

    /// <summary>
    /// Header (37 bytes) + "now playing" line (40 screen codes), followed by the standalone player
    /// (for standalone tunes) and the tune data:
    ///  0-1 load address   2-3 end address (exclusive)   4-5 init   6-7 play (0 = own interrupt)
    ///  8 songs   9 start song (0-based)   10 value for $01   11 flags   12 ticks per second
    ///  13 page and 14 entry of the next tune   15-32 length of subtunes 1-9 as BCD minutes, BCD seconds
    ///  33-34 folder, 35 page, 36 entry of this tune (the C64 uses them for the info screen and auto-next)
    /// </summary>
    public byte[] Response(FileEntry entry, int folder, int page, int index)
    {
        var (sid, file, plan) = Load(entry);

        byte flags = 0;
        if (plan.Uses(0x4400, 0x47e8) || plan.Uses(0x6000, 0x7f40))
            flags |= 0x01; // the picture viewer would overwrite the tune
        var cia = sid.UsesCiaTimer(sid.StartSong);
        if (cia)
            flags |= 0x02; // play from the CIA timer interrupt instead of the raster interrupt
        if (plan.Mode == SidPlayMode.Standalone)
            flags |= 0x04; // the browser hands over the machine to the tune

        var play = sid.IsRsid ? (ushort)0 : sid.PlayAddress;
        var bank = (byte)(plan.Uses(0xa000, 0xc000) ? 0x36 : 0x37); // bank out BASIC when the tune needs $a000-$bfff
        var (nextPage, nextIndex) = NextTune(folder, page * Catalog.PageSize + index);

        var header = new List<byte>
        {
            Low(sid.LoadAddress), High(sid.LoadAddress), Low(sid.EndAddress), High(sid.EndAddress),
            Low(sid.InitAddress), High(sid.InitAddress), Low(play), High(play),
            (byte)Math.Min(sid.Songs, 255), (byte)Math.Min(sid.StartSong - 1, 254), bank, flags,
            (byte)(cia ? 60 : 50), (byte)nextPage, (byte)nextIndex,
        };
        for (var song = 1; song <= SubtuneLengths; song++)
        {
            var length = songLengths.Of(file, song);
            var seconds = (int)Math.Ceiling(length.TotalSeconds);
            header.Add(Bcd(Math.Min(seconds / 60, 9)));
            header.Add(Bcd(seconds % 60));
        }
        header.AddRange([Low(folder), High(folder), (byte)page, (byte)index]);

        var songs = sid.Songs > 1 ? $" (1-{Math.Min(sid.Songs, SubtuneLengths)})" : "";
        var nowPlaying = new byte[40];
        ScreenCodes.Write(nowPlaying, plan.Mode == SidPlayMode.Standalone
            ? $"{sid.Name}{songs} - reset to return"
            : $"{sid.Name}{songs}".PadRight(30)[..30]);

        IEnumerable<byte> response = [.. header, .. nowPlaying];
        if (plan.Mode == SidPlayMode.Standalone)
            response = response.Concat(StandalonePlayer(sid, play, bank, cia));
        return [.. response, .. sid.Data];
    }

    /// <summary>The player from build/standalone.bin with its parameters filled in (see c64/standalone.asm).</summary>
    byte[] StandalonePlayer(SidFile sid, ushort play, byte bank, bool cia)
    {
        var path = Path.Combine(options.BuildFolder, "standalone.bin");
        if (!File.Exists(path))
            throw new FileNotFoundException("standalone player missing, run make", path);

        var player = new byte[StandalonePlayerSize];
        File.ReadAllBytes(path).CopyTo(player, 0);
        // CIA timer for the play interrupt: one PAL frame (63 cycles x 312 lines), or keep the KERNAL's 60 Hz
        var timer = cia ? 0 : 63 * 312 - 1;

        player[3] = Low(sid.Data.Length);
        player[4] = High(sid.Data.Length);
        player[5] = Low(sid.LoadAddress);
        player[6] = High(sid.LoadAddress);
        player[7] = Low(sid.InitAddress);
        player[8] = High(sid.InitAddress);
        player[9] = Low(play);
        player[10] = High(play);
        player[11] = bank;
        player[12] = Low(timer);
        player[13] = High(timer);
        player[14] = (byte)Math.Min(sid.StartSong - 1, 254);
        player[15] = (byte)Math.Min(sid.Songs, 255);
        return player;
    }

    /// <summary>The next tune in the folder that plays in the background (for auto-next), wrapping around.</summary>
    (int Page, int Index) NextTune(int folder, int position)
    {
        var entries = catalog.List(Section.Music, folder);
        for (var step = 1; step <= Math.Min(entries.Count, 50); step++)
        {
            var next = (position + step) % entries.Count;
            if (entries[next] is not FileEntry file)
                continue;
            try
            {
                var (_, _, plan) = Load(file);
                if (plan.Problem is null && plan.Mode == SidPlayMode.Background)
                    return (next / Catalog.PageSize, next % Catalog.PageSize);
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Skipping {File} for auto-next", file.Name);
            }
        }

        return (position / Catalog.PageSize, position % Catalog.PageSize);
    }

    /// <summary>The info screen shown with RETURN while a tune plays. The C64 keeps row 24 for the time.</summary>
    public Screen InfoScreen(FileEntry entry)
    {
        var (sid, file, plan) = Load(entry);
        var screen = new Screen();
        screen.Print(0, 0, " Now playing", 40, reverse: true);

        screen.Print(2, 1, sid.Name, 38);
        screen.Print(3, 1, sid.Author, 38);
        screen.Print(4, 1, sid.Released, 38);

        screen.Print(6, 1, $"File     {Path.GetFileName(entry.Path)}", 38);
        screen.Print(7, 1, $"Loads    ${sid.LoadAddress:x4}-${sid.EndAddress - 1:x4}", 38);
        screen.Print(8, 1, $"Uses     {Ranges(plan.Used)}", 38);
        screen.Print(9, 1, $"Type     {(sid.IsRsid ? "RSID" : "PSID")}, {(sid.UsesCiaTimer(sid.StartSong) ? "CIA timer" : "50 Hz")}, {plan.Mode.ToString().ToLowerInvariant()}", 38);
        screen.Print(10, 1, $"Songs    {sid.Songs}, starts with {sid.StartSong}", 38);

        screen.Print(12, 1, "Subtune lengths", 38);
        for (var song = 1; song <= Math.Min(sid.Songs, SubtuneLengths); song++)
        {
            var length = songLengths.Of(file, song);
            var row = 13 + (song - 1) / 3;
            var column = 1 + (song - 1) % 3 * 13;
            screen.Print(row, column, $"{song}  {(int)length.TotalMinutes}:{length.Seconds:00}", 12);
        }

        screen.Print(22, 1, "RETURN list  SPACE next  1-9 subtune");
        screen.Print(23, 1, "RUN/STOP or F7 stops the music");
        return screen;
    }

    public static string Ranges(bool[] used)
    {
        var ranges = new List<string>();
        for (var address = 0; address < used.Length; address++)
        {
            if (!used[address])
                continue;
            var start = address;
            while (address + 1 < used.Length && used[address + 1])
                address++;
            ranges.Add($"${start:x4}-${address:x4}");
        }

        return string.Join(" ", ranges);
    }

    static byte Low(int value) => (byte)value;
    static byte High(int value) => (byte)(value >> 8);
    static byte Bcd(int value) => (byte)(value / 10 << 4 | value % 10);
}
