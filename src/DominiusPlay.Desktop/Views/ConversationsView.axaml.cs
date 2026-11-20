using Avalonia.Controls;
using Avalonia.Interactivity;
using DominiusPlay.Desktop.ViewModels;

namespace DominiusPlay.Desktop.Views;

public partial class ConversationsView : UserControl
{
    public ConversationsView() => InitializeComponent();

    private async void OnAcceptSuggestion(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConversationsViewModel vm) return;
        await vm.AcceptSuggestionAsync(await KanbanView.ConfirmClosingIfNeeded(this, vm.SuggestsClosing) ?? false);
    }
}
