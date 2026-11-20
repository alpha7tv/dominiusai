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
    public static string[] Periods { get; } = ["Últimos 7 dias", "Últimos 30 dias", "Últimos 90 dias", "Últimos 12 meses"];
    private static readonly int[] PeriodDays = [7, 30, 90, 365];
    public bool IsAdmin => s.Api.Session?.Role == UserRole.Administrador;
    [ObservableProperty] private int _period = 1;
    [ObservableProperty] private ReportDto? _report;
    [ObservableProperty] private decimal? _purgeDays = 90;
    public ObservableCollection<ReportSection> Sections { get; } = new();
    public ObservableCollection<Kpi> Kpis { get; } = new();

    public override Task OnActivatedAsync() => Run(Load);
    partial void OnPeriodChanged(int value) => _ = Run(Load);
    [RelayCommand] private Task Refresh() => Run(Load);

    public DateTimeOffset From => DateTimeOffset.Now.AddDays(-PeriodDays[Math.Clamp(Period, 0, 3)]);

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
