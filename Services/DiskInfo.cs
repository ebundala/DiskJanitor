using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiskJanitor.Services;

public sealed record DiskUsage(string MountPoint, long Total, long Used, long Available);

public static partial class DiskInfo
{
    public static IReadOnlyList<string> MountPoints()
    {
        try
        {
            return File.ReadLines("/proc/mounts")
                .Select(l => l.Split(' '))
                .Where(p => p.Length > 2)
                .Select(p => OctalEscape().Replace(p[1], m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()))
                .Distinct()
                .ToList();
        }
        catch
        {
            return ["/"];
        }
    }

    public static DiskUsage For(string path)
    {
        var full = Path.GetFullPath(path);
        var mount = MountPoints()
            .Where(m => full == m || full.StartsWith(m.TrimEnd('/') + "/"))
            .OrderByDescending(m => m.Length)
            .FirstOrDefault() ?? "/";
        var drive = new DriveInfo(mount);
        return new DiskUsage(mount, drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace, drive.AvailableFreeSpace);
    }

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}
