using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiskJanitor.Models;
using DiskJanitor.Scanners;

namespace DiskJanitor.Views;

/// <summary>"Are you sure?" dialog that restates exactly what is about to be deleted.</summary>
public static class ConfirmDialog
{
    public static Task<bool> ShowAsync(Window owner, CleanupItem item)
    {
        var dialog = new Window
        {
            Title = "Confirm deletion",
            Width = 580,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Delete {item.Name}?",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        AddFact(facts, 0, "What", item.Kind);
        AddFact(facts, 1, "Where", item.Location);
        AddFact(facts, 2, "Frees", item.CountsTowardTotal ? $"about {item.SizeText}" : "nothing until the image's other tags are removed too");
        panel.Children.Add(facts);

        if (!string.IsNullOrEmpty(item.Details))
            panel.Children.Add(new TextBlock { Text = item.Details, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        if (!string.IsNullOrEmpty(item.Warning))
        {
            var callout = new Border
            {
                Child = new TextBlock { Text = "⚠ " + item.Warning, TextWrapping = TextWrapping.Wrap },
            };
            callout.Classes.Add("callout");
            panel.Children.Add(callout);
        }

        panel.Children.Add(new TextBlock
        {
            Text = item.Category == Categories.Trash
                ? "Emptying the trash is permanent."
                : "This is permanent — nothing is moved to the Trash.",
            Opacity = 0.7,
        });

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var delete = new Button { Content = "Delete", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        delete.Classes.Add("danger");
        cancel.Click += (_, _) => dialog.Close(false);
        delete.Click += (_, _) => dialog.Close(true);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { cancel, delete },
        });

        dialog.Content = panel;
        dialog.Opened += (_, _) => cancel.Focus();
        return dialog.ShowDialog<bool>(owner);
    }

    static void AddFact(Grid grid, int row, string label, string value)
    {
        var name = new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 2, 16, 2) };
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
    }
}
