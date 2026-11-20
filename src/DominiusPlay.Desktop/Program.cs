using Avalonia;

namespace DominiusPlay.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = pt;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = pt;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
