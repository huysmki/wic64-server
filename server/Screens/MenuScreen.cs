using Wic64Server.Content;
using Wic64Server.Music;

namespace Wic64Server.Screens;

/// <summary>Renders the browser's menu: the C64 copies these 1000 screen codes straight to $0400.</summary>
public sealed class MenuScreen(Catalog catalog, SidService sids)
{
    public Screen Render(Section section, int folder, int page, int pages, IReadOnlyList<Entry> entries)
    {
        var screen = new Screen();
        var title = section switch
        {
            Section.Programs => "Programs",
            Section.Pictures => "Pictures",
            _ => "Music",
        };
        var path = catalog.NameOf(section, folder);
        var heading = path == "" ? $" {title}" : $" {title}: {path}";
        screen.Print(0, 0, heading.Length > 33 ? heading[..32] + "~" : heading, 33, reverse: true);
        screen.Print(0, 33, $" {page + 1:00}/{pages:00}", 7, reverse: true);

        if (entries.Count == 0)
        {
            screen.Print(3, 2, folder == 0
                ? $"No files yet - add some to content/{Path.GetFileName(catalog.FolderOf(section))}"
                : "This folder is empty");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var row = 2 + i;
            screen.Print(row, 1, ((char)('A' + i)).ToString(), reverse: true);
            PrintEntry(screen, row, section, entries[i]);
        }

        screen.Print(22, 1, section switch
        {
            Section.Programs => "A-T run  SHIFT+A-T save  +/-  DEL up",
            Section.Pictures => "A-T show   +/- page   DEL up",
            _ => "A-T play  RET info  SPC next  STOP off",
        });
        screen.Print(23, 1, "F1/F3/F5 sections  F2 server  ← portal");
        return screen;
    }

    void PrintEntry(Screen screen, int row, Section section, Entry entry)
    {
        switch (entry)
        {
            case FolderEntry folder:
                screen.Print(row, 3, folder.Name, 32);
                screen.Print(row, 36, folder.Name.EndsWith(".d64/", StringComparison.OrdinalIgnoreCase) ? "d64" : "dir", 3);
                break;

            case DiskEntry disk:
                screen.Print(row, 3, disk.Name, 32);
                screen.Print(row, 36, $"{disk.File.Blocks,3}", 3);
                break;

            case FileEntry file when section == Section.Programs:
            {
                using var stream = File.OpenRead(file.Path);
                var load = stream.ReadByte() | stream.ReadByte() << 8;
                var blocks = (stream.Length + 253) / 254;
                screen.Print(row, 3, Path.GetFileNameWithoutExtension(file.Name), 27);
                screen.Print(row, 31, $"{load:x4}", 4);
                screen.Print(row, 36, $"{blocks,3}", 3);
                break;
            }

            case FileEntry file when section == Section.Pictures:
                screen.Print(row, 3, Path.GetFileNameWithoutExtension(file.Name), 31);
                screen.Print(row, 35, Path.GetExtension(file.Name).TrimStart('.'), 4);
                break;

            case FileEntry file:
                try
                {
                    var (sid, _, _) = sids.Load(file);
                    screen.Print(row, 3, sid.Name, 20);
                    screen.Print(row, 24, sid.Author, 16);
                }
                catch
                {
                    screen.Print(row, 3, Path.GetFileNameWithoutExtension(file.Name), 37);
                }
                break;
        }
    }
}
