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
        if (files.Count != 1) return;
        if (files[0].TryGetLocalPath() is { } path) await vm.ImportFile(path);
        else { await using var stream = await files[0].OpenReadAsync(); await vm.ImportFile(stream, files[0].Name); } // navegador
    }

    private async void OnExportLgpd(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LeadsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        if (vm.Selected is null) { vm.Error = "Selecione um lead na lista."; return; }
        var lead = vm.Selected;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Exportar dados do titular", SuggestedFileName = $"dados-titular-{lead.Model.Phone}.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;
        await vm.SaveExportAsync(lead.Id, async bytes => { await using var s = await file.OpenWriteAsync(); await s.WriteAsync(bytes); }, file.Name);
    }

    private async void OnDeleteLead(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LeadsViewModel vm) return;
        if (vm.Selected is null) { vm.Error = "Selecione um lead na lista."; return; }
        if (await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this), "Excluir dados do titular",
                $"Excluir definitivamente {vm.Selected.Name} ({vm.Selected.Phone}) com mensagens, histórico e fatos?\n\nO número continua na lista de bloqueio se tiver sido descadastrado. Esta ação não pode ser desfeita."))
            await vm.DeleteSelectedAsync();
    }

    private async void OnAnonymizeLead(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LeadsViewModel vm) return;
        if (vm.Selected is null) { vm.Error = "Selecione um lead na lista."; return; }
        if (await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this), "Anonimizar contato",
                $"Anonimizar {vm.Selected.Name} ({vm.Selected.Phone})?\n\nNome, telefone, cidade, notas, fatos e o texto das mensagens são apagados. Os números de campanha e funil continuam nos relatórios. O telefone entra na lista de descadastro para nunca mais ser abordado. Não pode ser desfeito."))
            await vm.AnonymizeSelectedAsync();
    }

    private async void OnDeleteChecked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LeadsViewModel vm) return;
        if (vm.CheckedCount == 0) { vm.Error = "Marque pelo menos um lead na lista."; return; }
        if (await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this), "Excluir leads selecionados",
                $"Excluir definitivamente {vm.CheckedText} com mensagens, histórico e fatos?\n\nNúmeros descadastrados continuam na lista de bloqueio. Esta ação não pode ser desfeita."))
            await vm.DeleteCheckedAsync();
    }
}
