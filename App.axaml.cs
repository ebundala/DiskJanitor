using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiskJanitor.Services;
using DiskJanitor.ViewModels;
using DiskJanitor.Views;

namespace DiskJanitor;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            // When started by "Relaunch as root", tell the original instance it can close.
            var args = desktop.Args ?? [];
            window.Opened += (_, _) => RootLauncher.CompleteHandoff(args);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
