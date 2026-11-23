using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Models;
using DiskJanitor.Services;

namespace DiskJanitor.Scanners;

/// <summary>Finds dependency/build folders (node_modules, build, bin/obj, …) under the project roots.</summary>
public sealed class ProjectArtifactsScanner : IScanner
{
    const int MaxDepth = 8;
    static readonly TimeSpan RecentlyActive = TimeSpan.FromDays(14);

    sealed record Rule(string Kind, string Restore, Func<string, bool> AppliesTo);

    static bool Has(string dir, params string[] files) => files.Any(f => File.Exists(Path.Combine(dir, f)));

    static bool HasExtension(string dir, params string[] extensions)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    static bool IsDotnetProject(string dir) => HasExtension(dir, ".csproj", ".fsproj", ".vbproj");
    static bool IsGradleProject(string dir) => Has(dir, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradlew");

    // Folder name -> rules checked against the folder's parent (first match wins).
    static readonly Dictionary<string, Rule[]> Rules = new()
    {
        ["node_modules"] = [new("Node packages", "Run npm/pnpm/yarn install to restore.", _ => true)],
        [".next"] = [new("Next.js build cache", "Rebuilt by next dev/build.", _ => true)],
        [".nuxt"] = [new("Nuxt build cache", "Rebuilt by nuxt dev/build.", _ => true)],
        [".output"] = [new("Nuxt/Nitro output", "Rebuilt by the build command.", d => Has(d, "package.json"))],
        [".svelte-kit"] = [new("SvelteKit build cache", "Rebuilt by vite dev/build.", _ => true)],
        [".angular"] = [new("Angular build cache", "Rebuilt by ng serve/build.", _ => true)],
        [".turbo"] = [new("Turborepo cache", "Rebuilt by turbo.", _ => true)],
        [".parcel-cache"] = [new("Parcel cache", "Rebuilt by parcel.", _ => true)],
        [".expo"] = [new("Expo local cache", "Recreated by expo start.", d => Has(d, "package.json"))],
        ["dist"] = [new("JS build output", "Recreated by the build script.", d => Has(d, "package.json"))],
        ["coverage"] = [new("Test coverage report", "Recreated by running tests with coverage.", d => Has(d, "package.json"))],
        ["build"] =
        [
            new("Flutter build output", "Recreated by flutter build/run.", d => Has(d, "pubspec.yaml")),
            new("Gradle build output", "Recreated by the next Gradle build.", IsGradleProject),
            new("JS build output", "Recreated by the build script.", d => Has(d, "package.json")),
            new("CMake build folder", "Recreated by cmake.", d => Has(d, "CMakeLists.txt")),
        ],
        [".dart_tool"] = [new("Dart tool cache", "Recreated by flutter/dart pub get.", d => Has(d, "pubspec.yaml"))],
        [".gradle"] = [new("Gradle project cache", "Recreated by the next Gradle build.", IsGradleProject)],
        ["Pods"] = [new("CocoaPods", "Run pod install to restore.", d => Has(d, "Podfile"))],
        ["target"] =
        [
            new("Rust build output", "Recreated by cargo build.", d => Has(d, "Cargo.toml")),
            new("Maven build output", "Recreated by mvn package.", d => Has(d, "pom.xml")),
        ],
        ["bin"] = [new(".NET build output", "Recreated by dotnet build.", IsDotnetProject)],
        ["obj"] = [new(".NET intermediate build files", "Recreated by dotnet restore/build.", IsDotnetProject)],
        [".venv"] = [new("Python virtualenv", "Recreate it and pip install -r requirements.", _ => true)],
        ["venv"] = [new("Python virtualenv", "Recreate it and pip install -r requirements.", d => File.Exists(Path.Combine(d, "venv", "pyvenv.cfg")))],
        [".pytest_cache"] = [new("pytest cache", "Recreated by pytest.", _ => true)],
        [".mypy_cache"] = [new("mypy cache", "Recreated by mypy.", _ => true)],
        ["vendor"] = [new("PHP Composer packages", "Run composer install to restore.", d => Has(d, "composer.json"))],
    };

    public Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        var notes = new List<string>();
        var activity = new Dictionary<string, DateTime>();
        var ownBuild = Path.GetFullPath(AppContext.BaseDirectory);

        foreach (var root in context.ProjectRoots)
        {
            if (!Directory.Exists(root))
            {
                notes.Add($"Project folder not found: {UserContext.Pretty(root)}");
                continue;
            }

            var pending = new Stack<(string Dir, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = pending.Pop();
                foreach (var sub in SubDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (Rules.TryGetValue(name, out var rules) && rules.FirstOrDefault(r => r.AppliesTo(dir)) is { } rule)
                    {
                        items.Add(CreateItem(root, sub, rule, activity, ownBuild));
                        continue;
                    }
                    if (name.StartsWith('.') || depth + 1 >= MaxDepth)
                        continue;
                    pending.Push((sub, depth + 1));
                }
            }
        }
        return Task.FromResult(new ScanResult(items, notes));
    }

    static CleanupItem CreateItem(string root, string path, Rule rule, Dictionary<string, DateTime> activity, string ownBuild)
    {
        var relative = Path.GetRelativePath(root, path);
        var firstSegment = relative.Split('/')[0];
        var projectDir = firstSegment == Path.GetFileName(path) && !relative.Contains('/') ? root : Path.Combine(root, firstSegment);
        var projectName = Path.GetFileName(projectDir);
        var inside = Path.GetRelativePath(projectDir, path);

        if (!activity.TryGetValue(projectDir, out var lastActive))
            activity[projectDir] = lastActive = LastActivity(projectDir);

        var details = $"{rule.Restore} Project last active {TimeText.Ago(lastActive)}.";
        if (rule.Kind == "Node packages" && Directory.Exists(Path.Combine(path, ".pnpm")))
            details += " Uses pnpm: files are hard-linked from the pnpm store, so less space may be freed than shown.";

        string? warning = null;
        if ((Path.GetFullPath(path) + "/").StartsWith(ownBuild) || ownBuild.StartsWith(Path.GetFullPath(path) + "/"))
            warning = "This is Disk Janitor's own build folder.";
        else if (DateTime.UtcNow - lastActive < RecentlyActive)
            warning = "You worked on this project recently — you'll need to reinstall/rebuild before your next run.";

        return ScanHelpers.Folder(Categories.ProjectBuilds, $"{projectName} › {inside}", rule.Kind, path, details, warning);
    }

    /// <summary>Git activity if available, otherwise the newest top-level file.</summary>
    static DateTime LastActivity(string projectDir)
    {
        var newest = DateTime.MinValue;
        foreach (var gitFile in new[] { ".git/index", ".git/HEAD", ".git/FETCH_HEAD", ".git/ORIG_HEAD" })
        {
            var path = Path.Combine(projectDir, gitFile);
            if (File.Exists(path))
                newest = Max(newest, File.GetLastWriteTimeUtc(path));
        }
        if (newest != DateTime.MinValue)
            return newest;
        try
        {
            foreach (var file in Directory.EnumerateFiles(projectDir))
                newest = Max(newest, File.GetLastWriteTimeUtc(file));
        }
        catch
        {
            // ignore
        }
        return newest == DateTime.MinValue ? Directory.GetLastWriteTimeUtc(projectDir) : newest;
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    static IEnumerable<string> SubDirectories(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(d => d.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
