using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;

namespace DominiusPlay.Desktop.ViewModels;

/// <summary>Pergunta de qualificação editável na tela.</summary>
public partial class QuestionRow : ObservableObject
{
    public Guid? Id { get; init; }
    [ObservableProperty] private int _order;
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private int _weight = 10;
    [ObservableProperty] private string _scoringHint = "";
    [ObservableProperty] private bool _active = true;
    /// <summary>true = pergunta geral (todos os agentes); false = só deste agente.</summary>
    [ObservableProperty] private bool _global;
}

/// <summary>Ação configurada para uma intenção.</summary>
public partial class IntentRow : ObservableObject
{
    public IntentCategory Intent { get; init; }
    public string Title { get; init; } = "";
    public bool Locked => Intent == IntentCategory.Descadastro;
    [ObservableProperty] private int _actionIndex;
    public static string[] Actions { get; } = ["Continuar com a IA", "Transferir para humano", "Prioridade alta e continuar", "Encerrar conversa"];
}

public record TestLine(bool FromContact, string Text, string? Detail)
{
    public string Who => FromContact ? "Cliente (você)" : "Agente";
}

/// <summary>
/// Agentes de IA (atendimento ou disparo): lista, editor do perfil, perguntas de qualificação, ações por intenção
/// e painel "Testar Agente" (conversa simulada: nada é salvo nem enviado pelo WhatsApp).
/// </summary>
public partial class AgentsViewModel(AppSession s, AgentKind kind) : ViewModelBase
{
    public AgentKind Kind { get; } = kind;
    public string Title => Kind == AgentKind.Atendimento ? "Agentes de atendimento" : "Agentes de disparo";
    public string Subtitle => Kind == AgentKind.Atendimento
        ? "Cada agente conversa com os clientes com o próprio prompt, tom, produtos, restrições e critérios de transferência. Teste antes de ativar."
        : "Cada agente de disparo tem número, horário, limites e campanhas próprios.";
    public bool IsAttendance => Kind == AgentKind.Atendimento;

    public ObservableCollection<AgentDto> Agents { get; } = new();
    public ObservableCollection<QuestionRow> Questions { get; } = new();
    public ObservableCollection<IntentRow> Intents { get; } = new();
    public ObservableCollection<TestLine> TestLines { get; } = new();
    public static string[] Statuses { get; } = ["Ativo", "Pausado", "Inativo"];

    [ObservableProperty] private AgentDto? _selected;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _assistantName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _objective = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _personality = "";
    [ObservableProperty] private string _tone = "";
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private string _products = "";
    [ObservableProperty] private string _allowedAnswers = "";
    [ObservableProperty] private string _restrictions = "";
    [ObservableProperty] private string _handoffCriteria = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _advancedModel = "";
    [ObservableProperty] private int _statusIndex;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private string _startTime = "09:00";
    [ObservableProperty] private string _endTime = "21:00";
    [ObservableProperty] private bool _sun, _mon = true, _tue = true, _wed = true, _thu = true, _fri = true, _sat = true;
    [ObservableProperty] private string _timeZoneId = "America/Sao_Paulo";
    [ObservableProperty] private string _instanceName = "";
    [ObservableProperty] private int _dailyLimit = 50;
    [ObservableProperty] private int _minIntervalMinutes = 5;

    // Testar Agente
    [ObservableProperty] private string _testInput = "";
    [ObservableProperty] private string? _testStatus;
    [ObservableProperty] private string? _testMemory;
    [ObservableProperty] private string? _testScore;
    private Guid? _testSession;

    public override Task OnActivatedAsync() => Run(async () =>
    {
        if (Intents.Count == 0)
            foreach (var i in await s.Api.IntentsAsync()) Intents.Add(new IntentRow { Intent = i.Intent, Title = i.Title, ActionIndex = (int)i.DefaultAction });
        await Reload(Selected?.Id);
    });

