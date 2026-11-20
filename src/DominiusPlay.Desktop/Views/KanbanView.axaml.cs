using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Desktop.Views;

/// <summary>Arrastar-e-soltar dos cards entre colunas.</summary>
public partial class KanbanView : UserControl
{
    private const string Format = "dominius/lead-id";

    public KanbanView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var card = (e.Source as Control)?.FindAncestorOfType<Border>(includeSelf: true);
        while (card is not null && card.Name != "Card") card = card.FindAncestorOfType<Border>();
        if (card?.Tag is not Guid id) return;
#pragma warning disable CS0618 // DataObject continua suportado no Avalonia 11.3
        var data = new DataObject();
        data.Set(Format, id.ToString());
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
#pragma warning restore CS0618
    }

    private static Border? ColumnOf(object? source)
    {
        var b = (source as Control)?.FindAncestorOfType<Border>(includeSelf: true);
        while (b is not null && b.Name != "Column") b = b.FindAncestorOfType<Border>();
        return b;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        e.DragEffects = e.Data.Contains(Format) && ColumnOf(e.Source) is not null ? DragDropEffects.Move : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        if (DataContext is not KanbanViewModel vm || ColumnOf(e.Source)?.Tag is not KanbanStage stage || e.Data.Get(Format) is not string raw) return;
#pragma warning restore CS0618
        var id = Guid.Parse(raw);
        var confirmed = false;
        if (stage == KanbanStage.NegocioFechado)
        {
            confirmed = await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(this) as Window,
                "Confirmar fechamento",
                "Confirma que a contratação foi validada comercialmente (pagamento confirmado ou aceite formal)?\n\nInteresse demonstrado não é suficiente para marcar 'Negócio fechado'.");
            if (!confirmed) return;
        }
        await vm.MoveAsync(id, stage, confirmed);
    }
}
