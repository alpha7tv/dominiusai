using System.Text;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

/// <summary>Escolhe o agente de atendimento de uma conversa: lead → campanha de origem (agente de disparo) → agente padrão.</summary>
public class AgentResolver(CrmDbContext db)
{
    public async Task<AgentProfile?> ResolveAttendanceAsync(Lead lead, CancellationToken ct = default)
    {
        if (lead.AttendanceAgentId is { } id && await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Kind == AgentKind.Atendimento, ct) is { } own)
            return own;
        if (lead.OriginCampaignId is { } cid)
        {
            var viaCampaign = await (from c in db.Campaigns.AsNoTracking()
                                     join d in db.AgentProfiles.AsNoTracking() on c.DispatchAgentId equals d.Id
                                     join a in db.AgentProfiles.AsNoTracking() on d.AttendanceAgentId equals a.Id
                                     where c.Id == cid && a.Kind == AgentKind.Atendimento
                                     select a).FirstOrDefaultAsync(ct);
            if (viaCampaign is not null) return viaCampaign;
        }
        return await DefaultAttendanceAsync(ct);
    }

    /// <summary>Agente de atendimento que assume as respostas das campanhas deste agente de disparo.</summary>
    public async Task<AgentProfile?> ForDispatchAsync(AgentProfile dispatch, CancellationToken ct = default) =>
        (dispatch.AttendanceAgentId is { } id ? await db.AgentProfiles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Kind == AgentKind.Atendimento, ct) : null)
        ?? await DefaultAttendanceAsync(ct);

    public Task<AgentProfile?> DefaultAttendanceAsync(CancellationToken ct = default) =>
        db.AgentProfiles.AsNoTracking().Where(a => a.Kind == AgentKind.Atendimento && a.Status != AgentStatus.Inativo)
            .OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>Perguntas de qualificação do agente (as gerais + as específicas dele), em ordem.</summary>
    public async Task<List<QualificationQuestion>> QuestionsAsync(Guid? agentId, CancellationToken ct = default) =>
        (await db.QualificationQuestions.AsNoTracking().Where(q => q.Active && (q.AgentId == null || q.AgentId == agentId)).ToListAsync(ct))
            .OrderBy(q => q.Order).ThenBy(q => q.Question).ToList();
}

/// <summary>
/// Transferência para atendimento humano (automática ou manual): pausa a IA, cancela envios automáticos,
/// atribui o atendente disponível com menos conversas abertas, gera a ficha (resumo, interesse, objeções,
/// dúvidas pendentes, pontuação), registra o horário e notifica os aplicativos.
/// </summary>
public class HandoffService(CrmDbContext db, ConversationService conversations, EvolutionGateway gateway, IHubContext<CrmHub> hub, TimeProvider clock)
{
    public const string DefaultNotice = "Certo! Vou passar sua conversa para um atendente da nossa equipe, que continua com você por aqui em instantes.";

