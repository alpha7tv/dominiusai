using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Desktop.Views;

namespace DominiusPlay.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single) // navegador
            single.MainView = new MainView { DataContext = new MainWindowViewModel() };
        base.OnFrameworkInitializationCompleted();
    }
}
