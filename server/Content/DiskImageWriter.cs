namespace Wic64Server.Content;

/// <summary>
/// Writing a PRG file into the disk image in memory, the way CBM DOS does: data blocks from the free sectors in the
/// BAM (nearest to the directory track first, with the drive's interleave), a directory entry, the BAM updated.
/// A file with the same name is replaced. Sectors a directory entry still uses are never taken, even when the BAM
/// says they are free.
/// </summary>
public sealed partial class DiskImage
{
    bool D71 => !d81 && image.Length > 200_000;

    /// <summary>The image's bytes, after <see cref="WriteProgram"/>.</summary>
    public byte[] Bytes => image;

    /// <summary>Writes (or replaces) a PRG file. Throws when the disk is full; the image is then unchanged.</summary>
    public void WriteProgram(byte[] name, byte[] program)
    {
        var used = UsedSectors();
        var existing = DirectoryEntries().FirstOrDefault(e => (image[e + 2] & 0x87) == 0x82 && NameAt(e).SequenceEqual(name));
        var freed = existing > 0 ? Chain(image[existing + 3], image[existing + 4]) : [];

        var blocks = Math.Max(1, (program.Length + 253) / 254);
        var free = FreeSectors(used.Except(freed).ToHashSet()).Take(blocks).ToList();
        if (free.Count < blocks)
            throw new InvalidOperationException("disk full");

        var slot = existing > 0 ? existing : DirectoryEntries().FirstOrDefault(e => image[e + 2] == 0);
        if (slot == 0)
            slot = ExtendDirectory(used);
        if (slot == 0)
            throw new InvalidOperationException("directory full");

        // From here on the image changes: free the old file, write the blocks, then the entry
        foreach (var (track, sector) in freed)
            SetFree(track, sector, true);

        for (var i = 0; i < blocks; i++)
        {
            var (track, sector) = free[i];
            SetFree(track, sector, false);
            var offset = Offset(track, sector);
            var part = program.AsSpan(i * 254, Math.Min(254, program.Length - i * 254));
            image.AsSpan(offset, 256).Clear();
            if (i < blocks - 1)
                (image[offset], image[offset + 1]) = ((byte)free[i + 1].Track, (byte)free[i + 1].Sector);
            else
                (image[offset], image[offset + 1]) = (0, (byte)(part.Length + 1));
            part.CopyTo(image.AsSpan(offset + 2));
        }

        image.AsSpan(slot + 2, 30).Clear();
        image[slot + 2] = 0x82; // closed PRG
        (image[slot + 3], image[slot + 4]) = ((byte)free[0].Track, (byte)free[0].Sector);
        image.AsSpan(slot + 5, 16).Fill(0xa0);
        name.AsSpan(0, Math.Min(16, name.Length)).CopyTo(image.AsSpan(slot + 5));
        (image[slot + 30], image[slot + 31]) = ((byte)blocks, (byte)(blocks >> 8));
    }

    byte[] NameAt(int entry)
    {
        var name = image.AsSpan(entry + 5, 16).ToArray();
        var length = Array.IndexOf(name, (byte)0xa0);
        return length < 0 ? name : name[..length];
    }

    /// <summary>Byte offsets of all 32-byte directory entries, in directory order.</summary>
    List<int> DirectoryEntries()
    {
        var entries = new List<int>();
        foreach (var (track, sector) in Chain(directoryTrack, d81 ? 3 : 1))
        {
            for (var entry = 0; entry < 8; entry++)
                entries.Add(Offset(track, sector) + entry * 32);
        }

        return entries;
    }

    /// <summary>The sectors of a chain (a file or the directory), stopping at a loop or a bad link.</summary>
    List<(int Track, int Sector)> Chain(int track, int sector)
    {
        var chain = new List<(int, int)>();
        while (track != 0 && track <= MaxTrack && sector < SectorsOnTrack(track) && !chain.Contains((track, sector)))
        {
            chain.Add((track, sector));
            var offset = Offset(track, sector);
            if (offset + 256 > image.Length)
                break;
            (track, sector) = (image[offset], image[offset + 1]);
        }

        return chain;
    }

