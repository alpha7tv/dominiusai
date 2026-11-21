using System.Text;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Ai;
using DominiusPlay.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DominiusPlay.Server.Services;

public sealed record ApproachResult(string Text, bool Personalized, string Note, double MaxSimilarity, int Tokens);

/// <summary>
/// Agente de Disparo: gera uma variação natural da abordagem a partir do modelo APROVADO e dos dados reais do contato.
/// A variação não serve para driblar filtros: só redação diferente, mesma identidade, mesmo objetivo e mesma opção de saída.
/// Se a IA falhar, o texto não passar no ApproachValidator ou ficar quase igual a envios recentes, usa-se o modelo aprovado.
/// </summary>
public class ApproachGenerator(CrmDbContext db, AiAgent agent, IAiProvider provider, TimeProvider clock, KnowledgeBaseService kbs)
{
    /// <summary>Similaridade (Jaccard de palavras) a partir da qual a mensagem é considerada repetida demais.</summary>
    public const double SimilarityAlert = 0.85;

    public const string Rules = """
Você escreve UMA mensagem de abordagem comercial para WhatsApp, em português do Brasil, variando a redação do MODELO APROVADO.
Regras obrigatórias:
- Mantenha o objetivo, a identificação da empresa e do remetente como assistente virtual, e a opção de saída exatamente como no modelo (ex.: "responda SAIR").
- Use apenas os dados do contato fornecidos. Se não houver nome, não use nome nenhum. Nunca invente dados pessoais, empresa, cidade, preço, prazo, link, número ou benefício.
- Não finja relação prévia ("como combinamos", "lembra de mim") e não use urgência artificial.
- Mesmo tamanho aproximado do modelo, texto puro, sem markdown, no máximo 1 emoji.
- Escreva de um jeito diferente das MENSAGENS RECENTES, sem copiar frases inteiras delas.
Responda somente com o texto da mensagem.
""";

    public async Task<ApproachResult> GenerateAsync(Lead lead, string approvedText, Campaign campaign, AgentProfile? dispatcher, CancellationToken ct = default)
    {
        var recent = await db.Messages.AsNoTracking().Where(m => m.CampaignId == campaign.Id && m.Direction == Domain.Enums.MessageDirection.Saida)
            .OrderByDescending(m => m.Timestamp).Take(30).Select(m => m.Text).ToListAsync(ct);
        double MaxSim(string t) => recent.Count == 0 ? 0 : recent.Max(r => TemplateRenderer.Similarity(t, r));

        var cfg = (await agent.LoadConfigAsync(ct)).WithAgent(dispatcher);
        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) return new(approvedText, false, "IA sem chave: enviado o modelo aprovado", MaxSim(approvedText), 0);
        if (await agent.TokensUsedTodayAsync(ct) >= cfg.DailyTokenLimit) return new(approvedText, false, "Limite diário da IA: enviado o modelo aprovado", MaxSim(approvedText), 0);

        var company = string.IsNullOrWhiteSpace(dispatcher?.Company) ? "Dominius Play" : dispatcher!.Company;
        var sb = new StringBuilder(Rules);
        if (dispatcher is not null)
        {
            if (!string.IsNullOrWhiteSpace(dispatcher.Personality)) sb.AppendLine("Personalidade: " + dispatcher.Personality);
            if (!string.IsNullOrWhiteSpace(dispatcher.Tone)) sb.AppendLine("Tom: " + dispatcher.Tone);
            if (!string.IsNullOrWhiteSpace(dispatcher.Prompt)) sb.AppendLine("Instruções do agente: " + dispatcher.Prompt);
        }
        if (!string.IsNullOrWhiteSpace(campaign.Objective)) sb.AppendLine("Objetivo da campanha: " + campaign.Objective);
        if (!string.IsNullOrWhiteSpace(campaign.Prompt)) sb.AppendLine("Instruções da campanha: " + campaign.Prompt);
        // Base Dominius Play liberada para o Disparo (Base de conhecimento → Acesso dos agentes): fatos oficiais só para não contradizer
        if ((await kbs.AccessAsync(ct)).Dispatch)
        {
            var cc = await kbs.ConfigAsync(ct);
            sb.AppendLine($"FATOS OFICIAIS (não acrescente nada que o modelo aprovado não diga; servem só para não contradizer): empresa {cc.CompanyName}; teste somente do serviço ({cc.TestHours} h); não existe teste do painel; pagamento via {cc.PaymentMethods}.");
        }

        var user = new StringBuilder();
        user.AppendLine("MODELO APROVADO:\n" + approvedText + "\n");
        user.AppendLine("DADOS DO CONTATO (use só estes):");
        user.AppendLine("- Nome: " + (string.IsNullOrWhiteSpace(lead.Name) ? "não informado" : lead.Name));
        if (!string.IsNullOrWhiteSpace(lead.Company)) user.AppendLine("- Empresa: " + lead.Company);
        if (!string.IsNullOrWhiteSpace(lead.Segment)) user.AppendLine("- Segmento: " + lead.Segment);
        if (!string.IsNullOrWhiteSpace(lead.City)) user.AppendLine("- Cidade: " + lead.City + (string.IsNullOrWhiteSpace(lead.State) ? "" : "/" + lead.State));
        if (recent.Count > 0) user.AppendLine("\nMENSAGENS RECENTES (não repita):\n" + string.Join("\n---\n", recent.Take(3)));

        var tokens = 0;
        string? lastProblem = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var turns = new List<AiTurn> { new AiUserText(user.ToString() + (lastProblem is null ? "" : $"\nA versão anterior foi recusada: {lastProblem}. Corrija.")) };
            var c = await provider.CompleteAsync(new AiRequest(cfg.ApiKey!, cfg.Model, sb.ToString(), turns, null, Math.Min(cfg.MaxTokens, 2000)), ct);
            tokens += c.InputTokens + c.OutputTokens;
            if (!c.Ok || string.IsNullOrWhiteSpace(c.Content)) { lastProblem = c.Error ?? "resposta vazia"; break; }
            var text = c.Content.Trim().Trim('"');
            lastProblem = ApproachValidator.Validate(text, approvedText, company, lead.Name);
            if (lastProblem is not null) continue;
            var sim = MaxSim(text);
            if (sim >= SimilarityAlert && attempt == 0) { lastProblem = $"muito parecida com uma mensagem recente ({sim:P0})"; continue; }
            await agent.AddUsageAsync(tokens, ct);
            return new(text, true, sim >= SimilarityAlert ? $"Personalizada pela IA · ALERTA: {sim:P0} parecida com envio recente" : $"Personalizada pela IA (semelhança máx. {sim:P0})", sim, tokens);
        }
        await agent.AddUsageAsync(tokens, ct);
        return new(approvedText, false, "IA recusada (" + lastProblem + "): enviado o modelo aprovado", MaxSim(approvedText), tokens);
    }
}
