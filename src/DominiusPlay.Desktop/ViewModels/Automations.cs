using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;

namespace DominiusPlay.Desktop.ViewModels;

/// <summary>Bloco editável do fluxo.</summary>
public partial class BlockRow : ObservableObject
{
    public static string[] Types { get; } = Enum.GetValues<AutomationBlockType>().Select(AutomationEngine.Title).ToArray();
    [ObservableProperty] private string _id = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDecision), nameof(Hint), nameof(Color), nameof(HasParam), nameof(NextLabel))] private int _typeIndex;
    [ObservableProperty] private string _param = "";
    [ObservableProperty] private string _next = "";
    [ObservableProperty] private string _nextNo = "";
    public AutomationBlockType Type => (AutomationBlockType)TypeIndex;
    public bool IsDecision => AutomationEngine.IsDecision(Type);
    public bool HasParam => AutomationEngine.ParamHint(Type).Length > 0;
    public string Hint => AutomationEngine.ParamHint(Type);
    public string NextLabel => IsDecision ? "Se sim →" : "Próximo →";
    public string Color => Type switch
    {
        AutomationBlockType.Inicio or AutomationBlockType.EventoRecebido => "#5B7FD6",
        AutomationBlockType.VerificarConsentimento or AutomationBlockType.VerificarCondicao or AutomationBlockType.AnalisarIntencao => "#C9962E",
        AutomationBlockType.ExecutarIa or AutomationBlockType.ConsultarBase => "#8B6FD6",
        AutomationBlockType.EnviarMensagem or AutomationBlockType.TransferirAtendente => "#2E9E6E",
        AutomationBlockType.Encerrar or AutomationBlockType.AguardarResposta => "#7A8194",
        _ => "#3FA7A0",
    };

    public AutomationBlock ToBlock() => new(Id.Trim(), Type, string.IsNullOrWhiteSpace(Param) ? null : Param.Trim(),
        string.IsNullOrWhiteSpace(Next) ? null : Next.Trim(), IsDecision && !string.IsNullOrWhiteSpace(NextNo) ? NextNo.Trim() : null);
}

/// <summary>Construtor de automações: lista de fluxos, editor de blocos (sim/não), validação e modo de teste sem envio.</summary>
public partial class AutomationsViewModel(AppSession s) : ViewModelBase
{
    public static string[] Triggers { get; } = ["Cada mensagem recebida", "Só resposta a campanha"];
    public static string[] Stages { get; } = Enum.GetValues<KanbanStage>().Select(KanbanRules.Title).ToArray();
    private static readonly KanbanStage[] StageValues = Enum.GetValues<KanbanStage>();
    public bool IsAdmin => s.Api.Session?.Role == UserRole.Administrador;

    public ObservableCollection<AutomationFlowDto> Flows { get; } = new();
    public ObservableCollection<BlockRow> Blocks { get; } = new();
    public ObservableCollection<string> Problems { get; } = new();
    public ObservableCollection<string> Trace { get; } = new();
    public ObservableCollection<string> Actions { get; } = new();

    [ObservableProperty] private AutomationFlowDto? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private int _triggerIndex;
    [ObservableProperty] private bool _active;
    [ObservableProperty] private int _addTypeIndex = (int)AutomationBlockType.VerificarCondicao;

    // modo de teste
    [ObservableProperty] private string _testMessage = "quanto custa para revender?";
    [ObservableProperty] private bool _testConsent = true;
    [ObservableProperty] private int _testStageIndex = Array.IndexOf(Enum.GetValues<KanbanStage>(), KanbanStage.Respondeu);
    [ObservableProperty] private string _testTags = "";
    [ObservableProperty] private int _testScore;
    [ObservableProperty] private string? _testSummary;

    public override Task OnActivatedAsync() => Run(async () =>
    {
        var keep = Selected?.Id;
        Flows.Clear(); foreach (var f in await s.Api.AutomationsAsync()) Flows.Add(f);
        Selected = Flows.FirstOrDefault(f => f.Id == keep) ?? Flows.FirstOrDefault();
        if (Selected is null) New();
    });

