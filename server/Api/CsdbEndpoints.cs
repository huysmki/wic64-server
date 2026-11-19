using Wic64Server.Activity;
using Wic64Server.Content;
using Wic64Server.Csdb;
using Wic64Server.Programs;

namespace Wic64Server.Api;

/// <summary>
/// The web UI's CSDb tab: searching CSDb (the results also become the folder "CSDb/Search: …" on the C64) and
/// running a release on the C64.
/// </summary>
/// <remarks>
/// <code>
/// GET  /api/csdb/search?q=last+ninja   C64 releases: id, name, group, type, page on csdb.dk
/// POST /api/csdb/run                   { id, save, run }: downloads the release and pushes its first program
/// </code>
/// </remarks>
public sealed class CsdbEndpoints(CsdbService csdb, PushQueue pushQueue, ActivityLog activity, ILogger<CsdbEndpoints> log)
{
    public sealed record RunRequest(int Id, bool Save, bool Run = true);

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
}
