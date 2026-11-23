using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Scanners;
using DiskJanitor.Services;

namespace DiskJanitor;

/// <summary>`DiskJanitor --report [project folders…]`: prints what a scan finds without opening a window or deleting anything.</summary>
static class Report
{
    public static async Task<int> RunAsync(string[] args)
    {
        var roots = args.Where(a => !a.StartsWith("--")).Select(UserContext.Expand).ToList();
        if (roots.Count == 0)
            roots.Add(UserContext.Expand("~/projects"));

        Console.WriteLine($"User: {UserContext.UserName} (root: {UserContext.IsRoot}), home: {UserContext.Home}");
        var context = new ScanContext(UserContext.Home, roots);
        IScanner[] scanners = [new ProjectArtifactsScanner(), new DockerScanner(), new CacheScanner(), new TrashScanner(), new SystemScanner()];
        var results = await Task.WhenAll(scanners.Select(s => s.ScanAsync(context, CancellationToken.None)));

        var items = results.SelectMany(r => r.Items).ToList();
        Parallel.ForEach(items.Where(i => i.Measure is not null), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            i => i.Size = i.Measure!(CancellationToken.None));

        foreach (var group in items.Where(i => i.Size > 0).GroupBy(i => i.Category))
        {
            var total = group.Where(i => i.CountsTowardTotal).Sum(i => i.Size ?? 0);
            Console.WriteLine($"\n== {group.Key}: {Bytes.Format(total)} in {group.Count()} items");
            foreach (var item in group.OrderByDescending(i => i.Size))
            {
                var flag = item.Risk switch { Models.Risk.Danger => "[IN USE] ", Models.Risk.Caution => "[CHECK] ", _ => "" };
                Console.WriteLine($"  {Bytes.Format(item.Size ?? 0),9}  {flag}{item.Name}  ({item.Kind}){(item.RequiresRoot ? " [root]" : "")}");
                if (item.Warning is { } warning)
                    Console.WriteLine($"             ! {warning.Replace("\n", "\n             ! ")}");
            }
        }
        foreach (var note in results.SelectMany(r => r.Notes))
            Console.WriteLine($"\nNote: {note}");
        return 0;
    }
}
