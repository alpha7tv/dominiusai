using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using DominiusPlay.Desktop;
using DominiusPlay.Desktop.Services;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Desktop.Views;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using Microsoft.EntityFrameworkCore;

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
    public async Task Tela_de_agentes_de_atendimento_e_Testar_Agente()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        using var f = new CrmFactory();
        var http = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(http);
        (await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(http, "/api/ai/settings",
            new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
        var api = new ApiClient("http://localhost", f.Server.CreateHandler());
        await api.LoginAsync("carlos", "SenhaForte123");
        var main = new MainWindowViewModel();
        var win = new MainWindow { DataContext = main, Width = 1440, Height = 880 };
        win.Show();
        await main.StartSessionAsync(new AppSession(api, new Realtime()));
        await main.NavigateCommand.ExecuteAsync("atendimento");
        await Pump(600);
        var vm = Assert.IsType<AgentsViewModel>(main.Current);
        Assert.Null(vm.Error);
        Assert.Equal("Atendimento IPTV (padrão)", Assert.Single(vm.Agents).Name);
        Assert.Equal(6, vm.Questions.Count);
        Assert.Equal(11, vm.Intents.Count);
        Shot(win, "15-agentes-atendimento");

        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("registrar_resposta_qualificacao", """{"chave":"ja_revende","resposta":"já revende há 2 anos","atende_criterio":true}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Que bom! Hoje você tem quantos clientes ativos, mais ou menos?"));
        vm.TestInput = "Oi, já revendo IPTV faz 2 anos";
        await vm.SendTestCommand.ExecuteAsync(null);
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Entendo, travamento derruba a renovação. Você usa qual painel hoje?"));
        vm.TestInput = "uns 120, mas o servidor atual trava muito";
        await vm.SendTestCommand.ExecuteAsync(null);
        vm.TestInput = "quero falar com uma pessoa";
        await vm.SendTestCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(vm.Error);
        Assert.Equal(7, vm.TestLines.Count); // 3 do cliente, 3 do agente, 1 ficha
        Assert.Contains("FICHA DA TRANSFERÊNCIA", vm.TestLines.Last().Text);
        Assert.Equal("Qualificação: 15/100", vm.TestScore);
        Assert.Equal(0, await f.WithDb(db => db.Messages.CountAsync()));
        Shot(win, "15b-testar-agente");
    }

    [AvaloniaFact]
    public async Task Tela_de_automacoes_valida_e_testa_sem_enviar()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        using var f = new CrmFactory();
        await f.AdminAsync();
        var api = new ApiClient("http://localhost", f.Server.CreateHandler());
        await api.LoginAsync("carlos", "SenhaForte123");
        var main = new MainWindowViewModel();
        var win = new MainWindow { DataContext = main, Width = 1440, Height = 880 };
        win.Show();
        await main.StartSessionAsync(new AppSession(api, new Realtime()));
        await main.NavigateCommand.ExecuteAsync("automacoes");
        await Pump(500);
        var vm = Assert.IsType<AutomationsViewModel>(main.Current);
        Assert.Null(vm.Error);
        Assert.Equal(6, vm.Blocks.Count); // exemplo inicial
        await vm.TestCommand.ExecuteAsync(null);
        await Pump();
        Assert.Empty(vm.Problems);
        Assert.Contains("Adicionar tag: quer-preco", vm.Actions);
        Shot(win, "17-automacoes");

        // cria um ciclo sem espera: validação recusa
        vm.Blocks.Single(b => b.Id == "5").Next = "3";
        await vm.TestCommand.ExecuteAsync(null);
        await Pump();
        Assert.Contains(vm.Problems, p => p.Contains("circular"));
        vm.Blocks.Single(b => b.Id == "5").Next = "9";
        vm.Name = "Pedido de preço"; vm.Active = true;
        await vm.SaveCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(vm.Error);
        Assert.Single(vm.Flows);
        Assert.True(vm.Flows[0].Active);
        Assert.Equal(0, await f.WithDb(db => db.OutboundQueue.CountAsync()));
    }

    [AvaloniaFact]
    public async Task Tela_de_agentes_de_disparo_e_campanha()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        using var f = new CrmFactory();
        var http = await f.AdminAsync();
        await f.ConfigureEvolutionAsync(http);
        (await System.Net.Http.Json.HttpClientJsonExtensions.PutAsJsonAsync(http, "/api/ai/settings",
            new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
        (await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(http, "/api/agents", new AgentUpsert(AgentKind.Disparo, "Prospecção revenda", "Lia",
            "Abordagem de revendedores", "Abrir conversa", "Dominius Play", "Consultiva", "Curto e cordial", "", "", "", "", "", "", "", AgentStatus.Ativo, true,
            "09:00", "20:00", "1,2,3,4,5", "America/Sao_Paulo", "", "", 40, 600))).EnsureSuccessStatusCode();
        var api = new ApiClient("http://localhost", f.Server.CreateHandler());
        await api.LoginAsync("carlos", "SenhaForte123");
        var main = new MainWindowViewModel();
        var win = new MainWindow { DataContext = main, Width = 1440, Height = 880 };
        win.Show();
        await main.StartSessionAsync(new AppSession(api, new Realtime()));
        await main.NavigateCommand.ExecuteAsync("disparo");
        await Pump(600);
        var vm = Assert.IsType<AgentsViewModel>(main.Current);
        Assert.Null(vm.Error);
        Assert.False(vm.IsAttendance);
        Assert.Equal("Prospecção revenda", Assert.Single(vm.Agents).Name);
        Assert.Single(vm.AttendanceAgents);
        Assert.Equal(40, vm.DailyLimit);
        Assert.Equal(10, vm.MinIntervalMinutes);

        vm.TestInput = "não tenho interesse, me tira da lista";
        await vm.SendTestCommand.ExecuteAsync(null);
        await Pump();
        Assert.Null(vm.Error);
        Assert.Contains("descadastro", vm.TestLines.Last().Detail ?? "");
        Assert.Equal("Conversa encerrada pelo agente.", vm.TestStatus);
        Assert.Equal(0, await f.WithDb(db => db.OptOuts.CountAsync())); // simulação não grava nada
        Shot(win, "16-agentes-disparo");

        await main.NavigateCommand.ExecuteAsync("campanhas");
        await Pump(600);
        var camp = Assert.IsType<CampaignsViewModel>(main.Current);
        Assert.Null(camp.Error);
        Assert.Single(camp.DispatchAgents);
        Assert.Equal("Prospecção revenda", camp.DispatchAgent?.Name);
        Shot(win, "16b-campanha-disparo");
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
        Assert.Equal(11, kanban.Columns.Count); // "Em dúvidas" aparece porque tem card
        Assert.Equal(["Primeiro contato", "Abordagem realizada", "Respondeu", "Em atendimento", "Qualificado", "Em fase de teste", "Proposta enviada",
            "Em negociação", "Negócio fechado", "Não tem interesse", "Em dúvidas"], kanban.Columns.Select(c => c.Title));
        Shot(win, "02-kanban");

        // clique no card abre a conversa do WhatsApp em popup, sem sair do Kanban
        var joaoCard = kanban.Columns.SelectMany(c => c.Cards).First(c => c.Name == "João (exemplo)");
        var cardBorder = win.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Card" && b.Tag is Guid g && g == joaoCard.Id);
        cardBorder.BringIntoView(); // quadro com rolagem horizontal (11 etapas)
        await Pump(200);
        var at = cardBorder.TranslatePoint(new Point(20, 20), win)!.Value;
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        await Pump(500);
        Assert.True(kanban.Chat.IsOpen);
        Assert.Same(kanban, main.Current);
        Assert.Equal(joaoCard.Id, kanban.Chat.LeadId);
        Assert.Equal(7, kanban.Chat.Messages.Count);
        Assert.False(kanban.Chat.IsHuman);
        Assert.False(kanban.Chat.SendCommand.CanExecute(null) && kanban.Chat.IsHuman);
        Shot(win, "02b-kanban-conversa-popup");
        await kanban.Chat.TakeOverCommand.ExecuteAsync(null);
        await Pump(400);
        Assert.True(kanban.Chat.IsHuman);
        kanban.Chat.Draft = "Oi! Vou te enviar agora os requisitos do apontamento.";
        await kanban.Chat.SendCommand.ExecuteAsync(null);
        await Pump(400);
        Assert.Null(kanban.Chat.Error);
        Assert.Equal("", kanban.Chat.Draft);
        Assert.Contains("fila de envio", kanban.Chat.Info);
        Assert.Equal(1, await f.WithDb(db => Task.FromResult(db.OutboundQueue.Count(q => q.LeadId == joaoCard.Id && q.Text.StartsWith("Oi! Vou te enviar agora")))));
        Shot(win, "02c-kanban-conversa-popup-atendimento");
        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await Pump();
        Assert.False(kanban.Chat.IsOpen);
        await kanban.Chat.ReturnToAiCommand.ExecuteAsync(null); // devolve para o restante do fluxo seguir igual
        await Pump(300);

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
        Assert.Equal(7, conv.Messages.Count(m => !m.IsSystem)); // + avisos de sistema de assumir/devolver feitos no popup
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
        Assert.Equal(8, Assert.IsType<PlansViewModel>(main.Current).Plans.Count);
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
        var kbPage = Assert.IsType<KbViewModel>(main.Current);
        Assert.Contains("75 perguntas", kbPage.Summary);
        Shot(win, "08-base-dominius-play");
        kbPage.Tab = 2; await Pump();
        Assert.Equal("supertv.tech@gmail.com", kbPage.PixKey);
        Shot(win, "08b-base-comercial");
        kbPage.TestQuestion = "achei caro, meu amigo também quer trabalhar";
        await kbPage.TestCommand.ExecuteAsync(null); kbPage.Tab = 1; await Pump();
        Assert.NotEmpty(kbPage.TestResults);
        Shot(win, "08c-base-testar");
        await main.NavigateCommand.ExecuteAsync("laboratorio");
        await Pump();
        var lab = Assert.IsType<LabViewModel>(main.Current);
        lab.SelectedScenario = lab.Scenarios.First(x => x.Key == "amigo");
        await lab.SimulateCommand.ExecuteAsync(null); await Pump();
        Assert.Equal(2, lab.Steps.Count);
        Shot(win, "08d-laboratorio-atendimento");
        await main.NavigateCommand.ExecuteAsync("conhecimento");
        kbPage.Tab = 3; await Pump();
        var kb = kbPage.Legacy;
        kb.Selected = kb.Articles.First(a => a.Title == "Canais e horário");
        await kb.ApproveCommand.ExecuteAsync(null);
        await Pump();
        Assert.Equal(ApprovalStatus.Aprovado, kb.Selected!.Approval);
        Shot(win, "08e-base-artigos-antigos");

        await main.NavigateCommand.ExecuteAsync("config");
        await Pump();
        var ai = Assert.IsType<AiSettingsViewModel>(main.Current);
        ai.Enabled = true; ai.ApiKey = "sk-ant-teste"; ai.AssistantName = "Lia";
        Assert.Contains("ROTEIRO DE DIAGNÓSTICO", ai.SystemPrompt); // editor já vem com o prompt padrão
        Assert.False(ai.IsCustomSystemPrompt);
        ai.Instructions = "TOM: simpático e direto, trate por você.\nAPRESENTAÇÃO: diga que é a Lia, assistente virtual da Dominius Play.\nOFERTA PRINCIPAL: para quem está começando, Mensalista 50.\nOBJEÇÃO \"está caro\": explique o custo por cliente ativo.\nTRANSFERIR PARA HUMANO: quando pedir teste, pagamento ou proposta formal.\nHORÁRIO DA EQUIPE: 9h às 21h.";
        await ai.SaveCommand.ExecuteAsync(null);
        Assert.True(ai.HasApiKey); Assert.Equal("", ai.ApiKey);
        Assert.Contains("caracteres", ai.ScriptInfo);
        Assert.Contains("OFERTA PRINCIPAL", (await f.WithDb(db => db.Settings.AsNoTracking().SingleAsync(x => x.Key == DominiusPlay.Infrastructure.Security.SettingKeys.AiInstructions))).Value);
        f.Ai.Script.Enqueue(_ => FakeAi.Tools(("atualizar_qualificacao", """{"experiencia":"JaTrabalha","painel":"Sigma","clientes_ativos":350,"dor_principal":"QuedasServidor"}""")));
        f.Ai.Script.Enqueue(_ => FakeAi.Text("Entendi! Essas quedas acontecem mais nos horários de pico ou o dia todo?"));
        await ai.TestCommand.ExecuteAsync(null);
        await Pump();
        Assert.Contains("horários de pico", ai.TestReply);
        Shot(win, "09-configuracoes-ia");
        var promptExp = win.GetVisualDescendants().OfType<Expander>().First(e => (e.Header as string)?.StartsWith("Prompt de sistema") == true);
        promptExp.IsExpanded = true;
        await Pump();
        promptExp.BringIntoView();
        await Pump();
        Shot(win, "09b-configuracoes-ia-prompt-sistema");

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

        // Relatório de mensagens enviadas: envia a campanha e simula entregas, leituras e uma falha
        var calls = 0;
        f.Evolution.Behavior = _ => ++calls == 3
            ? new DominiusPlay.Infrastructure.Evolution.EvolutionSendResult(false, null, null, "Número não existe no WhatsApp", 400)
            : new DominiusPlay.Infrastructure.Evolution.EvolutionSendResult(true, "WA" + Guid.NewGuid().ToString("N")[..10], "SERVER_ACK", null, 201);
        for (var i = 0; i < 5; i++) { await f.RunQueueAsync(); f.Clock.Advance(TimeSpan.FromMinutes(6)); }
        await f.WithDb(async db =>
        {
            var sent = await db.Messages.Where(m => m.CampaignId != null && m.Status == MessageStatus.EnviadaServidor).ToListAsync();
            for (var i = 0; i < sent.Count; i++)
            {
                sent[i].Status = i < 2 ? MessageStatus.Lida : i == 2 ? MessageStatus.Entregue : MessageStatus.EnviadaServidor;
                sent[i].Timestamp = DateTimeOffset.UtcNow.AddDays(-i); // espalha pelos últimos dias
            }
            var failed = await db.Messages.Where(m => m.Status == MessageStatus.Falhou).ToListAsync();
            foreach (var m in failed) m.Timestamp = DateTimeOffset.UtcNow.AddDays(-1);
            return await db.SaveChangesAsync();
        });
        rep.Tab = 1;
        await rep.RefreshCommand.ExecuteAsync(null);
        await Pump(400);
        Assert.Null(rep.Sent.Error);
        Assert.True(rep.Sent.Totals!.Model.Total >= 5);
        Assert.Equal(1, rep.Sent.Totals.Model.Failed);
        Assert.Contains(rep.Sent.ByCampaign, r => r.Label == "Revendedores novembro" && r.Model.Total == 5);
        Assert.Contains(rep.Sent.ByAuthor, r => r.Label == "IA (assistente virtual)");
        Assert.Equal(31, rep.Sent.Chart.Count);
        win.Height = 1750; await Pump(400); // captura a aba inteira
        Shot(win, "14-relatorio-mensagens-enviadas");
        win.Height = 880; await Pump();
        rep.Sent.Campaign = rep.Sent.CampaignOptions.First(o => o.Label == "Revendedores novembro");
        await Pump(400);
        Assert.Equal(5, rep.Sent.Totals!.Model.Total);
        Assert.Equal("Revendedores novembro", rep.Sent.Campaign!.Label);
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
