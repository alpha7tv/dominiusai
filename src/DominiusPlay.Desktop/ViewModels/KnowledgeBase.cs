using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Desktop.Services;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Services;

namespace DominiusPlay.Desktop.ViewModels;

/// <summary>
/// Agentes IA → Base de Conhecimento → Dominius Play: perguntas e respostas (editar, criar, publicar, desativar, histórico,
/// testar, importar/exportar), Configuração comercial (Pix, links, prazos, regras), acesso dos agentes e artigos antigos.
/// </summary>
public partial class KbViewModel(AppSession s) : ViewModelBase
{
    public static readonly string[] StatusFilters = ["Todas", "Publicadas", "Rascunhos", "Inativas"];
    public static readonly string[] StatusNames = ["Rascunho", "Publicada", "Inativa"];

    public ObservableCollection<KbEntryDto> Entries { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();
    public ObservableCollection<KbRevisionDto> History { get; } = new();
    public ObservableCollection<KbTestMatch> TestResults { get; } = new();
    public ObservableCollection<ReportRow> Placeholders { get; } = new();
    public ObservableCollection<CommercialHistoryDto> CommercialHistory { get; } = new();
    public KnowledgeViewModel Legacy { get; } = new(s);

    [ObservableProperty] private int _tab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _statusFilter;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private KbEntryDto? _selected;
    [ObservableProperty] private bool _isNew;

    // editor
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _variations = "";
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private string _keywords = "";
    [ObservableProperty] private string _products = "";
    [ObservableProperty] private decimal? _priority = 3;
    [ObservableProperty] private int _statusIndex = 1;
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private bool _needsConfirmation;
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private string _meta = "";

    // testar
    [ObservableProperty] private string _testQuestion = "Achei caro, meu amigo também quer trabalhar";
    [ObservableProperty] private string _testSummary = "";

    // comercial
    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private string _site = "";
    [ObservableProperty] private string _testUrl = "";
    [ObservableProperty] private string _playerUrl = "";
    [ObservableProperty] private string _pixKey = "";
    [ObservableProperty] private string _paymentMethods = "";
    [ObservableProperty] private string _appCode = "";
    [ObservableProperty] private string _appLink = "";
    [ObservableProperty] private string _p2pCode = "";
    [ObservableProperty] private string _p2pLink = "";
    [ObservableProperty] private string _smartTvApps = "";
    [ObservableProperty] private string _activePartnerApps = "";
    [ObservableProperty] private string _formerPartnerApps = "";
    [ObservableProperty] private decimal? _testHours;
    [ObservableProperty] private decimal? _panelReleaseMinutes;
    [ObservableProperty] private decimal? _franchiseDeliveryHours;
    [ObservableProperty] private decimal? _yearsInMarket;
    [ObservableProperty] private string _contractRequirements = "";
    [ObservableProperty] private string _supportInfo = "";
    [ObservableProperty] private string _creditRules = "";
    [ObservableProperty] private string _subpanelRules = "";
    [ObservableProperty] private string _promotions = "";
    [ObservableProperty] private bool _friendSplitEnabled;
    [ObservableProperty] private string _friendSplitPlanCode = "";
    [ObservableProperty] private bool _postPaidEnabled;
    [ObservableProperty] private string _postPaidTiers = "";
    [ObservableProperty] private decimal? _postPaidAdhesionFee;
    [ObservableProperty] private string _commercialInfo = "";

    // acesso dos agentes
    [ObservableProperty] private bool _attendanceAccess = true;
    [ObservableProperty] private bool _dispatchAccess;

    public override Task OnActivatedAsync() => Run(async () =>
    {
        if (Categories.Count == 0) foreach (var c in await s.Api.KbCategoriesAsync()) Categories.Add(c);
        if (Placeholders.Count == 0) foreach (var p in await s.Api.KbPlaceholdersAsync()) Placeholders.Add(p);
        await LoadEntries();
        await LoadCommercial();
        await Legacy.OnActivatedAsync();
    });

    private KbStatus? FilterStatus => StatusFilter switch { 1 => KbStatus.Publicado, 2 => KbStatus.Rascunho, 3 => KbStatus.Inativo, _ => null };

    private async Task LoadEntries()
    {
        var keep = Selected?.Id;
        var list = await s.Api.KbAsync(Search, FilterStatus);
        Entries.Clear(); foreach (var e in list) Entries.Add(e);
        var all = FilterStatus is null && string.IsNullOrWhiteSpace(Search) ? list : await s.Api.KbAsync();
        Summary = $"{all.Count} perguntas · {all.Count(e => e.Status == KbStatus.Publicado)} publicadas · {all.Count(e => e.Status == KbStatus.Rascunho)} rascunhos · {all.Count(e => e.Status == KbStatus.Inativo)} inativas"
            + (Entries.Count != all.Count ? $" · mostrando {Entries.Count}" : "");
        Selected = Entries.FirstOrDefault(e => e.Id == keep) ?? Entries.FirstOrDefault();
    }

    partial void OnStatusFilterChanged(int value) => _ = Run(LoadEntries);

    [RelayCommand] private Task Find() => Run(LoadEntries);

    partial void OnSelectedChanged(KbEntryDto? value)
    {
        if (value is null) return;
        IsNew = false;
        Code = value.Code; Category = value.Category; Question = value.Question; Variations = value.Variations; Answer = value.Answer;
        Keywords = value.Keywords; Products = value.Products; Priority = value.Priority; StatusIndex = (int)value.Status; Source = value.Source;
        NeedsConfirmation = value.NeedsConfirmation; Preview = value.RenderedAnswer; Warnings = string.Join("\n", value.Warnings);
        Meta = $"Versão {value.Version} · atualizada {value.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm} por {value.UpdatedBy}";
        History.Clear();
    }

    [RelayCommand]
    private void New()
    {
        Selected = null; IsNew = true;
        var next = Entries.Select(e => e.Code).Where(c => c.StartsWith("DP-") && int.TryParse(c[3..], out _)).Select(c => int.Parse(c[3..])).DefaultIfEmpty(0).Max() + 1;
        Code = $"DP-{next:000}"; Category = Categories.LastOrDefault() ?? ""; Question = ""; Variations = ""; Answer = ""; Keywords = ""; Products = "";
        Priority = 3; StatusIndex = (int)KbStatus.Rascunho; Source = "Painel administrativo"; NeedsConfirmation = false; Preview = ""; Warnings = ""; Meta = "Nova pergunta (começa como rascunho; teste e publique)";
        History.Clear();
    }

    private KbEntryUpsert Form() => new(Code, Category, Question, Variations, Answer, Keywords, Products, (int)(Priority ?? 3), (KbStatus)StatusIndex, Source, NeedsConfirmation);

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        var saved = IsNew ? await s.Api.CreateKbAsync(Form()) : Selected is { } sel ? await s.Api.UpdateKbAsync(sel.Id, Form()) : null;
        if (saved is null) { Error = "Selecione uma pergunta ou clique em Nova."; return; }
        Info = $"{saved.Code} salva (versão {saved.Version})." + (saved.Status == KbStatus.Publicado ? " A IA já usa a partir da próxima mensagem." : " Publique para a IA usar.");
        IsNew = false;
        Search = ""; StatusFilter = 0;
        await LoadEntries();
        Selected = Entries.FirstOrDefault(e => e.Id == saved.Id);
    });

    [RelayCommand] private Task Publish() => SetStatus(KbStatus.Publicado);
    [RelayCommand] private Task Deactivate() => SetStatus(KbStatus.Inativo);

    private Task SetStatus(KbStatus st) => Run(async () =>
    {
        if (Selected is not { } sel) { Error = "Selecione uma pergunta."; return; }
        Info = (await s.Api.SetKbStatusAsync(sel.Id, st)).Message;
        await LoadEntries();
    });

    public async Task DeleteSelectedAsync() => await Run(async () =>
    {
        if (Selected is not { } sel) return;
        Info = (await s.Api.DeleteKbAsync(sel.Id)).Message;
        await LoadEntries();
    });

    [RelayCommand]
    private Task LoadHistory() => Run(async () =>
    {
        if (Selected is not { } sel) return;
        History.Clear(); foreach (var h in await s.Api.KbHistoryAsync(sel.Id)) History.Add(h);
        if (History.Count == 0) Info = "Sem histórico.";
    });

    [RelayCommand]
    private Task Test() => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(TestQuestion)) { Error = "Escreva uma pergunta de cliente."; return; }
        var r = await s.Api.TestKbAsync(new KbTestRequest(TestQuestion, IncludeDrafts: true));
        TestResults.Clear(); foreach (var m in r.Matches) TestResults.Add(m);
        TestSummary = $"Intenção: {r.Intent} · Perfil: {r.Profile} · Gatilho: {r.Trigger}" + (r.OfferMessage is null ? "" : $"\nOferta sugerida: {r.OfferMessage}")
            + (r.Matches.Count == 0 ? "\nNenhuma resposta encontrada: a IA diria que vai confirmar com a equipe e transferiria." : "");
    });

    /// <summary>Testa a pergunta do editor (inclusive rascunho) antes de publicar.</summary>
    [RelayCommand]
    private Task TestCurrent() { TestQuestion = Question; Tab = 1; return Test(); }

    public async Task ExportAsync(Func<byte[], Task> write) => await Run(async () =>
    {
        var list = await s.Api.ExportKbAsync();
        await write(JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions(ApiClient.Json) { WriteIndented = true }));
        Info = $"{list.Count} perguntas exportadas.";
    });

    public async Task ImportAsync(string json, bool overwrite) => await Run(async () =>
    {
        List<KbEntryUpsert>? list;
        try { list = JsonSerializer.Deserialize<List<KbEntryUpsert>>(json, ApiClient.Json); }
        catch (JsonException ex) { Error = "Arquivo inválido: " + ex.Message; return; }
        if (list is null || list.Count == 0) { Error = "Arquivo sem perguntas."; return; }
        var r = await s.Api.ImportKbAsync(new KbImportRequest(list, overwrite));
        Info = $"Importação: {r.Created} criadas, {r.Updated} atualizadas, {r.Skipped} ignoradas." + (r.Errors.Count > 0 ? " Problemas: " + string.Join(" | ", r.Errors.Take(5)) : "");
        await LoadEntries();
    });

    // ---------------- comercial
    private async Task LoadCommercial()
    {
        var c = await s.Api.CommercialAsync();
        CompanyName = c.CompanyName; Site = c.Site; TestUrl = c.TestUrl; PlayerUrl = c.PlayerUrl; PixKey = c.PixKey; PaymentMethods = c.PaymentMethods;
        AppCode = c.AppCode; AppLink = c.AppLink; P2pCode = c.P2pCode; P2pLink = c.P2pLink; SmartTvApps = c.SmartTvApps; ActivePartnerApps = c.ActivePartnerApps;
        FormerPartnerApps = c.FormerPartnerApps; TestHours = c.TestHours; PanelReleaseMinutes = c.PanelReleaseMinutes; FranchiseDeliveryHours = c.FranchiseDeliveryHours;
        YearsInMarket = c.YearsInMarket; ContractRequirements = c.ContractRequirements; SupportInfo = c.SupportInfo; CreditRules = c.CreditRules; SubpanelRules = c.SubpanelRules;
        Promotions = c.Promotions; FriendSplitEnabled = c.FriendSplitEnabled; FriendSplitPlanCode = c.FriendSplitPlanCode; PostPaidEnabled = c.PostPaidEnabled;
        PostPaidTiers = c.PostPaidTiers; PostPaidAdhesionFee = c.PostPaidAdhesionFee;
        var acc = await s.Api.KbAccessAsync(); AttendanceAccess = acc.Attendance; DispatchAccess = acc.Dispatch;
        CommercialHistory.Clear(); foreach (var h in await s.Api.CommercialHistoryAsync()) CommercialHistory.Add(h);
    }

    [RelayCommand]
    private Task SaveCommercial() => Run(async () =>
    {
        var c = new CommercialConfig
        {
            CompanyName = CompanyName.Trim(), Site = Site.Trim(), TestUrl = TestUrl.Trim(), PlayerUrl = PlayerUrl.Trim(), PixKey = PixKey.Trim(), PaymentMethods = PaymentMethods.Trim(),
            AppCode = AppCode.Trim(), AppLink = AppLink.Trim(), P2pCode = P2pCode.Trim(), P2pLink = P2pLink.Trim(), SmartTvApps = SmartTvApps.Trim(),
            ActivePartnerApps = ActivePartnerApps.Trim(), FormerPartnerApps = FormerPartnerApps.Trim(), TestHours = (int)(TestHours ?? 4),
            PanelReleaseMinutes = (int)(PanelReleaseMinutes ?? 5), FranchiseDeliveryHours = (int)(FranchiseDeliveryHours ?? 4), YearsInMarket = (int)(YearsInMarket ?? 10),
            ContractRequirements = ContractRequirements.Trim(), SupportInfo = SupportInfo.Trim(), CreditRules = CreditRules.Trim(), SubpanelRules = SubpanelRules.Trim(),
            Promotions = Promotions.Trim(), FriendSplitEnabled = FriendSplitEnabled, FriendSplitPlanCode = FriendSplitPlanCode.Trim().ToUpperInvariant(),
            PostPaidEnabled = PostPaidEnabled, PostPaidTiers = PostPaidTiers.Trim(), PostPaidAdhesionFee = PostPaidAdhesionFee ?? 0,
        };
        var errors = c.Validate();
        if (errors.Count > 0) { Error = string.Join("; ", errors); return; }
        CommercialInfo = (await s.Api.SaveCommercialAsync(c)).Message;
        await s.Api.SaveKbAccessAsync(new KbAccessDto(AttendanceAccess, DispatchAccess));
        await LoadCommercial();
        await LoadEntries(); // as respostas renderizadas usam os novos valores
    });
}

