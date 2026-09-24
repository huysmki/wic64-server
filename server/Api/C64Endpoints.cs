using Wic64Server.Activity;
using Wic64Server.Content;
using Wic64Server.Music;
using Wic64Server.Pictures;
using Wic64Server.Programs;
using Wic64Server.Screens;
using static Wic64Server.Api.C64Response;

namespace Wic64Server.Api;

/// <summary>
/// The protocol between the browser on the C64 (c64/browser.asm) and this server: plain HTTP GET via the WiC64.
/// </summary>
/// <remarks>
/// <code>
/// GET /m/{section}/{folder}/{page}    section p|i|s: count, pages, parent folder (2), folder ids of the
///                                     20 entries (2 each, 0 = file), 1000 screen codes (the whole menu screen)
/// GET /p/{folder}/{page}/{entry}      program: name length, name (16 bytes PETSCII), .prg file
///                                     (load address $0800 or higher, ending at $d000 at the latest)
/// GET /i/{folder}/{page}/{entry}      10001 bytes Koala data: bitmap, screen RAM, color RAM, background
/// GET /s/{folder}/{page}/{entry}      tune (see SidService.Response)
/// GET /v/{folder}/{page}/{entry}      info screen of a tune: 1000 screen codes
/// GET /x                              something pushed? 0 = no, 1 = run, 2 = save and run, 3 = picture, 4 = tune
/// GET /x/p, /x/i, /x/s                the pushed program, picture or tune, like /p, /i and /s
/// POST /push?name=..&amp;save=1       push a .prg from the command line (make push)
/// GET /h                              LOAD helper code for $02a7 and $0334 (c64/loadhelper.asm)
/// GET /l/{folder}/{name}              LOAD from the helper, name in hex: .prg file, or only status $01
/// GET /o                              the WiC64 portal (fetched from x.wic64.net), like /p
/// GET /browser.prg                    the C64 browser itself
/// </code>
/// </remarks>
public sealed class C64Endpoints(
    Catalog catalog,
    MenuScreen menus,
    SidService sids,
    PictureService pictures,
    LoadService loads,
    PushQueue pushQueue,
    ActivityLog activity,
    ServerOptions options,
    ILogger<C64Endpoints> log)
{
    const int MaxProgramSize = 0xd000 - 0x0800 + 2; // the C64 loads programs to $0800-$cfff
    const string PortalUrl = "http://x.wic64.net/menue.prg";

    static readonly HttpClient portalClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    byte[]? portal;
    DateTime portalFetched;

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/browser.prg", Browser);
        app.MapGet("/o", Portal);
        app.MapGet("/m/{section}/{folder}/{page}", Menu);
        app.MapGet("/p/{folder}/{page}/{index}", Program);
        app.MapGet("/i/{folder}/{page}/{index}", Picture);
        app.MapGet("/s/{folder}/{page}/{index}", Tune);
        app.MapGet("/v/{folder}/{page}/{index}", TuneInfo);

        app.MapPost("/push", PushFromCommandLine);
        app.MapGet("/x", PushState);
        app.MapGet("/x/p", PushedProgram);
        app.MapGet("/x/i", PushedPicture);
        app.MapGet("/x/s", PushedTune);

        app.MapGet("/h", LoadHelper);
        app.MapGet("/h/{folder}", LegacyLoadHelper);
        app.MapGet("/l/{folder}/{name}", LoadFile);
    }

    IResult Browser()
    {
        var path = Path.Combine(options.BuildFolder, "browser.prg");
        return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
    }

    /// <summary>The WiC64 portal, fetched for the C64 (keeps the browser small). Cached for an hour.</summary>
    async Task<IResult> Portal()
    {
        try
        {
            if (portal is null || DateTime.UtcNow - portalFetched > TimeSpan.FromHours(1))
            {
                portal = await portalClient.GetByteArrayAsync(PortalUrl);
                portalFetched = DateTime.UtcNow;
            }

            activity.Add("program", "Loading the WiC64 portal");
            return C64Response.Program("MENUE"u8.ToArray(), portal);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not fetch the WiC64 portal");
            return Error($"portal: {e.Message}");
        }
    }

    // -------------------------------------------------------------------------------------------
    // Menus and entries

    IResult Menu(string section, string folder, string page)
    {
        if (ParseSection(section) is not { } s || ParseHex(folder) is not { } f || ParseHex(page) is not { } p)
            return Error("bad menu request");

        try
        {
            var entries = catalog.List(s, f);
            var pages = Catalog.PageCount(entries);
            p = Math.Clamp(p, 0, pages - 1);
            var onPage = entries.Skip(p * Catalog.PageSize).Take(Catalog.PageSize).ToList();

            var parent = catalog.ParentOf(s, f);
            var ids = new byte[Catalog.PageSize * 2];
            for (var i = 0; i < onPage.Count; i++)
            {
                if (onPage[i] is FolderEntry folderEntry)
                {
                    ids[i * 2] = (byte)folderEntry.Id;
                    ids[i * 2 + 1] = (byte)(folderEntry.Id >> 8);
                }
            }

            var screen = menus.Render(s, f, p, pages, onPage);
            log.LogInformation("Menu {Section} /{Folder} page {Page}/{Pages}", s, catalog.NameOf(s, f), p + 1, pages);
            return Ok([(byte)onPage.Count, (byte)pages, (byte)parent, (byte)(parent >> 8)], ids, screen.Data);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not list folder {Folder}", f);
            return Error($"cannot open: {e.Message}");
        }
    }

    IResult Program(string folder, string page, string index)
    {
        try
        {
            switch (Lookup(Section.Programs, folder, page, index))
            {
                case FileEntry file:
                    log.LogInformation("Program {File}", file.Name);
                    activity.Add("program", $"Loading {file.Name}");
                    return C64Response.Program(Petscii.FileName(file.Name), File.ReadAllBytes(file.Path));
                case DiskEntry disk:
                    log.LogInformation("Program {File} from {Image}", disk.Name, Path.GetFileName(disk.ImagePath));
                    activity.Add("program", $"Loading {disk.Name} from {Path.GetFileName(disk.ImagePath)}");
                    return C64Response.Program(disk.File.Name, DiskImage.Load(disk.ImagePath).Read(disk.File));
                default:
                    return Error("program not found");
            }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not read program");
            return Error($"cannot read: {e.Message}");
        }
    }

    IResult Picture(string folder, string page, string index)
    {
        if (Lookup(Section.Pictures, folder, page, index) is not FileEntry file)
            return Error("picture not found");

        try
        {
            var koala = pictures.Koala(file.Path);
            log.LogInformation("Picture {File}", file.Name);
            activity.Add("picture", $"Showing {file.Name}");
            return Ok(koala);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not convert {File}", file.Name);
            return Error($"cannot convert: {e.Message}");
        }
    }

    IResult Tune(string folder, string page, string index)
    {
        if (Lookup(Section.Music, folder, page, index) is not FileEntry file)
            return Error("tune not found");

        return TuneResponse(file, ParseHex(folder)!.Value, ParseHex(page)!.Value, ParseHex(index)!.Value, pushed: false);
    }

    IResult TuneInfo(string folder, string page, string index)
    {
        if (Lookup(Section.Music, folder, page, index) is not FileEntry file)
            return Error("tune not found");

        try
        {
            return Ok(sids.InfoScreen(file).Data);
        }
        catch (Exception e)
        {
            return Error($"bad sid: {e.Message}");
        }
    }

    IResult TuneResponse(FileEntry file, int folder, int page, int index, bool pushed)
    {
        try
        {
            var (sid, _, plan) = sids.Load(file);
            if (plan.Problem is not null)
            {
                log.LogWarning("Rejected {File}: {Problem}", file.Name, plan.Problem);
                activity.Add("error", $"Can not play {file.Name}: {plan.Problem}");
                return Error(plan.Problem);
            }

            var mode = plan.Mode.ToString().ToLowerInvariant();
            log.LogInformation("Music {File}: {Name} by {Author}, {Songs} songs, {Mode}", file.Name, sid.Name, sid.Author, sid.Songs, mode);
            activity.Add("music", $"Playing {sid.Name} by {sid.Author} ({mode}{(pushed ? ", pushed" : "")})");
            return Ok(sids.Response(file, folder, page, index));
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not load {File}", file.Name);
            return Error($"bad sid: {e.Message}");
        }
    }

    Entry? Lookup(Section section, string folder, string page, string index) =>
        ParseHex(folder) is { } f && ParseHex(page) is { } p && ParseHex(index) is { } i ? catalog.Get(section, f, p, i) : null;

    // -------------------------------------------------------------------------------------------
    // Pushing from the computer (make push and the web UI)

    async Task<IResult> PushFromCommandLine(HttpRequest request, string? name, string? save)
    {
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body);
        var program = body.ToArray();
        if (program.Length is < 3 or > MaxProgramSize)
            return Results.BadRequest($"a .prg of 3 to {MaxProgramSize} bytes is expected, got {program.Length}\n");

        var saveToDisk = save is "1" or "true" or "yes";
        var petsciiName = Petscii.FileName(name ?? "pushed");
        pushQueue.Push(new PushQueue.Pushed(PushQueue.Kind.Program, petsciiName, program, saveToDisk));
        activity.Add("push", $"Pushed {Petscii.ToText(petsciiName)} from the command line{(saveToDisk ? " (save to disk)" : "")}");
        log.LogInformation("Pushed {Name} ({Bytes} bytes{Save}), waiting for the C64",
            Petscii.ToText(petsciiName), program.Length, saveToDisk ? ", save to disk" : "");
        return Results.Text($"Pushed {Petscii.ToText(petsciiName)}{(saveToDisk ? " (save to disk)" : "")}. " +
                            "The C64 picks it up within a second while the browser menu is on screen.\n");
    }

    IResult PushState() => Ok([pushQueue.Peek()?.State ?? 0]);

    IResult PushedProgram()
    {
        if (pushQueue.Take(PushQueue.Kind.Program) is not { } pushed)
            return Error("nothing pushed");

        log.LogInformation("C64 picked up {Name}", Petscii.ToText(pushed.Name));
        activity.Add("push", $"C64 picked up {Petscii.ToText(pushed.Name)}{(pushed.Save ? " and saves it to disk" : "")}");
        return C64Response.Program(pushed.Name, pushed.Program!); // programs always carry their bytes
    }

    IResult PushedPicture()
    {
        if (pushQueue.Take(PushQueue.Kind.Picture) is not { Path: { } path })
            return Error("nothing pushed");

        try
        {
            var koala = pictures.Koala(path);
            activity.Add("picture", $"Showing {Path.GetFileName(path)} (pushed)");
            return Ok(koala);
        }
        catch (Exception e)
        {
            return Error($"cannot convert: {e.Message}");
        }
    }

    IResult PushedTune()
    {
        if (pushQueue.Take(PushQueue.Kind.Tune) is not { Path: { } path })
            return Error("nothing pushed");

        // The C64 needs the tune's place in the menu for the info screen and auto-next
        var (folder, page, index) = catalog.Locate(Section.Music, path) ?? (0, 0, 0);
        return TuneResponse(new FileEntry(Path.GetFileName(path), Path.GetFullPath(path)), folder, page, index, pushed: true);
    }

    // -------------------------------------------------------------------------------------------
    // LOAD helper: programs started from the browser load more files from the server

    IResult LoadHelper()
    {
        try
        {
            return Ok(loads.Helper());
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not build the LOAD helper");
            return Error($"no load helper: {e.Message}");
        }
    }

    /// <summary>For browsers built before the browser wrote the helper's URL itself.</summary>
    IResult LegacyLoadHelper(HttpRequest request, string folder) =>
        ParseHex(folder) is { } f ? Ok(loads.LegacyHelper(request.Host.Value ?? "", f)) : Error("bad helper request");

    IResult LoadFile(string folder, string name)
    {
        try
        {
            var program = ParseHex(folder) is { } f ? loads.Load(f, Convert.FromHexString(name)) : null;
            return program is null ? NotFound() : Ok(program); // on NotFound the helper tries the real drive
        }
        catch (Exception e)
        {
            log.LogWarning(e, "LOAD failed");
            return NotFound();
        }
    }
}
