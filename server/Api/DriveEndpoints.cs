using System.Text;
using Wic64Server.Content;
using Wic64Server.Uploads;
using static Wic64Server.Api.C64Response;

namespace Wic64Server.Api;

/// <summary>
/// Requests of the disk tools plugin (c64/plugins/disk-tools.asm). The plugin sends data with the WiC64's
/// HTTP POST, which wraps it as the form field "data" of a multipart/form-data body.
/// </summary>
/// <remarks>
/// <code>
/// POST /d/dir/{device}                    the directory (LOAD"$",8 without load address) -> page 0, see DriveSession.Page
/// GET  /d/page/{page}                     another page of that directory
/// POST /d/file/{page}/{entry}/{part}/{last}  part of a file (last = 1: the final part) -> message line when done
/// POST /d/track/{track}                   one track of a disk backup (sectors + status codes) -> message line
/// POST /d/track/01/{name}                 the first track, with the name of the .d64 (PETSCII in hex)
/// </code>
/// </remarks>
public sealed class DriveEndpoints(DriveSession drive, ILogger<DriveEndpoints> log)
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/d/dir", (HttpRequest request) => Directory(request, "08"));
        app.MapPost("/d/dir/{device}", Directory);
        app.MapGet("/d/page/{page}", Page);
        app.MapPost("/d/file/{page}/{index}/{part}/{last}", FilePart);
        app.MapPost("/d/track/{track}", (HttpRequest request, string track) => Track(request, track, null));
        app.MapPost("/d/track/{track}/{name}", Track);
    }

    async Task<IResult> Directory(HttpRequest request, string device)
    {
        try
        {
            drive.SetDirectory(await PostedData(request), ParseHex(device) ?? 8);
            return Ok(drive.Page(0));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read the directory from the C64");
            return Error($"directory: {e.Message}");
        }
    }

    IResult Page(string page) =>
        ParseHex(page) is { } p ? Ok(drive.Page(p)) : Error("bad page");

    async Task<IResult> FilePart(HttpRequest request, string page, string index, string part, string last)
    {
        if (ParseHex(page) is not { } p || ParseHex(index) is not { } i || ParseHex(part) is not { } n)
            return Error("bad upload request");

        try
        {
            var message = drive.AddFilePart(p, i, n, last == "1", await PostedData(request));
            return message is null ? Ok() : Ok(Screens.ScreenCodes.Line(message));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Upload from the C64 failed");
            return Error($"upload: {e.Message}");
        }
    }

    /// <summary>A track of a disk backup; track 1 can carry the name of the .d64 as PETSCII in hex.</summary>
    async Task<IResult> Track(HttpRequest request, string track, string? name)
    {
        if (ParseHex(track) is not { } t)
            return Error("bad track");

        try
        {
            var text = name is null ? null : Petscii.ToText(Convert.FromHexString(name));
            return Ok(Screens.ScreenCodes.Line(drive.AddTrack(t, await PostedData(request), text)));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Disk backup from the C64 failed");
            return Error($"backup: {e.Message}");
        }
    }

    const string Boundary = "--WiC64-Binary-Data";

    /// <summary>
    /// The posted bytes. The WiC64 sends "--WiC64-Binary-Data\n", a Content-Disposition line and an empty line,
    /// the data, then "\r\n--WiC64-Binary-Data--\r\n". A plain (non-multipart) body is taken as is.
    /// </summary>
    static async Task<byte[]> PostedData(HttpRequest request)
    {
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body);
        var bytes = body.ToArray();

        if (request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
            return bytes;

        var headerEnd = IndexOf(bytes, "\r\n\r\n"u8);
        if (headerEnd < 0 || !bytes.AsSpan(0, Math.Min(bytes.Length, Boundary.Length)).SequenceEqual(Encoding.ASCII.GetBytes(Boundary)))
            throw new InvalidDataException("unexpected POST body");

        var start = headerEnd + 4;
        var footer = Encoding.ASCII.GetBytes("\r\n" + Boundary + "--");
        var end = LastIndexOf(bytes, footer);
        if (end < start)
            throw new InvalidDataException("POST body without end");
        return bytes[start..end];
    }

    static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);

    static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().LastIndexOf(pattern);
}