/// <summary>Uma linha do Laboratório (análise da base + resposta do Claude, quando ligado).</summary>
public sealed record LabStepView(LabStepDto Step)
{
    public string Header => $"{Step.Index}. Cliente: {Step.Message}";
    public string Analysis => $"Intenção: {Step.Intent} · Perfil: {Step.Profile} · Gatilho: {Step.Trigger}";
    public string Found => Step.MatchedCode is null ? "Pergunta encontrada: nenhuma" : $"Pergunta encontrada: {Step.MatchedCode} · {Step.MatchedQuestion} ({Step.Category}; relevância {Step.Score.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"))})";
    public string Reply => Step.Reply is null ? "(nenhuma resposta automática)" : Step.Reply;
    public string Product => "Produto considerado: " + (Step.Product ?? "—");
    public string Memory => "Memória usada: " + (Step.MemoryUsed.Count == 0 ? "nenhuma ainda" : string.Join(" · ", Step.MemoryUsed));
    public string Action => "Ação sugerida: " + Step.SuggestedAction;
    public string Alerts => string.Join("\n", Step.Alerts.Select(a => "⚠ " + a));
    public bool HasAlerts => Step.Alerts.Count > 0;
    public string Handoff => Step.Handoff ? "→ Encaminhar para atendimento humano" : Step.OptOut ? "→ Descadastro: nenhum envio" : "";
    public bool HasHandoff => Step.Handoff || Step.OptOut;
    public bool HasAi => Step.AiReply is not null || Step.AiAlerts.Count > 0;
    public string AiReply => "Claude: " + (Step.AiReply ?? "—") + (Step.AiAlerts.Count > 0 ? "\n⚠ Bloqueado pela conferência: " + string.Join("; ", Step.AiAlerts) : "")
        + (Step.AiActions.Count > 0 ? "\nAções: " + string.Join(" | ", Step.AiActions) : "");
}