    private async Task Reload(Guid? keep)
    {
        var list = await s.Api.AgentsAsync(Kind);
        Agents.Clear(); foreach (var a in list) Agents.Add(a);
        Selected = Agents.FirstOrDefault(a => a.Id == keep) ?? Agents.FirstOrDefault();
        if (Selected is null) await LoadQuestions(null);
    }

    partial void OnSelectedChanged(AgentDto? value)
    {
        if (value is null) return;
        IsNew = false;
        Name = value.Name; AssistantName = value.AssistantName; Description = value.Description; Objective = value.Objective; Company = value.Company;
        Personality = value.Personality; Tone = value.Tone; Prompt = value.Prompt; Products = value.ProductsAndServices; AllowedAnswers = value.AllowedAnswers;
        Restrictions = value.NegotiationRestrictions; HandoffCriteria = value.HandoffCriteria; Model = value.Model; AdvancedModel = value.AdvancedModel;
        StatusIndex = (int)value.Status; IsDefault = value.IsDefault; StartTime = value.StartTime; EndTime = value.EndTime; TimeZoneId = value.TimeZoneId;
        InstanceName = value.InstanceName; DailyLimit = value.DailyLimit; MinIntervalMinutes = Math.Max(5, value.MinIntervalSeconds / 60);
        var days = BusinessHours.ParseDays(value.AllowedDays);
        Sun = days.Contains(DayOfWeek.Sunday); Mon = days.Contains(DayOfWeek.Monday); Tue = days.Contains(DayOfWeek.Tuesday); Wed = days.Contains(DayOfWeek.Wednesday);
        Thu = days.Contains(DayOfWeek.Thursday); Fri = days.Contains(DayOfWeek.Friday); Sat = days.Contains(DayOfWeek.Saturday);
        var map = IntentDetector.ParseActions(value.IntentActions);
        foreach (var r in Intents) r.ActionIndex = (int)map.GetValueOrDefault(r.Intent, IntentAction.Continuar);
        ResetTestState();
        _ = Run(() => LoadQuestions(value.Id));
    }

    private async Task LoadQuestions(Guid? agentId)
    {
        Questions.Clear();
        foreach (var q in await s.Api.QuestionsAsync(null))
            Questions.Add(new QuestionRow { Id = q.Id, Order = q.Order, Key = q.Key, Question = q.Question, Weight = q.Weight, ScoringHint = q.ScoringHint, Active = q.Active, Global = true });
        if (agentId is { } id)
            foreach (var q in await s.Api.QuestionsAsync(id))
                Questions.Add(new QuestionRow { Id = q.Id, Order = q.Order, Key = q.Key, Question = q.Question, Weight = q.Weight, ScoringHint = q.ScoringHint, Active = q.Active, Global = false });
    }

    private string Days() => string.Join(",", new[] { (Sun, 0), (Mon, 1), (Tue, 2), (Wed, 3), (Thu, 4), (Fri, 5), (Sat, 6) }.Where(d => d.Item1).Select(d => d.Item2));

    public AgentUpsert BuildUpsert() => new(Kind, Name, AssistantName, Description, Objective, Company, Personality, Tone, Prompt, Products, AllowedAnswers,
        Restrictions, HandoffCriteria, Model, AdvancedModel, (AgentStatus)StatusIndex, IsDefault, StartTime, EndTime, Days(), TimeZoneId,
        IntentDetector.FormatActions(Intents.ToDictionary(i => i.Intent, i => (IntentAction)i.ActionIndex)), InstanceName, DailyLimit, MinIntervalMinutes * 60,
        Selected?.AttendanceAgentId);