    partial void OnSelectedChanged(AutomationFlowDto? value)
    {
        if (value is null) return;
        Name = value.Name; Description = value.Description; TriggerIndex = (int)value.Trigger; Active = value.Active;
        Blocks.Clear();
        foreach (var b in value.Blocks) Blocks.Add(new BlockRow { Id = b.Id, TypeIndex = (int)b.Type, Param = b.Param ?? "", Next = b.Next ?? "", NextNo = b.NextNo ?? "" });
        Problems.Clear(); Trace.Clear(); Actions.Clear(); TestSummary = null;
    }

    /// <summary>Fluxo novo já vem com um exemplo útil: pedido de preço marca tag, move o card e deixa a IA responder.</summary>
    [RelayCommand]
    private void New()
    {
        Selected = null;
        Name = "Novo fluxo"; Description = ""; TriggerIndex = 0; Active = false;
        Blocks.Clear();
        Blocks.Add(new BlockRow { Id = "1", TypeIndex = (int)AutomationBlockType.Inicio, Next = "2" });
        Blocks.Add(new BlockRow { Id = "2", TypeIndex = (int)AutomationBlockType.VerificarConsentimento, Next = "3", NextNo = "9" });
        Blocks.Add(new BlockRow { Id = "3", TypeIndex = (int)AutomationBlockType.AnalisarIntencao, Param = "SolicitacaoPreco", Next = "4", NextNo = "5" });
        Blocks.Add(new BlockRow { Id = "4", TypeIndex = (int)AutomationBlockType.AdicionarTag, Param = "quer-preco", Next = "5" });
        Blocks.Add(new BlockRow { Id = "5", TypeIndex = (int)AutomationBlockType.ExecutarIa, Next = "9" });
        Blocks.Add(new BlockRow { Id = "9", TypeIndex = (int)AutomationBlockType.Encerrar });
        Problems.Clear(); Trace.Clear(); Actions.Clear(); TestSummary = null;
    }

    [RelayCommand]
    private void AddBlock()
    {
        var n = 1;
        while (Blocks.Any(b => b.Id == n.ToString())) n++;
        Blocks.Add(new BlockRow { Id = n.ToString(), TypeIndex = AddTypeIndex });
    }

    [RelayCommand]
    private void RemoveBlock(BlockRow? b) { if (b is not null) Blocks.Remove(b); }

    private AutomationFlowUpsert Build() => new(Name, Description, (AutomationTrigger)TriggerIndex, Active, Blocks.Select(b => b.ToBlock()).ToList());

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        var saved = Selected is null ? await s.Api.CreateAutomationAsync(Build()) : await s.Api.UpdateAutomationAsync(Selected.Id, Build());
        Info = saved.Active ? $"Fluxo salvo e ativo (versão {saved.Version})." : $"Fluxo salvo como rascunho inativo (versão {saved.Version}).";
        Flows.Clear(); foreach (var f in await s.Api.AutomationsAsync()) Flows.Add(f);
        Selected = Flows.FirstOrDefault(f => f.Id == saved.Id);
    });

    [RelayCommand]
    private Task Delete() => Run(async () =>
    {
        if (Selected is null) return;
        Info = (await s.Api.DeleteAutomationAsync(Selected.Id)).Message;
        Selected = null;
        await OnActivatedAsync();
    });

    /// <summary>Valida e simula o fluxo (mesmo sem salvar) com um contato fictício. Nada é gravado nem enviado.</summary>
    [RelayCommand]
    private Task Test() => Run(async () =>
    {
        var r = await s.Api.TestAutomationAsync(new AutomationTestRequest(Build().Blocks, TestMessage, TestConsent, false, StageValues[Math.Clamp(TestStageIndex, 0, StageValues.Length - 1)],
            TestTags, TestScore));
        Problems.Clear(); foreach (var p in r.Problems) Problems.Add(p);
        Trace.Clear(); foreach (var t in r.Trace) Trace.Add(t);
        Actions.Clear(); foreach (var a in r.Actions) Actions.Add(a);
        TestSummary = !r.Valid ? "Fluxo com problemas: corrija antes de ativar."
            : $"Intenção detectada: {r.Intent}. " + (r.AiWouldReply ? "O Agente de Atendimento IA responde depois do fluxo." : "A IA não responde (o fluxo respondeu, transferiu ou a conversa não está com a IA).");
    });
}
