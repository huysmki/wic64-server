using Wic64Server.Activity;
using Wic64Server.Content;
using Wic64Server.Csdb;
using Wic64Server.Programs;

namespace Wic64Server.Api;

/// <summary>
/// The web UI's CSDb tab: searching CSDb (the results also become the folder "CSDb/Search: …" on the C64), running a
/// release on the C64, and keeping a release in a Programs folder.
/// </summary>
/// <remarks>
/// <code>
/// GET  /api/csdb/search?q=last+ninja   C64 releases: id, name, group, type, page on csdb.dk
/// POST /api/csdb/run                   { id, save, run }: downloads the release and pushes its first program
/// POST /api/csdb/keep                  { id, folder }: copies the release's programs and disk images into a Programs
///                                      folder (more than one file: into a subfolder named after the release)
/// </code>
/// </remarks>
public sealed class CsdbEndpoints(
    CsdbService csdb, Catalog catalog, PushQueue pushQueue, ActivityLog activity, ILogger<CsdbEndpoints> log)
{
    public sealed record RunRequest(int Id, bool Save, bool Run = true);
    public sealed record KeepRequest(int Id, string? Folder);

    public void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/csdb");

        api.MapGet("/search", (string? q) =>
        {
            if (!csdb.Enabled)
                return Results.BadRequest(new { error = "CSDb is turned off (\"Csdb\": false in appsettings.json)" });
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "type something to search for" });

            try
            {
                var releases = csdb.Search(q);
                return Results.Ok(new
                {
                    folder = $"CSDb/Search: {q.Trim()}",
                    results = releases.Select(r => new { r.Id, r.Name, r.Group, r.Type, Url = $"https://csdb.dk/release/?id={r.Id}" }),
                });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning(e, "CSDb search failed");
                return Results.Problem($"CSDb can't be reached: {e.Message}", statusCode: 502);
            }
        });

        api.MapPost("/keep", (KeepRequest r) =>
        {
            try
            {
                var root = Path.GetFullPath(catalog.FolderOf(Section.Programs));
                var folder = Path.GetFullPath(Path.Combine(root, r.Folder ?? ""));
                if (folder != root && !folder.StartsWith(root + Path.DirectorySeparatorChar) || !Directory.Exists(folder))
                    return Results.BadRequest(new { error = "choose a folder in Programs" });

                var release = csdb.Download(r.Id);
                var name = csdb.NameOf(r.Id);
                var files = Directory.EnumerateFiles(release).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList();
                if (files.Count > 1)
                {
                    folder = Unique(Path.Combine(folder, SafeName(name)));
                    Directory.CreateDirectory(folder);
                }

                foreach (var file in files)
                    File.Copy(file, Unique(Path.Combine(folder, Path.GetFileName(file))));

                var kept = Path.GetRelativePath(root, folder) is "." ? "" : Path.GetRelativePath(root, folder).Replace('\\', '/');
                activity.Add("csdb", $"Kept {name} from CSDb in Programs/{kept} ({files.Count} file{(files.Count == 1 ? "" : "s")})");
                return Results.Ok(new { name, folder = kept, files = files.Count });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or IOException)
            {
                log.LogWarning(e, "Keeping CSDb release {Id} failed", r.Id);
                return Results.BadRequest(new { error = e.Message });
            }
        });

        api.MapPost("/run", (RunRequest r) =>
        {
            try
            {
                if (csdb.FirstProgram(r.Id) is not var (name, program))
                    return Results.BadRequest(new { error = "this release has no program the C64 can start" });

                var load = program[0] | program[1] << 8;
                var end = load + program.Length - 2;
                if (load < 0x0800 || end > 0xd000)
                    return Results.BadRequest(new { error = $"{Petscii.ToText(name)} loads at ${load:x4}-${end - 1:x4}, the browser can only load $0800-$cfff" });

                pushQueue.Push(new PushQueue.Pushed(PushQueue.Kind.Program, name, program, r.Save || !r.Run, Run: r.Run));
                activity.Add("push", $"Pushed {Petscii.ToText(name)} from CSDb, waiting for the C64");
                return Results.Ok(new { name = Petscii.ToText(name) });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException)
            {
                log.LogWarning(e, "Running CSDb release {Id} failed", r.Id);
                return Results.BadRequest(new { error = e.Message });
            }
        });
    }

    /// <summary>The path itself, or with " (2)", " (3)", … when it exists.</summary>
    static string Unique(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Directory.Exists(path) ? "" : Path.GetExtension(path);
        if (Directory.Exists(path))
            name = Path.GetFileName(path);
        for (var n = 2; File.Exists(path) || Directory.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        return path;
    }

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "release" : safe;
    }
}
