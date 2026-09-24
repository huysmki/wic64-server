using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Wic64Server.Content;

namespace Wic64Server.Music;

/// <summary>
/// Song lengths from the High Voltage SID Collection's Songlengths.md5 (C64Music/DOCUMENTS/Songlengths.md5).
/// Put the file anywhere in content/sid. Lines look like "&lt;md5 of the sid file&gt;=3:15 0:42.5 ...".
/// </summary>
public sealed partial class SongLengths(Catalog catalog, ServerOptions options)
{
    readonly string sidFolder = catalog.FolderOf(Section.Music);
    readonly TimeSpan fallback = options.SidDefaultLength;
    Dictionary<string, TimeSpan[]> lengths = new();
    string? loadedFrom;
    DateTime loadedModified;
    readonly Lock loadLock = new();

    public TimeSpan Fallback => fallback;

    /// <summary>The Songlengths.md5 in use, if any.</summary>
    public string? DatabaseFile
    {
        get
        {
            Database();
            return loadedFrom;
        }
    }

    /// <summary>Whether the database has lengths for this tune.</summary>
    public bool Knows(byte[] sidFile) => Database().ContainsKey(Convert.ToHexStringLower(MD5.HashData(sidFile)));

    /// <summary>Length of each subtune, or the fallback length when the tune is not in the database.</summary>
    public TimeSpan Of(byte[] sidFile, int song)
    {
        var database = Database();
        var md5 = Convert.ToHexStringLower(MD5.HashData(sidFile));
        return database.TryGetValue(md5, out var songs) && song >= 1 && song <= songs.Length ? songs[song - 1] : fallback;
    }

    Dictionary<string, TimeSpan[]> Database()
    {
        lock (loadLock)
        {
            var file = Directory.Exists(sidFolder)
                ? Directory.EnumerateFiles(sidFolder, "Songlengths.md5", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (file is null)
            {
                loadedFrom = null;
                return lengths = new();
            }

            var modified = File.GetLastWriteTimeUtc(file);
            if (file == loadedFrom && modified == loadedModified)
                return lengths;

            var result = new Dictionary<string, TimeSpan[]>();
            foreach (var line in File.ReadLines(file))
            {
                var match = Entry().Match(line);
                if (match.Success)
                    result[match.Groups[1].Value.ToLowerInvariant()] = Time().Matches(match.Groups[2].Value).Select(Parse).ToArray();
            }

            (lengths, loadedFrom, loadedModified) = (result, file, modified);
            return lengths;
        }
    }

    static TimeSpan Parse(Match time) =>
        TimeSpan.FromMinutes(int.Parse(time.Groups[1].Value))
        + TimeSpan.FromSeconds(double.Parse(time.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

    [GeneratedRegex(@"^([0-9a-fA-F]{32})=(.*)$")]
    private static partial Regex Entry();

    [GeneratedRegex(@"(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex Time();
}
