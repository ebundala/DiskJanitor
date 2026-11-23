using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Models;
using DiskJanitor.Services;

namespace DiskJanitor.Scanners;

public sealed record ScanContext(string Home, IReadOnlyList<string> ProjectRoots);

public sealed record ScanResult(IReadOnlyList<CleanupItem> Items, IReadOnlyList<string> Notes);

public interface IScanner
{
    Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct);
}

public static class Categories
{
    public const string ProjectBuilds = "Project build folders";
    public const string DockerImages = "Docker images";
    public const string DockerContainers = "Docker containers";
    public const string DockerVolumes = "Docker volumes";
    public const string DockerBuildCache = "Docker build cache";
    public const string PackageCaches = "Package manager caches";
    public const string AppCaches = "App & browser caches";
    public const string Trash = "Trash";
    public const string System = "System (root)";

    public static readonly (string Name, string Description)[] All =
    [
        (ProjectBuilds, "Dependency and build folders inside your projects (node_modules, build, bin/obj, .dart_tool, …). Source code is never touched — reinstall or rebuild to get them back. Orange means the project was worked on recently."),
        (DockerImages, "Images stored by Docker. Orange: a Dockerfile/compose file in your projects references it, or a project built it. Red: a container still uses it. The size is the space unique to that image."),
        (DockerContainers, "Containers keep their image (and volumes) from being removed. Removing a container does not delete its volumes."),
        (DockerVolumes, "Docker's persistent data. Named volumes often hold databases or uploads — check the project before deleting."),
        (DockerBuildCache, "Layers cached by docker build. Safe to clear; the next build of each image will take longer."),
        (PackageCaches, "Download caches of npm, pnpm, NuGet, Gradle, pub, cargo and friends. Packages are downloaded again when a project needs them."),
        (AppCaches, "Caches of editors, browsers and the graphics driver. Apps rebuild them automatically."),
        (Trash, "Files you already moved to the Trash. Emptying it is permanent."),
        (System, "System logs, apt's package cache and old snap revisions. Deleting these needs root."),
    ];
}

internal static class ScanHelpers
{
    public static CleanupItem Folder(string category, string name, string kind, string path, string? details,
        string? warning = null, Risk risk = Risk.Safe, bool contentsOnly = false, bool requiresRoot = false) => new()
    {
        Category = category,
        Name = name,
        Kind = kind,
        Location = UserContext.Pretty(path),
        Details = details,
        Warning = warning,
        Risk = warning is not null && risk == Risk.Safe ? Risk.Caution : risk,
        RequiresRoot = requiresRoot,
        Measure = ct => FileOps.Measure(path, ct),
        Delete = _ => Task.FromResult(FileOps.Delete(path, contentsOnly)),
    };

    public static async Task<DeleteResult> RunCommand(string file, params string[] args)
    {
        var result = await Shell.RunAsync(file, args);
        return result.Ok ? DeleteResult.Ok() : DeleteResult.Fail(result.Error);
    }

    public static string JoinShort(IEnumerable<string> values, int max = 3)
    {
        var list = new List<string>(values);
        return list.Count <= max
            ? string.Join(", ", list)
            : string.Join(", ", list.GetRange(0, max)) + $" and {list.Count - max} more";
    }
}