    /// <summary>
    /// Aplica a transferência sobre o lead rastreado (não salva: quem chama salva junto com as demais mudanças).
    /// <paramref name="noticeToCustomer"/> põe na fila um aviso ao cliente (só quando a IA não vai mandar o dela).
    /// </summary>
    public async Task<string?> ApplyAsync(Lead lead, string reason, string actor, bool noticeToCustomer, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await conversations.CancelAutomatedAsync(lead.Id, "Transferido para atendimento humano", ct);
        ConversationStateMachine.Apply(lead, ConversationTrigger.TransferirHumano);
        lead.Priority = lead.Priority < Priority.Alta ? Priority.Alta : lead.Priority;
        lead.NextAction = "Atendimento humano: " + reason;
        lead.HandoffAt = now;

        string? assigned = null;
        if (lead.AssignedUserId is null && await PickAttendantAsync(ct) is { } user) { lead.AssignedUserId = user.Id; assigned = user.DisplayName; }
        else if (lead.AssignedUserId is { } uid) assigned = await db.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

        var questions = await db.QualificationQuestions.AsNoTracking().Where(q => q.Active && (q.AgentId == null || q.AgentId == lead.AttendanceAgentId)).ToListAsync(ct);
        var facts = lead.Facts.Count > 0 ? lead.Facts : await db.LeadFacts.AsNoTracking().Where(f => f.LeadId == lead.Id).ToListAsync(ct);
        lead.HandoffSummary = BuildSummary(lead, facts, questions, reason, actor, assigned, now);
        lead.ConcurrencyStamp = Guid.NewGuid();
        lead.UpdatedAt = now;

        db.Messages.Add(new Message { LeadId = lead.Id, Direction = MessageDirection.Saida, Author = MessageAuthor.Sistema, Status = MessageStatus.Recebida, Timestamp = now,
            Text = "Transferido para atendimento humano" + (assigned is null ? " (aguardando atendente)" : $" — atribuído a {assigned}") + ".\n" + lead.HandoffSummary });
        db.AuditLogs.Add(new AuditLog { Actor = actor, Action = "conversation.handoff", EntityType = "Lead", EntityId = lead.Id.ToString(), Details = $"{reason}; atendente: {assigned ?? "nenhum disponível"}" });

        if (noticeToCustomer && await gateway.GetConfigAsync(ct) is { } evo)
            db.OutboundQueue.Add(new OutboundQueueItem { LeadId = lead.Id, Kind = OutboundKind.Transicao, Text = DefaultNotice, InstanceName = evo.InstanceName, NotBefore = now,
                IdempotencyKey = $"handoff:{lead.Id:N}:{now.ToUnixTimeSeconds()}" });
        return assigned;
    }

