using System.Net;
using System.Net.Http.Json;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Services;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Security;

namespace DominiusPlay.Tests;

/// <summary>Erro 500 no Laboratório IA: a tela nunca deve cair inteira, e o motivo de um erro inesperado vai para o aplicativo e para o log.</summary>
public class LabErrorTests
{
    private static async Task CorruptAiKey(CrmFactory f) => await f.WithDb(async db =>
    {
        // chave gravada com outras chaves de criptografia (ex.: pasta keys trocada): não pode ser lida
        db.Settings.Add(new AppSetting { Key = SettingKeys.AiApiKey, Value = "CfDJ8-valor-que-nao-decifra", IsSecret = true });
        db.Settings.Add(new AppSetting { Key = SettingKeys.AiEnabled, Value = "true" });
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Laboratorio_com_chave_ilegivel_mostra_a_analise_e_explica_em_vez_de_erro_500()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await CorruptAiKey(f);
        var res = await c.PostAsJsonAsync("/api/lab/run", new LabRequest(null, ["Quanto custa o painel de 500 créditos?", "Achei caro, tenho pouco dinheiro para começar"], UseAi: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = (await res.Content.ReadFromJsonAsync<LabResponse>())!;
        Assert.Equal(2, r.Steps.Count);
        Assert.All(r.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Reply)));
        Assert.False(r.AiUsed);
        Assert.Contains("Salve a chave de novo", r.AiError);
        Assert.Contains(Directory.GetFiles(Path.Combine(f.DataDir, "logs")), p => File.ReadAllText(p).Contains("Laboratório"));
    }

    [Fact]
    public async Task Laboratorio_com_perfis_escritos_na_tela_e_Claude_ligado_responde_todos()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        (await c.PutAsJsonAsync("/api/ai/settings", new AiSettingsUpdate(true, "claude-haiku-5-5", "claude-sonnet-5-5", "sk-ant-teste-123", 4000, "Lia", "", 100000, false, 0))).EnsureSuccessStatusCode();
        foreach (var sc in AttendanceLab.Scenarios)
        {
            // o aplicativo manda as mensagens da caixa de texto (sem a chave do perfil)
            var res = await c.PostAsJsonAsync("/api/lab/run", new LabRequest(null, sc.Messages.ToList(), UseAi: true));
            Assert.True(res.IsSuccessStatusCode, sc.Key + ": " + await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Erro_inesperado_devolve_o_motivo_ao_aplicativo_e_grava_no_log()
    {
        using var f = new CrmFactory();
        var c = await f.AdminAsync();
        await CorruptAiKey(f);
        var res = await c.GetAsync("/api/ai/settings");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var op = (await res.Content.ReadFromJsonAsync<OperationResult>())!;
        Assert.False(op.Ok);
        Assert.Contains("logs", op.Message);
        Assert.Contains("Cryptographic", op.Detail);
        Assert.Contains(Directory.GetFiles(Path.Combine(f.DataDir, "logs"), "erros-*.log"), p => File.ReadAllText(p).Contains("GET /api/ai/settings"));
    }

    [Fact]
    public void Resposta_da_base_apagada_nao_derruba_a_analise()
    {
        var entries = DominiusKnowledge.Entries().ToList();
        foreach (var e in entries) { e.Answer = ""; e.Products = ""; }
        var plans = SeedData.CampaignPlans().ToList();
        foreach (var sc in AttendanceLab.Scenarios)
        {
            var steps = AttendanceLab.Run([.. sc.Messages, "Quanto custa? meu nome é ana", "sou o carlos"], entries, new CommercialConfig(), plans);
            Assert.All(steps, s => Assert.False(s.Reply is { Length: 0 }));
        }
    }
}
