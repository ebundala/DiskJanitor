using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskJanitor.Services;

namespace DiskJanitor.Models;

public enum Risk
{
    /// <summary>Nothing known depends on it.</summary>
    Safe,
    /// <summary>Something references it or it may hold data; you can delete it but should know why it exists.</summary>
    Caution,
    /// <summary>In use right now (e.g. by a container); deleting will fail or break something.</summary>
    Danger,
}

public sealed record DeleteResult(bool Success, string Message)
{
    public static DeleteResult Ok(string message = "Deleted") => new(true, message);
    public static DeleteResult Fail(string message) => new(false, message);
}

/// <summary>One thing that can be deleted: a folder, a docker object, a log set…</summary>
public partial class CleanupItem : ObservableObject
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    /// <summary>Short label of what this is, e.g. "Node packages" or "Docker image".</summary>
    public required string Kind { get; init; }
    public string Location { get; init; } = "";
    public string? Details { get; init; }
    public string? Warning { get; init; }
    public Risk Risk { get; init; }
    public bool RequiresRoot { get; init; }
    /// <summary>False when the same space is already counted by another item (e.g. a second tag of one image).</summary>
    public bool CountsTowardTotal { get; init; } = true;
    /// <summary>Computes <see cref="Size"/> in the background; null when the size is known up front.</summary>
    public Func<CancellationToken, long>? Measure { get; init; }
    public required Func<CancellationToken, Task<DeleteResult>> Delete { get; init; }

    public IAsyncRelayCommand? DeleteCommand { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? Size { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    public partial bool IsDeleted { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    public string SizeText => Size is { } size ? Bytes.Format(size) : "measuring…";
    public bool CanDelete => !IsBusy && !IsDeleted && (!RequiresRoot || UserContext.IsRoot);
    public bool IsCaution => Risk == Risk.Caution;
    public bool IsDanger => Risk == Risk.Danger;
    public bool NeedsElevation => RequiresRoot && !UserContext.IsRoot;

    public string DeleteTooltip => NeedsElevation
        ? "Needs root — use “Relaunch as root” at the top"
        : "Delete permanently (does not go to the Trash)";

    public bool Matches(string filter) =>
        Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Kind.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Location.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Details?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Warning?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