    /// <summary>Header and BAM sectors, the directory, and the sectors of every file in it (also REL side sectors).</summary>
    HashSet<(int Track, int Sector)> UsedSectors()
    {
        var used = new HashSet<(int, int)> { (directoryTrack, 0) };
        if (d81)
            used.UnionWith([(40, 1), (40, 2)]);
        if (D71)
            used.Add((53, 0));

        used.UnionWith(Chain(directoryTrack, d81 ? 3 : 1));
        foreach (var entry in DirectoryEntries())
        {
            if (image[entry + 2] == 0)
                continue;
            used.UnionWith(Chain(image[entry + 3], image[entry + 4]));
            if ((image[entry + 2] & 0x07) == 4)
                used.UnionWith(Chain(image[entry + 21], image[entry + 22]));
        }

        return used;
    }

    /// <summary>Free sectors for data: tracks nearest to the directory first, sectors in the drive's interleave.</summary>
    IEnumerable<(int Track, int Sector)> FreeSectors(HashSet<(int Track, int Sector)> used)
    {
        var lastTrack = d81 ? 80 : D71 ? 70 : 35;
        int[] reserved = d81 ? [40] : D71 ? [18, 53] : [18];
        var tracks = Enumerable.Range(1, lastTrack).Except(reserved)
            .OrderBy(t => Math.Abs(t - (t > 35 && D71 ? 53 : directoryTrack)) + (t > 35 && D71 ? 100 : 0))
            .ThenBy(t => t);

        foreach (var track in tracks)
        {
            foreach (var sector in Interleaved(track, d81 ? 1 : 10))
            {
                if (IsFree(track, sector) && !used.Contains((track, sector)))
                    yield return (track, sector);
            }
        }
    }

    /// <summary>All sectors of a track, stepping by the interleave and taking the next one when a sector was had.</summary>
    IEnumerable<int> Interleaved(int track, int interleave)
    {
        var count = SectorsOnTrack(track);
        var taken = new bool[count];
        var sector = 0;
        for (var i = 0; i < count; i++)
        {
            while (taken[sector])
                sector = (sector + 1) % count;
            taken[sector] = true;
            yield return sector;
            sector = (sector + interleave) % count;
        }
    }

    /// <summary>A new directory sector on the directory track, linked after the last one: its first entry.</summary>
    int ExtendDirectory(HashSet<(int Track, int Sector)> used)
    {
        var last = Chain(directoryTrack, d81 ? 3 : 1)[^1];
        foreach (var sector in Interleaved(directoryTrack, d81 ? 1 : 3))
        {
            if (!IsFree(directoryTrack, sector) || used.Contains((directoryTrack, sector)))
                continue;

            SetFree(directoryTrack, sector, false);
            var offset = Offset(directoryTrack, sector);
            image.AsSpan(offset, 256).Clear();
            image[offset + 1] = 0xff;
            var lastOffset = Offset(last.Track, last.Sector);
            (image[lastOffset], image[lastOffset + 1]) = ((byte)directoryTrack, (byte)sector);
            return offset;
        }

        return 0;
    }

    /// <summary>Offsets of a track's free-block count and its bitmap (bit set = free) in the BAM.</summary>
    (int Count, int Bitmap) BamEntry(int track)
    {
        if (d81)
        {
            var bam = track <= 40 ? Offset(40, 1) + 0x10 + 6 * (track - 1) : Offset(40, 2) + 0x10 + 6 * (track - 41);
            return (bam, bam + 1);
        }

        if (track <= 35)
            return (Offset(18, 0) + 4 * track, Offset(18, 0) + 4 * track + 1);
        return (Offset(18, 0) + 0xdd + track - 36, Offset(53, 0) + 3 * (track - 36));
    }

    bool IsFree(int track, int sector)
    {
        var (_, bitmap) = BamEntry(track);
        return (image[bitmap + sector / 8] & 1 << sector % 8) != 0;
    }

    void SetFree(int track, int sector, bool free)
    {
        if (IsFree(track, sector) == free)
            return;
        var (count, bitmap) = BamEntry(track);
        image[bitmap + sector / 8] ^= (byte)(1 << sector % 8);
        image[count] = (byte)(image[count] + (free ? 1 : -1));
    }
}
