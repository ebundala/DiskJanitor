using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiskJanitor.Models;

namespace DiskJanitor.Scanners;

/// <summary>Well-known cache folders in the user's home. Only their contents are removed.</summary>
public sealed class CacheScanner : IScanner
{
    sealed record CacheDir(string Category, string Name, string RelativePath, string Details, string? Warning = null);

    static readonly CacheDir[] Known = BuildList();

    static CacheDir[] BuildList()
    {
        const string pkg = Categories.PackageCaches;
        const string app = Categories.AppCaches;
        var list = new List<CacheDir>
        {
            new(pkg, "npm cache", ".npm/_cacache", "npm's download cache. Packages are fetched again on the next install."),
            new(pkg, "pnpm store", ".local/share/pnpm/store", "pnpm's shared package store. node_modules folders hard-link into it, so the space only comes back once those are gone too."),
            new(pkg, "pnpm metadata cache", ".cache/pnpm", "Cached registry metadata for pnpm."),
            new(pkg, "Yarn cache", ".cache/yarn", "Yarn classic's download cache."),
            new(pkg, "Yarn Berry cache", ".yarn/berry/cache", "Yarn 2+ global download cache."),
            new(pkg, "Bun cache", ".bun/install/cache", "Bun's package cache."),
            new(pkg, "NuGet packages", ".nuget/packages", "Global NuGet package folder. Restored automatically on the next dotnet build."),
            new(pkg, "NuGet HTTP cache", ".local/share/NuGet", "NuGet's HTTP and plugin caches."),
            new(pkg, "Gradle caches", ".gradle/caches", "Downloaded dependencies and build caches for Android/Gradle builds."),
            new(pkg, "Gradle wrapper distributions", ".gradle/wrapper/dists", "Downloaded Gradle versions. Each project's wrapper re-downloads the one it needs."),
            new(pkg, "Gradle daemon logs", ".gradle/daemon", "Logs and state of old Gradle daemons."),
            new(pkg, "Maven repository", ".m2/repository", "Local Maven artifact cache."),
            new(pkg, "Dart/Flutter pub cache", ".pub-cache/hosted", "Packages from pub.dev. Re-downloaded by flutter pub get."),
            new(pkg, "Dart analysis server cache", ".dartServer", "Analyzer cache used by Dart/Flutter editors."),
            new(pkg, "Cargo registry", ".cargo/registry", "Downloaded Rust crates."),
            new(pkg, "Go build cache", ".cache/go-build", "Compiled Go packages."),
            new(pkg, "Go module cache", "go/pkg/mod", "Downloaded Go modules."),
            new(pkg, "pip cache", ".cache/pip", "Python package downloads."),
            new(pkg, "uv cache", ".cache/uv", "uv's Python package cache."),
            new(pkg, "Composer cache", ".cache/composer", "PHP Composer download cache."),
            new(pkg, "node-gyp headers", ".cache/node-gyp", "Node.js headers for building native modules."),
            new(pkg, "Minikube downloads", ".minikube/cache", "Cached Kubernetes images and binaries for minikube."),
            new(pkg, "Playwright browsers", ".cache/ms-playwright", "Browsers downloaded for Playwright tests.", "Tests need `npx playwright install` again."),
            new(pkg, "Puppeteer browsers", ".cache/puppeteer", "Chrome downloaded by Puppeteer."),
            new(pkg, "Electron downloads", ".cache/electron", "Electron binaries downloaded by npm installs."),
            new(pkg, "Hugging Face cache", ".cache/huggingface", "Downloaded AI models and datasets.", "Models can be several GB to download again."),

            new(app, "Chrome cache", ".cache/google-chrome", "Google Chrome's web cache. Your profile, logins and history are not affected."),
            new(app, "Chromium cache", ".cache/chromium", "Chromium's web cache."),
            new(app, "Brave cache", ".cache/BraveSoftware", "Brave's web cache."),
            new(app, "Firefox cache", ".cache/mozilla", "Firefox's web cache."),
            new(app, "Mesa shader cache", ".cache/mesa_shader_cache", "Compiled GPU shaders.", "Games and apps recompile shaders, so their first runs will stutter."),
            new(app, "Mesa shader cache (db)", ".cache/mesa_shader_cache_db", "Compiled GPU shaders.", "Games and apps recompile shaders, so their first runs will stutter."),
            new(app, "Thumbnails", ".cache/thumbnails", "File manager thumbnails."),
            new(app, "TypeScript cache", ".cache/typescript", "Type definitions downloaded by editors."),
            new(app, "JetBrains caches", ".cache/JetBrains", "IDE indexes and caches; rebuilt on next open."),
            new(app, "GNOME search index", ".cache/tracker3", "Desktop search index; rebuilt in the background."),
            new(app, "dotslash cache", ".cache/dotslash", "Binaries fetched by dotslash."),
            new(app, "Android SDK cache", ".android/cache", "Android SDK manager downloads."),
        };

        foreach (var (folder, label) in new[] { ("Code", "VS Code"), ("Code - Insiders", "VS Code Insiders"), ("Cursor", "Cursor") })
        {
            var root = $".config/{folder}";
            list.Add(new(app, $"{label} extension installers", $"{root}/CachedExtensionVSIXs", "Copies of extension packages kept after installing."));
            list.Add(new(app, $"{label} cache", $"{root}/Cache", "Web content cache."));
            list.Add(new(app, $"{label} compiled code cache", $"{root}/CachedData", "Compiled JavaScript from previous versions."));
            list.Add(new(app, $"{label} GPU cache", $"{root}/GPUCache", "GPU shader cache."));
            list.Add(new(app, $"{label} logs", $"{root}/logs", "Old log files."));
            list.Add(new(app, $"{label} workspace storage", $"{root}/User/workspaceStorage", "Per-folder editor state and extension data.",
                $"Open tabs, per-workspace settings and extension data (such as AI chat history) are lost. Close {label} first."));
        }
        return list.ToArray();
    }

    public Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var items = new List<CleanupItem>();
        foreach (var cache in Known)
        {
            var path = Path.Combine(context.Home, cache.RelativePath);
            if (!Directory.Exists(path))
                continue;
            items.Add(ScanHelpers.Folder(cache.Category, cache.Name, cache.Category == Categories.PackageCaches ? "Package cache" : "App cache",
                path, cache.Details, cache.Warning, contentsOnly: true));
        }
        return Task.FromResult(new ScanResult(items, []));
    }
}
