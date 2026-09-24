using Wic64Server;
using Wic64Server.Activity;
using Wic64Server.Api;
using Wic64Server.Content;
using Wic64Server.Music;
using Wic64Server.Pictures;
using Wic64Server.Programs;
using Wic64Server.Screens;

// WiC64 server: serves programs, pictures and SID tunes to the browser on the C64 (Api/C64Endpoints.cs)
// and a web UI to manage them (wwwroot, Api/AdminEndpoints.cs).

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // appsettings.json and wwwroot are next to the executable
});

var options = ServerOptions.From(builder.Configuration);
builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

builder.Services
    .AddSingleton(options)
    .AddSingleton<Catalog>()
    .AddSingleton<ActivityLog>()
    .AddSingleton<PushQueue>()
    .AddSingleton<SongLengths>()
    .AddSingleton<SidService>()
    .AddSingleton<PictureService>()
    .AddSingleton<LoadService>()
    .AddSingleton<MenuScreen>()
    .AddSingleton<C64Endpoints>()
    .AddSingleton<AdminEndpoints>();

var app = builder.Build();

app.UseAccessRules();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Services.GetRequiredService<C64Endpoints>().Map(app);
app.Services.GetRequiredService<AdminEndpoints>().Map(app);

if (Directory.Exists(options.ContentFolder))
{
    // prg/, img/ and sid/ are created when missing, so a new content folder works right away
    var catalog = app.Services.GetRequiredService<Catalog>();
    foreach (var section in Enum.GetValues<Section>())
        Directory.CreateDirectory(catalog.FolderOf(section));
    app.Logger.LogInformation("Serving content from {Content}", options.ContentFolder);
}
else
{
    app.Logger.LogWarning("Content folder {Content} does not exist: set \"Content\" in server/appsettings.json", options.ContentFolder);
}
app.Run();
