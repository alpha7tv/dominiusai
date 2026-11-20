using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Desktop.ViewModels;

/// <summary>Card do lead pronto para exibição (Kanban, listas e conversas).</summary>
public class LeadCardVm(LeadCard c)
{
    public LeadCard Model { get; } = c;
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string Phone => Model.PhoneFormatted;
    public string Plan => Model.InterestPlanCode is null ? "Plano: a definir" : "Plano: " + Model.InterestPlanCode.Replace('_', ' ');
    public string Clients => Model.ActiveClients is null ? "" : $"{Model.ActiveClients} clientes";
    public string Pain => Model.MainPain == PainCategory.Nenhuma ? "" : "Dor: " + Ui.Pain(Model.MainPain);
    public string LastMessage => Model.LastMessagePreview ?? "Sem mensagens";
    public string Ago => Ui.Ago(Model.LastInteractionAt);
    public string Owner => Model.AssignedTo ?? "Sem responsável";
    public string Mode => Ui.Mode(Model.Mode);
    public bool IsHuman => Model.Mode == ConversationMode.AtendimentoHumano;
    public string Priority => Model.Priority switch { Domain.Enums.Priority.Alta => "Alta", Domain.Enums.Priority.Urgente => "Urgente", Domain.Enums.Priority.Baixa => "Baixa", _ => "Normal" };
    public string NextAction => Model.NextAction is null ? "" : "Próxima ação: " + Model.NextAction;
    public string Consent => Ui.Consent(Model.Consent);
    public string Stage => Ui.Stage(Model.Stage);
    public string Tags => Model.Tags;
    public bool HasSuggestion => Model.SuggestedStage is not null;
    public string SuggestionText => Model.SuggestedStage is { } st ? $"IA sugere: {Ui.Stage(st)}" + (Model.SuggestedStageReason is { Length: > 0 } r ? $" ({r})" : "") : "";
}

public record Stat(string Key, int Value);

public partial class DashboardViewModel(AppSession s) : ViewModelBase
{
    [ObservableProperty] private DashboardDto? _data;
    public ObservableCollection<Stat> Stages { get; } = new();
    public ObservableCollection<Stat> Pains { get; } = new();

    public override Task OnActivatedAsync() => Refresh();

    [RelayCommand]
    private Task Refresh() => Run(async () =>
    {
        Data = await s.Api.DashboardAsync();
        Stages.Clear(); foreach (var kv in Data.ByStage) Stages.Add(new(kv.Key, kv.Value));
        Pains.Clear(); foreach (var kv in Data.TopPains) Pains.Add(new(kv.Key, kv.Value));
    });
}

public partial class KanbanColumnVm : ObservableObject
{
    public KanbanStage Stage { get; init; }
    public string Title { get; init; } = "";
    public string Color { get; init; } = "#888";
    public ObservableCollection<LeadCardVm> Cards { get; } = new();
    [ObservableProperty] private int _count;
}

public partial class KanbanViewModel : ViewModelBase
{
    private readonly AppSession _s;
    public ObservableCollection<KanbanColumnVm> Columns { get; } = new();
    [ObservableProperty] private string _filter = "";

    public KanbanViewModel(AppSession s)
    {
        _s = s;
        s.Realtime.LeadChanged += id => Dispatcher.UIThread.Post(() => { _ = Refresh(); });
    }

    public override Task OnActivatedAsync() => Refresh();

