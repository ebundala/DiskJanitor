using System;
using Avalonia.Controls;
using DiskJanitor.ViewModels;

namespace DiskJanitor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
            vm.ConfirmDelete = item => ConfirmDialog.ShowAsync(this, item);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Routine use: start scanning straight away.
        if (DataContext is MainViewModel vm)
            vm.ScanCommand.Execute(null);
    }
}
