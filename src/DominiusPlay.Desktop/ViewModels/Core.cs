using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Desktop.Services;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;

namespace DominiusPlay.Desktop.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _info;

    /// <summary>Executa uma ação mostrando carregamento e mensagens claras de erro.</summary>
    protected async Task Run(Func<Task> action)
    {
        IsBusy = true; Error = null;
        try { await action(); }
        catch (ApiException ex) { Error = ex.Message; }
        catch (Exception ex) { Error = "Erro inesperado: " + ex.Message; }
        finally { IsBusy = false; }
    }

    public virtual Task OnActivatedAsync() => Task.CompletedTask;
}

/// <summary>Contexto compartilhado entre as telas após o login.</summary>
public class AppSession(ApiClient api, Realtime realtime)
{
    public ApiClient Api { get; } = api;
    public Realtime Realtime { get; } = realtime;
}

public record NavItem(string Key, string Icon, string Title);

public partial class MainWindowViewModel : ViewModelBase
{
    public static readonly NavItem[] Menu =
    [
        new("dashboard", "▦", "Dashboard"), new("atendimento", "🤖", "Atendimento IA"), new("disparo", "🚀", "Disparo IA"), new("conversas", "💬", "Conversas"), new("kanban", "▥", "Kanban"),
        new("leads", "👥", "Leads"), new("campanhas", "📣", "Campanhas"), new("mensagens", "✉", "Mensagens"),
        new("planos", "💲", "Planos"), new("conhecimento", "📚", "Base de conhecimento"), new("relatorios", "📊", "Relatórios"),
        new("equipe", "👤", "Equipe"), new("integracoes", "🔌", "Integrações"), new("config", "⚙", "Configurações"),
    ];

    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _activeKey = "dashboard";
    [ObservableProperty] private string? _userName;
    [ObservableProperty] private string _connectionText = "WhatsApp: verificando…";
    [ObservableProperty] private string? _notification;
    private AppSession? _session;
    private readonly Dictionary<string, ViewModelBase> _pages = new();

    public MainWindowViewModel()
    {
        var login = new LoginViewModel();
        login.LoggedIn += OnLoggedIn;
        _current = login;
    }

    private async void OnLoggedIn(AppSession s) => await StartSessionAsync(s);

    public async Task StartSessionAsync(AppSession s)
    {
        _session = s;
        UserName = s.Api.Session?.DisplayName;
        IsLoggedIn = true;
        s.Realtime.ConnectionChanged += st => Dispatcher.UIThread.Post(() => ConnectionText = DescribeState(st));
        s.Realtime.MessageAdded += _ => Dispatcher.UIThread.Post(() => Notification = $"Nova atividade às {DateTime.Now:HH:mm}");
        await Navigate("dashboard");
        try { var st = await s.Api.EvolutionStatusAsync(); ConnectionText = st.Configured ? DescribeState(st.Reachable ? st.State : "unreachable") : "WhatsApp: não configurado"; }
        catch { ConnectionText = "WhatsApp: indisponível"; }
    }

    public static string DescribeState(string? st) => st switch
    {
        "open" => "● WhatsApp conectado",
        "connecting" => "◐ WhatsApp conectando",
        "close" => "○ WhatsApp desconectado",
        "unreachable" => "○ Evolution API inacessível",
        _ => "WhatsApp: " + (st ?? "desconhecido"),
    };

    [RelayCommand]
    private async Task Navigate(string key)
    {
        if (_session is null) return;
        ActiveKey = key;
        if (!_pages.TryGetValue(key, out var vm))
        {
            vm = key switch
            {
                "dashboard" => new DashboardViewModel(_session),
                "conversas" => new ConversationsViewModel(_session),
                "kanban" => new KanbanViewModel(_session),
                "leads" => new LeadsViewModel(_session),
                "planos" => new PlansViewModel(_session),
                "conhecimento" => new KnowledgeViewModel(_session),
                "mensagens" => new TemplatesViewModel(_session),
                "integracoes" => new IntegrationsViewModel(_session),
                "campanhas" => new CampaignsViewModel(_session),
                "relatorios" => new ReportsViewModel(_session),
                "equipe" => new TeamViewModel(_session),
                "config" => new AiSettingsViewModel(_session),
                "atendimento" => new AgentsViewModel(_session, AgentKind.Atendimento),
                "disparo" => new AgentsViewModel(_session, AgentKind.Disparo),
                _ => new PendingModuleViewModel(key, "", ""),
            };
            _pages[key] = vm;
        }
        Current = vm;
        await vm.OnActivatedAsync();
    }

    [RelayCommand]
    private void Logout()
    {
        _pages.Clear(); _session = null; IsLoggedIn = false;
        var login = new LoginViewModel();
        login.LoggedIn += OnLoggedIn;
        Current = login;
    }
}

public partial class LoginViewModel : ViewModelBase
{
    private readonly LocalSettings _settings = LocalSettings.Load();
    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _userName;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _needsSetup;
    public event Action<AppSession>? LoggedIn;

    public LoginViewModel() { _serverUrl = _settings.ServerUrl; _userName = _settings.LastUser ?? ""; }

    [RelayCommand]
    private Task CheckServer() => Run(async () =>
    {
        var api = new ApiClient(ServerUrl);
        NeedsSetup = await api.NeedsSetupAsync();
        Info = NeedsSetup ? "Primeiro acesso: crie o usuário administrador." : "Servidor encontrado.";
    });

    [RelayCommand]
    private Task Enter() => Run(async () =>
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out _)) { Error = "Endereço do servidor inválido."; return; }
        var api = new ApiClient(ServerUrl);
        if (NeedsSetup)
        {
            if (Password.Length < 8) { Error = "A senha deve ter pelo menos 8 caracteres."; return; }
            await api.SetupAsync(UserName, string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName, Password);
        }
        else await api.LoginAsync(UserName, Password);
        _settings.ServerUrl = ServerUrl; _settings.LastUser = UserName; _settings.Save();
        var rt = new Realtime();
        await rt.StartAsync(api);
        LoggedIn?.Invoke(new AppSession(api, rt));
    });
}

public partial class PendingModuleViewModel(string title, string phase, string description) : ViewModelBase
{
    public string Title { get; } = title;
    public string Phase { get; } = phase;
    public string Description { get; } = description;
}

public static class Ui
{
    public static string Ago(DateTimeOffset? t)
    {
        if (t is null) return "—";
        var d = DateTimeOffset.UtcNow - t.Value;
        return d.TotalMinutes < 1 ? "agora" : d.TotalHours < 1 ? $"há {(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"há {(int)d.TotalHours} h" : $"há {(int)d.TotalDays} d";
    }

    public static string Mode(ConversationMode m) => KanbanRules.ModeTitle(m);
    public static string Pain(PainCategory p) => PainClassifier.Title(p);
    public static string Stage(KanbanStage s) => KanbanRules.Title(s);

    public static string StageColor(KanbanStage s) => s switch
    {
        KanbanStage.PrimeiroContato => "#5B7FD6",
        KanbanStage.EmFaseDeTeste => "#C9962E",
        KanbanStage.EmNegociacao => "#8B6FD6",
        KanbanStage.NegocioFechado => "#2E9E6E",
        KanbanStage.EmDuvidas => "#D9822B",
        _ => "#7A8194",
    };

    public static string Consent(ConsentStatus c) => c switch
    {
        ConsentStatus.Valido => "Autorizado",
        ConsentStatus.Revogado => "Descadastrado",
        _ => "Sem autorização",
    };
}