    [RelayCommand]
    private void New()
    {
        Selected = null; IsNew = true;
        Name = Kind == AgentKind.Atendimento ? "Novo agente de atendimento" : "Novo agente de disparo";
        AssistantName = Description = Objective = Personality = Tone = Prompt = Products = AllowedAnswers = Restrictions = HandoffCriteria = Model = AdvancedModel = InstanceName = "";
        Company = "Dominius Play"; StatusIndex = (int)AgentStatus.Pausado; IsDefault = false; StartTime = "09:00"; EndTime = "21:00";
        Sun = false; Mon = Tue = Wed = Thu = Fri = Sat = true; DailyLimit = 50; MinIntervalMinutes = 5;
        foreach (var r in Intents) r.ActionIndex = (int)(r.Locked ? IntentAction.Encerrar : IntentDetector.DefaultAction(r.Intent));
        ResetTestState();
        Info = "Preencha e clique em Salvar. O agente começa pausado: teste antes de ativar.";
    }

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        var r = IsNew || Selected is null ? await s.Api.CreateAgentAsync(BuildUpsert()) : await s.Api.UpdateAgentAsync(Selected.Id, BuildUpsert());
        foreach (var q in Questions.Where(q => !q.Global || q.Id is null).ToList())
        {
            var up = new QualificationQuestionUpsert(q.Global ? null : r.Id, q.Order, q.Key, q.Question, q.Weight, q.ScoringHint, q.Active);
            if (q.Id is { } qid) await s.Api.UpdateQuestionAsync(qid, up); else await s.Api.CreateQuestionAsync(up);
        }
        foreach (var q in Questions.Where(q => q.Global && q.Id is { }))
            await s.Api.UpdateQuestionAsync(q.Id!.Value, new QualificationQuestionUpsert(null, q.Order, q.Key, q.Question, q.Weight, q.ScoringHint, q.Active));
        await Reload(r.Id);
        Info = $"Agente \"{r.Name}\" salvo.";
    });

    [RelayCommand]
    private Task Delete() => Run(async () =>
    {
        if (Selected is null) return;
        var r = await s.Api.DeleteAgentAsync(Selected.Id);
        await Reload(null);
        Info = r.Message;
    });

    [RelayCommand]
    private void AddQuestion() => Questions.Add(new QuestionRow { Order = Questions.Count + 1, Key = "nova_pergunta_" + (Questions.Count + 1), Question = "", Weight = 10, Global = Selected is null });

    [RelayCommand]
    private Task RemoveQuestion(QuestionRow? q) => Run(async () =>
    {
        if (q is null) return;
        if (q.Id is { } id) await s.Api.DeleteQuestionAsync(id);
        Questions.Remove(q);
    });

    // ------------------------------------------------ Testar Agente

    private void ResetTestState()
    {
        _testSession = null; TestLines.Clear(); TestMemory = null; TestScore = null;
        TestStatus = "Simulação: escreva como se fosse o cliente. Nada é salvo nem enviado pelo WhatsApp (só usa tokens da IA). Salve antes de testar mudanças.";
    }

    [RelayCommand]
    private void ResetTest() => ResetTestState();

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        var text = TestInput.Trim();
        if (text.Length == 0) return;
        if (IsNew) { Error = "Salve o agente antes de testar."; return; }
        TestLines.Add(new TestLine(true, text, null));
        TestInput = "";
        var r = await s.Api.TestAgentAsync(Selected?.Id, new AgentTestRequest(_testSession, text));
        _testSession = r.SessionId;
        var detail = $"Intenção: {r.FinalIntent}" + (r.Actions.Count > 0 ? " · " + string.Join(" · ", r.Actions) : "") + (r.Tokens > 0 ? $" · {r.Tokens} tokens" : "");
        if (!r.Ok) TestLines.Add(new TestLine(false, "Erro: " + r.Error, null));
        else if (r.Reply is { } reply) TestLines.Add(new TestLine(false, reply, detail));
        else TestLines.Add(new TestLine(false, "(sem resposta)", detail));
        if (r.HandoffSummary is { } h) TestLines.Add(new TestLine(false, "→ Transferido para humano. Ficha que o atendente recebe:\n" + h, null));
        TestScore = r.Score is { } sc ? $"Qualificação: {sc}/100" : null;
        TestMemory = (r.ScoreExplanation is { Length: > 0 } ex ? ex + "\n\n" : "") + "Memória:\n" + string.Join("\n", r.Memory);
        TestStatus = r.Transferred ? "Conversa transferida: o agente não responde mais nesta simulação. Clique em Reiniciar." : r.Closed ? "Conversa encerrada pelo agente." : null;
    });
}