/// <summary>Laboratório de Atendimento IA: simula clientes antes de ligar a IA com clientes reais. Nada é enviado nem pago.</summary>
public partial class LabViewModel(AppSession s) : ViewModelBase
{
    public ObservableCollection<LabScenarioDto> Scenarios { get; } = new();
    public ObservableCollection<LabStepView> Steps { get; } = new();
    [ObservableProperty] private LabScenarioDto? _selectedScenario;
    [ObservableProperty] private string _messages = "";
    [ObservableProperty] private bool _useAi;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _aiNote;

    public override Task OnActivatedAsync() => Run(async () =>
    {
        if (Scenarios.Count > 0) return;
        foreach (var sc in await s.Api.LabScenariosAsync()) Scenarios.Add(sc);
        SelectedScenario = Scenarios.FirstOrDefault();
    });

    partial void OnSelectedScenarioChanged(LabScenarioDto? value) { if (value is not null) Messages = string.Join("\n", value.Messages); }

    [RelayCommand]
    private Task Simulate() => Run(async () =>
    {
        var lines = Messages.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (lines.Count == 0) { Error = "Escreva ao menos uma mensagem do cliente (uma por linha)."; return; }
        var r = await s.Api.RunLabAsync(new LabRequest(null, lines, UseAi));
        Title = (SelectedScenario is { } sc && string.Join("\n", sc.Messages) == string.Join("\n", lines) ? sc.Title : r.Title) + $" · {r.Steps.Count} mensagens · nada foi enviado";
        AiNote = r.AiError ?? (r.AiUsed ? "Respostas do Claude incluídas (consumo de tokens contabilizado; nada enviado ao cliente)." : null);
        Steps.Clear(); foreach (var st in r.Steps) Steps.Add(new LabStepView(st));
    });
}