    [RelayCommand]
    public Task Refresh() => Run(async () =>
    {
        var board = await _s.Api.KanbanAsync();
        Columns.Clear();
        foreach (var col in board)
        {
            var vm = new KanbanColumnVm { Stage = col.Stage, Title = col.Title, Color = Ui.StageColor(col.Stage) };
            foreach (var c in col.Cards.Where(c => Filter.Length == 0 || c.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) || c.Phone.Contains(Filter)))
                vm.Cards.Add(new LeadCardVm(c));
            vm.Count = vm.Cards.Count;
            Columns.Add(vm);
        }
    });

    partial void OnFilterChanged(string value) => _ = Refresh();

    /// <summary>Chamado pelo arrastar-e-soltar. "Negócio fechado" pede confirmação comercial explícita.</summary>
    public Task MoveAsync(Guid leadId, KanbanStage to, bool confirmed = false) => Run(async () =>
    {
        var r = await _s.Api.MoveAsync(leadId, to, null, confirmed);
        if (r.Applied) Info = r.Message; else Error = r.Message;
        await RefreshCore();
    });

    public Task AcceptSuggestionAsync(Guid leadId, bool confirmed) => Run(async () =>
    {
        var r = await _s.Api.AcceptSuggestionAsync(leadId, confirmed);
        if (r.Applied) Info = r.Message; else Error = r.Message;
        await RefreshCore();
    });

    public Task RejectSuggestionAsync(Guid leadId) => Run(async () =>
    {
        var r = await _s.Api.RejectSuggestionAsync(leadId);
        Info = r.Message;
        await RefreshCore();
    });

    private async Task RefreshCore()
    {
        var board = await _s.Api.KanbanAsync();
        Columns.Clear();
        foreach (var col in board)
        {
            var vm = new KanbanColumnVm { Stage = col.Stage, Title = col.Title, Color = Ui.StageColor(col.Stage) };
            foreach (var c in col.Cards) vm.Cards.Add(new LeadCardVm(c));
            vm.Count = vm.Cards.Count;
            Columns.Add(vm);
        }
    }
}

public partial class ConversationsViewModel : ViewModelBase
{
    private readonly AppSession _s;
    public ObservableCollection<LeadCardVm> Contacts { get; } = new();
    public ObservableCollection<MessageVm> Messages { get; } = new();
    [ObservableProperty] private LeadCardVm? _selected;
    [ObservableProperty] private LeadDetail? _detail;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _search = "";

    // Fase 6: qualificação editável pelo atendente
    public static string[] Experiences { get; } = ["Não informado", "Já trabalha com revenda", "Está começando"];
    public static string[] Priorities { get; } = ["Baixa", "Normal", "Alta", "Urgente"];
    public static PainCategory[] PainValues { get; } = Enum.GetValues<PainCategory>();
    public static string[] Pains { get; } = PainValues.Select(p => p == PainCategory.Nenhuma ? "Nenhuma identificada" : Ui.Pain(p)).ToArray();
    public ObservableCollection<string> PlanCodes { get; } = new() { "A definir" };
    public ObservableCollection<FactDto> Facts { get; } = new();
    public ObservableCollection<UserDto> Users { get; } = new();
    [ObservableProperty] private int _qExperience;
    [ObservableProperty] private string _qPanel = "";
    [ObservableProperty] private decimal? _qClients;
    [ObservableProperty] private int _qPain;
    [ObservableProperty] private string? _qPlan = "A definir";
    [ObservableProperty] private string _qInvestment = "";
    [ObservableProperty] private int _qPriority = 1;
    [ObservableProperty] private string _qNextAction = "";
    [ObservableProperty] private string _qSummary = "";
    [ObservableProperty] private string _newFactKey = "";
    [ObservableProperty] private string _newFactValue = "";
    [ObservableProperty] private UserDto? _assignee;
    public bool IsSupervisor => _s.Api.Session?.Role is UserRole.Supervisor or UserRole.Administrador;
    public bool HasSuggestion => Detail?.SuggestedStage is not null;
    public string SuggestionText => Detail?.SuggestedStage is { } st ? $"IA sugere mover para: {Ui.Stage(st)}" + (Detail.SuggestedStageReason is { Length: > 0 } r ? $" ({r})" : "") : "";
    public bool SuggestsClosing => Detail?.SuggestedStage == KanbanStage.NegocioFechado;

    public ConversationsViewModel(AppSession s)
    {
        _s = s;
        s.Realtime.MessageAdded += id => Dispatcher.UIThread.Post(async () =>
        {
            await LoadContacts();
            if (Selected?.Id == id) await LoadSelected();
        });
    }

