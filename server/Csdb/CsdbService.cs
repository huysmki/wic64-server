using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Wic64Server.Activity;
using Wic64Server.Content;

namespace Wic64Server.Csdb;

/// <summary>A release on CSDb, as a list shows it.</summary>
public sealed record Release(int Id, string Name, string? Group, string? Type = null);

/// <summary>
/// CSDb (csdb.dk), the C64 scene database, as the folder "CSDb/" at the top of the programs: its latest releases, top
/// lists, and the last search from the web UI. A release is downloaded the first time it is opened, a .zip unpacked,
/// and kept in content/prg/.csdb.
/// From then on it is an ordinary folder: its programs and disk images run like any other.
/// </summary>
public sealed partial class CsdbService(ServerOptions options, ActivityLog activity, ILogger<CsdbService> log)
{
    const string Site = "https://csdb.dk";
    const int TopListLength = 100;
    const long MaxDownload = 20_000_000;
    static readonly TimeSpan ListLifetime = TimeSpan.FromHours(1);

    /// <summary>The folders in "CSDb/": a key, the name the C64 shows, and the top list's release type (0: latest).</summary>
    public static readonly (string Key, string Name, int Type)[] Lists =
    [
        ("latest", "Latest releases", 0),
        ("demos", "Top demos", 1),
        ("onefile", "Top one-file demos", 2),
        ("intros", "Top intros", 3),
        ("games", "Top games", 11),
        ("tools", "Top tools", 15),
    ];

    /// <summary>What the C64 can use from a release: its programs and disk images, also inside a .zip.</summary>
    static readonly string[] Runnable = [".prg", ".d64", ".d71", ".d81", ".seq"];

    static readonly HttpClient http = CreateClient();

    readonly Lock listLock = new();
    readonly Dictionary<string, (DateTime Fetched, List<Release> Releases)> lists = new();
    readonly Dictionary<int, Release> known = new();
    readonly Lock downloadLock = new();
    string? lastSearch;

    public bool Enabled => options.Csdb;

    /// <summary>The folders in "CSDb/": the last search from the web UI first, then the lists.</summary>
    public IReadOnlyList<(string Key, string Name)> Folders
    {
        get
        {
            var folders = Lists.Select(l => (l.Key, l.Name)).ToList();
            lock (listLock)
            {
                if (lastSearch is not null)
                    folders.Insert(0, ("search", $"Search: {lastSearch}"));
            }

            return folders;
        }
    }

    /// <summary>The cache: lists/{key} are the lists (not on disk), releases/{id} the downloaded releases.</summary>
    public string Root { get; } = Path.Combine(options.ContentFolder, "prg", ".csdb");

    public string ListPath(string key) => Path.Combine(Root, "lists", key);

    public string ReleasePath(int id) => Path.Combine(Root, "releases", id.ToString());

    public string? ListKey(string path) =>
        Path.GetDirectoryName(path) == Path.Combine(Root, "lists") ? Path.GetFileName(path) : null;

    public int? ReleaseId(string path) =>
        Path.GetDirectoryName(path) == Path.Combine(Root, "releases") && int.TryParse(Path.GetFileName(path), out var id) ? id : null;

    /// <summary>The title bar for a CSDb folder (also a disk image in a release), or null when the path is not one.</summary>
    public string? Title(string path)
    {
        if (path == Root)
            return "CSDb";
        if (ListKey(path) is { } key)
            return "CSDb/" + Folders.FirstOrDefault(l => l.Key == key).Name;

        return Release(path) is var (name, inside) ? "CSDb/" + name + (inside is null ? "" : "/" + inside) : null;
    }

    /// <summary>For a path in a downloaded release: the release's name and the path inside it (null: the release itself).</summary>
    public (string Name, string? Inside)? Release(string path)
    {
        var releases = Path.Combine(Root, "releases") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(releases))
            return null;
        var parts = path[releases.Length..].Split(Path.DirectorySeparatorChar, 2);
        if (!int.TryParse(parts[0], out var id))
            return null;

