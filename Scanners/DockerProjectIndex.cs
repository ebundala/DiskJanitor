using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiskJanitor.Scanners;

/// <summary>
/// Reads Dockerfiles, compose files, devcontainers and CI workflows in the project folders to find
/// which images each project depends on and which compose projects (and so built images) it owns.
/// </summary>
public sealed partial class DockerProjectIndex
{
    const int MaxDepth = 6;
    const long MaxFileSize = 1024 * 1024;

    static readonly HashSet<string> SkippedDirs =
    [
        "node_modules", ".git", "bin", "obj", "build", "dist", "target", ".dart_tool", ".gradle",
        "vendor", "Pods", ".venv", "venv", ".next", ".nuxt", ".angular", ".turbo", "coverage",
    ];

    /// <summary>Normalized "repo:tag" -> files that reference it.</summary>
    public Dictionary<string, SortedSet<string>> ImageRefs { get; } = new();
    /// <summary>Normalized repo (no tag) -> files that reference any tag of it.</summary>
    public Dictionary<string, SortedSet<string>> RepoRefs { get; } = new();
    /// <summary>Compose project name -> folder that defines it (relative to its project root).</summary>
    public Dictionary<string, string> ComposeProjects { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Top-level project folder name (normalized like compose does) -> folder.</summary>
    public Dictionary<string, string> TopFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static DockerProjectIndex Build(IEnumerable<string> roots, CancellationToken ct)
    {
        var index = new DockerProjectIndex();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var top in SafeDirs(root))
                index.TopFolders.TryAdd(ComposeName(Path.GetFileName(top)), Path.GetFileName(top));

            var pending = new Stack<(string Dir, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = pending.Pop();
                foreach (var file in SafeFiles(dir))
                    index.ReadFile(root, file);
                if (depth + 1 >= MaxDepth)
                    continue;
                foreach (var sub in SafeDirs(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (SkippedDirs.Contains(name) || (name.StartsWith('.') && name is not ".github" and not ".devcontainer"))
                        continue;
                    pending.Push((sub, depth + 1));
                }
            }
        }
        return index;
    }

    /// <summary>Which project folder probably built this image (by compose label or the compose naming convention).</summary>
    public string? FindBuilder(string repository, string? composeProjectLabel)
    {
        if (composeProjectLabel is { Length: > 0 } && ComposeProjects.TryGetValue(composeProjectLabel, out var labelled))
            return labelled;

        var repo = repository.ToLowerInvariant();
        repo = repo[(repo.LastIndexOf('/') + 1)..];
        return ComposeProjects.Concat(TopFolders)
            .Where(p => p.Key.Length >= 3)
            .OrderByDescending(p => p.Key.Length)
            .Where(p => repo == p.Key || repo.StartsWith(p.Key + "-") || repo.StartsWith(p.Key + "_"))
            .Select(p => p.Value)
            .FirstOrDefault();
    }

    /// <summary>docker.io/library/postgres -> postgres:latest, Node:20 -> node:20.</summary>
    public static string Normalize(string reference)
    {
        var r = reference.Trim().Trim('"', '\'').ToLowerInvariant();
        var at = r.IndexOf('@');
        if (at >= 0) r = r[..at];
        foreach (var prefix in new[] { "docker.io/", "index.docker.io/", "registry-1.docker.io/" })
            if (r.StartsWith(prefix)) r = r[prefix.Length..];
        if (r.StartsWith("library/")) r = r["library/".Length..];
        if (r.LastIndexOf(':') <= r.LastIndexOf('/')) r += ":latest";
        return r;
    }

    public static string RepoOf(string normalized) => normalized[..normalized.LastIndexOf(':')];

    void ReadFile(string root, string path)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        var isDockerfile = name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
                           || name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase);
        var isCompose = ComposeFileRegex().IsMatch(name);
        var isDevcontainer = parent == ".devcontainer" && name.EndsWith(".json");
        var isWorkflow = parent == "workflows" && (name.EndsWith(".yml") || name.EndsWith(".yaml"));
        if (!isDockerfile && !isCompose && !isDevcontainer && !isWorkflow)
            return;

        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileSize) return;
            text = File.ReadAllText(path);
        }
        catch
        {
            return;
        }

        var display = Path.GetRelativePath(root, path);
        if (isDockerfile)
        {
            var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in FromRegex().Matches(text))
            {
                // "FROM builder" pointing at an earlier stage is not an image reference.
                var image = m.Groups["image"].Value;
                if (!stages.Contains(image))
                    AddRef(image, display);
                if (m.Groups["stage"].Success)
                    stages.Add(m.Groups["stage"].Value);
            }
        }
        else
        {
            foreach (Match m in ImageKeyRegex().Matches(text))
                AddRef(m.Groups["image"].Value, display);
        }

        if (isCompose)
        {
            var folder = Path.GetDirectoryName(path)!;
            var relativeFolder = Path.GetRelativePath(root, folder);
            var projectName = ComposeNameRegex().Match(text) is { Success: true } named
                ? named.Groups[1].Value
                : ComposeName(Path.GetFileName(folder));
            ComposeProjects.TryAdd(projectName, relativeFolder == "." ? Path.GetFileName(root) : relativeFolder);
        }
    }

    void AddRef(string raw, string file)
    {
        var image = VariableDefaultRegex().Replace(raw, m => m.Groups[1].Value);
        if (image.Contains('$') || image.Length == 0 || image.Equals("scratch", StringComparison.OrdinalIgnoreCase))
            return;
        var normalized = Normalize(image);
        Add(ImageRefs, normalized, file);
        Add(RepoRefs, RepoOf(normalized), file);
    }

    static void Add(Dictionary<string, SortedSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
            map[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }

    /// <summary>Compose's default project name: lowercase, only [a-z0-9_-].</summary>
    static string ComposeName(string folder) => InvalidComposeChars().Replace(folder.ToLowerInvariant(), "");

    static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(d => d.FullName).ToList();
        }
        catch
        {
            return [];
        }
    }

    static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch
        {
            return [];
        }
    }

    [GeneratedRegex(@"^(docker-)?compose([.-][\w.-]*)?\.ya?ml$", RegexOptions.IgnoreCase)]
    private static partial Regex ComposeFileRegex();

    [GeneratedRegex(@"^\s*FROM\s+(?:--\S+\s+)*(?<image>[^\s#]+)(?:\s+AS\s+(?<stage>\S+))?", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FromRegex();

    [GeneratedRegex(@"^\s*-?\s*""?image""?\s*:\s*[""']?(?<image>[^""'\s#,]+)", RegexOptions.Multiline)]
    private static partial Regex ImageKeyRegex();

    [GeneratedRegex(@"^name:\s*[""']?([\w.-]+)", RegexOptions.Multiline)]
    private static partial Regex ComposeNameRegex();

    [GeneratedRegex(@"\$\{[A-Za-z_][A-Za-z0-9_]*:?-([^}]*)\}")]
    private static partial Regex VariableDefaultRegex();

    [GeneratedRegex(@"[^a-z0-9_-]")]
    private static partial Regex InvalidComposeChars();
}
