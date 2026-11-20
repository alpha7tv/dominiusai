using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DominiusPlay.Desktop;
using DominiusPlay.Desktop.Services;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Desktop.Views;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

[assembly: AvaloniaTestApplication(typeof(DominiusPlay.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DominiusPlay.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Teste de ponta a ponta do aplicativo Windows: servidor real (em memória) + telas reais renderizadas
/// em modo headless. Gera capturas em artifacts/screenshots para revisão visual.
/// </summary>
public class UiTests
{
    private static readonly string OutDir = Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task Pump(int ms = 300)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Directory.CreateDirectory(OutDir);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(OutDir, name + ".png"));
    }

    [AvaloniaFact]
    public async Task Fluxo_completo_das_telas()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        using var f = new CrmFactory();
        var http = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(http);
        await SeedAsync(f, http);

        var api = new ApiClient("http://localhost", f.Server.CreateHandler());
        await api.LoginAsync("carlos", "SenhaForte123");
        var main = new MainWindowViewModel();
        var win = new MainWindow { DataContext = main, Width = 1440, Height = 880 };
        win.Show();
        await main.StartSessionAsync(new AppSession(api, new Realtime()));
        await Pump();
        Assert.True(main.IsLoggedIn);
        Assert.IsType<DashboardViewModel>(main.Current);
        Assert.Null(main.Current.Error);
        Shot(win, "01-dashboard");

        await main.NavigateCommand.ExecuteAsync("kanban");
        await Pump();
        var kanban = Assert.IsType<KanbanViewModel>(main.Current);
        Assert.Equal(6, kanban.Columns.Count);
        Assert.Equal(["Primeiro contato", "Em fase de teste", "Em negociação", "Negócio fechado", "Em dúvidas", "Não tem interesse"], kanban.Columns.Select(c => c.Title));
        Shot(win, "02-kanban");

        // movimentação via o mesmo método usado pelo arrastar-e-soltar
        var card = kanban.Columns[0].Cards.First();
        await kanban.MoveAsync(card.Id, KanbanStage.EmDuvidas);
        await Pump();
        Assert.Contains(kanban.Columns.Single(c => c.Stage == KanbanStage.EmDuvidas).Cards, c => c.Id == card.Id);
        await kanban.MoveAsync(card.Id, KanbanStage.NegocioFechado); // sem confirmação → recusado
        await Pump();
        Assert.NotNull(kanban.Error);

        await main.NavigateCommand.ExecuteAsync("conversas");
        await Pump();
        var conv = Assert.IsType<ConversationsViewModel>(main.Current);
        conv.Selected = conv.Contacts.First(c => c.Name == "João (exemplo)");
        await Pump(500);
        Assert.Equal(7, conv.Messages.Count);
        Assert.Contains("Apontamento Sigma 600", conv.RecommendationText);
        Shot(win, "03-conversas");

        await conv.TakeOverCommand.ExecuteAsync(null);
        await Pump(400);
        Assert.Equal(ConversationMode.AtendimentoHumano, conv.Detail!.Mode);
        Assert.True(conv.CanReturn);
        Shot(win, "04-conversa-atendimento-humano");

        await main.NavigateCommand.ExecuteAsync("leads");
        await Pump();
        var leads = Assert.IsType<LeadsViewModel>(main.Current);
        leads.PasteText = "Ana Souza - (21) 99876-5432\nNúmero ruim 123\nMaria;(11) 98888-7766";
        await leads.ImportPasteCommand.ExecuteAsync(null);
        await Pump();
        Assert.Contains("2 novos", leads.ImportSummary);
        Assert.Contains("1 inválidos", leads.ImportSummary);
        Shot(win, "05-leads-importacao");

        await main.NavigateCommand.ExecuteAsync("planos");
        await Pump();
        Assert.Equal(5, Assert.IsType<PlansViewModel>(main.Current).Plans.Count);
        Shot(win, "06-planos");

        await main.NavigateCommand.ExecuteAsync("integracoes");
        await Pump();
        var integ = Assert.IsType<IntegrationsViewModel>(main.Current);
        Assert.Equal("https://evo.exemplo.com", integ.BaseUrl);
        Assert.True(integ.HasApiKey);
        await integ.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal("● WhatsApp conectado", integ.StatusText);
        await Pump();
        Shot(win, "07-integracoes");

        await main.NavigateCommand.ExecuteAsync("conhecimento");
        await Pump();
        Shot(win, "08-base-conhecimento");
        win.Close();
    }

    [AvaloniaFact]
    public async Task Tela_de_login_e_primeiro_acesso()
    {
        var main = new MainWindowViewModel();
        var win = new MainWindow { DataContext = main, Width = 1200, Height = 760 };
        win.Show();
        await Pump();
        Assert.IsType<LoginViewModel>(main.Current);
        Assert.False(main.IsLoggedIn);
        Shot(win, "00-login");
        win.Close();
    }

    private static async Task SeedAsync(CrmFactory f, HttpClient http)
    {
        await f.WithDb(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            Lead L(string name, string phone, KanbanStage st, string? plan, int? clients, PainCategory pain, string last, int minsAgo, ConversationMode mode = ConversationMode.Ia, Priority pr = Priority.Normal) =>
                new() { Name = name, Phone = phone, Stage = st, InterestPlanCode = plan, ActiveClients = clients, MainPain = pain, LastMessagePreview = last,
                        LastInteractionAt = now.AddMinutes(-minsAgo), Mode = mode, Priority = pr, ConsentStatus = ConsentStatus.Valido, ConsentSource = "Formulário do site" };
            var joao = L("João (exemplo)", "5514988159045", KanbanStage.EmNegociacao, "APONTAMENTO_SIGMA_600", 350, PainCategory.QuedasServidor, "Principalmente nos jogos.", 3, pr: Priority.Alta);
            joao.Experience = IptvExperience.JaTrabalha; joao.PanelUsed = "Painel Sigma"; joao.NextAction = "Enviar requisitos de apontamento";
            db.Leads.AddRange(
                joao,
                L("Contato 001", "5511987650001", KanbanStage.PrimeiroContato, null, null, PainCategory.Nenhuma, "Aguardando diagnóstico", 40),
                L("Contato 002", "5511987650002", KanbanStage.EmFaseDeTeste, "MENSALISTA_100", 40, PainCategory.SuporteInsuficiente, "Avaliando painel", 90),
                L("Contato 004", "5511987650004", KanbanStage.NegocioFechado, "MENSALISTA_100", 80, PainCategory.PrecosElevados, "Pagamento confirmado", 300, ConversationMode.Encerrado),
                L("Contato 005", "5511987650005", KanbanStage.EmDuvidas, "FRANQUIA_COMPLETA", 1200, PainCategory.FaltaMarcaPropria, "Como funciona o app personalizado?", 15, ConversationMode.AtendimentoHumano),
                L("Contato 006", "5511987650006", KanbanStage.NaoTemInteresse, null, null, PainCategory.Nenhuma, "Contato encerrado", 2000, ConversationMode.Encerrado));
            string[] conv =
            [
                "out|Olá! Sou a assistente virtual da Dominius Play. Você já trabalha com revenda de IPTV ou está começando agora?",
                "in|Já trabalho, tenho um Painel Sigma.",
                "out|Entendi! Hoje sua maior dificuldade está na estabilidade do servidor, no suporte ou no custo da operação?",
                "in|Meu servidor cai muito e tenho 350 clientes.",
                "out|Compreendo. Essas quedas acontecem principalmente nos horários de pico ou durante todo o dia?",
                "in|Principalmente nos jogos.",
                "out|Obrigado por explicar. Como você já utiliza Sigma e possui 350 clientes, podemos avaliar nosso apontamento de R$ 250 mensais, com DNS exclusiva e até 600 logins ativos. Gostaria de conhecer os requisitos para utilizar sua estrutura atual com nossos servidores?",
            ];
            for (var i = 0; i < conv.Length; i++)
            {
                var parts = conv[i].Split('|', 2);
                var outbound = parts[0] == "out";
                db.Messages.Add(new Message { LeadId = joao.Id, Text = parts[1], Timestamp = now.AddMinutes(-20 + i * 2), InstanceName = "dominius", ExternalId = "SEED" + i,
                    Direction = outbound ? MessageDirection.Saida : MessageDirection.Entrada, Author = outbound ? MessageAuthor.Ia : MessageAuthor.Contato,
                    Status = outbound ? MessageStatus.Lida : MessageStatus.Recebida });
            }
            return await db.SaveChangesAsync();
        });
    }
}
