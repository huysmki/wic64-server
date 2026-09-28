using System.Text;
using Wic64Server.Activity;
using Wic64Server.Content;
using Wic64Server.Screens;

namespace Wic64Server.Uploads;

/// <summary>
/// What the disk tools plugin (c64/plugins/disk-tools.asm) does with the C64's drive (8-11): its directory,
/// files uploaded from it and whole disks backed up as .d64. Everything ends up in the UploadFolder setting
/// (by default content/prg/From C64, so it shows up in the browser and the web UI).
/// </summary>
public sealed class DriveSession(ServerOptions options, ActivityLog activity, ILogger<DriveSession> log)
{
    const int PageSize = 20;

    public sealed record DirectoryEntry(byte[] Name, string Type, int Blocks)
    {
        static readonly string[] ImageExtensions = [".d64", ".d71", ".d81", ".m2i"];

        /// <summary>
        /// 0 = PRG, 1 = SEQ, 2 = USR, 3 = REL, 4 = DEL: the plugin can read 0-2. On an SD2IEC also
        /// 5 = folder and 6 = disk image: the plugin opens those with "CD:name".
        /// </summary>
        public byte TypeCode => Type switch
        {
            "DIR" => 5,
            _ when IsDiskImage => 6,
            "PRG" => 0,
            "SEQ" => 1,
            "USR" => 2,
            "REL" => 3,
            _ => 4,
        };

        /// <summary>An SD2IEC shows disk images as files with the image's extension, e.g. "GAME.D64".</summary>
        public bool IsDiskImage => ImageExtensions.Any(e => Petscii.ToText(Name).EndsWith(e, StringComparison.OrdinalIgnoreCase));

        public string Label => TypeCode switch
        {
            5 => "dir",
            6 => Path.GetExtension(Petscii.ToText(Name)).TrimStart('.').ToLowerInvariant(),
            _ => Type.ToLowerInvariant(),
        };
    }

    readonly Lock sessionLock = new();
    string diskName = "", diskId = "", backupName = "";
    byte[] diskNamePetscii = [];
    int blocksFree, device = 8;
    List<DirectoryEntry> entries = [];
    MemoryStream? upload;
    byte[]? image;
    int badSectors;

    public string UploadFolder => options.UploadFolder;

    /// <summary>The upload folder's own name, for messages on the C64 and in the activity log.</summary>
    string UploadFolderName => Path.GetFileName(UploadFolder.TrimEnd(Path.DirectorySeparatorChar));

    // -------------------------------------------------------------------------------------------
    // Directory

    /// <summary>Reads the directory as LOAD"$",8 gives it: a BASIC program without load address.</summary>
    public void SetDirectory(byte[] listing, int drive = 8)
    {
        device = drive;
        var lines = new List<(int Number, byte[] Text)>();
        for (var pos = 0; pos + 4 <= listing.Length;)
        {
            var link = listing[pos] | listing[pos + 1] << 8;
            if (link == 0)
                break;
            var number = listing[pos + 2] | listing[pos + 3] << 8;
            var end = Array.IndexOf(listing, (byte)0, pos + 4);
            if (end < 0)
                end = listing.Length;
            lines.Add((number, listing[(pos + 4)..end]));
            pos = end + 1;
        }

        lock (sessionLock)
        {
            entries = [];
            diskName = diskId = "";
            diskNamePetscii = [];
            blocksFree = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var (number, text) = lines[i];
                var firstQuote = Array.IndexOf(text, (byte)'"');
                var lastQuote = Array.LastIndexOf(text, (byte)'"');
                if (i == 0 && firstQuote >= 0 && lastQuote > firstQuote)
                {
                    diskNamePetscii = text[(firstQuote + 1)..lastQuote].AsEnumerable().Reverse()
                        .SkipWhile(c => c is 0x20 or 0xa0).Reverse().Take(16).ToArray();
                    diskName = Petscii.ToText(diskNamePetscii);
                    diskId = Petscii.ToText(text[(lastQuote + 1)..]).Trim();
                }
                else if (firstQuote >= 0 && lastQuote > firstQuote)
                {
                    var name = text[(firstQuote + 1)..lastQuote];
                    var type = Encoding.ASCII.GetString(text[(lastQuote + 1)..]).Trim().TrimStart('*').TrimEnd('<').Trim();
                    entries.Add(new DirectoryEntry(name, type.Length >= 3 ? type[..3] : type, number));
                }
                else
                {
                    blocksFree = number; // "BLOCKS FREE."
                }
            }
        }

