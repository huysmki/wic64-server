namespace Wic64Server.Content;

public enum Section { Programs, Pictures, Music }

public abstract record Entry(string Name);

/// <summary>A subfolder, or a .d64 disk image that can be opened like a folder.</summary>
public sealed record FolderEntry(string Name, int Id) : Entry(Name);

public sealed record FileEntry(string Name, string Path) : Entry(Name);

public sealed record DiskEntry(string Name, string ImagePath, DiskFile File) : Entry(Name);

/// <summary>
/// The files the C64 can browse, read from content/prg, content/img and content/sid.
/// Folders get a numeric id the C64 uses in its requests; id 0 is the root of a section.
/// </summary>
public sealed class Catalog(ServerOptions options)
{
    public const int PageSize = 20;

    public string Root { get; } = options.ContentFolder;

    static readonly Dictionary<Section, (string Folder, string[] Extensions)> Sources = new()
    {
        [Section.Programs] = ("prg", [".prg"]),
        [Section.Pictures] = ("img", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".koa", ".kla"]),
        [Section.Music] = ("sid", [".sid"]),
    };

    sealed record Folder(Section Section, string Path, int Parent, bool IsDiskImage);

    readonly List<Folder?> folders = [null]; // id 0 = root, resolved per section
    readonly Dictionary<string, int> folderIds = new();
    readonly Lock folderLock = new();

    public string FolderOf(Section section) => Path.Combine(Root, Sources[section].Folder);

    public IReadOnlyList<Entry> List(Section section, int folderId)
    {
        var folder = Resolve(section, folderId);
        if (folder.IsDiskImage)
        {
            return DiskImage.Load(folder.Path).Programs()
                .Select(file => (Entry)new DiskEntry(file.DisplayName, folder.Path, file))
                .ToList();
        }

        var directory = new DirectoryInfo(folder.Path);
        if (!directory.Exists)
            return [];

        var subfolders = directory.EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .Select(d => (d.Name, Path: d.FullName, IsDiskImage: false));
        var diskImages = section == Section.Programs
            ? directory.EnumerateFiles("*.d64").Select(f => (f.Name, Path: f.FullName, IsDiskImage: true))
            : [];

        var folderEntries = subfolders.Concat(diskImages)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => (Entry)new FolderEntry(f.Name + "/", IdOf(section, f.Path, folderId, f.IsDiskImage)));

        var fileEntries = directory.EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.') && Sources[section].Extensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => (Entry)new FileEntry(f.Name, f.FullName));

        return folderEntries.Concat(fileEntries).ToList();
    }

    public int ParentOf(Section section, int folderId) => Resolve(section, folderId).Parent;

    /// <summary>Folder id, page and entry of a file, as the C64 sees it; null if it is not in the section.</summary>
    public (int Folder, int Page, int Index)? Locate(Section section, string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(FolderOf(section), Path.GetDirectoryName(full)!);
        if (relative.StartsWith(".."))
            return null;

        var folderId = 0;
        if (relative != ".")
        {
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                List(section, folderId); // gives the subfolders their ids
                var path = Path.Combine(PathOf(section, folderId).Path, part);
                lock (folderLock)
                {
                    if (!folderIds.TryGetValue($"{section}:{path}", out folderId))
                        return null;
                }
            }
        }

        var entries = List(section, folderId);
        for (var i = 0; i < entries.Count; i++)
            if (entries[i] is FileEntry file && file.Path == full)
                return (folderId, i / PageSize, i % PageSize);
        return null;
    }

    /// <summary>The directory or .d64 image behind a folder id.</summary>
    public (string Path, bool IsDiskImage) PathOf(Section section, int folderId)
    {
        var folder = Resolve(section, folderId);
        return (folder.Path, folder.IsDiskImage);
    }

    /// <summary>Folder name relative to the section, for the title bar.</summary>
    public string NameOf(Section section, int folderId)
    {
        var folder = Resolve(section, folderId);
        return Path.GetRelativePath(FolderOf(section), folder.Path) is "." ? "" : Path.GetRelativePath(FolderOf(section), folder.Path);
    }

    public static int PageCount(IReadOnlyList<Entry> entries) => Math.Max(1, (entries.Count + PageSize - 1) / PageSize);

    public Entry? Get(Section section, int folderId, int page, int index)
    {
        var entries = List(section, folderId);
        var position = page * PageSize + index;
        return index is >= 0 and < PageSize && position >= 0 && position < entries.Count ? entries[position] : null;
    }

    Folder Resolve(Section section, int folderId)
    {
        lock (folderLock)
        {
            if (folderId > 0 && folderId < folders.Count && folders[folderId] is { } folder && folder.Section == section)
                return folder;
        }

        return new Folder(section, FolderOf(section), 0, false);
    }

    int IdOf(Section section, string path, int parent, bool isDiskImage)
    {
        var key = $"{section}:{path}";
        lock (folderLock)
        {
            if (folderIds.TryGetValue(key, out var id))
                return id;

            id = folders.Count;
            if (id > 0xffff)
                throw new InvalidOperationException("too many folders");
            folders.Add(new Folder(section, path, parent, isDiskImage));
            folderIds[key] = id;
            return id;
        }
    }
}
