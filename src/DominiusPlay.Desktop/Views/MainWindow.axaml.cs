using Avalonia.Controls;
using Avalonia.VisualTree;
using DominiusPlay.Desktop.ViewModels;

namespace DominiusPlay.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
    }

    private void Hook()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.ActiveKey)) UpdateActive(vm.ActiveKey);
        };
    }

    /// <summary>Destaca o item ativo do menu lateral.</summary>
    private void UpdateActive(string key)
    {
        foreach (var b in NavList.GetVisualDescendants().OfType<Button>())
            b.Classes.Set("active", (b.CommandParameter as string) == key);
    }
}