    public bool CanTakeOver => Detail is not null && Detail.Mode != ConversationMode.AtendimentoHumano;
    public bool CanReturn => Detail is not null && Detail.Mode is ConversationMode.AtendimentoHumano or ConversationMode.Pausado;
    public string ModeText => Detail is null ? "" : Ui.Mode(Detail.Mode) + (Detail.AssignedTo is null ? "" : " · " + Detail.AssignedTo);
    public string ExperienceText => Detail?.Experience switch { IptvExperience.JaTrabalha => "Já trabalha com revenda", IptvExperience.Iniciante => "Está começando", _ => "Não informado" };
    public string PainText => Detail is null ? "" : Ui.Pain(Detail.MainPain);
    public string StageText => Detail is null ? "" : Ui.Stage(Detail.Stage);
    public string ConsentText => Detail is null ? "" : Ui.Consent(Detail.Consent) + (Detail.ConsentSource is null ? "" : $" ({Detail.ConsentSource})");
    public string RecommendationText => Detail?.Recommendation is not { } r ? "" :
        (r.PlanNames.Count > 0 ? string.Join("\n", r.PlanNames) : "Nenhum plano ainda") + "\n" + r.Rationale + (r.MissingInfo is null ? "" : $"\nFalta saber: {r.MissingInfo}");

    public override Task OnActivatedAsync() => Run(async () =>
    {
        if (PlanCodes.Count == 1)
            foreach (var code in (await _s.Api.PlansAsync()).Where(p => p.Approval == ApprovalStatus.Aprovado).Select(p => p.Code).Distinct()) PlanCodes.Add(code);
        if (IsSupervisor && Users.Count == 0)
            foreach (var u in (await _s.Api.UsersAsync()).Where(u => u.Active)) Users.Add(u);
        await LoadContacts();
    });

    private async Task LoadContacts()
    {
        var list = await _s.Api.ConversationsAsync(Search);
        var sel = Selected?.Id;
        Contacts.Clear();
        foreach (var c in list) Contacts.Add(new LeadCardVm(c));
        if (sel is not null) _selected = Contacts.FirstOrDefault(c => c.Id == sel);
        OnPropertyChanged(nameof(Selected));
    }

    partial void OnSearchChanged(string value) => _ = Run(LoadContacts);
    partial void OnSelectedChanged(LeadCardVm? value) { if (value is not null) _ = Run(LoadSelected); }

    private async Task LoadSelected()
    {
        if (Selected is null) return;
        Detail = await _s.Api.LeadAsync(Selected.Id);
        var msgs = await _s.Api.MessagesAsync(Selected.Id);
        Messages.Clear();
        foreach (var m in msgs) Messages.Add(new MessageVm(m));
        var d = Detail;
        QExperience = (int)d.Experience; QPanel = d.PanelUsed ?? ""; QClients = d.ActiveClients; QPain = Array.IndexOf(PainValues, d.MainPain);
        if (d.InterestPlanCode is { } pc && !PlanCodes.Contains(pc)) PlanCodes.Add(pc);
        QPlan = d.InterestPlanCode ?? "A definir"; QInvestment = d.InvestmentRange ?? ""; QPriority = (int)d.Priority;
        QNextAction = d.NextAction ?? ""; QSummary = d.ConversationSummary ?? "";
        Facts.Clear(); foreach (var f in d.Facts) Facts.Add(f);
        Assignee = Users.FirstOrDefault(u => u.Id == d.AssignedUserId);
        foreach (var p in new[] { nameof(HasSuggestion), nameof(SuggestionText), nameof(SuggestsClosing), nameof(CanTakeOver), nameof(CanReturn), nameof(ModeText), nameof(ExperienceText), nameof(PainText), nameof(StageText), nameof(ConsentText), nameof(RecommendationText) })
            OnPropertyChanged(p);
    }

    [RelayCommand] private Task TakeOver() => Act(id => _s.Api.TakeOverAsync(id));
    [RelayCommand] private Task ReturnToAi() => Act(id => _s.Api.ReturnToAiAsync(id));
    [RelayCommand] private Task Pause() => Act(id => _s.Api.PauseAsync(id, true));

    /// <summary>A IA sugere uma resposta para o atendente revisar; nada é enviado automaticamente.</summary>
    [RelayCommand]
    private Task Suggest() => Run(async () =>
    {
        if (Selected is null) return;
        var r = await _s.Api.SuggestAsync(Selected.Id);
        if (r.Ok && r.Reply is not null) { Draft = r.Reply; Info = "Sugestão da IA no campo de mensagem. Revise antes de enviar."; }
        else Error = r.Error ?? "A IA não gerou sugestão.";
    });

