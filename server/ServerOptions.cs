namespace Wic64Server;

/// <summary>
/// The server's settings, from server/appsettings.json, overridable on the command line (e.g. <c>--Port=6465</c>).
/// Relative folders are relative to the project folder (the one with the Makefile), wherever the server is started.
/// </summary>
/// <param name="Port">Port for the C64 and the web UI.</param>
/// <param name="ContentFolder">Folder with prg/, img/ and sid/.</param>
/// <param name="BuildFolder">Where make puts browser.prg, standalone.bin and loadhelper.bin.</param>
/// <param name="SidDefaultLength">Song length for tunes that are not in Songlengths.md5.</param>
/// <param name="AllowRemoteAdmin">Allow the web UI from other computers, not only from this computer.</param>
public sealed record ServerOptions(
    int Port,
    string ContentFolder,
    string BuildFolder,
    TimeSpan SidDefaultLength,
    bool AllowRemoteAdmin)
{
    public static ServerOptions From(IConfiguration configuration)
    {
        var projectFolder = FindProjectFolder();
        string Folder(string key, string fallback) =>
            Path.GetFullPath(Path.Combine(projectFolder, configuration[key] is { Length: > 0 } value ? value : fallback));

        return new ServerOptions(
            configuration.GetValue("Port", 6464),
            Folder("Content", "content"),
            Folder("Build", "build"),
            TimeSpan.FromSeconds(configuration.GetValue("SidDefaultSeconds", 180)),
            configuration.GetValue<bool>("AllowRemoteAdmin"));
    }

    /// <summary>
    /// The folder with the Makefile and server/, found upwards from the executable (server/bin/...)
    /// or from the current folder; for a published copy elsewhere, the executable's own folder.
    /// </summary>
    static string FindProjectFolder()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            {
                if (File.Exists(Path.Combine(folder.FullName, "Makefile")) && Directory.Exists(Path.Combine(folder.FullName, "server")))
                    return folder.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }
}
