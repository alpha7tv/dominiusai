using System.Net;
using System.Net.Http.Json;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Tests;

/// <summary>CRM v0.4: etapas configuráveis do Kanban e movimentação automática do funil com auditoria.</summary>
public class CrmTests
{
    [Fact]
    public void Movimentacao_automatica_so_avanca()
    {
        var l = new Lead();
        Assert.NotNull(KanbanRules.AutoAdvance(l, KanbanStage.AbordagemRealizada, "x"));
        Assert.NotNull(KanbanRules.AutoAdvance(l, KanbanStage.Respondeu, "x"));
        Assert.NotNull(KanbanRules.AutoAdvance(l, KanbanStage.EmAtendimento, "x"));
        Assert.Null(KanbanRules.AutoAdvance(l, KanbanStage.Respondeu, "x")); // não volta
        Assert.NotNull(KanbanRules.AutoAdvance(l, KanbanStage.Qualificado, "x"));
        Assert.Equal(4, l.StageChanges.Count);
        Assert.All(l.StageChanges, c => Assert.Equal("Sistema", c.ChangedBy));
        var closed = new Lead { Stage = KanbanStage.NegocioFechado };
        Assert.Null(KanbanRules.AutoAdvance(closed, KanbanStage.Qualificado, "x"));
        var lost = new Lead { Stage = KanbanStage.NaoTemInteresse };
        Assert.Null(KanbanRules.AutoAdvance(lost, KanbanStage.Respondeu, "x"));
    }

    [Fact]
    public void Configuracao_das_etapas_valida_regras()
    {
        var d = KanbanBoardConfig.Default.ToList();
        Assert.Equal(10, d.Count(s => s.Visible));
        Assert.Null(KanbanBoardConfig.Validate(d));
        Assert.NotNull(KanbanBoardConfig.Validate(d.Select(s => s.Stage == KanbanStage.NegocioFechado ? s with { Visible = false } : s).ToList()));
        Assert.NotNull(KanbanBoardConfig.Validate(d.Select(s => s.Stage == KanbanStage.Respondeu ? s with { Title = "Qualificado" } : s).ToList()));
        Assert.NotNull(KanbanBoardConfig.Validate([.. d, d[0]]));
        // configuração antiga sem etapas novas: entram com o padrão
        var parsed = KanbanBoardConfig.Parse("""[{"Stage":1,"Title":"Novo contato","Order":1,"Visible":true}]""");
        Assert.Equal(11, parsed.Count);
        Assert.Equal("Novo contato", parsed.Single(s => s.Stage == KanbanStage.PrimeiroContato).Title);
    }

    [Fact]
    public async Task Etapas_renomeadas_e_ocultas_no_quadro_com_auditoria()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        var stages = (await c.GetFromJsonAsync<List<KanbanStageDto>>("/api/kanban/stages"))!;
        Assert.Equal(11, stages.Count);
        var edited = stages.Select(s => s.Stage switch
        {
            KanbanStage.PrimeiroContato => s with { Title = "Novo contato" },
            KanbanStage.NegocioFechado => s with { Title = "Venda concluída" },
            KanbanStage.EmFaseDeTeste => s with { Visible = false },
            _ => s,
        }).ToList();
        (await c.PutAsJsonAsync("/api/kanban/stages", edited)).EnsureSuccessStatusCode();
        var board = (await c.GetFromJsonAsync<List<KanbanColumn>>("/api/kanban"))!;
        Assert.Equal(9, board.Count);
        Assert.Equal("Novo contato", board[0].Title);
        Assert.Contains(board, b => b.Title == "Venda concluída");
        Assert.DoesNotContain(board, b => b.Stage == KanbanStage.EmFaseDeTeste);
        Assert.Equal(1, await f.WithDb(db => db.AuditLogs.CountAsync(a => a.Action == "kanban.stages")));
        // regra do sistema não pode ser ocultada
        var bad = await c.PutAsJsonAsync("/api/kanban/stages", stages.Select(s => s.Stage == KanbanStage.NaoTemInteresse ? s with { Visible = false } : s).ToList());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