    private Task Act(Func<Guid, Task<OperationResult>> f) => Run(async () =>
    {
        if (Selected is null) return;
        var r = await f(Selected.Id);
        if (r.Ok) Info = r.Message; else Error = r.Message;
        await LoadSelected(); await LoadContacts();
    });

    [RelayCommand]
    private Task SaveQualification() => Run(async () =>
    {
        if (Selected is null) return;
        var r = await _s.Api.SaveQualificationAsync(Selected.Id, new QualificationUpdate((IptvExperience)QExperience, QPanel, QClients is null ? null : (int)QClients.Value,
            PainValues[Math.Max(0, QPain)], QPlan is null or "A definir" ? null : QPlan, QInvestment, (Priority)QPriority, QNextAction, QSummary));
        Info = r.Message; await LoadSelected();
    });

    [RelayCommand]
    private Task AddFact() => Run(async () =>
    {
        if (Selected is null) return;
        var r = await _s.Api.AddFactAsync(Selected.Id, new FactCreate(NewFactKey, NewFactValue));
        Info = r.Message; NewFactKey = ""; NewFactValue = ""; await LoadSelected();
    });

    /// <summary>Aceita a etapa sugerida pela IA. "Negócio fechado" chega aqui só depois da confirmação comercial na tela.</summary>
    public Task AcceptSuggestionAsync(bool confirmed) => Run(async () =>
    {
        if (Selected is null) return;
        var r = await _s.Api.AcceptSuggestionAsync(Selected.Id, confirmed);
        if (r.Applied) Info = r.Message; else Error = r.Message;
        await LoadSelected(); await LoadContacts();
    });

    [RelayCommand] private Task RejectSuggestion() => Act(id => _s.Api.RejectSuggestionAsync(id));
    [RelayCommand] private Task Assign() => Act(id => _s.Api.AssignAsync(id, Assignee?.Id));

    [RelayCommand]
    private Task Send() => Run(async () =>
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Draft)) return;
        var r = await _s.Api.SendAsync(Selected.Id, Draft);
        if (r.Queued) { Draft = ""; Info = "Mensagem na fila de envio."; } else Error = r.Reason;
    });
}

public class MessageVm(MessageDto m)
{
    public string Text { get; } = m.Text;
    public bool IsOutbound { get; } = m.Direction == MessageDirection.Saida && m.Author != MessageAuthor.Sistema;
    public bool IsSystem { get; } = m.Author == MessageAuthor.Sistema && m.Status != MessageStatus.Falhou;
    public bool IsInbound { get; } = m.Direction == MessageDirection.Entrada;
    public bool IsFailed { get; } = m.Status == MessageStatus.Falhou;
    public string Meta { get; } = $"{m.Timestamp.ToLocalTime():dd/MM HH:mm} · " + m.Author switch
    {
        MessageAuthor.Contato => "Contato",
        MessageAuthor.Ia => "Assistente virtual (IA)",
        MessageAuthor.Atendente => m.SentBy ?? "Atendente",
        MessageAuthor.ManualNoAparelho => "Enviada pelo aparelho",
        _ => "Sistema",
    } + (m.Direction == MessageDirection.Saida ? " · " + m.Status switch
    {
        MessageStatus.Lida => "lida", MessageStatus.Entregue => "entregue", MessageStatus.EnviadaServidor => "enviada",
        MessageStatus.Pendente => "pendente", MessageStatus.Falhou => "FALHOU: " + m.Error, _ => "",
    } : "");
}

public partial class LeadsViewModel(AppSession s) : ViewModelBase
{
    public ObservableCollection<LeadCardVm> Leads { get; } = new();
    public ObservableCollection<ImportRowDto> Problems { get; } = new();
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _filterIndex; // 0 todos, 1 autorizados, 2 pendências, 3 descadastrados
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private string _importSource = "";
    [ObservableProperty] private string _importTags = "";
    [ObservableProperty] private string? _importSummary;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newPhone = "";
    [ObservableProperty] private string _newSource = "Cadastro manual";
    [ObservableProperty] private LeadCardVm? _selected;
    [ObservableProperty] private string _consentSource = "";
    [ObservableProperty] private string _optOutText = "";
    [ObservableProperty] private int _total;

