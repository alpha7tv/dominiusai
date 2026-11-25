using System.Collections.ObjectModel;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Enums;
using Xunit;

namespace DominiusPlay.Tests;

/// <summary>Atualização das listas sem recriar tudo (rolagem e seleção ficam onde estavam).</summary>
public class CardSyncTests
{
    private static LeadCard Card(Guid id, string name, string? last = null) => new(id, name, "5514999990000", "+55 (14) 99999-0000",
        KanbanStage.PrimeiroContato, ConversationMode.Ia, Priority.Normal, null, null, PainCategory.Nenhuma, last, null, null, null, ConsentStatus.Valido, "");

    [Fact]
    public void Mantem_os_itens_que_nao_mudaram_e_troca_so_os_alterados()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var list = new ObservableCollection<LeadCardVm>();
        CardSync.Sync(list, [Card(a, "A"), Card(b, "B"), Card(c, "C")]);
        var vmA = list[0]; var vmC = list[2];

        // B recebeu mensagem e subiu para o topo; C saiu; D entrou
        var d = Guid.NewGuid();
        CardSync.Sync(list, [Card(b, "B", "oi"), Card(a, "A"), Card(d, "D")]);

        Assert.Equal([b, a, d], list.Select(x => x.Id));
        Assert.Same(vmA, list[1]);
        Assert.Equal("oi", list[0].LastMessage);
        Assert.DoesNotContain(vmC, list);
    }

    [Fact]
    public void Lista_sem_nada_em_comum_e_recriada()
    {
        var list = new ObservableCollection<LeadCardVm>();
        CardSync.Sync(list, [Card(Guid.NewGuid(), "A")]);
        var novos = new[] { Card(Guid.NewGuid(), "X"), Card(Guid.NewGuid(), "Y") };
        CardSync.Sync(list, novos);
        Assert.Equal(novos.Select(x => x.Id), list.Select(x => x.Id));
        CardSync.Sync(list, []);
        Assert.Empty(list);
    }
}
