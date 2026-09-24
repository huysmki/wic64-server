using System.Buffers.Binary;
using System.Text;

namespace Wic64Server.Music;

/// <summary>A parsed PSID/RSID file (see https://www.hvsc.c64.org/download/C64Music/DOCUMENTS/SID_file_format.txt).</summary>
public sealed record SidFile(
    string Name,
    string Author,
    string Released,
    bool IsRsid,
    bool IsBasic,
    ushort LoadAddress,
    ushort InitAddress,
    ushort PlayAddress,
    int Songs,
    int StartSong,
    uint Speed,
    byte[] Data)
{
    public int EndAddress => LoadAddress + Data.Length;

    /// <summary>True when the given 1-based song expects CIA timing (60Hz) instead of the vertical blank.</summary>
    public bool UsesCiaTimer(int song) => (Speed >> (Math.Clamp(song, 1, 32) - 1) & 1) != 0;

    public static SidFile Parse(byte[] file)
    {
        if (file.Length < 0x76)
            throw new InvalidDataException("file too small");

        var magic = Encoding.ASCII.GetString(file, 0, 4);
        if (magic is not ("PSID" or "RSID"))
            throw new InvalidDataException("not a sid file");

        var header = file.AsSpan();
        var dataOffset = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
        var load = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
        var init = BinaryPrimitives.ReadUInt16BigEndian(header[10..]);
        var play = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);
        var songs = BinaryPrimitives.ReadUInt16BigEndian(header[14..]);
        var startSong = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        var speed = BinaryPrimitives.ReadUInt32BigEndian(header[18..]);
        var flags = dataOffset >= 0x7c ? BinaryPrimitives.ReadUInt16BigEndian(header[0x76..]) : 0;

        var data = file[dataOffset..];
        if (load == 0)
        {
            // The load address is stored in the first two bytes of the data (little endian)
            load = (ushort)(data[0] | data[1] << 8);
            data = data[2..];
        }

        if (init == 0)
            init = load;

        return new SidFile(
            ReadString(file, 0x16),
            ReadString(file, 0x36),
            ReadString(file, 0x56),
            magic == "RSID",
            (flags & 0x02) != 0 && magic == "RSID", // "C64 BASIC" flag: the tune is a BASIC program
            load,
            init,
            play,
            Math.Max(1, (int)songs),
            Math.Max(1, (int)startSong),
            speed,
            data);
    }

    static string ReadString(byte[] file, int offset)
    {
        var span = file.AsSpan(offset, 32);
        var end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]).Trim();
    }
}
