using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Models;
using DiskJanitor.Services;

namespace DiskJanitor.Scanners;

/// <summary>The home Trash plus the per-drive .Trash-UID folders on mounted disks.</summary>
public sealed class TrashScanner : IScanner
{
    public Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var trashes = new List<(string Label, string Path)> { ("Home trash", Path.Combine(context.Home, ".local/share/Trash")) };
        if (UserContext.IsRoot)
            trashes.Add(("Root's trash", "/root/.local/share/Trash"));
        foreach (var mount in DiskInfo.MountPoints().Where(m => m != "/"))
        {
            trashes.Add(($"Trash on {mount}", Path.Combine(mount, $".Trash-{UserContext.Uid}")));
            trashes.Add(($"Trash on {mount}", Path.Combine(mount, ".Trash", UserContext.Uid.ToString())));
        }

        var items = new List<CleanupItem>();
        foreach (var (label, path) in trashes.Where(t => Directory.Exists(t.Path)))
        {
            var filesDir = Path.Combine(path, "files");
            var names = Directory.Exists(filesDir) ? SafeNames(filesDir) : [];
            if (names.Count == 0 && !Directory.Exists(Path.Combine(path, "expunged")))
                continue;

            items.Add(new CleanupItem
            {
                Category = Categories.Trash,
                Name = label,
                Kind = "Trash",
                Location = UserContext.Pretty(path),
                Details = names.Count == 0
                    ? "Contains partially removed files."
                    : $"{names.Count} item(s), e.g. {ScanHelpers.JoinShort(names.OrderBy(n => n), 5)}.",
                Measure = c => FileOps.Measure(path, c),
                Delete = _ => Task.FromResult(Empty(path)),
            });
        }
        return Task.FromResult(new ScanResult(items, []));
    }

    static DeleteResult Empty(string trash)
    {
        foreach (var part in new[] { "files", "info", "expunged" })
        {
            var dir = Path.Combine(trash, part);
            if (Directory.Exists(dir) && FileOps.Delete(dir, contentsOnly: true) is { Success: false } failure)
                return failure;
        }
        return DeleteResult.Ok("Trash emptied");
    }

    static List<string> SafeNames(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch
        {
            return [];
        }
    }
}
