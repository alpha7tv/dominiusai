using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Desktop.ViewModels;

// ======================= Equipe (Fase 7) =======================
public partial class TeamViewModel(AppSession s) : ViewModelBase
{
    public static string[] Roles { get; } = ["Atendente", "Supervisor", "Administrador"];
    public ObservableCollection<UserRowVm> Users { get; } = new();
    public bool IsAdmin => s.Api.Session?.Role == UserRole.Administrador;
    [ObservableProperty] private UserRowVm? _selected;
    [ObservableProperty] private string _newUser = "";
    [ObservableProperty] private string _newDisplay = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private int _newRole;
    [ObservableProperty] private string _editDisplay = "";
    [ObservableProperty] private int _editRole;
    [ObservableProperty] private bool _editActive = true;
    [ObservableProperty] private string _editPassword = "";
    [ObservableProperty] private string _myCurrent = "";
    [ObservableProperty] private string _myNew = "";

    public override Task OnActivatedAsync() => Run(Load);

    private async Task Load()
    {
        var sel = Selected?.Model.Id;
        Users.Clear();
        foreach (var u in await s.Api.UsersAsync()) Users.Add(new UserRowVm(u));
        Selected = Users.FirstOrDefault(u => u.Model.Id == sel);
    }

    partial void OnSelectedChanged(UserRowVm? value)
    {
        if (value is null) return;
        EditDisplay = value.Model.DisplayName; EditRole = (int)value.Model.Role; EditActive = value.Model.Active; EditPassword = "";
    }

    [RelayCommand]
    private Task Create() => Run(async () =>
    {
        var u = await s.Api.CreateUserAsync(new UserCreate(NewUser, NewDisplay, NewPassword, (UserRole)NewRole));
        Info = $"Usuário {u.UserName} criado."; NewUser = ""; NewDisplay = ""; NewPassword = ""; NewRole = 0;
        await Load();
    });

    [RelayCommand]
    private Task Update() => Run(async () =>
    {
        if (Selected is null) { Error = "Selecione um usuário."; return; }
        var r = await s.Api.UpdateUserAsync(Selected.Model.Id, new UserUpdate(EditDisplay, (UserRole)EditRole, EditActive, string.IsNullOrEmpty(EditPassword) ? null : EditPassword));
        Info = r.Message; EditPassword = "";
        await Load();
    });

    [RelayCommand]
    private Task ChangeMyPassword() => Run(async () =>
    {
        var r = await s.Api.ChangePasswordAsync(MyCurrent, MyNew);
        Info = r.Message; MyCurrent = ""; MyNew = "";
    });
}

public class UserRowVm(UserDto u)
{
    public UserDto Model { get; } = u;
    public string Name => Model.DisplayName;
    public string Login => "@" + Model.UserName;
    public string Role => Model.Role switch { UserRole.Administrador => "Administrador", UserRole.Supervisor => "Supervisor", _ => "Atendente" };
    public string Status => Model.Active ? $"Ativo · {Model.OpenConversations} conversa(s) em atendimento" : "Desativado";
}

// ======================= Mensagens / modelos (Fase 8) =======================
public partial class TemplatesViewModel(AppSession s) : ViewModelBase
{
    public ObservableCollection<TemplateRowVm> Templates { get; } = new();
    public bool IsAdmin => s.Api.Session?.Role == UserRole.Administrador;
    [ObservableProperty] private TemplateRowVm? _selected;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newPurpose = "Abordagem inicial";
    [ObservableProperty] private string _body = "Olá{, nome}! Sou {assistente}, assistente virtual da Dominius Play. ";
    [ObservableProperty] private string? _preview;
    [ObservableProperty] private string? _similarWarning;
    [ObservableProperty] private string _channelName = "";

    public override Task OnActivatedAsync() => Run(Load);

    private async Task Load()
    {
        var sel = Selected?.Model.VersionId;
        Templates.Clear();
        foreach (var t in await s.Api.TemplatesAsync()) Templates.Add(new TemplateRowVm(t));
        Selected = Templates.FirstOrDefault(t => t.Model.VersionId == sel);
    }

    partial void OnSelectedChanged(TemplateRowVm? value)
    {
        if (value is null) return;
        Body = value.Model.Body; NewName = value.Model.Name; NewPurpose = value.Model.Purpose; ChannelName = value.Model.ChannelTemplateName ?? "";
        _ = Run(DoPreview);
    }

