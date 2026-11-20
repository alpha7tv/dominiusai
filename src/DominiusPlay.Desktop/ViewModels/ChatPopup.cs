using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Desktop.ViewModels;

/// <summary>
/// Conversa do WhatsApp aberta em popup sobre o Kanban (clique no card).
/// Mostra o histórico e permite responder sem sair do quadro. As regras são as mesmas da tela
/// Conversas: para responder é preciso assumir o atendimento (a IA é pausada nesta conversa).
/// </summary>
public partial class ChatPopupViewModel : ViewModelBase
{
    private readonly AppSession _s;
    public ObservableCollection<MessageVm> Messages { get; } = new();
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private Guid? _leadId;
    [ObservableProperty] private LeadDetail? _detail;
    [ObservableProperty] private string _draft = "";

    /// <summary>Disparado depois que o histórico é recarregado (a tela rola até a última mensagem).</summary>
    public event Action? MessagesReloaded;
    /// <summary>Disparado quando o modo da conversa muda (o Kanban recarrega os cards).</summary>
    public event Action? LeadUpdated;

    public ChatPopupViewModel(AppSession s)
    {
        _s = s;
        s.Realtime.MessageAdded += id => Dispatcher.UIThread.Post(async () =>
        {
            if (IsOpen && LeadId == id) await Run(LoadMessages);
        });
        s.Realtime.LeadChanged += id => Dispatcher.UIThread.Post(async () =>
        {
            if (IsOpen && LeadId == id) await Run(LoadDetail);
        });
    }

    public string Title => Detail?.DisplayName ?? "";
    public string Subtitle => Detail is null ? "" : $"{Detail.PhoneFormatted} · {Ui.Stage(Detail.Stage)}";
    public string ModeText => Detail is null ? "" : Ui.Mode(Detail.Mode) + (Detail.AssignedTo is null ? "" : " · " + Detail.AssignedTo);
    public bool IsHuman => Detail?.Mode == ConversationMode.AtendimentoHumano;
    public bool CanTakeOver => Detail is not null && !IsHuman;
    public bool CanReturn => Detail is not null && Detail.Mode is ConversationMode.AtendimentoHumano or ConversationMode.Pausado;
    public bool HasNoMessages => IsOpen && Messages.Count == 0;
    public string ComposerHint => IsHuman
        ? "Digite a mensagem para o WhatsApp do contato (Enter envia, Shift+Enter quebra linha)"
        : "Para responder, clique em ASSUMIR ATENDIMENTO (a IA é pausada nesta conversa)";

    public Task OpenAsync(Guid leadId) => Run(async () =>
    {
        if (LeadId != leadId) { Draft = ""; Messages.Clear(); Detail = null; }
        LeadId = leadId;
        Info = null;
        IsOpen = true;
        await LoadDetail();
        await LoadMessages();
    });

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Info = null; Error = null;
        OnPropertyChanged(nameof(HasNoMessages));
    }

    private async Task LoadDetail()
    {
        if (LeadId is not { } id) return;
        Detail = await _s.Api.LeadAsync(id);
    }

    private async Task LoadMessages()
    {
        if (LeadId is not { } id) return;
        var msgs = await _s.Api.MessagesAsync(id);
        Messages.Clear();
        foreach (var m in msgs) Messages.Add(new MessageVm(m));
        OnPropertyChanged(nameof(HasNoMessages));
        MessagesReloaded?.Invoke();
    }

    partial void OnDetailChanged(LeadDetail? value)
    {
        foreach (var p in new[] { nameof(Title), nameof(Subtitle), nameof(ModeText), nameof(IsHuman), nameof(CanTakeOver), nameof(CanReturn), nameof(ComposerHint) })
            OnPropertyChanged(p);
    }

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(HasNoMessages));

    [RelayCommand] private Task TakeOver() => Act(id => _s.Api.TakeOverAsync(id));
    [RelayCommand] private Task ReturnToAi() => Act(id => _s.Api.ReturnToAiAsync(id));

    private Task Act(Func<Guid, Task<OperationResult>> f) => Run(async () =>
    {
        if (LeadId is not { } id) return;
        var r = await f(id);
        if (r.Ok) Info = r.Message; else Error = r.Message;
        await LoadDetail();
        await LoadMessages();
        LeadUpdated?.Invoke();
    });

    /// <summary>A IA sugere uma resposta para o atendente revisar; nada é enviado automaticamente.</summary>
    [RelayCommand]
    private Task Suggest() => Run(async () =>
    {
        if (LeadId is not { } id) return;
        var r = await _s.Api.SuggestAsync(id);
        if (r.Ok && r.Reply is not null) { Draft = r.Reply; Info = "Sugestão da IA no campo de mensagem. Revise antes de enviar."; }
        else Error = r.Error ?? "A IA não gerou sugestão.";
    });

    [RelayCommand]
    public Task Send() => Run(async () =>
    {
        if (LeadId is not { } id || string.IsNullOrWhiteSpace(Draft)) return;
        var r = await _s.Api.SendAsync(id, Draft);
        if (r.Queued) { Draft = ""; Info = "Mensagem na fila de envio do WhatsApp."; await LoadMessages(); }
        else Error = r.Reason;
    });
}