        log.LogInformation("Drive {Device}: \"{Disk}\" with {Count} files, {Free} blocks free", device, diskName, entries.Count, blocksFree);
        activity.Add("disk", $"Read the directory of \"{diskName}\" in drive {device} ({entries.Count} entries)");
    }

    /// <summary>
    /// One page of the directory for the plugin: count, pages, for each of the 20 entries name length, type code
    /// and name (16 bytes PETSCII), the disk name (length and 16 bytes PETSCII, proposed as the name of a backup),
    /// then the screen (1000 screen codes).
    /// </summary>
    public byte[] Page(int page)
    {
        lock (sessionLock)
        {
            var pages = Math.Max(1, (entries.Count + PageSize - 1) / PageSize);
            page = Math.Clamp(page, 0, pages - 1);
            var onPage = entries.Skip(page * PageSize).Take(PageSize).ToList();

            var names = new byte[PageSize * 18];
            for (var i = 0; i < onPage.Count; i++)
            {
                names[i * 18] = (byte)Math.Min(onPage[i].Name.Length, 16);
                names[i * 18 + 1] = onPage[i].TypeCode;
                onPage[i].Name.AsSpan(0, Math.Min(onPage[i].Name.Length, 16)).CopyTo(names.AsSpan(i * 18 + 2));
            }

            var screen = new Screen();
            var title = diskName == "" ? $" Drive {device}" : $" Drive {device}: {diskName} {diskId}";
            screen.Print(0, 0, title, 33, reverse: true);
            screen.Print(0, 33, $" {page + 1:00}/{pages:00}", 7, reverse: true);
            if (onPage.Count == 0)
                screen.Print(3, 2, "No files on this disk");
            for (var i = 0; i < onPage.Count; i++)
            {
                var row = 2 + i;
                screen.Print(row, 1, ((char)('A' + i)).ToString(), reverse: true);
                screen.Print(row, 3, Petscii.ToText(onPage[i].Name), 17);
                screen.Print(row, 26, onPage[i].Label, 3);
                screen.Print(row, 36, $"{onPage[i].Blocks,3}", 3);
            }

            screen.Print(22, 1, "A-T open/upload DEL up F3 backup disk");
            screen.Print(23, 1, "F1 read  F5 drive  +/- page  ← browser");
            screen.Print(24, 0, $"{blocksFree} blocks free - uploads to {UploadFolderName}", 40);

            var disk = new byte[17];
            disk[0] = (byte)diskNamePetscii.Length;
            diskNamePetscii.CopyTo(disk, 1);

            return [(byte)onPage.Count, (byte)pages, .. names, .. disk, .. screen.Data];
        }
    }

    // -------------------------------------------------------------------------------------------
    // Files

    /// <summary>Part of a file (the plugin sends up to 32 KB at a time). Returns the message for the C64 when done.</summary>
    public string? AddFilePart(int page, int index, int part, bool last, byte[] data)
    {
        lock (sessionLock)
        {
            var position = page * PageSize + index;
            if (position < 0 || position >= entries.Count)
                throw new InvalidOperationException("file not in the directory");
            if (part == 0)
                upload = new MemoryStream();
            if (upload is null)
                throw new InvalidOperationException("upload was not started");

            upload.Write(data);
            if (!last)
                return null;

            var entry = entries[position];
            // A disk image file from an SD2IEC keeps its own name ("game.d64"); files get their type as extension
            var name = Petscii.ToText(entry.Name);
            var extension = entry.IsDiskImage ? Path.GetExtension(name).ToLowerInvariant()
                : entry.Type == "PRG" ? ".prg" : "." + entry.Type.ToLowerInvariant();
            if (entry.IsDiskImage)
                name = Path.GetFileNameWithoutExtension(name);
            var path = SaveUnique(name, extension, upload.ToArray());
            var size = upload.Length;
            upload = null;

            log.LogInformation("Uploaded {File} from drive {Device} ({Bytes} bytes)", path, device, size);
            activity.Add("disk", $"Uploaded {Petscii.ToText(entry.Name)} from drive {device} as {UploadFolderName}/{Path.GetFileName(path)}");
            return $"Saved as {Path.GetFileName(path)}";
        }
    }

    // -------------------------------------------------------------------------------------------
    // Whole disks

    static readonly int[] SectorsPerTrack = [.. Enumerable.Range(1, 35).Select(t => t <= 17 ? 21 : t <= 24 ? 19 : t <= 30 ? 18 : 17)];
    const int ImageSize = 174848; // 35 tracks, 683 sectors

    /// <summary>
    /// One track from the plugin: its sectors (256 bytes each) followed by one drive status code per sector
    /// (0 = read fine). Unreadable sectors are stored as zeros. Track 1 can carry the name typed on the C64
    /// (otherwise the disk's name is used). Returns the progress message for the C64.
    /// </summary>
    public string AddTrack(int track, byte[] data, string? name = null)
    {
        if (track is < 1 or > 35)
            throw new InvalidOperationException($"bad track {track}");
        var sectors = SectorsPerTrack[track - 1];
        if (data.Length != sectors * 257)
            throw new InvalidOperationException($"track {track}: expected {sectors * 257} bytes, got {data.Length}");

        lock (sessionLock)
        {
            if (track == 1)
            {
                image = new byte[ImageSize];
                badSectors = 0;
                backupName = !string.IsNullOrWhiteSpace(name) ? name.Trim() : diskName == "" ? "disk" : diskName;
            }
            if (image is null)
                throw new InvalidOperationException("backup was not started with track 1");

            var offset = SectorsPerTrack.Take(track - 1).Sum() * 256;
            for (var sector = 0; sector < sectors; sector++)
            {
                var status = data[sectors * 256 + sector];
                if (status is 0 or 1)
                    data.AsSpan(sector * 256, 256).CopyTo(image.AsSpan(offset + sector * 256));
                else
                    badSectors++;
            }

            if (track < 35)
                return $"Backing up: track {track} of 35 done" + (badSectors > 0 ? $", {badSectors} bad" : "");

            var path = SaveUnique(backupName, ".d64", image);
            image = null;
            log.LogInformation("Backed up drive {Device} as {File}, {Bad} unreadable sectors", device, path, badSectors);
            activity.Add("disk", $"Backed up \"{backupName}\" from drive {device} as {UploadFolderName}/{Path.GetFileName(path)}" +
                                 (badSectors > 0 ? $" ({badSectors} unreadable sectors)" : ""));
            return $"Saved as {Path.GetFileName(path)}" + (badSectors > 0 ? $" ({badSectors} bad)" : "");
        }
    }

    /// <summary>Saves in content/prg/From C64 without overwriting: "name (2).prg" when "name.prg" exists.</summary>
    string SaveUnique(string name, string extension, byte[] data)
    {
        Directory.CreateDirectory(UploadFolder);
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '-' : c)).Trim();
        if (safe is "" or "." or "..")
            safe = "upload";

        var path = Path.Combine(UploadFolder, safe + extension);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(UploadFolder, $"{safe} ({i}){extension}");
        File.WriteAllBytes(path, data);
        return path;
    }
}