    private async Task DoPreview()
    {
        var p = await s.Api.PreviewTemplateAsync(Body);
        Preview = p.Text;
        SimilarWarning = p.SimilarTo is null ? null : $"⚠ Muito parecido com {p.SimilarTo}. Mensagens quase idênticas não ajudam e podem parecer disparo em massa.";
    }

    [RelayCommand] private Task PreviewNow() => Run(DoPreview);

    [RelayCommand]
    private Task CreateTemplate() => Run(async () =>
    {
        var r = await s.Api.CreateTemplateAsync(new TemplateCreate(NewName, NewPurpose, Body));
        Info = r.Message; await Load();
    });

    [RelayCommand]
    private Task NewVersion() => Run(async () =>
    {
        if (Selected is null) { Error = "Selecione o modelo que receberá a nova versão."; return; }
        var r = await s.Api.NewTemplateVersionAsync(Selected.Model.Id, Body);
        Info = r.Message; await Load();
    });

    [RelayCommand]
    private Task Approve() => Run(async () =>
    {
        if (Selected is null) { Error = "Selecione uma versão."; return; }
        var r = await s.Api.ApproveTemplateAsync(Selected.Model.VersionId, string.IsNullOrWhiteSpace(ChannelName) ? null : ChannelName);
        Info = r.Message; await Load();
    });

    [RelayCommand]
    private Task Archive() => Run(async () =>
    {
        if (Selected is null) { Error = "Selecione uma versão."; return; }
        var r = await s.Api.ArchiveTemplateAsync(Selected.Model.VersionId);
        Info = r.Message; await Load();
    });
}

public class TemplateRowVm(TemplateDto t)
{
    public TemplateDto Model { get; } = t;
    public string Title => $"{Model.Name}  v{Model.Version}";
    public string Status => Model.Approval switch
    {
        ApprovalStatus.Aprovado => "Aprovado" + (Model.ChannelTemplateName is null ? "" : $" · canal: {Model.ChannelTemplateName}"),
        ApprovalStatus.EmRevisao => "Em revisão",
        ApprovalStatus.Arquivado => "Arquivado",
        _ => "Rascunho",
    };
    public string Usage => $"{Model.Purpose} · usado {Model.TimesUsed}x";
    public bool IsApproved => Model.Approval == ApprovalStatus.Aprovado;
    public bool HasWarning => Model.SimilarTo is not null;
    public string Warning => Model.SimilarTo is null ? "" : $"⚠ Parecido com {Model.SimilarTo}";
}

// ======================= Campanhas (Fase 8) =======================
public partial class CampaignsViewModel(AppSession s) : ViewModelBase
{
    public ObservableCollection<CampaignRowVm> Campaigns { get; } = new();
    public ObservableCollection<TemplateDto> ApprovedTemplates { get; } = new();
    [ObservableProperty] private CampaignRowVm? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _startTime = "09:00";
    [ObservableProperty] private string _endTime = "18:00";
    [ObservableProperty] private bool _sun;
    [ObservableProperty] private bool _mon = true;
    [ObservableProperty] private bool _tue = true;
    [ObservableProperty] private bool _wed = true;
    [ObservableProperty] private bool _thu = true;
    [ObservableProperty] private bool _fri = true;
    [ObservableProperty] private bool _sat;
    [ObservableProperty] private decimal? _maxContacts = 100;
    [ObservableProperty] private decimal? _dailyLimit = 50;
    [ObservableProperty] private decimal? _intervalMinutes = 5;
    [ObservableProperty] private string _objective = "";
    [ObservableProperty] private string _tagFilter = "";
    [ObservableProperty] private TemplateDto? _template;

    public bool CanEdit => Selected is null || Selected.Model.Status is CampaignStatus.Rascunho or CampaignStatus.Pausada;
    public bool CanStart => Selected?.Model.Status == CampaignStatus.Rascunho;
    public bool CanPause => Selected?.Model.Status == CampaignStatus.Ativa;
    public bool CanResume => Selected?.Model.Status == CampaignStatus.Pausada;
    public bool CanCancel => Selected?.Model.Status is CampaignStatus.Rascunho or CampaignStatus.Ativa or CampaignStatus.Pausada;
    public string FormTitle => Selected is null ? "Nova campanha" : $"Campanha: {Selected.Model.Name}";

