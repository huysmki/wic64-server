namespace Wic64Server.Content;

public enum FileType { Seq = 1, Prg = 2, Usr = 3 }

/// <summary>A file inside a disk image. Name is the raw PETSCII file name.</summary>
public sealed record DiskFile(byte[] Name, int Blocks, byte Track, byte Sector, FileType Type = FileType.Prg)
{
    public string DisplayName => Petscii.ToText(Name);
}

/// <summary>
/// Reads the directory and program files of a disk image: .d64 (35 or 40 tracks), .d71 (70 tracks) or .d81 (80 tracks).
/// </summary>
public sealed class DiskImage
{
    /// <summary>The file extensions of the disk images the server opens like folders.</summary>
    public static readonly string[] Extensions = [".d64", ".d71", ".d81"];

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The disk images in a folder.</summary>
    public static IEnumerable<string> In(string folder) => Directory.EnumerateFiles(folder).Where(IsImage);

    readonly byte[] image;
    readonly bool d81;
    readonly int directoryTrack;

    DiskImage(byte[] image, bool d81)
    {
        this.image = image;
        this.d81 = d81;
        directoryTrack = d81 ? 40 : 18;
    }

    public static DiskImage Load(string path) =>
        new(File.ReadAllBytes(path), Path.GetExtension(path).Equals(".d81", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Byte offset of a sector. .d64/.d71: tracks 1-17 have 21 sectors, 18-24 19, 25-30 18 and 31-35 17
    /// (40-track .d64: 36-40 17 too; .d71: tracks 36-70 repeat the layout of 1-35). .d81: 40 sectors on every track.
    /// </summary>
    int Offset(int track, int sector)
    {
        var offset = 0;
        for (var t = 1; t < track; t++)
            offset += SectorsOnTrack(t);
        return (offset + sector) * 256;
    }

    int SectorsOnTrack(int track) => d81 ? 40 : (track > 35 && image.Length > 200_000 ? track - 35 : track) switch
    {
        <= 17 => 21,
        <= 24 => 19,
        <= 30 => 18,
        _ => 17,
    };

    int MaxTrack => d81 ? 80 : image.Length > 200_000 ? 70 : 40;

    ReadOnlySpan<byte> Sector(int track, int sector)
    {
        if (track < 1 || track > MaxTrack || sector >= SectorsOnTrack(track))
            throw new InvalidDataException($"bad sector {track}/{sector}");
        var offset = Offset(track, sector);
        if (offset + 256 > image.Length)
            throw new InvalidDataException($"bad sector {track}/{sector}");
        return image.AsSpan(offset, 256);
    }

    /// <summary>Disk name, id and DOS type as the directory header shows them: 16 + 6 characters.</summary>
    public byte[] Title()
    {
        var header = Sector(directoryTrack, 0);
        var (name, id, dos) = d81 ? (0x04, 0x16, 0x19) : (0x90, 0xa2, 0xa5);
        byte[] title = [.. header.Slice(name, 16), (byte)' ', header[id], header[id + 1], (byte)' ', header[dos], header[dos + 1]];
        return title.Select(b => b == 0xa0 ? (byte)' ' : b).ToArray();
    }

    /// <summary>The PRG files in the directory (track 18, on a .d81 track 40), in directory order.</summary>
    public IReadOnlyList<DiskFile> Programs() => Files().Where(f => f.Type == FileType.Prg).ToList();

    /// <summary>The SEQ, PRG and USR files in the directory, in directory order (no REL files).</summary>
    public IReadOnlyList<DiskFile> Files()
    {
        var files = new List<DiskFile>();
        var visited = new HashSet<(int, int)>();
        var (track, sector) = (directoryTrack, d81 ? 3 : 1);

        while (track != 0 && visited.Add((track, sector)))
        {
            var data = Sector(track, sector);
            for (var entry = 0; entry < 8; entry++)
            {
                var e = data.Slice(entry * 32, 32);
                var type = e[2];
                if ((type & 0x80) == 0 || (type & 0x07) is not (1 or 2 or 3))
                    continue; // deleted, unclosed, REL or a partition

                var name = e.Slice(5, 16).ToArray();
                var length = Array.IndexOf(name, (byte)0xa0);
                files.Add(new DiskFile(length < 0 ? name : name[..length], e[30] | e[31] << 8, e[3], e[4], (FileType)(type & 0x07)));
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
