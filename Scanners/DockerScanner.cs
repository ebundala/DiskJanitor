using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Models;
using DiskJanitor.Services;

namespace DiskJanitor.Scanners;

/// <summary>Images, containers, volumes and build cache, each annotated with who uses or references it.</summary>
public sealed class DockerScanner : IScanner
{
    sealed record Container(string Id, string Name, string State, string ImageId, string ImageName,
        string? WorkingDir, string? ComposeProject, string[] Volumes)
    {
        public string Label => $"{Name} ({State})";
    }

    const string ContainerFormat =
        "{{.Id}}|{{.Name}}|{{.State.Status}}|{{.Image}}|{{.Config.Image}}|" +
        "{{index .Config.Labels \"com.docker.compose.project.working_dir\"}}|" +
        "{{index .Config.Labels \"com.docker.compose.project\"}}|" +
        "{{range .Mounts}}{{if eq .Type \"volume\"}}{{.Name}},{{end}}{{end}}";

    public async Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        var notes = new List<string>();
        if (Shell.Which("docker") is not { } docker)
            return new ScanResult(items, notes);

        var df = await Shell.RunAsync(docker, ["system", "df", "-v", "--format", "{{json .}}"], ct);
        if (!df.Ok)
        {
            var hint = UserContext.IsRoot ? "" : " If this is a permission error, relaunch as root.";
            notes.Add($"Docker: {df.Error}.{hint}");
            return new ScanResult(items, notes);
        }

        using var json = JsonDocument.Parse(df.StdOut);
        var root = json.RootElement;
        var index = DockerProjectIndex.Build(context.ProjectRoots, ct);
        var containers = await InspectContainersAsync(docker, ct);
        var imageIds = Array(root, "Images").Select(i => Str(i, "ID")).Where(id => id.Length > 0).Distinct().ToList();
        var composeLabels = await InspectImageComposeLabelsAsync(docker, imageIds, ct);