    public override Task OnActivatedAsync() => Run(Load);

    private async Task Load()
    {
        var sel = Selected?.Model.Id;
        ApprovedTemplates.Clear();
        foreach (var t in (await s.Api.TemplatesAsync()).Where(t => t.Approval == ApprovalStatus.Aprovado).GroupBy(t => t.Id).Select(g => g.First()))
            ApprovedTemplates.Add(t);
        Campaigns.Clear();
        foreach (var c in await s.Api.CampaignsAsync()) Campaigns.Add(new CampaignRowVm(c, ApprovedTemplates.FirstOrDefault(t => t.Id == c.TemplateId)?.Name));
        Selected = Campaigns.FirstOrDefault(c => c.Model.Id == sel);
        Notify();
    }

    private void Notify()
    {
        foreach (var p in new[] { nameof(CanEdit), nameof(CanStart), nameof(CanPause), nameof(CanResume), nameof(CanCancel), nameof(FormTitle) }) OnPropertyChanged(p);
    }

    partial void OnSelectedChanged(CampaignRowVm? value)
    {
        Notify();
        if (value is null) return;
        var c = value.Model;
        Name = c.Name; StartTime = c.StartTime; EndTime = c.EndTime; MaxContacts = c.MaxContacts; DailyLimit = c.DailyLimit;
        IntervalMinutes = c.MinIntervalSeconds / 60m; Objective = c.Objective ?? ""; TagFilter = c.TagFilter;
        Template = ApprovedTemplates.FirstOrDefault(t => t.Id == c.TemplateId);
        var d = c.AllowedDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        Sun = d.Contains(0); Mon = d.Contains(1); Tue = d.Contains(2); Wed = d.Contains(3); Thu = d.Contains(4); Fri = d.Contains(5); Sat = d.Contains(6);
    }

    [RelayCommand]
    private void New()
    {
        Selected = null; Name = ""; Objective = ""; TagFilter = ""; Template = ApprovedTemplates.FirstOrDefault();
        Info = "Preencha os dados e salve. A campanha só envia depois de clicar em Iniciar.";
    }

    private CampaignUpsert Form()
    {
        var days = new[] { Sun, Mon, Tue, Wed, Thu, Fri, Sat }.Select((on, i) => (on, i)).Where(x => x.on).Select(x => x.i.ToString());
        return new CampaignUpsert(Name, StartTime, EndTime, string.Join(',', days), (int)(MaxContacts ?? 0), (int)((IntervalMinutes ?? 5) * 60),
            (int)(DailyLimit ?? 0), Objective, Template?.Id, TagFilter);
    }

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        var dto = Selected is null ? await s.Api.CreateCampaignAsync(Form()) : await s.Api.UpdateCampaignAsync(Selected.Model.Id, Form());
        Info = "Campanha salva.";
        Selected = new CampaignRowVm(dto, null);
        await Load();
    });

    [RelayCommand] private Task Start() => Act("start");
    [RelayCommand] private Task Pause() => Act("pause");
    [RelayCommand] private Task Resume() => Act("resume");
    public Task CancelAsync() => Act("cancel");
    [RelayCommand] private Task Refresh() => Run(Load);

    private Task Act(string action) => Run(async () =>
    {
        if (Selected is null) return;
        var r = await s.Api.CampaignActionAsync(Selected.Model.Id, action);
        if (r.Ok) Info = r.Message; else Error = r.Message;
        await Load();
    });
}

public class CampaignRowVm(CampaignDto c, string? templateName)
{
    public CampaignDto Model { get; } = c;
    public string Name => Model.Name;
    public string Status => Model.Status switch
    {
        CampaignStatus.Ativa => "● Ativa", CampaignStatus.Pausada => "❚❚ Pausada", CampaignStatus.Cancelada => "✕ Cancelada",
        CampaignStatus.Concluida => "✓ Concluída", _ => "Rascunho",
    };
    public string Schedule
    {
        get
        {
            string[] n = ["Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb"];
            var days = string.Join(", ", Model.AllowedDays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(d => n[int.Parse(d)]));
            return $"{days} · {Model.StartTime}–{Model.EndTime} · 1 a cada {Model.MinIntervalSeconds / 60} min · até {Model.DailyLimit}/dia";
        }
    }
    public string Template => "Modelo: " + (templateName ?? "—") + (Model.TagFilter.Length > 0 ? $" · tags: {Model.TagFilter}" : " · todos os leads autorizados");
    public string Numbers => Model.Status == CampaignStatus.Rascunho
        ? $"{Model.Eligible} lead(s) elegível(is) agora (máx. {Model.MaxContacts})"
        : $"Na fila {Model.Queued} · Enviadas {Model.Sent} · Entregues {Model.Delivered} · Responderam {Model.Replied} · Falhas {Model.Failed} · Canceladas {Model.Cancelled}";
}