        string? name;
        lock (listLock)
            name = known.TryGetValue(id, out var release) ? release.Name : null;
        var nameFile = Path.Combine(ReleasePath(id), NameFile);
        name ??= File.Exists(nameFile) ? File.ReadAllText(nameFile).Trim() : id.ToString();
        return (name, parts.Length > 1 ? parts[1] : null);
    }

    /// <summary>
    /// The name of a downloaded release; asks CSDb when this server hasn't seen it yet (a release downloaded before
    /// its folder kept the name), and remembers the answer.
    /// </summary>
    public string NameOf(int id)
    {
        var folder = ReleasePath(id);
        if (Release(folder) is var (name, _) && name != id.ToString())
            return name;

        try
        {
            name = (string?)XDocument.Parse(Get($"{Site}/webservice/?type=release&id={id}")).Descendants("Release").First().Element("Name")
                   ?? id.ToString();
            if (Directory.Exists(folder))
                File.WriteAllText(Path.Combine(folder, NameFile), name);
            lock (listLock)
                known.TryAdd(id, new Release(id, name, null));
            return name;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return id.ToString();
        }
    }

    /// <summary>The release's name, in its folder (hidden from the menu: it starts with a dot).</summary>
    const string NameFile = ".name";

    /// <summary>The name in the menu: "Next Level (Performers)".</summary>
    public static string MenuName(Release release) => release.Group is null ? release.Name : $"{release.Name} ({release.Group})";

    /// <summary>The releases of a list, fetched from CSDb at most once an hour; for "search" the last search.</summary>
    public IReadOnlyList<Release> Releases(string key)
    {
        if (key == "search")
        {
            lock (listLock)
                return lists.TryGetValue(key, out var found) ? found.Releases : [];
        }

        var list = Lists.First(l => l.Key == key);
        lock (listLock)
        {
            if (lists.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Fetched < ListLifetime)
                return cached.Releases;
        }

        var releases = list.Type == 0 ? LatestReleases() : TopList(list.Type);
        log.LogInformation("CSDb {List}: {Count} releases", list.Name, releases.Count);
        lock (listLock)
        {
            lists[key] = (DateTime.UtcNow, releases);
            foreach (var release in releases)
                known[release.Id] = release;
        }

        return releases;
    }

    /// <summary>
    /// Searches CSDb's releases (only C64 releases, at most 100). The results also become the folder
    /// "CSDb/Search: …" on the C64.
    /// </summary>
    public IReadOnlyList<Release> Search(string text)
    {
        text = text.Trim();
        using var response = http.GetAsync($"{Site}/search/?seinsel=releases&search={Uri.EscapeDataString(text)}&Go.x=0&Go.y=0")
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var html = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        List<Release> releases;
        if (ReleaseLink().Match(response.RequestMessage?.RequestUri?.ToString() ?? "") is { Success: true } single
            && response.RequestMessage!.RequestUri!.AbsolutePath.StartsWith("/release"))
        {
            // One match: CSDb shows the release itself
            var id = int.Parse(single.Groups[1].Value);
            var xml = XDocument.Parse(Get($"{Site}/webservice/?type=release&id={id}"));
            var release = xml.Descendants("Release").First();
            var type = (string?)release.Element("Type");
            var group = release.Element("ReleasedBy")?.Descendants("Name").FirstOrDefault()?.Value;
            releases = type?.StartsWith("C64 ") == true ? [new Release(id, (string?)release.Element("Name") ?? id.ToString(), group, type)] : [];
        }
        else
        {
            releases = SearchResult().Matches(html)
                .Select(m => new Release(
                    int.Parse(m.Groups[1].Value),
                    WebUtility.HtmlDecode(m.Groups[2].Value),
                    m.Groups[4].Success ? ReleaseDate().Replace(WebUtility.HtmlDecode(Tag().Replace(m.Groups[4].Value, "").Split(',')[0]), "").Trim() : null,
                    WebUtility.HtmlDecode(m.Groups[3].Value)))
                .Where(r => r.Type!.StartsWith("C64 "))
                .DistinctBy(r => r.Id)
                .Take(TopListLength)
                .ToList();
        }

        log.LogInformation("CSDb search \"{Text}\": {Count} C64 releases", text, releases.Count);
        activity.Add("csdb", $"CSDb search \"{text}\": {releases.Count} release{(releases.Count == 1 ? "" : "s")}, on the C64 in CSDb/Search");
        lock (listLock)
        {
            lastSearch = text;
            lists["search"] = (DateTime.UtcNow, releases);
            foreach (var release in releases)
                known[release.Id] = release;
        }

        return releases;
    }

    /// <summary>
    /// The program to start for a release: its first .prg, otherwise the first program on its first disk image.
    /// Downloads the release first. Null when it has no program.
    /// </summary>
    public (byte[] Name, byte[] Program)? FirstProgram(int id)
    {
        var folder = Download(id);
        if (Directory.EnumerateFiles(folder, "*.prg").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } prg)
            return (Petscii.FileName(Path.GetFileName(prg)), File.ReadAllBytes(prg));

        foreach (var imagePath in DiskImage.In(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var image = DiskImage.Load(imagePath);
            if (image.Programs().FirstOrDefault() is { } file)
                return (file.Name, image.Read(file));
        }

        return null;
    }

    /// <summary>The RSS feed of the latest releases: only C64 releases (not "Other Platform C64 Tool").</summary>
    static List<Release> LatestReleases()
    {
        var rss = XDocument.Parse(Get($"{Site}/rss/latestreleases.php"));
        var releases = new List<Release>();
        foreach (var item in rss.Descendants("item"))
        {
            var description = (string?)item.Element("description") ?? "";
            var type = TypeLink().Match(description) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value) : "";
            if (!type.StartsWith("C64 ") || ReleaseLink().Match((string?)item.Element("guid") ?? "") is not { Success: true } id)
                continue;
            var group = ReleasedBy().Match(description) is { Success: true } g ? WebUtility.HtmlDecode(g.Groups[1].Value) : null;
            releases.Add(new Release(int.Parse(id.Groups[1].Value), WebUtility.HtmlDecode((string?)item.Element("title") ?? ""), group));
        }

        return releases;
    }

    /// <summary>The first releases of a top list (toplist.php): place, release, "by" its groups.</summary>
    static List<Release> TopList(int type)
    {
        var html = Get($"{Site}/toplist.php?type=release&subtype=%28{type}%29");
        return TopListRow().Matches(html)
            .Select(m => new Release(
                int.Parse(m.Groups[1].Value),
                WebUtility.HtmlDecode(m.Groups[2].Value),
                m.Groups[3].Success ? WebUtility.HtmlDecode(Tag().Replace(m.Groups[3].Value, "").Split(',')[0].Trim()) : null))
            .DistinctBy(r => r.Id)
            .Take(TopListLength)
            .ToList();
    }

    /// <summary>
    /// The folder of a release, downloaded and unpacked the first time: its programs, disk images and SEQ files.
    /// Throws when CSDb can't be reached or the release has nothing the C64 can run.
    /// </summary>
    public string Download(int id)
    {
        var folder = ReleasePath(id);
        lock (downloadLock)
        {
            if (Directory.Exists(folder))
                return folder;

            var xml = XDocument.Parse(Get($"{Site}/webservice/?type=release&id={id}"));
            var release = xml.Descendants("Release").First();
            var name = (string?)release.Element("Name") ?? id.ToString();
            var links = release.Descendants("DownloadLink")
                .Select(l => (Link: (string?)l.Element("Link") ?? "", Counter: (string?)l.Element("CounterLink")))
                .Where(l => Path.GetExtension(FileNameOf(l.Link)).ToLowerInvariant() is ".zip" || Runnable.Contains(Path.GetExtension(FileNameOf(l.Link)).ToLowerInvariant()))
                .ToList();
            if (links.Count == 0)
                throw new InvalidOperationException("nothing for the C64 to download");

            // Prefer a disk image or program over a .zip; the counter link counts the download on CSDb
            var (link, counter) = links.OrderBy(l => Path.GetExtension(FileNameOf(l.Link)).ToLowerInvariant() == ".zip" ? 1 : 0).First();
            var fileName = FileNameOf(link);
            activity.Add("csdb", $"Downloading {name} from CSDb ({fileName})");
            var bytes = GetBytes(counter ?? link);

            var temporary = folder + ".downloading";
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, true);
            Directory.CreateDirectory(temporary);
            try
            {
                if (Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    Unzip(bytes, temporary);
                else
                    File.WriteAllBytes(Path.Combine(temporary, SafeName(fileName)), bytes);

                if (!Directory.EnumerateFiles(temporary).Any())
                    throw new InvalidOperationException("nothing for the C64 in this release");
                File.WriteAllText(Path.Combine(temporary, NameFile), name); // for titles after a restart
                Directory.Move(temporary, folder);
            }
            catch
            {
                Directory.Delete(temporary, true);
                throw;
            }

            lock (listLock)
                known.TryAdd(id, new Release(id, name, null));
            log.LogInformation("CSDb release {Id} ({Name}) downloaded: {File}", id, name, fileName);
            return folder;
        }
    }

    /// <summary>The programs, disk images and SEQ files in a .zip, without its folders.</summary>
    static void Unzip(byte[] bytes, string folder)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var entry in zip.Entries)
        {
            if (entry.Length is 0 or > MaxDownload || !Runnable.Contains(Path.GetExtension(entry.Name).ToLowerInvariant()))
                continue;
            var target = Path.Combine(folder, SafeName(entry.Name));
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(entry.Name)} ({n}){Path.GetExtension(entry.Name)}");
            entry.ExtractToFile(target);
        }
    }

    static string FileNameOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) : "";

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).TrimStart('.');
        return safe.Length == 0 ? "release" : safe;
    }

    static string Get(string url) => http.GetStringAsync(url).GetAwaiter().GetResult();

    static byte[] GetBytes(string url)
    {
        using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownload)
            throw new InvalidOperationException("the download is too large");
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wic64-server (+https://github.com/huysmki/wic64-server)");
        return client;
    }

    [GeneratedRegex(@"Type: <a [^>]*>([^<]*)</a>")]
    private static partial Regex TypeLink();

    [GeneratedRegex(@"Released by: <a [^>]*>([^<]*)</a>")]
    private static partial Regex ReleasedBy();

    [GeneratedRegex(@"release/\?id=(\d+)")]
    private static partial Regex ReleaseLink();

    [GeneratedRegex(@"<tr><td align=right>\d+</td><td><a href=""/release/\?id=(\d+)"">([^<]*)</a>(?: by (.*?))?</td>")]
    private static partial Regex TopListRow();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    // the release date behind the group in search results: "Macc (18/11-2026)"
    [GeneratedRegex(@"\s*\(\d{1,2}/\d{1,2}-\d{4}\)\s*$|\s*\(\d{4}\)\s*$")]
    private static partial Regex ReleaseDate();

    // <a href="/release/?id=214862">Last Ninja</a> (C64 One-File Demo) by  <a ...>Finnish Ice-Breakers (FIB)</a><br>
    [GeneratedRegex(@"<a href=""/release/\?id=(\d+)"">([^<]*)</a> \(([^)]*)\)(?: by\s+(.*?))?<br>")]
    private static partial Regex SearchResult();
}
