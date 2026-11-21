using Avalonia.Controls;
using Avalonia.Interactivity;
using DominiusPlay.Desktop.ViewModels;

namespace DominiusPlay.Desktop.Views;

public partial class CampaignsView : UserControl
{
    public CampaignsView() => InitializeComponent();

    private async void OnStart(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignsViewModel vm || vm.Selected is null) return;
        var c = vm.Selected.Model;
        if (await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this), "Iniciar campanha",
                $"Iniciar \"{c.Name}\"?\n\n{c.Eligible} lead(s) elegível(is) entram na fila (máximo {c.MaxContacts}). Os envios são REAIS pelo WhatsApp, um a cada {c.MinIntervalSeconds / 60} minutos, somente das {c.StartTime} às {c.EndTime}. Você pode pausar ou cancelar a qualquer momento."))
            vm.StartCommand.Execute(null);
    }

    private async void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CampaignsViewModel vm || vm.Selected is null) return;
        if (await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this), "Cancelar campanha",
                "Cancelar a campanha? Todos os envios ainda na fila serão cancelados. Esta ação não pode ser desfeita."))
            await vm.CancelAsync();
    }
}