        AddImages(items, docker, Array(root, "Images"), containers, index, composeLabels);
        AddContainers(items, docker, Array(root, "Containers"), containers);
        AddVolumes(items, docker, Array(root, "Volumes"), containers, index);
        AddBuildCache(items, docker, Array(root, "BuildCache"));
        return new ScanResult(items, notes);
    }

    static void AddImages(List<CleanupItem> items, string docker, IEnumerable<JsonElement> images,
        List<Container> containers, DockerProjectIndex index, Dictionary<string, string> composeLabels)
    {
        var seenIds = new HashSet<string>();
        foreach (var image in images)
        {
            var id = Str(image, "ID");
            var repo = Str(image, "Repository");
            var tag = Str(image, "Tag");
            var tagged = repo != "<none>" && tag != "<none>";
            var name = tagged ? $"{repo}:{tag}" : $"<untagged> {ShortId(id)}";
            var firstTagOfImage = seenIds.Add(id);
            var total = Bytes.ParseDocker(Str(image, "Size"));
            var shared = Bytes.ParseDocker(Str(image, "SharedSize"));

            var details = new List<string> { $"Created {Str(image, "CreatedSince")}. {Bytes.Format(total)} in total, {Bytes.Format(shared)} shared with other images." };
            var warnings = new List<string>();
            var risk = Risk.Safe;

            var users = containers.Where(c => c.ImageId == id).ToList();
            if (users.Count > 0)
            {
                risk = Risk.Danger;
                warnings.Add($"Used by container {ScanHelpers.JoinShort(users.Select(c => c.Label))} — remove the container(s) first (Docker containers tab).");
            }

            if (tagged)
            {
                var normalized = DockerProjectIndex.Normalize(name);
                if (index.ImageRefs.TryGetValue(normalized, out var files))
                {
                    risk = Max(risk, Risk.Caution);
                    warnings.Add($"Referenced by {ScanHelpers.JoinShort(files)} — it will be pulled again next time that project starts.");
                }
                else if (index.RepoRefs.TryGetValue(DockerProjectIndex.RepoOf(normalized), out var otherTags))
                {
                    details.Add($"Your projects use other tags of {repo} ({ScanHelpers.JoinShort(otherTags)}), but not :{tag}.");
                }

                if (index.FindBuilder(repo, composeLabels.GetValueOrDefault(id)) is { } builder)
                {
                    risk = Max(risk, Risk.Caution);
                    warnings.Add($"Built from project {builder} — you'd have to rebuild it.");
                }
            }
            else
            {
                details.Add("Untagged (dangling) image, usually left behind when a newer build replaced it.");
            }

            if (!firstTagOfImage)
                details.Add("Same image as another tag in this list — the space is freed only once every tag is removed.");
            if (risk == Risk.Safe && tagged)
                details.Add("No container or project file found that uses it.");

            items.Add(new CleanupItem
            {
                Category = Categories.DockerImages,
                Name = name,
                Kind = tagged ? "Docker image" : "Dangling image",
                Location = $"image {ShortId(id)}",
                Size = Bytes.ParseDocker(Str(image, "UniqueSize")),
                CountsTowardTotal = firstTagOfImage,
                Details = string.Join(" ", details),
                Warning = warnings.Count > 0 ? string.Join("\n", warnings) : null,
                Risk = risk,
                Delete = _ => ScanHelpers.RunCommand(docker, "rmi", tagged ? name : id),
            });
        }
    }

    static void AddContainers(List<CleanupItem> items, string docker, IEnumerable<JsonElement> dfContainers, List<Container> containers)
    {
        var sizes = dfContainers.ToDictionary(c => Str(c, "ID"), c => Bytes.ParseDocker(Str(c, "Size")));
        foreach (var c in containers)
        {
            var details = new List<string> { $"From image {c.ImageName}." };
            if (c.WorkingDir is { Length: > 0 })
                details.Add($"Created by docker compose in {UserContext.Pretty(c.WorkingDir)} — compose up will recreate it.");
            if (c.Volumes.Length > 0)
                details.Add($"Mounts volume(s) {ScanHelpers.JoinShort(c.Volumes.Select(Short))}; removing the container keeps them.");

            var running = c.State is "running" or "paused" or "restarting";
            items.Add(new CleanupItem
            {
                Category = Categories.DockerContainers,
                Name = c.Name,
                Kind = $"Container · {c.State}",
                Location = $"container {ShortId(c.Id)}",
                Size = sizes.FirstOrDefault(s => c.Id.StartsWith(s.Key) || s.Key.StartsWith(c.Id)).Value,
                Details = string.Join(" ", details),
                Warning = running ? "Running — stop it first (docker stop), otherwise removing fails." : null,
                Risk = running ? Risk.Danger : Risk.Safe,
                Delete = _ => ScanHelpers.RunCommand(docker, "rm", c.Id),
            });
        }
    }

    static void AddVolumes(List<CleanupItem> items, string docker, IEnumerable<JsonElement> volumes,
        List<Container> containers, DockerProjectIndex index)
    {
        foreach (var volume in volumes)
        {
            var name = Str(volume, "Name");
            var labels = ParseLabels(Str(volume, "Labels"));
            var anonymous = labels.ContainsKey("com.docker.volume.anonymous")
                            || (name.Length == 64 && name.All(Uri.IsHexDigit) && !labels.ContainsKey("com.docker.compose.project"));
            var users = containers.Where(c => c.Volumes.Contains(name)).ToList();

            var details = new List<string>();
            if (labels.TryGetValue("com.docker.compose.project", out var project))
            {
                var folder = index.ComposeProjects.TryGetValue(project, out var dir) ? $" ({dir})" : "";
                details.Add($"Belongs to compose project “{project}”{folder}.");
            }
            if (anonymous)
                details.Add("Anonymous volume — created automatically, not named in any compose file.");

            string? warning = null;
            var risk = Risk.Safe;
            if (users.Count > 0)
            {
                risk = Risk.Danger;
                warning = $"Attached to {ScanHelpers.JoinShort(users.Select(c => c.Label))} — remove the container(s) first.";
            }
            else if (!anonymous)
            {
                risk = Risk.Caution;
                warning = "Named volume — may hold a project's database or uploads. Deleting it is permanent.";
            }
            else
            {
                details.Add("Not attached to any container (left over from a removed one).");
            }

            items.Add(new CleanupItem
            {
                Category = Categories.DockerVolumes,
                Name = anonymous ? $"Anonymous volume {name[..Math.Min(12, name.Length)]}" : name,
                Kind = "Docker volume",
                Location = Str(volume, "Mountpoint"),
                Size = Bytes.ParseDocker(Str(volume, "Size")),
                Details = details.Count > 0 ? string.Join(" ", details) : null,
                Warning = warning,
                Risk = risk,
                Delete = _ => ScanHelpers.RunCommand(docker, "volume", "rm", name),
            });
        }
    }

    static void AddBuildCache(List<CleanupItem> items, string docker, IEnumerable<JsonElement> cacheEntries)
    {
        var entries = cacheEntries.ToList();
        if (entries.Count == 0)
            return;
        var size = entries.Sum(e => Bytes.ParseDocker(Str(e, "Size")));
        var inUse = entries.Count(e => Str(e, "InUse") == "true");
        items.Add(new CleanupItem
        {
            Category = Categories.DockerBuildCache,
            Name = "Docker build cache",
            Kind = "Build cache",
            Location = $"{entries.Count} cache entries",
            Size = size,
            Details = "Layers and RUN --mount caches from docker build. The next build of each image starts from scratch."
                      + (inUse > 0 ? $" {inUse} entries are in use by a running build and will be kept." : ""),
            Delete = _ => ScanHelpers.RunCommand(docker, "builder", "prune", "--all", "--force"),
        });
    }

    static async Task<List<Container>> InspectContainersAsync(string docker, CancellationToken ct)
    {
        var ids = await Shell.RunAsync(docker, ["ps", "-aq", "--no-trunc"], ct);
        var idList = Lines(ids.StdOut);
        if (!ids.Ok || idList.Count == 0)
            return [];

        var inspect = await Shell.RunAsync(docker, ["inspect", "--type", "container", "--format", ContainerFormat, .. idList], ct);
        return Lines(inspect.StdOut)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 8)
            .Select(p => new Container(p[0], p[1].TrimStart('/'), p[2], p[3], p[4], NullIfEmpty(p[5]), NullIfEmpty(p[6]),
                p[7].Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .ToList();
    }

    static async Task<Dictionary<string, string>> InspectImageComposeLabelsAsync(string docker, List<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var inspect = await Shell.RunAsync(docker,
            ["image", "inspect", "--format", "{{.Id}}|{{index .Config.Labels \"com.docker.compose.project\"}}", .. ids], ct);
        return Lines(inspect.StdOut)
            .Select(l => l.Split('|'))
            .Where(p => p.Length == 2 && p[1].Length > 0 && p[1] != "<no value>")
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.First()[1]);
    }

    static Dictionary<string, string> ParseLabels(string labels) =>
        labels.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.First().Length > 1 ? g.First()[1] : "");

    static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    static List<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static string? NullIfEmpty(string value) => value.Length == 0 || value == "<no value>" ? null : value;

    static string ShortId(string id) => id.Replace("sha256:", "")[..Math.Min(12, id.Replace("sha256:", "").Length)];

    static string Short(string volume) => volume.Length == 64 ? volume[..12] : volume;

    static Risk Max(Risk a, Risk b) => a > b ? a : b;
}
