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
        var card = kanban.Columns[0].Cards.First(c => c.Name == "Contato 001");
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
        Assert.Equal(350m, conv.QClients);
        conv.QClients = 400; conv.QNextAction = "Enviar requisitos e agendar teste";
        await conv.SaveQualificationCommand.ExecuteAsync(null);
        await Pump(400);
        Assert.Null(conv.Error);
        Assert.Equal(400, conv.Detail!.ActiveClients);
        Shot(win, "03-conversas");

        await conv.TakeOverCommand.ExecuteAsync(null);
        await Pump(400);
        Assert.Equal(ConversationMode.AtendimentoHumano, conv.Detail!.Mode);
        Assert.True(conv.CanReturn);
        Shot(win, "04-conversa-atendimento-humano");

        await main.NavigateCommand.ExecuteAsync("leads");
        await Pump();
        var leads = Assert.IsType<LeadsViewModel>(main.Current);
        // lista só com números, sem nomes
        leads.PasteText = "(21) 99876-5432\n123\n11 98888-7766\n+55 31 99777-6655";
        leads.ImportConsentSource = "Pediram informações no Instagram";
        await leads.ImportPasteCommand.ExecuteAsync(null);
        await Pump();
        Assert.Contains("3 novos", leads.ImportSummary);
        Assert.Contains("1 inválidos", leads.ImportSummary);
        Assert.Contains("3 com consentimento", leads.ImportSummary);
        Assert.Contains(leads.Leads, l => l.Name == "+55 (21) 99876-5432");
        leads.NewPhone = "14 99666-5544"; // cadastro manual sem nome
        await leads.AddManualCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(leads.Error);
        Assert.Contains(leads.Leads, l => l.Name == "+55 (14) 99666-5544");
        Shot(win, "05-leads-importacao");
        // seleção em lote: marcar todos, desmarcar um e autorizar os demais
        var pendentes = leads.Leads.Count(l => l.Model.Consent == ConsentStatus.Pendente);
        leads.AllChecked = true;
        Assert.Equal(leads.Leads.Count, leads.CheckedCount);
        leads.Leads[0].IsChecked = false;
        Assert.Null(leads.AllChecked);
        Assert.True(leads.HasChecked);
        leads.AllChecked = true;
        leads.BulkConsentSource = "Pediram informações nos grupos";
        await Pump();
        Shot(win, "05b-leads-selecao-em-lote");
        await leads.AuthorizeCheckedCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(leads.Error);
        Assert.Contains($"{pendentes} leads autorizados", leads.Info);
        Assert.DoesNotContain(leads.Leads, l => l.Model.Consent == ConsentStatus.Pendente);
        Assert.False(leads.HasChecked);

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
        var kb = Assert.IsType<KnowledgeViewModel>(main.Current);
        kb.Selected = kb.Articles.First(a => a.Title == "Canais e horário");
        await kb.ApproveCommand.ExecuteAsync(null);
        await Pump();
        Assert.Equal(ApprovalStatus.Aprovado, kb.Selected!.Approval);
        Shot(win, "08-base-conhecimento");

        await main.NavigateCommand.ExecuteAsync("config");
        await Pump();
        var ai = Assert.IsType<AiSettingsViewModel>(main.Current);
        ai.Enabled = true; ai.ApiKey = "sk-ant-teste"; ai.AssistantName = "Lia";
        await ai.SaveCommand.ExecuteAsync(null);
        Assert.True(ai.HasApiKey); Assert.Equal("", ai.ApiKey);
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("atualizar_qualificacao", """{"experiencia":"JaTrabalha","painel":"Sigma","clientes_ativos":350,"dor_principal":"QuedasServidor"}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Entendi! Essas quedas acontecem mais nos horários de pico ou o dia todo?"));
        await ai.TestCommand.ExecuteAsync(null);
        await Pump();
        Assert.Contains("horários de pico", ai.TestReply);
        Shot(win, "09-configuracoes-ia");

        // Fase 7: equipe
        await main.NavigateCommand.ExecuteAsync("equipe");
        await Pump();
        var team = Assert.IsType<TeamViewModel>(main.Current);
        team.NewUser = "maria"; team.NewDisplay = "Maria Atendente"; team.NewPassword = "SenhaMaria1"; team.NewRole = 0;
        await team.CreateCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(team.Error);
        Assert.Equal(2, team.Users.Count);
        Shot(win, "10-equipe");

        // Fase 8: modelos e campanhas
        await main.NavigateCommand.ExecuteAsync("mensagens");
        await Pump();
        var tpl = Assert.IsType<TemplatesViewModel>(main.Current);
        tpl.Selected = tpl.Templates.First();
        await Pump(400);
        Assert.True(tpl.Preview?.StartsWith("Olá, Carlos, tudo bem? Sou Lia") == true, $"Preview=[{tpl.Preview}] Err=[{tpl.Error}]");
        Assert.Contains("Lia, assistente virtual", tpl.Preview);
        await tpl.ApproveCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(tpl.Error);
        Assert.Contains(tpl.Templates, t => t.IsApproved);
        tpl.Selected = tpl.Templates.First(t => t.IsApproved);
        await Pump(400);
        Shot(win, "11-mensagens");

        await main.NavigateCommand.ExecuteAsync("campanhas");
        await Pump();
        var camp = Assert.IsType<CampaignsViewModel>(main.Current);
        camp.NewCommand.Execute(null);
        camp.Name = "Revendedores novembro"; camp.StartTime = "00:00"; camp.EndTime = "23:59";
        camp.Sat = camp.Sun = true; camp.Objective = "Apresentar planos de revenda";
        await camp.SaveCommand.ExecuteAsync(null);
        await Pump();
        Assert.Equal(5, camp.Selected!.Model.Eligible); // 1 do exemplo + 3 importados com autorização + 1 manual autorizado em lote
        await camp.StartCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(camp.Error);
        Assert.Equal(CampaignStatus.Ativa, camp.Selected!.Model.Status);
        Assert.Equal(5, camp.Selected.Model.Queued);
        Shot(win, "12-campanhas");

        // Fase 9: relatórios
        await main.NavigateCommand.ExecuteAsync("relatorios");
        await Pump();
        var rep = Assert.IsType<ReportsViewModel>(main.Current);
        Assert.Null(rep.Error);
        Assert.Equal(8, rep.Sections.Count);
        Assert.Equal(5, rep.Kpis.Count);
        Shot(win, "13-relatorios");
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
            var c2 = L("Contato 002", "5511987650002", KanbanStage.EmFaseDeTeste, "MENSALISTA_100", 40, PainCategory.SuporteInsuficiente, "Avaliando painel", 90);
            c2.SuggestedStage = KanbanStage.EmNegociacao; c2.SuggestedStageReason = "pediu valores do Mensalista 100";
            var fresh = L("Contato 007", "5511987650007", KanbanStage.PrimeiroContato, null, null, PainCategory.Nenhuma, "", 0, ConversationMode.AguardandoContato);
            fresh.LastInteractionAt = null; fresh.LastMessagePreview = null; fresh.Tags = "revenda";
            db.Leads.AddRange(
                joao, fresh,
                L("Contato 001", "5511987650001", KanbanStage.PrimeiroContato, null, null, PainCategory.Nenhuma, "Aguardando diagnóstico", 40),
                c2,
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
