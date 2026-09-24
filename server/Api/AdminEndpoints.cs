using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using SkiaSharp;
using Wic64Server.Activity;
using Wic64Server.Content;
using Wic64Server.Music;
using Wic64Server.Pictures;
using Wic64Server.Programs;

namespace Wic64Server.Api;

/// <summary>
/// JSON API for the web UI (wwwroot): browse, upload, rename and delete content, push programs,
/// preview pictures and tunes, and follow what the C64 does.
/// </summary>
public sealed class AdminEndpoints(
    Catalog catalog,
    SidService sids,
    SongLengths songLengths,
    PictureService pictures,
    PushQueue pushQueue,
    ActivityLog activity,
    ServerOptions options)
{
    public sealed record FileItem(
        string Name,
        string Kind,        // folder, disk, program, picture, tune, other
        string Path,        // relative to the section folder
        int? Index,         // program inside a .d64
        long Size,
        DateTime? Modified,
        string Details);

    public sealed record NameRequest(string Section, string Path, string Name);
    public sealed record PushRequest(string? Section, string Path, int? Index, bool Save);

    static readonly string[] ProgramExtensions = [".prg"];
    static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".koa", ".kla"];

    public void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", () => new
        {
            c64LastSeen = activity.LastSeen,
            c64Address = activity.LastAddress,
            serverAddresses = LocalAddresses().Select(ip => $"{ip}:{options.Port}").Append($"{BonjourName()}:{options.Port}").ToArray(),
            pushed = pushQueue.Peek() is { } p ? new { name = p.Title, kind = p.What.ToString().ToLowerInvariant(), save = p.Save } : null,
            contentFolder = catalog.Root,
            songLengths = songLengths.DatabaseFile,
        });

        api.MapGet("/activity", (long? since) => activity.Since(since ?? 0));

        api.MapGet("/files", (string section, string? path) =>
        {
            var root = SectionRoot(catalog, section);
            var full = Resolve(root, path);
            var relative = Relative(root, full);

            if (File.Exists(full) && full.EndsWith(".d64", StringComparison.OrdinalIgnoreCase))
                return Results.Ok(new { path = relative, items = DiskItems(full, relative) });
            if (!Directory.Exists(full))
            {
                return Results.NotFound(new
                {
                    error = Directory.Exists(options.ContentFolder)
                        ? $"folder not found: {full}"
                        : $"content folder not found: {options.ContentFolder} (set \"Content\" in server/appsettings.json)",
                });
            }

            var directory = new DirectoryInfo(full);
            var items = new List<FileItem>();
            foreach (var d in directory.EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                items.Add(new FileItem(d.Name, "folder", Relative(root, d.FullName), null, 0, d.LastWriteTimeUtc,
                    Items(d.EnumerateFileSystemInfos().Count(f => !f.Name.StartsWith('.')))));

            foreach (var f in directory.EnumerateFiles().Where(f => !f.Name.StartsWith('.')).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                items.Add(Describe(section, root, f));

            return Results.Ok(new { path = relative, items });
        });

        api.MapPost("/upload", async (HttpRequest request, string section, string? path) =>
        {
            var folder = Resolve(SectionRoot(catalog, section), path);
            if (!Directory.Exists(folder))
                return Results.NotFound(new { error = "folder not found" });

            var form = await request.ReadFormAsync();
            var saved = new List<string>();
            foreach (var file in form.Files)
            {
                var name = SafeName(Path.GetFileName(file.FileName));
                await using var target = File.Create(Path.Combine(folder, name));
                await file.CopyToAsync(target);
                saved.Add(name);
            }

            activity.Add("files", $"Uploaded {string.Join(", ", saved)}");
            return Results.Ok(new { saved });
        }).DisableAntiforgery();

        api.MapPost("/folder", (NameRequest r) =>
        {
            var folder = Resolve(SectionRoot(catalog, r.Section), Path.Combine(r.Path ?? "", SafeName(r.Name)));
            Directory.CreateDirectory(folder);
            return Results.Ok();
        });

        api.MapPost("/rename", (NameRequest r) =>
        {
            var root = SectionRoot(catalog, r.Section);
            var source = Resolve(root, r.Path);
            if (source == root)
                return Results.BadRequest(new { error = "can not rename the section folder" });
            var target = Path.Combine(Path.GetDirectoryName(source)!, SafeName(r.Name));
            if (File.Exists(target) || Directory.Exists(target))
                return Results.Conflict(new { error = $"{r.Name} already exists" });

            if (Directory.Exists(source))
                Directory.Move(source, target);
            else
                File.Move(source, target);
            return Results.Ok();
        });

        api.MapDelete("/files", (string section, string path) =>
        {
            var root = SectionRoot(catalog, section);
            var full = Resolve(root, path);
            if (full == root)
                return Results.BadRequest(new { error = "can not delete the section folder" });

            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
            else if (File.Exists(full))
                File.Delete(full);
            else
                return Results.NotFound(new { error = "not found" });

            activity.Add("files", $"Deleted {Path.GetFileName(full)}");
            return Results.Ok();
        });

        api.MapGet("/download", (string section, string path) =>
        {
            var full = Resolve(SectionRoot(catalog, section), path);
            return File.Exists(full) ? Results.File(full, "application/octet-stream", Path.GetFileName(full)) : Results.NotFound();
        });

        // The picture as it looks on the C64
        api.MapGet("/picture", (string path) =>
        {
            var full = Resolve(SectionRoot(catalog, "img"), path);
            return File.Exists(full) ? Results.File(pictures.PreviewPng(full), "image/png") : Results.NotFound();
        });

        // The original picture, for the side-by-side preview
        api.MapGet("/original", (string path) =>
        {
            var full = Resolve(SectionRoot(catalog, "img"), path);
            return File.Exists(full) ? Results.File(full, MimeType(full)) : Results.NotFound();
        });

        api.MapGet("/sid", (string path) =>
        {
            var full = Resolve(SectionRoot(catalog, "sid"), path);
            if (!File.Exists(full))
                return Results.NotFound();

            var (sid, file, plan) = sids.Load(new FileEntry(Path.GetFileName(full), full));
            return Results.Ok(new
            {
                sid.Name,
                sid.Author,
                sid.Released,
                type = sid.IsRsid ? "RSID" : "PSID",
                sid.Songs,
                sid.StartSong,
                load = $"${sid.LoadAddress:x4}-${sid.EndAddress - 1:x4}",
                init = $"${sid.InitAddress:x4}",
                play = sid.PlayAddress == 0 ? "own interrupt" : $"${sid.PlayAddress:x4}",
                timing = sid.UsesCiaTimer(sid.StartSong) ? "CIA timer" : "50 Hz",
                mode = plan.Problem is null ? plan.Mode.ToString().ToLowerInvariant() : "not playable",
                problem = plan.Problem,
                uses = SidService.Ranges(plan.Used),
                lengths = Enumerable.Range(1, Math.Min(sid.Songs, 32)).Select(song =>
                {
                    var length = songLengths.Of(file, song);
                    return $"{(int)length.TotalMinutes}:{length.Seconds:00}";
                }),
                knownLengths = songLengths.Knows(file),
            });
        });

        api.MapPost("/push", (PushRequest r) =>
        {
            if (r.Section is "img" or "sid")
                return PushMedia(r);

            var full = Resolve(SectionRoot(catalog, "prg"), r.Path);
            byte[] name, program;
            if (r.Index is { } index && full.EndsWith(".d64", StringComparison.OrdinalIgnoreCase))
            {
                var image = DiskImage.Load(full);
                var files = image.Programs();
                if (index < 0 || index >= files.Count)
                    return Results.NotFound(new { error = "program not found" });
                name = files[index].Name;
                program = image.Read(files[index]);
            }
            else if (File.Exists(full))
            {
                name = Petscii.FileName(Path.GetFileName(full));
                program = File.ReadAllBytes(full);
            }
            else
            {
                return Results.NotFound(new { error = "program not found" });
            }

            pushQueue.Push(new PushQueue.Pushed(PushQueue.Kind.Program, name, program, r.Save));
            activity.Add("push", $"Pushed {Petscii.ToText(name)}{(r.Save ? " (save to disk)" : "")}, waiting for the C64");
            return Results.Ok(new { name = Petscii.ToText(name) });
        });

        api.MapDelete("/push", () =>
        {
            if (pushQueue.Take() is { } pushed)
                activity.Add("push", $"Cancelled push of {pushed.Title}");
            return Results.Ok();
        });
    }

    /// <summary>Show a picture or play a tune on the C64.</summary>
    IResult PushMedia(PushRequest r)
    {
        var full = Resolve(SectionRoot(catalog, r.Section!), r.Path);
        if (!File.Exists(full))
            return Results.NotFound(new { error = "file not found" });

        if (r.Section == "sid")
        {
            var (sid, _, plan) = sids.Load(new FileEntry(Path.GetFileName(full), full));
            if (plan.Problem is not null)
                return Results.BadRequest(new { error = $"can not be played: {plan.Problem}" });
            pushQueue.Push(new PushQueue.Pushed(PushQueue.Kind.Tune, [], Path: full));
            activity.Add("push", $"Pushed {sid.Name} to play, waiting for the C64");
        }
        else
        {
            pictures.Koala(full); // convert now, so the C64 does not have to wait
            pushQueue.Push(new PushQueue.Pushed(PushQueue.Kind.Picture, [], Path: full));
            activity.Add("push", $"Pushed {Path.GetFileName(full)} to show, waiting for the C64");
        }

        return Results.Ok(new { name = Path.GetFileName(full) });
    }

    FileItem Describe(string section, string root, FileInfo f)
    {
        var relative = Relative(root, f.FullName);
        var ext = f.Extension.ToLowerInvariant();
        try
        {
            switch (section)
            {
                case "prg" when ext == ".d64":
                    var programs = DiskImage.Load(f.FullName).Programs();
                    return new FileItem(f.Name, "disk", relative, null, f.Length, f.LastWriteTimeUtc,
                        $"disk image, {programs.Count} program{(programs.Count == 1 ? "" : "s")}");
                case "prg" when ProgramExtensions.Contains(ext):
                {
                    using var stream = f.OpenRead();
                    var load = stream.ReadByte() | stream.ReadByte() << 8;
                    return new FileItem(f.Name, "program", relative, null, f.Length, f.LastWriteTimeUtc,
                        $"${load:x4}-${load + f.Length - 3:x4}, {Blocks((f.Length + 253) / 254)}");
                }
                case "img" when PictureExtensions.Contains(ext):
                {
                    var details = ext is ".koa" or ".kla" ? "Koala picture" : "";
                    if (details == "")
                    {
                        using var codec = SKCodec.Create(f.FullName);
                        details = codec is null ? "unsupported image" : $"{codec.Info.Width} x {codec.Info.Height}";
                    }
                    return new FileItem(f.Name, "picture", relative, null, f.Length, f.LastWriteTimeUtc, details);
                }
                case "sid" when ext == ".sid":
                {
                    var (sid, _, plan) = sids.Load(new FileEntry(f.Name, f.FullName));
                    var mode = plan.Problem is null ? plan.Mode.ToString().ToLowerInvariant() : "not playable";
                    return new FileItem(f.Name, "tune", relative, null, f.Length, f.LastWriteTimeUtc,
                        $"{sid.Name} · {sid.Author} · {sid.Songs} song{(sid.Songs == 1 ? "" : "s")} · {mode}");
                }
            }
        }
        catch (Exception e)
        {
            return new FileItem(f.Name, "other", relative, null, f.Length, f.LastWriteTimeUtc, $"unreadable: {e.Message}");
        }

        var note = f.Name.Equals("Songlengths.md5", StringComparison.OrdinalIgnoreCase) ? "song length database" : "not shown on the C64";
        return new FileItem(f.Name, "other", relative, null, f.Length, f.LastWriteTimeUtc, note);
    }

    static IEnumerable<FileItem> DiskItems(string imagePath, string relative)
    {
        var image = DiskImage.Load(imagePath);
        return image.Programs().Select((file, index) =>
        {
            string details;
            try
            {
                var data = image.Read(file);
                var load = data[0] | data[1] << 8;
                details = $"${load:x4}-${load + data.Length - 3:x4}, {Blocks(file.Blocks)}";
            }
            catch (Exception e)
            {
                details = $"unreadable: {e.Message}";
            }

            return new FileItem(file.DisplayName, "program", relative, index, file.Blocks * 254L, null, details);
        });
    }

    static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    static string Blocks(long count) => count == 1 ? "1 block" : $"{count} blocks";

    static string SectionRoot(Catalog catalog, string section) => section switch
    {
        "prg" => catalog.FolderOf(Section.Programs),
        "img" => catalog.FolderOf(Section.Pictures),
        "sid" => catalog.FolderOf(Section.Music),
        _ => throw new BadHttpRequestException($"unknown section {section}"),
    };

    /// <summary>A path inside the section folder; anything that points outside is refused.</summary>
    static string Resolve(string root, string? relative)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative ?? ""));
        if (full != rootFull && !full.StartsWith(rootFull + Path.DirectorySeparatorChar))
            throw new BadHttpRequestException("path outside the content folder");
        return full;
    }

    static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full) is "." ? "" : Path.GetRelativePath(root, full).Replace('\\', '/');

    static string SafeName(string name)
    {
        name = name.Trim();
        if (name is "" or "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.StartsWith('.'))
            throw new BadHttpRequestException($"invalid name: {name}");
        return name;
    }

    static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>This computer's mDNS name, e.g. Kims-iMac.local; the WiC64 can resolve these too.</summary>
    static string BonjourName() => bonjourName.Value;

    static readonly Lazy<string> bonjourName = new(() =>
    {
        // macOS keeps its Bonjour name apart from the host name .NET reports (System Settings > General > Sharing)
        try
        {
            using var scutil = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("scutil", "--get LocalHostName")
            {
                RedirectStandardOutput = true,
            });
            var name = scutil?.StandardOutput.ReadToEnd().Trim();
            scutil?.WaitForExit();
            if (!string.IsNullOrEmpty(name))
                return name + ".local";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // not a Mac: Windows 10+ and Linux (Avahi) answer to hostname.local
        }

        var host = Dns.GetHostName();
        return host.Contains('.') ? host : host + ".local";
    });

    /// <summary>This computer's IPv4 addresses on the network: what to type on the C64.</summary>
    static IEnumerable<IPAddress> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."));
}
