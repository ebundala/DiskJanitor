using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskJanitor.Models;
using DiskJanitor.Services;

namespace DiskJanitor.ViewModels;

public partial class CategoryViewModel(string name, string description) : ViewModelBase
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public List<CleanupItem> Items { get; } = [];

    [ObservableProperty]
    public partial string TotalText { get; set; } = "";

    [ObservableProperty]
    public partial string CountText { get; set; } = "not scanned";

    [ObservableProperty]
    public partial long Total { get; set; }

    public void Recalculate(bool scanning)
    {
        var live = Items.Where(i => !i.IsDeleted).ToList();
        Total = live.Where(i => i.CountsTowardTotal).Sum(i => i.Size ?? 0);
        var measuring = live.Any(i => i.Size is null);
        TotalText = live.Count == 0 ? "" : Bytes.Format(Total) + (measuring ? "+" : "");
        CountText = live.Count switch
        {
            0 when scanning => "scanning…",
            0 => "nothing found",
            1 => "1 item",
            var n => $"{n} items",
        };
    }
}