// ======================= Relatórios (Fase 9) =======================
public partial class ReportsViewModel(AppSession s) : ViewModelBase
{
    public static string[] Periods { get; } = ["Hoje", "Últimos 7 dias", "Últimos 30 dias", "Últimos 90 dias", "Últimos 12 meses"];
    private static readonly int[] PeriodDays = [0, 7, 30, 90, 365];
    public bool IsAdmin => s.Api.Session?.Role == UserRole.Administrador;
    [ObservableProperty] private int _period = 2;
    /// <summary>0 = Visão geral, 1 = Mensagens enviadas.</summary>
    [ObservableProperty] private int _tab;
    public SentMessagesReportViewModel Sent { get; } = new(s);
    [ObservableProperty] private ReportDto? _report;
    [ObservableProperty] private decimal? _purgeDays = 90;
    public ObservableCollection<ReportSection> Sections { get; } = new();
    public ObservableCollection<Kpi> Kpis { get; } = new();

    public override async Task OnActivatedAsync() { Sent.From = From; await Run(Load); await Sent.ReloadAsync(); }
    partial void OnPeriodChanged(int value) { Sent.From = From; _ = Run(Load); _ = Sent.ReloadAsync(); }
    [RelayCommand] private async Task Refresh() { await Run(Load); await Sent.ReloadAsync(); }

    public DateTimeOffset From => PeriodDays[Math.Clamp(Period, 0, PeriodDays.Length - 1)] is var d && d == 0
        ? new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset)
        : DateTimeOffset.Now.AddDays(-d);

    private async Task Load()
    {
        var r = await s.Api.ReportAsync(From, DateTimeOffset.Now);
        Report = r;
        Kpis.Clear();
        Kpis.Add(new Kpi("Novos leads", r.NewLeads.ToString()));
        Kpis.Add(new Kpi("Contatados", r.Contacted.ToString()));
        Kpis.Add(new Kpi("Responderam", r.Replied.ToString(), r.Contacted == 0 ? null : $"{(double)r.Replied / r.Contacted:P0} dos contatados"));
        Kpis.Add(new Kpi("Negócios fechados", r.Closed.ToString()));
        Kpis.Add(new Kpi("Sem interesse", r.Lost.ToString()));
        Sections.Clear();
        void Add(string title, IReadOnlyList<ReportRow> rows) => Sections.Add(new ReportSection(title, rows));
        Add("Funil atual (Kanban)", r.Funnel);
        Add("Fechamentos por plano", r.ClosedByPlan);
        Add("Motivos de perda", r.LossReasons);
        Add("Dores mais citadas", r.Pains);
        Add("Mensagens enviadas por status", r.MessagesByStatus);
        Add("Desempenho dos modelos", r.Templates);
        Add("Atendimento (IA e equipe)", r.ByAttendant);
        Add("Origem dos leads", r.Sources);
    }

    public Task<byte[]> ExportLeadsAsync() => s.Api.DownloadAsync("api/export/leads.csv");
    public Task<byte[]> ExportMessagesAsync() => s.Api.DownloadAsync($"api/export/messages.csv?from={Uri.EscapeDataString(From.ToString("o"))}");
    public void ShowInfo(string msg) => Info = msg;
    public Task RunSafe(Func<Task> f) => Run(f);

    [RelayCommand]
    private Task Backup() => Run(async () => { var r = await s.Api.BackupAsync(); if (r.Ok) Info = $"{r.Message}: {r.Detail}"; else Error = r.Message; });

    [RelayCommand]
    private Task Purge() => Run(async () => { var r = await s.Api.PurgeAsync((int)(PurgeDays ?? 90)); Info = r.Message; });
}

public record Kpi(string Label, string Value, string? Hint = null);

