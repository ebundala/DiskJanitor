using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Models;
using DiskJanitor.Services;

namespace DiskJanitor.Scanners;

/// <summary>System-wide space: journal, apt cache, rotated logs, crash dumps and disabled snap revisions. All need root.</summary>
public sealed partial class SystemScanner : IScanner
{
    const long JournalKeep = 200L * 1024 * 1024;

    public async Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        var partial = UserContext.IsRoot ? "" : " (Size may be incomplete until you run as root.)";

        if (Shell.Which("journalctl") is { } journalctl)
        {
            var usage = await Shell.RunAsync(journalctl, ["--disk-usage"], ct);
            var size = Bytes.ParseBinary(TakeUpRegex().Match(usage.StdOut).Groups[1].Value);
            if (usage.Ok && size > JournalKeep)
            {
                items.Add(new CleanupItem
                {
                    Category = Categories.System,
                    Name = "System journal logs",
                    Kind = "Logs",
                    Location = "/var/log/journal",
                    Size = size - JournalKeep,
                    Details = $"The journal uses {Bytes.Format(size)}; this keeps the newest {Bytes.Format(JournalKeep)}. "
                              + "To cap it for good, set SystemMaxUse=500M in /etc/systemd/journald.conf.",
                    RequiresRoot = true,
                    Delete = _ => ScanHelpers.RunCommand(journalctl, "--vacuum-size=200M"),
                });
            }
        }

        if (Shell.Which("apt-get") is { } aptGet && Directory.Exists("/var/cache/apt/archives"))
        {
            items.Add(new CleanupItem
            {
                Category = Categories.System,
                Name = "apt package cache",
                Kind = "Package cache",
                Location = "/var/cache/apt/archives",
                Details = "Downloaded .deb installers of packages that are already installed.",
                RequiresRoot = true,
                Measure = c => FileOps.Measure("/var/cache/apt/archives", c, name => name.EndsWith(".deb")),
                Delete = _ => ScanHelpers.RunCommand(aptGet, "clean"),
            });
        }

        items.Add(new CleanupItem
        {
            Category = Categories.System,
            Name = "Old rotated log files",
            Kind = "Logs",
            Location = "/var/log",
            Details = "Compressed/rotated logs such as syslog.2.gz. Current logs are kept." + partial,
            RequiresRoot = true,
            Measure = c => RotatedLogs(c).Sum(f => SafeLength(f)),
            Delete = c => Task.FromResult(DeleteFiles(RotatedLogs(c))),
        });

        foreach (var (name, dir) in new[] { ("Crash reports", "/var/crash"), ("Core dumps", "/var/lib/systemd/coredump") })
        {
            if (Directory.Exists(dir))
                items.Add(ScanHelpers.Folder(Categories.System, name, "Crash data", dir,
                    "Saved crash reports from programs that crashed." + partial, contentsOnly: true, requiresRoot: true));
        }

        if (Shell.Which("snap") is { } snap)
            items.AddRange(await DisabledSnapsAsync(snap, ct));

        return new ScanResult(items, []);
    }

    static async Task<IEnumerable<CleanupItem>> DisabledSnapsAsync(string snap, CancellationToken ct)
    {
        var list = await Shell.RunAsync(snap, ["list", "--all"], ct);
        if (!list.Ok)
            return [];

        return list.StdOut.Split('\n').Skip(1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(c => c.Length >= 4 && c[^1].Contains("disabled"))
            .Select(c =>
            {
                var (name, version, revision) = (c[0], c[1], c[2]);
                var file = $"/var/lib/snapd/snaps/{name}_{revision}.snap";
                return new CleanupItem
                {
                    Category = Categories.System,
                    Name = $"{name} {version} (revision {revision})",
                    Kind = "Old snap revision",
                    Location = file,
                    Size = SafeLength(file),
                    Details = "A disabled older version kept for rollback. Run `snap set system refresh.retain=2` to keep fewer.",
                    RequiresRoot = true,
                    Delete = _ => ScanHelpers.RunCommand(snap, "remove", name, $"--revision={revision}"),
                };
            })
            .ToList();
    }

    static List<string> RotatedLogs(CancellationToken ct)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles("/var/log", "*", options)
                .Where(f => !f.StartsWith("/var/log/journal/") && RotatedLogRegex().IsMatch(f))
                .TakeWhile(_ => !ct.IsCancellationRequested)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    static DeleteResult DeleteFiles(List<string> files)
    {
        var failures = files.Select(f => FileOps.Delete(f)).Where(r => !r.Success).ToList();
        return failures.Count == 0 ? DeleteResult.Ok() : DeleteResult.Fail($"{failures.Count} files could not be removed: {failures[0].Message}");
    }

    static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    [GeneratedRegex(@"take up ([\d.]+\s*[BKMGTP])")]
    private static partial Regex TakeUpRegex();

    [GeneratedRegex(@"(\.\d+(\.(gz|xz|bz2|zst))?|\.(gz|xz|bz2|zst|old))$")]
    private static partial Regex RotatedLogRegex();
}
