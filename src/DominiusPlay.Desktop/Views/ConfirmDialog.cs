using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DominiusPlay.Desktop.Views;

/// <summary>Diálogo simples de confirmação (Sim/Não).</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string title, string message)
    {
        var result = false;
        var yes = new Button { Content = "Confirmar", Classes = { "primary" }, Padding = new Thickness(16, 6) };
        var no = new Button { Content = "Cancelar", Padding = new Thickness(16, 6) };
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
                },
            },
        };
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        if (owner is null) return false;
        await dlg.ShowDialog(owner);
        return result;
    }
}