// ======================= Relatório de mensagens enviadas =======================
/// <summary>Aba "Mensagens enviadas" da tela Relatórios: total por período, campanha e atendente/IA, com status de entrega.</summary>
public partial class SentMessagesReportViewModel(AppSession s) : ViewModelBase
{
    public static readonly FilterOption AllCampaigns = new("", "Todas as campanhas");
    public static readonly FilterOption AllAuthors = new("", "IA e toda a equipe");
    private bool _syncing;

    public DateTimeOffset From { get; set; } = DateTimeOffset.Now.AddDays(-30);
    [ObservableProperty] private SentMessagesReportDto? _report;
    [ObservableProperty] private FilterOption? _campaign = AllCampaigns;
    [ObservableProperty] private FilterOption? _author = AllAuthors;
    [ObservableProperty] private SentRowVm? _totals;
    [ObservableProperty] private string _periodTitle = "Por dia";
    [ObservableProperty] private bool _hasData;
    public ObservableCollection<FilterOption> CampaignOptions { get; } = [AllCampaigns];
    public ObservableCollection<FilterOption> AuthorOptions { get; } = [AllAuthors];
    public ObservableCollection<Kpi> Kpis { get; } = new();
    public ObservableCollection<PeriodBarVm> Chart { get; } = new();
    public ObservableCollection<SentRowVm> ByPeriod { get; } = new();
    public ObservableCollection<SentRowVm> ByCampaign { get; } = new();
    public ObservableCollection<SentRowVm> ByAuthor { get; } = new();
    public ObservableCollection<ReportRowVm> Failures { get; } = new();

    partial void OnCampaignChanged(FilterOption? value) { if (!_syncing && value is not null) _ = ReloadAsync(); }
    partial void OnAuthorChanged(FilterOption? value) { if (!_syncing && value is not null) _ = ReloadAsync(); }

    public Task ReloadAsync() => Run(Load);

    private async Task Load()
    {
        var r = await s.Api.SentMessagesReportAsync(From, DateTimeOffset.Now, Campaign?.Key, Author?.Key);
        Report = r;
        _syncing = true;
        try
        {
            Campaign = Sync(CampaignOptions, AllCampaigns, r.CampaignOptions, Campaign);
            Author = Sync(AuthorOptions, AllAuthors, r.AuthorOptions, Author);
        }
        finally { _syncing = false; }

        var t = r.Totals;
        Totals = new SentRowVm(t, Math.Max(1, t.Total));
        HasData = t.Total > 0;
        Kpis.Clear();
        Kpis.Add(new Kpi("Mensagens enviadas", t.Total.ToString("N0"), r.QueuedNow > 0 ? $"{r.QueuedNow} na fila agora" : "nenhuma na fila agora"));
        Kpis.Add(new Kpi("Enviado (sem confirmação)", t.Sent.ToString("N0"), Pct(t.Sent, t.Total)));
        Kpis.Add(new Kpi("Entregue", t.Delivered.ToString("N0"), Pct(t.Delivered, t.Total)));
        Kpis.Add(new Kpi("Lido", t.Read.ToString("N0"), Pct(t.Read, t.Total)));
        Kpis.Add(new Kpi("Falhou", t.Failed.ToString("N0"), Pct(t.Failed, t.Total)));

        PeriodTitle = r.Grouping switch { "hora" => "Por hora", "mes" => "Por mês", _ => "Por dia" };
        var maxSlot = Math.Max(1, r.ByPeriod.Count == 0 ? 1 : r.ByPeriod.Max(x => x.Total));
        Chart.Clear();
        // Rótulo a cada N colunas para não encavalar (ex.: 31 dias → um rótulo a cada 2 dias; 91 dias → a cada 6)
        var step = Math.Max(1, (int)Math.Ceiling(r.ByPeriod.Count / 16.0));
        for (var i = 0; i < r.ByPeriod.Count; i++)
        {
            var p = r.ByPeriod[i];
            var showLabel = (r.ByPeriod.Count - 1 - i) % step == 0; // sempre mostra o período mais recente
            Chart.Add(new PeriodBarVm(p, maxSlot, showLabel ? ShortLabel(p.Key, r.Grouping) : ""));
        }
        Fill(ByPeriod, r.ByPeriod.Where(x => x.Total > 0).Reverse());
        Fill(ByCampaign, r.ByCampaign);
        Fill(ByAuthor, r.ByAuthor);
        Failures.Clear();
        var maxFail = Math.Max(1, r.FailureReasons.Count == 0 ? 1 : r.FailureReasons.Max(x => x.Value));
        foreach (var f in r.FailureReasons) Failures.Add(new ReportRowVm(f, maxFail));
    }

