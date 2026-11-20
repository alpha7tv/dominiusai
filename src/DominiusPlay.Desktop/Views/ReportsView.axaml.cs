using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DominiusPlay.Desktop.ViewModels;

namespace DominiusPlay.Desktop.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private void OnExportLeads(object? sender, RoutedEventArgs e) => Export("leads", vm => vm.ExportLeadsAsync());
    private void OnExportMessages(object? sender, RoutedEventArgs e) => Export("mensagens", vm => vm.ExportMessagesAsync());

    private async void Export(string name, Func<ReportsViewModel, Task<byte[]>> download)
    {
        if (DataContext is not ReportsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar exportação", SuggestedFileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv", DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV (Excel)") { Patterns = ["*.csv"] }],
        });
        if (file is null) return;
        await vm.RunSafe(async () =>
        {
            var bytes = await download(vm);
            await using var s = await file.OpenWriteAsync();
            await s.WriteAsync(bytes);
            vm.ShowInfo($"Arquivo salvo: {file.Name}");
        });
    }
}
