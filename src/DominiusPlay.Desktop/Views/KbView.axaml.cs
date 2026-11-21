using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DominiusPlay.Desktop.ViewModels;

namespace DominiusPlay.Desktop.Views;

public partial class KbView : UserControl
{
    public KbView() => InitializeComponent();

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KbViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar base Dominius Play", SuggestedFileName = $"base-dominius-play-{DateTime.Now:yyyyMMdd}.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;
        await vm.ExportAsync(async bytes => { await using var s = await file.OpenWriteAsync(); await s.WriteAsync(bytes); });
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KbViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar perguntas (JSON exportado)", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count != 1) return;
        await using var stream = await files[0].OpenReadAsync();
        var json = await new StreamReader(stream).ReadToEndAsync();
        var overwrite = await ConfirmDialog.ShowAsync(top, "Importar perguntas",
            "Atualizar as perguntas que já existem com o mesmo ID?\n\nSim = atualiza (o histórico guarda a versão anterior). Não = só cria as novas.");
        await vm.ImportAsync(json, overwrite);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KbViewModel vm || vm.Selected is null) return;
        if (await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this), "Excluir pergunta",
                $"Excluir {vm.Selected.Code} — {vm.Selected.Question}?\n\nPrefira Desativar se quiser só tirar da IA. A cópia completa fica no histórico."))
            await vm.DeleteSelectedAsync();
    }
}
