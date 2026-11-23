using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using DiskJanitor;
using DiskJanitor.Models;
using DiskJanitor.Scanners;
using DiskJanitor.ViewModels;
using DiskJanitor.Views;

// Renders Disk Janitor's main window with made-up sample data (no real files are scanned) and
// writes PNG screenshots, in light and dark theme, to the folder given as the first argument.
var outDir = Path.GetFullPath(args.FirstOrDefault() ?? "docs/screenshots");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
{
    Application.Current!.RequestedThemeVariant = variant;
    Shot(vm => { }, $"everything-{suffix}.png");
    Shot(vm => vm.SelectedCategory = vm.Categories.First(c => c.Name == Categories.DockerImages), $"docker-{suffix}.png");
    Shot(vm => vm.SelectedCategory = vm.Categories.First(c => c.Name == Categories.ProjectBuilds), $"projects-{suffix}.png");
}
Console.WriteLine($"Wrote screenshots to {outDir}");
return 0;

void Shot(Action<MainViewModel> arrange, string file)
{
    var vm = new MainViewModel(() => [new SampleScanner()])
    {
        UserText = "Running as dev · items marked “needs root” require “Relaunch as root”",
    };
    var window = new MainWindow { DataContext = vm, Width = 1240, Height = 820 };
    window.Show(); // OnOpened starts the scan
    Pump(() => vm.HasScanned && !vm.IsScanning);
    vm.DiskText = "418 GB used of 476 GB  ·  58 GB free on /";
    vm.DiskPercent = 418.0 / 476 * 100;
    vm.DiskCritical = false;
    vm.ProjectRoots = "~/projects";
    arrange(vm);
    Pump(() => true);
    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered");
    frame.Save(Path.Combine(outDir, file));
    window.Close();
    Console.WriteLine(file);
}

static void Pump(Func<bool> done)
{
    var until = DateTime.UtcNow.AddSeconds(10);
    do
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(20);
    } while (!done() && DateTime.UtcNow < until);
    for (var i = 0; i < 5; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

/// <summary>Fixed, fictional scan results that cover every category and risk level.</summary>
sealed class SampleScanner : IScanner
{
    const long KB = 1024, MB = KB * 1024, GB = MB * 1024;

    public Task<ScanResult> ScanAsync(ScanContext context, CancellationToken ct)
    {
        var items = new List<CleanupItem>
        {
            Item(Categories.ProjectBuilds, "storefront › node_modules", "Node packages", "~/projects/storefront/node_modules", (long)(2.8 * GB),
                "Run npm/pnpm/yarn install to restore."),
            Item(Categories.ProjectBuilds, "mobile-app › build", "Flutter build output", "~/projects/mobile-app/build", (long)(1.6 * GB),
                "Recreated by flutter build / flutter run."),
            Item(Categories.ProjectBuilds, "api-gateway › target", "Rust build output", "~/projects/api-gateway/target", (long)(4.1 * GB),
                "Recreated by cargo build.",
                "You worked on this project recently — you'll need to reinstall/rebuild before your next run."),
            Item(Categories.ProjectBuilds, "dashboard › .next", "Next.js build cache", "~/projects/dashboard/.next", 640 * MB,
                "Recreated by next build / next dev."),
            Item(Categories.ProjectBuilds, "billing-service › bin/obj", ".NET build output", "~/projects/billing-service/src/Billing.Api/obj", 310 * MB,
                "Recreated by dotnet build."),
            Item(Categories.ProjectBuilds, "ml-notebooks › .venv", "Python virtualenv", "~/projects/ml-notebooks/.venv", (long)(3.3 * GB),
                "Recreate with python -m venv .venv and pip install -r requirements.txt."),

            Item(Categories.DockerImages, "postgres:16", "Docker image", "image 3f1a9c2b7d44", 438 * MB,
                "Used by 1 container.", "In use by container storefront-db-1 (running).", Risk.Danger),
            Item(Categories.DockerImages, "storefront-web:latest", "Docker image", "image a81d0e55c912", (long)(1.2 * GB),
                null, "Built from project storefront — you'd have to rebuild it.", Risk.Caution),
            Item(Categories.DockerImages, "node:18-bullseye", "Docker image", "image 9c3e47f1b0aa", (long)(1.1 * GB),
                "No container or project file found that uses it."),
            Item(Categories.DockerImages, "<none>:<none>", "Dangling image", "image 51b7e2c0d9f3", 870 * MB,
                "Untagged (dangling) image, usually left behind when a newer build replaced it."),
            Item(Categories.DockerContainers, "storefront-db-1", "Container · running", "container 7e2f91ab3c10", 64 * MB,
                "From image postgres:16. Mounts volume(s) storefront_pgdata; removing the container keeps them.",
                "Running — stop it first (docker stop), otherwise removing fails.", Risk.Danger),
            Item(Categories.DockerVolumes, "storefront_pgdata", "Docker volume", "volume storefront_pgdata", (long)(1.9 * GB),
                "Compose project storefront. Attached to storefront-db-1.",
                "Named volume — may hold a project's database or uploads. Deleting it is permanent.", Risk.Caution),
            Item(Categories.DockerBuildCache, "Docker build cache", "Build cache", "docker builder prune", (long)(5.4 * GB),
                "Layers and RUN --mount caches from docker build. The next build of each image starts from scratch."),

            Item(Categories.PackageCaches, "npm cache", "Package cache", "~/.npm/_cacache", (long)(2.2 * GB),
                "Packages are downloaded again when a project needs them."),
            Item(Categories.PackageCaches, "NuGet packages", "Package cache", "~/.nuget/packages", (long)(3.7 * GB),
                "Restored by dotnet restore."),
            Item(Categories.PackageCaches, "Gradle caches", "Package cache", "~/.gradle/caches", (long)(2.9 * GB),
                "Dependencies and build caches of Gradle; downloaded again on the next build."),
            Item(Categories.AppCaches, "Chrome cache", "App cache", "~/.cache/google-chrome", 920 * MB,
                "Browser cache; rebuilt as you browse."),
            Item(Categories.AppCaches, "VS Code cached extensions", "App cache", "~/.config/Code/CachedExtensionVSIXs", 410 * MB,
                "Extension installers kept after installing."),
            Item(Categories.Trash, "Home trash", "Trash", "~/.local/share/Trash", 760 * MB,
                "214 items, including old-screenshots, design-v1.fig and 212 more."),

            Item(Categories.System, "System journal logs", "Logs", "journalctl --vacuum-size=200M", (long)(1.4 * GB),
                "The journal uses 1.6 GB; this keeps the newest 200 MB.", requiresRoot: true),
            Item(Categories.System, "apt package cache", "Package cache", "/var/cache/apt/archives", 520 * MB,
                "Downloaded .deb installers of packages that are already installed.", requiresRoot: true),
            Item(Categories.System, "firefox (revision 4793)", "Old snap revision", "/var/lib/snapd/snaps", 270 * MB,
                "A disabled older version kept for rollback. Run `snap set system refresh.retain=2` to keep fewer.", requiresRoot: true),
        };
        return Task.FromResult(new ScanResult(items, []));
    }

    static CleanupItem Item(string category, string name, string kind, string location, long size, string? details,
        string? warning = null, Risk risk = Risk.Safe, bool requiresRoot = false) => new()
    {
        Category = category,
        Name = name,
        Kind = kind,
        Location = location,
        Size = size,
        Details = details,
        Warning = warning,
        Risk = warning is not null && risk == Risk.Safe ? Risk.Caution : risk,
        RequiresRoot = requiresRoot,
        Delete = _ => Task.FromResult(DeleteResult.Ok()),
    };
}
