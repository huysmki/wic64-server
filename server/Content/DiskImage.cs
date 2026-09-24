namespace Wic64Server.Content;

/// <summary>A program file inside a .d64 disk image. Name is the raw PETSCII file name.</summary>
public sealed record DiskFile(byte[] Name, int Blocks, byte Track, byte Sector)
{
    public string DisplayName => Petscii.ToText(Name);
}

/// <summary>Reads the directory and program files of a .d64 disk image (35 or 40 tracks).</summary>
public sealed class DiskImage
{
    readonly byte[] image;

    DiskImage(byte[] image) => this.image = image;

    public static DiskImage Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Byte offset of a sector: tracks 1-17 have 21 sectors, 18-24 19, 25-30 18 and 31-40 17.</summary>
    static int Offset(int track, int sector)
    {
        var offset = 0;
        for (var t = 1; t < track; t++)
            offset += SectorsOnTrack(t);
        return (offset + sector) * 256;
    }

    static int SectorsOnTrack(int track) => track switch
    {
        <= 17 => 21,
        <= 24 => 19,
        <= 30 => 18,
        _ => 17,
    };

    ReadOnlySpan<byte> Sector(int track, int sector)
    {
        var offset = Offset(track, sector);
        if (track is < 1 or > 40 || sector >= SectorsOnTrack(track) || offset + 256 > image.Length)
            throw new InvalidDataException($"bad sector {track}/{sector}");
        return image.AsSpan(offset, 256);
    }

    /// <summary>Disk name, id and DOS type as the directory header shows them: 16 + 6 characters.</summary>
    public byte[] Title()
    {
        var header = Sector(18, 0);
        byte[] title = [.. header.Slice(0x90, 16), (byte)' ', header[0xa2], header[0xa3], (byte)' ', header[0xa5], header[0xa6]];
        return title.Select(b => b == 0xa0 ? (byte)' ' : b).ToArray();
    }

    /// <summary>The PRG files in the directory (track 18), in directory order.</summary>
    public IReadOnlyList<DiskFile> Programs()
    {
        var files = new List<DiskFile>();
        var visited = new HashSet<(int, int)>();
        var (track, sector) = (18, 1);

        while (track != 0 && visited.Add((track, sector)))
        {
            var data = Sector(track, sector);
            for (var entry = 0; entry < 8; entry++)
            {
                var e = data.Slice(entry * 32, 32);
                var type = e[2];
                if ((type & 0x80) == 0 || (type & 0x07) != 2)
                    continue; // deleted, unclosed, or not a PRG file

                var name = e.Slice(5, 16).ToArray();
                var length = Array.IndexOf(name, (byte)0xa0);
                files.Add(new DiskFile(length < 0 ? name : name[..length], e[30] | e[31] << 8, e[3], e[4]));
            }

            (track, sector) = (data[0], data[1]);
        }

        return files;
    }

    /// <summary>Follows the sector chain of a file and returns its bytes (starting with the load address).</summary>
    public byte[] Read(DiskFile file)
    {
        var result = new List<byte>();
        var visited = new HashSet<(int, int)>();
        var (track, sector) = ((int)file.Track, (int)file.Sector);

        while (track != 0)
        {
            if (!visited.Add((track, sector)))
                throw new InvalidDataException("sector chain loops");

            var data = Sector(track, sector);
            if (data[0] == 0)
            {
                // last sector: the second byte is the index of the last used byte
                result.AddRange(data[2..(Math.Max(data[1], (byte)1) + 1)].ToArray());
                break;
            }

            result.AddRange(data[2..].ToArray());
            (track, sector) = (data[0], data[1]);
        }

        return result.ToArray();
    }
}
