using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace DominiusPlay.Desktop.Views;

/// <summary>Diálogo simples de confirmação (Sim/Não). No Windows abre uma janela; no navegador, uma camada sobre a página.</summary>
public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(TopLevel? owner, string title, string message)
    {
        if (owner is null) return false;
        var tcs = new TaskCompletionSource<bool>();
        var yes = new Button { Content = "Confirmar", Classes = { "primary" }, Padding = new Thickness(16, 6) };
        var no = new Button { Content = "Cancelar", Padding = new Thickness(16, 6) };
        var body = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
            },
        };

        if (owner is Window window)
        {
            var dlg = new Window
            {
                Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body,
            };
            yes.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
            no.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(window);
            tcs.TrySetResult(false);
            return await tcs.Task;
        }

        // Navegador: não existem janelas, então o diálogo é desenhado por cima da tela atual
        if (OverlayLayer.GetOverlayLayer(owner) is not { } layer) return false;
        var card = new Border
        {
            Width = 460, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = Application.Current?.FindResource("PanelBrush") as IBrush ?? Brushes.DimGray,
            BorderBrush = Application.Current?.FindResource("BorderBrush") as IBrush ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(20, 18, 20, 0) }, body },
            },
        };
        // A camada de sobreposição é um Canvas: o fundo escuro precisa do tamanho da página para centralizar o cartão
        var shade = new Grid { Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), Width = owner.ClientSize.Width, Height = owner.ClientSize.Height, Children = { card } };
        void Resize(object? _, SizeChangedEventArgs e) { shade.Width = e.NewSize.Width; shade.Height = e.NewSize.Height; }
        owner.SizeChanged += Resize;
        yes.Click += (_, _) => tcs.TrySetResult(true);
        no.Click += (_, _) => tcs.TrySetResult(false);
        layer.Children.Add(shade);
        try { return await tcs.Task; }
        finally { owner.SizeChanged -= Resize; layer.Children.Remove(shade); }
    }
}
