using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskJanitor.Models;
using DiskJanitor.Scanners;
using DiskJanitor.Services;

namespace DiskJanitor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    readonly Dictionary<string, CategoryViewModel> _categoriesByName = [];
    readonly CategoryViewModel _everything;
    readonly Func<IScanner[]> _createScanners;
    CancellationTokenSource? _scanCts;
    bool _recalculatePending;

    /// <param name="createScanners">Scanners to run on each scan; the real ones by default
    /// (tools/Screenshots passes sample data).</param>
    public MainViewModel(Func<IScanner[]>? createScanners = null)
    {
        _createScanners = createScanners ?? (() =>
            [new ProjectArtifactsScanner(), new DockerScanner(), new CacheScanner(), new TrashScanner(), new SystemScanner()]);
        _everything = new CategoryViewModel("Everything", "All reclaimable items, largest first. Use the categories on the left to focus on one kind.");
        Categories.Add(_everything);
        foreach (var (name, description) in Scanners.Categories.All)
        {
            var category = new CategoryViewModel(name, description);
            _categoriesByName[name] = category;
            Categories.Add(category);
        }
        SelectedCategory = _everything;
        VisibleItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        RefreshDisk();
    }

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<CleanupItem> VisibleItems { get; } = [];

    /// <summary>Set by the window: shows the confirmation dialog.</summary>
    public Func<CleanupItem, Task<bool>>? ConfirmDelete { get; set; }

    public bool IsRoot => UserContext.IsRoot;

    public string UserText { get; init; } = UserContext.IsRoot
        ? $"Running as root · cleaning {UserContext.UserName}'s home ({UserContext.Home})"
        : $"Running as {UserContext.UserName} · items marked “needs root” require “Relaunch as root”";

    [ObservableProperty]
    public partial CategoryViewModel? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlySafe { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText), nameof(EmptyText))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText), nameof(EmptyText))]
    public partial bool HasScanned { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready.";

    [ObservableProperty]
    public partial string DiskText { get; set; } = "";

    [ObservableProperty]
    public partial double DiskPercent { get; set; }

    [ObservableProperty]
    public partial bool DiskCritical { get; set; }

    [ObservableProperty]
    public partial string ProjectRoots { get; set; } = "~/projects";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FreedText))]
    public partial long Freed { get; set; }

    public string FreedText => Freed > 0 ? $"Freed this session: {Bytes.Format(Freed)}" : "";
    public string ScanButtonText => IsScanning ? "Stop" : HasScanned ? "Rescan" : "Scan";
    public string EmptyText => IsScanning ? "Scanning…" : HasScanned ? "Nothing to clean here." : "Press Scan to look for reclaimable space.";
    public bool IsEmpty => VisibleItems.Count == 0;

    partial void OnSelectedCategoryChanged(CategoryViewModel? value) => RefreshVisible();
    partial void OnFilterChanged(string value) => RefreshVisible();
    partial void OnOnlySafeChanged(bool value) => RefreshVisible();

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task Scan()
    {
        if (IsScanning)
        {
            _scanCts?.Cancel();
            return;
        }

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        HasScanned = true;
        foreach (var category in Categories)
            category.Items.Clear();
        RecalculateNow();
        RefreshVisible();
        StatusText = "Scanning…";

        var context = new ScanContext(UserContext.Home, ParseProjectRoots());
        var scanners = _createScanners();
        var notes = new ConcurrentBag<string>();
        try
        {
            await Task.WhenAll(scanners.Select(s => RunScannerAsync(s, context, notes, ct)));
            RecalculateNow(); // totals are otherwise updated on a deferred dispatcher job
            StatusText = $"Found {Bytes.Format(_everything.Total)} in {_everything.Items.Count} items."
                         + (notes.IsEmpty ? "" : "  ⚠ " + string.Join("  ⚠ ", notes));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan stopped — sizes shown so far may be incomplete.";
        }
        finally
        {
            IsScanning = false;
            RecalculateNow();
            RefreshVisible();
        }
    }

    async Task RunScannerAsync(IScanner scanner, ScanContext context, ConcurrentBag<string> notes, CancellationToken ct)
    {
        ScanResult result;
        try
        {
            result = await Task.Run(() => scanner.ScanAsync(context, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            notes.Add($"{scanner.GetType().Name} failed: {ex.Message}");
            return;
        }

        foreach (var note in result.Notes)
            notes.Add(note);
        foreach (var item in result.Items)
            Add(item);
        RefreshVisible();

        var options = new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct };
        await Parallel.ForEachAsync(result.Items.Where(i => i.Measure is not null), options, async (item, token) =>
        {
            long size;
            try
            {
                size = item.Measure!(token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                size = 0;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                item.Size = size;
                if (size == 0)
                    Remove(item);
            });
        });
        RefreshVisible();
    }

    void Add(CleanupItem item)
    {
        item.DeleteCommand = new AsyncRelayCommand(() => DeleteAsync(item));
        item.PropertyChanged += OnItemChanged;
        _categoriesByName[item.Category].Items.Add(item);
        _everything.Items.Add(item);
        ScheduleRecalculate();
    }

    void Remove(CleanupItem item)
    {
        item.PropertyChanged -= OnItemChanged;
        _categoriesByName[item.Category].Items.Remove(item);
        _everything.Items.Remove(item);
        VisibleItems.Remove(item);
        ScheduleRecalculate();
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupItem.Size) or nameof(CleanupItem.IsDeleted))
            ScheduleRecalculate();
    }

    void ScheduleRecalculate()
    {
        if (_recalculatePending)
            return;
        _recalculatePending = true;
        Dispatcher.UIThread.Post(RecalculateNow, DispatcherPriority.Background);
    }

    void RecalculateNow()
    {
        _recalculatePending = false;
        foreach (var category in Categories)
            category.Recalculate(IsScanning);
    }

    void RefreshVisible()
    {
        IEnumerable<CleanupItem> query = (SelectedCategory ?? _everything).Items;
        if (!string.IsNullOrWhiteSpace(Filter))
            query = query.Where(i => i.Matches(Filter.Trim()));
        if (OnlySafe)
            query = query.Where(i => i.Risk == Risk.Safe);

        var sorted = query.OrderBy(i => i.IsDeleted).ThenByDescending(i => i.Size ?? -1).ToList();
        VisibleItems.Clear();
        foreach (var item in sorted)
            VisibleItems.Add(item);
    }

    async Task DeleteAsync(CleanupItem item)
    {
        if (ConfirmDelete is not null && !await ConfirmDelete(item))
            return;

        item.IsBusy = true;
        item.StatusIsError = false;
        item.Status = "Deleting…";
        var before = item.Size ?? 0;

        DeleteResult result;
        try
        {
            result = await Task.Run(() => item.Delete(CancellationToken.None));
        }
        catch (Exception ex)
        {
            result = DeleteResult.Fail(ex.Message);
        }

        if (result.Success)
        {
            var freed = item.CountsTowardTotal ? before : 0;
            Freed += freed;
            item.IsDeleted = true;
            item.Status = freed > 0 ? $"{result.Message} · freed {Bytes.Format(freed)}" : result.Message;
            StatusText = $"Deleted {item.Name}.";
        }
        else
        {
            item.StatusIsError = true;
            item.Status = result.Message;
            StatusText = $"Could not fully delete {item.Name}.";
            if (item.Measure is { } measure)
            {
                // Partially deleted folders: show what is left and count what went.
                var after = await Task.Run(() => measure(CancellationToken.None));
                Freed += Math.Max(0, before - after);
                item.Size = after;
            }
        }
        item.IsBusy = false;
        RefreshDisk();
    }

    [RelayCommand]
    async Task RelaunchAsRoot()
    {
        StatusText = "Waiting for you to authenticate…";
        var error = await RootLauncher.RelaunchAsync();
        if (error is null)
        {
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            return;
        }
        StatusText = error;
    }

    void RefreshDisk()
    {
        try
        {
            var disk = DiskInfo.For(UserContext.Home);
            DiskPercent = disk.Total == 0 ? 0 : disk.Used * 100.0 / disk.Total;
            DiskCritical = DiskPercent >= 90;
            DiskText = $"{Bytes.Format(disk.Used)} used of {Bytes.Format(disk.Total)}  ·  {Bytes.Format(disk.Available)} free on {disk.MountPoint}";
        }
        catch (Exception ex)
        {
            DiskText = $"Disk usage unavailable: {ex.Message}";
        }
    }

    List<string> ParseProjectRoots() =>
        ProjectRoots.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UserContext.Expand)
            .Distinct()
            .ToList();
}