    public Task NotifyAsync(Guid leadId, string? assigned, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(HubEvents.Handoff, leadId, assigned, ct);

    /// <summary>Atendente ativo (Atendente/Supervisor; senão qualquer ativo) com menos conversas humanas abertas.</summary>
    public async Task<User?> PickAttendantAsync(CancellationToken ct = default)
    {
        var users = await db.Users.AsNoTracking().Where(u => u.Active).ToListAsync(ct);
        if (users.Count == 0) return null;
        var pool = users.Where(u => u.Role != UserRole.Administrador).ToList();
        if (pool.Count == 0) pool = users;
        var open = await db.Leads.AsNoTracking().Where(l => l.Mode == ConversationMode.AtendimentoHumano && l.AssignedUserId != null)
            .GroupBy(l => l.AssignedUserId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return pool.OrderBy(u => open.FirstOrDefault(o => o.Key == u.Id)?.N ?? 0).ThenBy(u => u.Role).ThenBy(u => u.DisplayName).First();
    }

    public static string BuildSummary(Lead lead, IReadOnlyList<LeadFact> facts, IReadOnlyList<QualificationQuestion> questions, string reason, string actor, string? assigned, DateTimeOffset at)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FICHA DA TRANSFERÊNCIA ({at.ToOffset(TimeSpan.FromHours(-3)):dd/MM HH:mm}, por {actor})");
        sb.AppendLine("Motivo: " + reason);
        if (!string.IsNullOrWhiteSpace(lead.ConversationSummary)) sb.AppendLine("Resumo: " + lead.ConversationSummary);
        var interest = new List<string>();
        if (!string.IsNullOrWhiteSpace(lead.InterestPlanCode)) interest.Add("plano " + lead.InterestPlanCode);
        interest.AddRange(facts.Where(f => f.Key is "produto_interesse" or "interesse").Select(f => f.Value));
        if (interest.Count > 0) sb.AppendLine("Interesse: " + string.Join("; ", interest.Distinct()));
        var objections = facts.Where(f => f.Key.StartsWith("objecao")).Select(f => f.Value).Distinct().ToList();
        if (objections.Count > 0) sb.AppendLine("Objeções: " + string.Join("; ", objections));
        var doubts = facts.Where(f => f.Key is "duvida_pendente" or "pergunta_pendente" or "duvida").Select(f => f.Value).Distinct().ToList();
        if (doubts.Count > 0) sb.AppendLine("Dúvidas pendentes: " + string.Join("; ", doubts));
        var prices = facts.Where(f => f.Key is "preco_apresentado" or "proposta_enviada").Select(f => f.Value).Distinct().ToList();
        if (prices.Count > 0) sb.AppendLine("Preços/propostas apresentados: " + string.Join("; ", prices));
        var tmp = new Lead { Experience = lead.Experience, ActiveClients = lead.ActiveClients, MainPain = lead.MainPain, InterestPlanCode = lead.InterestPlanCode,
            InvestmentRange = lead.InvestmentRange, Stage = lead.Stage, Facts = facts.ToList() };
        var score = QualificationScorer.Compute(tmp, questions);
        sb.Append($"Qualificação: {score.Score}/100");
        if (score.Pending.Count > 0) sb.Append(" · falta saber: " + string.Join("; ", score.Pending.Take(3)));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Aplica a ação configurada para a intenção detectada numa mensagem recebida (antes da IA).</summary>
public class IntentRouter(AgentResolver agents, HandoffService handoff)
{
    /// <returns>Nome do atendente quando houve transferência; string vazia se transferiu sem atendente; null se não transferiu.</returns>
    public async Task<string?> RouteAsync(Lead lead, IntentCategory intent, CancellationToken ct = default)
    {
        if (intent is IntentCategory.Nenhuma or IntentCategory.Descadastro) return null; // descadastro tem fluxo próprio
        if (lead.Mode is not (ConversationMode.Ia or ConversationMode.AguardandoContato or ConversationMode.Prospeccao)) return null;
        var agent = await agents.ResolveAttendanceAsync(lead, ct);
        var action = IntentDetector.ParseActions(agent?.IntentActions).GetValueOrDefault(intent, IntentAction.Continuar);
        switch (action)
        {
            case IntentAction.TransferirHumano:
                return await handoff.ApplyAsync(lead, IntentDetector.Title(intent), "Sistema (intenção)", noticeToCustomer: true, ct) ?? "";
            case IntentAction.PriorizarEContinuar:
                if (lead.Priority < Priority.Alta) lead.Priority = Priority.Alta;
                return null;
            case IntentAction.Encerrar:
                ConversationStateMachine.Apply(lead, ConversationTrigger.Encerrar);
                return null;
        }
        return null;
    }
}

/// <summary>Cria as perguntas de qualificação padrão na primeira execução (o administrador pode editar ou excluir depois).</summary>
public static class AgentSeed
{
    /// <summary>
    /// Agente de atendimento inicial (revenda IPTV), com tom e respostas a objeções baseados na pesquisa de prospecção.
    /// Usa o prompt de sistema global (campo Prompt vazio) e os modelos globais; o administrador edita tudo na tela.
    /// </summary>
    public static AgentProfile DefaultAttendanceAgent() => new()
    {
        Kind = AgentKind.Atendimento, Name = "Atendimento IPTV (padrão)", IsDefault = true, Status = AgentStatus.Ativo, Company = "Dominius Play",
        Description = "Atende quem responde às campanhas ou chama no WhatsApp: entende a operação, qualifica e passa para o time fechar.",
        Objective = "Qualificar revendedores de IPTV (iniciantes, experientes e quem quer marca própria), recomendar o plano aprovado adequado e transferir para humano quando houver intenção de compra.",
        Personality = "Consultiva, não empurradora: entende antes de oferecer. Honesta: confirma que é assistente virtual se perguntarem. Usa o nome quando o cliente informar.",
        Tone = "WhatsApp, cordial e direto: 1 a 3 frases por mensagem, uma pergunta por vez, linguagem simples (\"tudo bem?\", \"perfeito\"), no máximo 1 emoji, sem gírias forçadas.",
        ProductsAndServices = "Planos mensalistas de créditos, apontamento de estrutura para quem já tem Painel Sigma, apontamento sem limite contratado, franquia completa com marca própria (painel, DNS, app e identidade visual). Preços e condições: somente os PLANOS APROVADOS.",
        AllowedAnswers = """
"Quanto custa?": responda com o plano aprovado que combina com o que a pessoa contou e faça uma pergunta. Se ainda não sabe o tamanho da operação, pergunte antes de dar preço.
"Tá caro / achei mais barato": não dê desconto. Diga que além do preço do crédito vale comparar estabilidade e suporte, e pergunte quanto paga hoje e que problema tem com o atual.
"Trava? Garante que não cai?": seja honesta: não dá para garantir zero travamento, porque depende também da internet do cliente. Ofereça só o que estiver na base (teste, suporte).
"Tem todos os canais / futebol?": só confirme o que estiver na base de conhecimento aprovada; senão diga que vai pedir para o time confirmar.
"É golpe? É confiável?": dê só dados objetivos da base (tempo de mercado, forma de contratação, teste) e ofereça falar com uma pessoa do time.
"Vou pensar": "Claro! Quer que eu te mande um resumo para ver com calma?" e respeite a resposta, sem insistir.
"Não tenho interesse": agradeça e encerre ("Tudo bem, obrigado pelo retorno! Fico à disposição."), sem novos contatos.
"Me tire da lista / pare": confirme a remoção ("Pronto, removi seu número e você não vai mais receber nossas mensagens. Desculpe o incômodo.") e encerre.
Sinal de compra (como pago, quero, forma de pagamento): resuma o plano escolhido e transfira para o time finalizar. Nunca gere cobrança nem passe chave Pix.
""".Trim(),
        NegotiationRestrictions = "Nenhum desconto, brinde, prazo ou condição especial além do que está nos PLANOS APROVADOS. Pedido de desconto ou condição diferente: diga que vai ver com o time e transfira.",
        HandoffCriteria = "Pedido de pessoa/atendente; intenção de compra, pagamento ou proposta formal; desconto ou condição fora da tabela; reclamação séria ou cliente muito insatisfeito; pergunta sem resposta na base aprovada.",
        IntentActions = "PedidoHumano=TransferirHumano;IntencaoCompra=PriorizarEContinuar;Negociacao=PriorizarEContinuar;Reclamacao=PriorizarEContinuar",
    };

    public const string SeededKey = "qualification.seeded";

    public static async Task EnsureAsync(CrmDbContext db, SettingsStore settings, CancellationToken ct = default)
    {
        if (await settings.GetAsync(SeededKey, ct) == "true") return;
        if (!await db.QualificationQuestions.AnyAsync(ct))
            db.QualificationQuestions.AddRange(QualificationScorer.Defaults());
        if (!await db.MessageTemplates.AnyAsync(t => t.Purpose == "Acompanhamento", ct))
        {
            // Acompanhamento em 3 etapas (D+1, D+3, D+7) — fica EM REVISÃO até o administrador aprovar cada versão
            var t = new MessageTemplate { Name = "Acompanhamento revenda (3 etapas)", Purpose = "Acompanhamento" };
            string[] steps =
            [
                "Oi{, nome}, aqui é {assistente}, assistente virtual da Dominius Play. Passando só para saber se viu minha mensagem: você trabalha com revenda de IPTV? Se não quiser receber mensagens, responda SAIR.",
                "Oi{, nome}! {assistente} da Dominius Play de novo (assistente virtual). Muita gente que revende reclama de crédito caro e suporte lento. Se isso te interessar, te conto como funciona aqui; se não, sem problema. Responda SAIR para não receber mais.",
                "Última mensagem para não te incomodar{, nome}: aqui é {assistente}, assistente virtual da Dominius Play. Se um dia quiser conhecer, é só me chamar aqui. Para não receber mais nada, responda SAIR.",
            ];
            for (var i = 0; i < steps.Length; i++)
                t.Versions.Add(new MessageTemplateVersion { TemplateId = t.Id, Version = i + 1, Body = steps[i], Approval = ApprovalStatus.EmRevisao });
            db.MessageTemplates.Add(t);
        }
        if (!await db.AgentProfiles.AnyAsync(a => a.Kind == AgentKind.Atendimento, ct))
            db.AgentProfiles.Add(DefaultAttendanceAgent());
        await db.SaveChangesAsync(ct);
        await settings.SetAsync(SeededKey, "true", ct: ct);
    }
}
