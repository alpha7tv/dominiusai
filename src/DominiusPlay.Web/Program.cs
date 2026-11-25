using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using DominiusPlay.Desktop;
using DominiusPlay.Desktop.Services;

[assembly: SupportedOSPlatform("browser")]

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = pt;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = pt;
        System.Globalization.CultureInfo.CurrentCulture = pt;
        System.Globalization.CultureInfo.CurrentUICulture = pt;

        // A página é servida pelo próprio servidor do CRM: a API fica no mesmo endereço
        if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var page))
            LocalSettings.ForcedServerUrl = page.GetLeftPart(UriPartial.Authority);

        return AppBuilder.Configure<App>()
            .WithInterFont()
            .With(new FontManagerOptions
            {
                // O navegador não tem fontes do sistema: símbolos (●, ✓, ⚠, setas) vêm da DejaVu embutida
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily("avares://DominiusPlayCRM.Web/Assets/Fonts#DejaVu Sans") }],
            })
            .StartBrowserAppAsync("out");
    }
}
