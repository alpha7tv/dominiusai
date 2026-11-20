using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DominiusPlay.Desktop.ViewModels;

namespace DominiusPlay.Desktop.Views;

public partial class LeadsView : UserControl
{
    public LeadsView() => InitializeComponent();

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LeadsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar leads", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Planilhas") { Patterns = ["*.csv", "*.xlsx", "*.txt"] }],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path) await vm.ImportFile(path);
    }
}