    private static void Fill(ObservableCollection<SentRowVm> target, IEnumerable<SentMessagesRow> rows)
    {
        var list = rows.ToList();
        var max = Math.Max(1, list.Count == 0 ? 1 : list.Max(x => x.Total));
        target.Clear();
        foreach (var r in list) target.Add(new SentRowVm(r, max));
    }

    /// <summary>Atualiza as opções do filtro sem perder a escolha atual (mantém a opção mesmo se não houver envio no período).</summary>
    private static FilterOption Sync(ObservableCollection<FilterOption> target, FilterOption all, IReadOnlyList<FilterOption> options, FilterOption? current)
    {
        var wanted = new List<FilterOption> { all };
        wanted.AddRange(options);
        if (current is { Key.Length: > 0 } && wanted.All(o => o.Key != current.Key)) wanted.Add(current);
        if (!target.SequenceEqual(wanted))
        {
            target.Clear();
            foreach (var o in wanted) target.Add(o);
        }
        return target.FirstOrDefault(o => o.Key == current?.Key) ?? all;
    }

    private static string? Pct(int v, int total) => total == 0 ? null : $"{(double)v / total:P0} do total";

    private static string ShortLabel(string key, string grouping) =>
        !DateTimeOffset.TryParse(key, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? key
        : grouping switch { "hora" => t.ToString("HH'h'"), "mes" => t.ToString("MMM/yy"), _ => t.ToString("dd/MM") };
}

/// <summary>Linha das tabelas do relatório de mensagens enviadas, com barra empilhada por status.</summary>
public class SentRowVm(SentMessagesRow r, int max)
{
    private const double BarWidth = 260;
    public SentMessagesRow Model => r;
    public string Label => r.Label;
    public string Total => r.Total.ToString("N0");
    public string Sent => r.Sent.ToString("N0");
    public string Delivered => r.Delivered.ToString("N0");
    public string Read => r.Read.ToString("N0");
    public string Failed => r.Failed.ToString("N0");
    /// <summary>Entregues + lidas sobre o total (uma mensagem lida também foi entregue).</summary>
    public string DeliveryRate => r.Total == 0 ? "—" : $"{(double)(r.Delivered + r.Read) / r.Total:P0}";
    public string ReadRate => r.Total == 0 ? "—" : $"{(double)r.Read / r.Total:P0}";
    public double SentWidth => BarWidth * r.Sent / max;
    public double DeliveredWidth => BarWidth * r.Delivered / max;
    public double ReadWidth => BarWidth * r.Read / max;
    public double FailedWidth => BarWidth * r.Failed / max;
}

/// <summary>Coluna do gráfico por período (barra vertical empilhada por status).</summary>
public class PeriodBarVm(SentMessagesRow r, int max, string shortLabel)
{
    private const double Height = 140;
    public string Label => shortLabel;
    public string Tip => $"{r.Label}: {r.Total} enviada(s) · enviado {r.Sent} · entregue {r.Delivered} · lido {r.Read} · falhou {r.Failed}";
    public string Total => r.Total == 0 ? "" : r.Total.ToString();
    public double SentHeight => Height * r.Sent / max;
    public double DeliveredHeight => Height * r.Delivered / max;
    public double ReadHeight => Height * r.Read / max;
    public double FailedHeight => Height * r.Failed / max;
}

public class ReportSection(string title, IReadOnlyList<ReportRow> rows)
{
    public string Title { get; } = title;
    public IReadOnlyList<ReportRowVm> Rows { get; } = rows.Count == 0
        ? [new ReportRowVm(new ReportRow("Sem dados no período", 0), 1)]
        : rows.Select(r => new ReportRowVm(r, Math.Max(1, rows.Max(x => x.Value)))).ToList();
}

public class ReportRowVm(ReportRow r, int max)
{
    public string Label => r.Label;
    public string Value => r.Value.ToString();
    public string? Detail => r.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(r.Detail);
    public double Bar => 220.0 * r.Value / max;
}
