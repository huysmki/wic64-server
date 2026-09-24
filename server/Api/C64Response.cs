using System.Globalization;
using Wic64Server.Content;
using Wic64Server.Screens;

namespace Wic64Server.Api;

/// <summary>
/// Building blocks of the C64 protocol. Every response starts with a status byte:
/// $00 = ok, followed by the payload; $01 = error, followed by a 40 byte screen code line
/// the C64 shows in its status row.
/// </summary>
public static class C64Response
{
    const string ContentType = "application/octet-stream";

    public static IResult Ok(params byte[][] parts) =>
        Results.Bytes([0, .. parts.SelectMany(part => part)], ContentType);

    public static IResult Error(string message) =>
        Results.Bytes([1, .. ScreenCodes.Line(message)], ContentType);

    /// <summary>Only the error status, without a message (what the LOAD helper expects).</summary>
    public static IResult NotFound() => Results.Bytes([1], ContentType);

    /// <summary>A program: name length, name padded to 16 bytes, then the .prg file (load address first).</summary>
    public static IResult Program(byte[] name, byte[] program)
    {
        if (program.Length < 3)
            return Error("program is empty");

        // Below $0800 are the screen and the system, from $d000 on the I/O area
        var load = program[0] | program[1] << 8;
        var end = load + program.Length - 2;
        if (load < 0x0800 || end > 0xd000)
            return Error($"can not load ${load:x4}-${end - 1:x4}, only $0800-$cfff");

        var paddedName = new byte[16];
        Array.Fill(paddedName, (byte)0xa0);
        name.AsSpan(0, Math.Min(name.Length, 16)).CopyTo(paddedName);
        return Ok([(byte)Math.Min(name.Length, 16)], paddedName, program);
    }

    /// <summary>Numbers in the C64's URLs are hex: folders 4 digits, pages and entries 2.</summary>
    public static int? ParseHex(string value) =>
        int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result) ? result : null;

    public static Section? ParseSection(string value) => value switch
    {
        "p" => Section.Programs,
        "i" => Section.Pictures,
        "s" => Section.Music,
        _ => null,
    };
}