    public override Task OnActivatedAsync() => Refresh();

    partial void OnSearchChanged(string value) => _ = Refresh();
    partial void OnFilterIndexChanged(int value) => _ = Refresh();

    [RelayCommand]
    public Task Refresh() => Run(async () =>
    {
        ConsentStatus? c = FilterIndex switch { 1 => ConsentStatus.Valido, 2 => ConsentStatus.Pendente, 3 => ConsentStatus.Revogado, _ => null };
        var list = await s.Api.LeadsAsync(Search, c);
        Leads.Clear();
        foreach (var l in list) Leads.Add(new LeadCardVm(l));
        Total = Leads.Count;
    });

    [RelayCommand]
    private Task ImportPaste() => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(PasteText)) { Error = "Cole os contatos (um por linha)."; return; }
        Show(await s.Api.ImportPasteAsync(PasteText, NullIfEmpty(ImportSource), NullIfEmpty(ImportTags)));
        PasteText = "";
        await RefreshCore();
    });

    public Task ImportFile(string path) => Run(async () =>
    {
        Show(await s.Api.ImportFileAsync(path, NullIfEmpty(ImportSource), NullIfEmpty(ImportTags)));
        await RefreshCore();
    });

    [RelayCommand]
    private Task AddManual() => Run(async () =>
    {
        await s.Api.CreateLeadAsync(new LeadUpsert(NewName, NewPhone, null, null, NullIfEmpty(NewSource), null, null, IptvExperience.NaoInformado, null, null, PainCategory.Nenhuma, null, null, Domain.Enums.Priority.Normal, null));
        Info = $"Lead {NewName} cadastrado (sem autorização de mensagens até registrar o consentimento).";
        NewName = ""; NewPhone = "";
        await RefreshCore();
    });

    [RelayCommand]
    private Task RegisterConsent() => Run(async () =>
    {
        if (Selected is null) { Error = "Selecione um lead na lista."; return; }
        if (string.IsNullOrWhiteSpace(ConsentSource)) { Error = "Informe a origem verificável do consentimento (ex.: formulário do site, pedido do contato)."; return; }
        var r = await s.Api.ConsentAsync(Selected.Id, new ConsentRequest(ConsentSource, null, null));
        Info = r.Message; ConsentSource = "";
        await RefreshCore();
    });

    public Task SaveExportAsync(Guid id, Func<byte[], Task> write, string fileName) => Run(async () =>
    {
        await write(await s.Api.DownloadAsync($"api/leads/{id}/export"));
        Info = $"Dados exportados para {fileName}";
    });

    public Task DeleteSelectedAsync() => Run(async () =>
    {
        if (Selected is null) return;
        Info = (await s.Api.DeleteLeadAsync(Selected.Id)).Message;
        await RefreshCore();
    });

    [RelayCommand]
    private Task OptOutSelected() => Run(async () =>
    {
        if (Selected is null) { Error = "Selecione um lead na lista."; return; }
        Info = (await s.Api.OptOutAsync(Selected.Id)).Message;
        await RefreshCore();
    });

    [RelayCommand]
    private Task ImportOptOuts() => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(OptOutText)) { Error = "Cole os números da lista de bloqueio."; return; }
        Info = (await s.Api.ImportOptOutsAsync(OptOutText, "Lista de bloqueio importada")).Message;
        OptOutText = "";
        await RefreshCore();
    });

    private void Show(ImportResponse r)
    {
        ImportSummary = $"{r.Total} linhas · {r.Created} novos · {r.Updated} já existiam · {r.Duplicates} duplicados · {r.Invalid} inválidos · {r.Blocked} bloqueados · {r.WithConsent} com consentimento registrado";
        Problems.Clear();
        foreach (var p in r.Problems) Problems.Add(p);
    }

    private async Task RefreshCore()
    {
        ConsentStatus? c = FilterIndex switch { 1 => ConsentStatus.Valido, 2 => ConsentStatus.Pendente, 3 => ConsentStatus.Revogado, _ => null };
        var list = await s.Api.LeadsAsync(Search, c);
        Leads.Clear();
        foreach (var l in list) Leads.Add(new LeadCardVm(l));
        Total = Leads.Count;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public partial class PlansViewModel(AppSession s) : ViewModelBase
{
    public ObservableCollection<PlanDto> Plans { get; } = new();
    [ObservableProperty] private PlanDto? _selected;
    [ObservableProperty] private string _editPrice = "";
    [ObservableProperty] private string _editBenefits = "";

    public override Task OnActivatedAsync() => Refresh();

    partial void OnSelectedChanged(PlanDto? value)
    {
        EditPrice = value?.MonthlyPrice.ToString("0.00") ?? "";
        EditBenefits = value?.Benefits ?? "";
    }

    [RelayCommand]
    private Task Refresh() => Run(async () =>
    {
        Plans.Clear();
        foreach (var p in await s.Api.PlansAsync()) Plans.Add(p);
    });

    [RelayCommand]
    private Task NewVersion() => Run(async () =>
    {
        if (Selected is not { } p) { Error = "Selecione um plano."; return; }
        if (!decimal.TryParse(EditPrice.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0)
        { Error = "Preço inválido."; return; }
        var v = await s.Api.NewPlanVersionAsync(new PlanUpsert(p.Code, p.Name, price, p.Credits, p.MaxActiveLogins, p.Description, EditBenefits, p.Limits, p.TechnicalRequirements, p.TargetProfile, p.ValidFrom, p.ValidUntil));
        Info = $"Versão {v.Version} criada como rascunho. A IA só usa após aprovação.";
        await Refresh();
    });

    [RelayCommand]
    private Task Approve() => Run(async () =>
    {
        if (Selected is not { } p) { Error = "Selecione um plano."; return; }
        await s.Api.ApprovePlanAsync(p.Id);
        Info = $"{p.Name} v{p.Version} aprovado e ativo.";
        await Refresh();
    });
}

public partial class KnowledgeViewModel(AppSession s) : ViewModelBase
{
    public ObservableCollection<KnowledgeDto> Articles { get; } = new();
    [ObservableProperty] private KnowledgeDto? _selected;
    public override Task OnActivatedAsync() => Run(Load);

    private async Task Load()
    {
        var sel = Selected?.Id;
        Articles.Clear(); foreach (var a in await s.Api.KnowledgeAsync()) Articles.Add(a);
        Selected = Articles.FirstOrDefault(a => a.Id == sel) ?? Articles.FirstOrDefault();
    }

    [RelayCommand] private Task Approve() => Set(true);
    [RelayCommand] private Task Unapprove() => Set(false);

    private Task Set(bool approve) => Run(async () =>
    {
        if (Selected is null) return;
        Info = (await s.Api.ApproveKnowledgeAsync(Selected.Id, approve)).Message;
        await Load();
    });
}

public partial class AiSettingsViewModel(AppSession s) : ViewModelBase
{
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _model = "gpt-4o";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _hasApiKey;
    [ObservableProperty] private double _temperature = 0.4;
    [ObservableProperty] private decimal? _maxTokens = 600;
    [ObservableProperty] private string _assistantName = "";
    [ObservableProperty] private string _instructions = "";
    [ObservableProperty] private decimal? _dailyTokenLimit = 500000;
    [ObservableProperty] private int _tokensUsedToday;
    [ObservableProperty] private bool _typing = true;
    [ObservableProperty] private decimal? _debounceSeconds = 6;
    [ObservableProperty] private string _testMessage = "Oi, já trabalho com Painel Sigma e tenho 350 clientes. Meu servidor cai muito.";
    [ObservableProperty] private string? _testReply;
    public ObservableCollection<string> Models { get; } = new() { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-4.1-mini", "gpt-5", "gpt-5-mini" };

    public override Task OnActivatedAsync() => Run(async () =>
    {
        var c = await s.Api.AiSettingsAsync();
        Enabled = c.Enabled; Model = c.Model; HasApiKey = c.HasApiKey; Temperature = c.Temperature; MaxTokens = c.MaxTokens;
        AssistantName = c.AssistantName; Instructions = c.Instructions; DailyTokenLimit = c.DailyTokenLimit; TokensUsedToday = c.TokensUsedToday;
        Typing = c.TypingIndicator; DebounceSeconds = c.DebounceSeconds;
        if (!Models.Contains(Model)) Models.Insert(0, Model);
    });

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        var r = await s.Api.SaveAiSettingsAsync(new AiSettingsUpdate(Enabled, Model, string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey, Temperature,
            (int)(MaxTokens ?? 600), AssistantName, Instructions, (int)(DailyTokenLimit ?? 0), Typing, (int)(DebounceSeconds ?? 6)));
        Info = r.Message;
        if (!string.IsNullOrWhiteSpace(ApiKey)) { HasApiKey = true; ApiKey = ""; }
    });

    [RelayCommand]
    private Task LoadModels() => Run(async () =>
    {
        var r = await s.Api.AiModelsAsync();
        if (!r.Ok) { Error = r.Error; return; }
        var current = Model;
        Models.Clear(); foreach (var m in r.Models) Models.Add(m);
        Model = Models.Contains(current) ? current : Models.FirstOrDefault() ?? current;
        Info = $"{r.Models.Count} modelos disponíveis na sua conta.";
    });

    [RelayCommand]
    private Task Test() => Run(async () =>
    {
        TestReply = null;
        var r = await s.Api.AiSimulateAsync(new AiSimulateRequest([new SimTurn(true, TestMessage)]));
        TestReply = r.Ok
            ? r.Reply + (r.Actions.Count > 0 ? "\n\nAções internas: " + string.Join("; ", r.Actions) : "") + $"\n({r.Tokens} tokens · simulação: nada foi enviado ou salvo)"
            : "Erro: " + r.Error;
    });
}

public partial class IntegrationsViewModel(AppSession s) : ViewModelBase
{
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _instanceName = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _hasApiKey;
    [ObservableProperty] private int _modeIndex; // 0 Baileys, 1 WhatsApp Business (Cloud API oficial)
    [ObservableProperty] private string _webhookPublicUrl = "";
    [ObservableProperty] private string _statusText = "—";
    [ObservableProperty] private string _testPhone = "";
    [ObservableProperty] private string _testText = "Teste de integração do DOMINIUS PLAY AI CRM.";
    [ObservableProperty] private string? _webhookDetail;
    public ObservableCollection<WebhookEventDto> Events { get; } = new();
    public static string[] Modes { get; } = ["WHATSAPP-BAILEYS (WhatsApp Web)", "WHATSAPP-BUSINESS (Cloud API oficial)"];

    public override Task OnActivatedAsync() => Run(async () =>
    {
        var cfg = await s.Api.EvolutionSettingsAsync();
        BaseUrl = cfg.BaseUrl ?? ""; InstanceName = cfg.InstanceName ?? ""; HasApiKey = cfg.HasApiKey;
        ModeIndex = cfg.IntegrationMode == "WHATSAPP-BUSINESS" ? 1 : 0;
        WebhookPublicUrl = cfg.WebhookPublicUrl ?? "";
        await LoadEvents();
    });

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        var r = await s.Api.SaveEvolutionAsync(new EvolutionSettingsUpdate(BaseUrl, InstanceName, string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey,
            ModeIndex == 1 ? "WHATSAPP-BUSINESS" : "WHATSAPP-BAILEYS", WebhookPublicUrl));
        Info = r.Message; ApiKey = ""; HasApiKey = true;
    });

    [RelayCommand]
    private Task TestConnection() => Run(async () =>
    {
        var st = await s.Api.EvolutionStatusAsync();
        StatusText = !st.Configured ? "Não configurado" : !st.Reachable ? "Servidor inacessível: " + st.Error
            : st.Error is not null ? "Erro: " + st.Error : MainWindowViewModel.DescribeState(st.State);
    });

    [RelayCommand]
    private Task RegisterWebhook() => Run(async () =>
    {
        var r = await s.Api.RegisterWebhookAsync();
        if (r.Ok) Info = r.Message; else Error = r.Message;
        WebhookDetail = r.Detail;
    });

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        var r = await s.Api.TestMessageAsync(TestPhone, TestText);
        if (r.Ok) Info = r.Message; else Error = r.Message;
    });

    [RelayCommand]
    private Task LoadEvents() => Run(async () => { Events.Clear(); foreach (var e in await s.Api.WebhookEventsAsync()) Events.Add(e); });
}
