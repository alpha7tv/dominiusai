using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Desktop.Views;

/// <summary>Arrastar-e-soltar dos cards entre colunas; clique simples abre a conversa em popup.</summary>
public partial class KanbanView : UserControl
{
    private const string Format = "dominius/lead-id";

    /// <summary>Distância (px) que o ponteiro precisa andar para virar arraste; abaixo disso é clique.</summary>
    private const double DragThreshold = 6;
    private Guid? _pressedCard;
    private Point _pressedAt;
    private bool _dragging;

    public KanbanView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ChatPopupViewModel? _chat;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_chat is not null) _chat.MessagesReloaded -= ScrollChatToEnd;
        _chat = (DataContext as KanbanViewModel)?.Chat;
        if (_chat is not null) _chat.MessagesReloaded += ScrollChatToEnd;
    }

    private void ScrollChatToEnd() => Dispatcher.UIThread.Post(() => ChatHistory.ScrollToEnd(), DispatcherPriority.Background);

    /// <summary>Card sob o ponteiro (null se o clique foi num botão do card ou fora de um card).</summary>
    public static Guid? CardIdOf(object? source)
    {
        if ((source as Control)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return null; // botões dentro do card
        var card = (source as Control)?.FindAncestorOfType<Border>(includeSelf: true);
        while (card is not null && card.Name != "Card") card = card.FindAncestorOfType<Border>();
        return card?.Tag as Guid?;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedCard = null; _dragging = false;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ChatOverlay.IsVisible) return; // popup aberto: cards atrás não respondem
        if (CardIdOf(e.Source) is not { } id) return;
        _pressedCard = id;
        _pressedAt = e.GetPosition(this);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedCard is not { } id || _dragging) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _pressedCard = null; return; }
        var d = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(d.X) < DragThreshold && Math.Abs(d.Y) < DragThreshold) return;
        _dragging = true;
#pragma warning disable CS0618 // DataObject continua suportado no Avalonia 11.3
        var data = new DataObject();
        data.Set(Format, id.ToString());
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        finally { _pressedCard = null; _dragging = false; }
#pragma warning restore CS0618
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedCard is not { } id || _dragging) return;
        _pressedCard = null;
        if (e.InitialPressMouseButton != MouseButton.Left || CardIdOf(e.Source) != id) return;
        if (DataContext is KanbanViewModel vm) await vm.OpenChatAsync(id);
        if (!ChatDraft.Focus()) CloseChat.Focus(); // Esc fecha mesmo antes de assumir o atendimento
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as KanbanViewModel)?.Chat.Close();
        e.Handled = true;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not KanbanViewModel { Chat: { IsOpen: true } chat }) return;
        if (e.Key == Key.Escape) { chat.Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && e.Source is Control c && c.FindAncestorOfType<TextBox>(includeSelf: true) == ChatDraft)
        {
            e.Handled = true;
            await chat.Send();
        }
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
        if (await ConfirmClosingIfNeeded(this, stage == KanbanStage.NegocioFechado) is not { } confirmed) return;
        await vm.MoveAsync(id, stage, confirmed);
    }

    /// <summary>Retorna false se não precisa confirmar, true se confirmado, null se o usuário desistiu.</summary>
    public static async Task<bool?> ConfirmClosingIfNeeded(Control owner, bool closing)
    {
        if (!closing) return false;
        var ok = await ConfirmDialog.ShowAsync(TopLevel.GetTopLevel(owner) as Window, "Confirmar fechamento",
            "Confirma que a contratação foi validada comercialmente (pagamento confirmado ou aceite formal)?\n\nInteresse demonstrado não é suficiente para marcar 'Negócio fechado'.");
        return ok ? true : null;
    }

    private async void OnAcceptSuggestion(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KanbanViewModel vm || (sender as Control)?.Tag is not LeadCardVm card) return;
        if (await ConfirmClosingIfNeeded(this, card.Model.SuggestedStage == KanbanStage.NegocioFechado) is not { } confirmed) return;
        await vm.AcceptSuggestionAsync(card.Id, confirmed);
    }

    private async void OnRejectSuggestion(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KanbanViewModel vm && (sender as Control)?.Tag is LeadCardVm card) await vm.RejectSuggestionAsync(card.Id);
    }
}
