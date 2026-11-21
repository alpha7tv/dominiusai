using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DominiusPlay.Domain.Contracts;
using DominiusPlay.Domain.Entities;
using DominiusPlay.Domain.Enums;

namespace DominiusPlay.Desktop.Services;

public class ApiException(string message) : Exception(message);

/// <summary>Cliente HTTP do servidor central. O aplicativo Windows nunca fala direto com a Evolution API nem guarda chaves.</summary>
public class ApiClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web); // enums trafegam como números (padrão do servidor)
    private readonly HttpClient _http;
    public string BaseUrl { get; }
    public string? Token { get; private set; }
    public LoginResponse? Session { get; private set; }

    public ApiClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    private void SetSession(LoginResponse r)
    {
        Session = r; Token = r.Token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", r.Token);
    }

    public async Task<bool> NeedsSetupAsync() => (await Get<SetupStatus>("api/auth/setup")).NeedsSetup;

    public async Task LoginAsync(string user, string pass)
    {
        var res = await _http.PostAsJsonAsync("api/auth/login", new LoginRequest(user, pass), Json);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ApiException("Usuário ou senha inválidos.");
        SetSession(await Read<LoginResponse>(res));
    }

    public async Task SetupAsync(string user, string display, string pass) =>
        SetSession(await Read<LoginResponse>(await _http.PostAsJsonAsync("api/auth/setup", new SetupRequest(user, display, pass), Json)));

    public Task<DashboardDto> DashboardAsync() => Get<DashboardDto>("api/dashboard");
    public Task<AgentsDashboardDto> AgentsDashboardAsync(DateTimeOffset? from, Guid? agentId, Guid? campaignId) =>
        Get<AgentsDashboardDto>("api/dashboard/agents?" + string.Join("&", new[] {
            from is { } f ? "from=" + Uri.EscapeDataString(f.ToString("O")) : null, agentId is { } a ? $"agentId={a}" : null, campaignId is { } c ? $"campaignId={c}" : null }.Where(x => x is not null)));
    public Task<List<KanbanColumn>> KanbanAsync() => Get<List<KanbanColumn>>("api/kanban");
    public Task<List<AutomationFlowDto>> AutomationsAsync() => Get<List<AutomationFlowDto>>("api/automations");
    public Task<AutomationFlowDto> CreateAutomationAsync(AutomationFlowUpsert f) => Post<AutomationFlowDto>("api/automations", f);
    public Task<AutomationFlowDto> UpdateAutomationAsync(Guid id, AutomationFlowUpsert f) => Send<AutomationFlowDto>(HttpMethod.Put, $"api/automations/{id}", f);
    public Task<OperationResult> DeleteAutomationAsync(Guid id) => Send<OperationResult>(HttpMethod.Delete, $"api/automations/{id}", null);
    public Task<AutomationTestResponse> TestAutomationAsync(AutomationTestRequest r) => Post<AutomationTestResponse>("api/automations/test", r);
    public Task<List<KanbanStageDto>> KanbanStagesAsync() => Get<List<KanbanStageDto>>("api/kanban/stages");
    public Task<OperationResult> SaveKanbanStagesAsync(List<KanbanStageDto> s) => Send<OperationResult>(HttpMethod.Put, "api/kanban/stages", s);
    public Task<MoveCardResponse> MoveAsync(Guid id, KanbanStage to, string? reason, bool confirm) => Post<MoveCardResponse>($"api/kanban/{id}/move", new MoveCardRequest(to, reason, confirm));
    public Task<List<LeadCard>> LeadsAsync(string? q = null, ConsentStatus? consent = null) =>
        Get<List<LeadCard>>($"api/leads?take=2000{(string.IsNullOrWhiteSpace(q) ? "" : "&q=" + Uri.EscapeDataString(q))}{(consent is null ? "" : "&consent=" + consent)}");
    public Task<LeadDetail> LeadAsync(Guid id) => Get<LeadDetail>($"api/leads/{id}");
    public Task<LeadCard> CreateLeadAsync(LeadUpsert l) => Post<LeadCard>("api/leads", l);
    public Task<OperationResult> UpdateLeadAsync(Guid id, LeadUpsert l) => Send<OperationResult>(HttpMethod.Put, $"api/leads/{id}", l);
    public Task<OperationResult> ConsentAsync(Guid id, ConsentRequest r) => Post<OperationResult>($"api/leads/{id}/consent", r);
    public Task<OperationResult> DeleteLeadAsync(Guid id) => Send<OperationResult>(HttpMethod.Delete, $"api/leads/{id}", null);
    public Task<OperationResult> AnonymizeLeadAsync(Guid id) => Post<OperationResult>($"api/leads/{id}/anonymize", null);
    public Task<BulkLeadResponse> BulkLeadsAsync(BulkLeadRequest r) => Post<BulkLeadResponse>(r.Action == BulkLeadAction.Excluir ? "api/leads/bulk-delete" : "api/leads/bulk", r);
    public Task<OperationResult> OptOutAsync(Guid id) => Post<OperationResult>($"api/leads/{id}/optout", null);
    public Task<List<LeadCard>> ConversationsAsync(string? q = null) => Get<List<LeadCard>>("api/conversations" + (string.IsNullOrWhiteSpace(q) ? "" : "?q=" + Uri.EscapeDataString(q)));
    public Task<List<MessageDto>> MessagesAsync(Guid id) => Get<List<MessageDto>>($"api/conversations/{id}/messages");
    public Task<OperationResult> TakeOverAsync(Guid id) => Post<OperationResult>($"api/conversations/{id}/takeover", null);
    public Task<OperationResult> ReturnToAiAsync(Guid id) => Post<OperationResult>($"api/conversations/{id}/return-to-ai", null);
    public Task<OperationResult> PauseAsync(Guid id, bool pause) => Post<OperationResult>($"api/conversations/{id}/pause?pause={pause}", null);
    public Task<SendMessageResponse> SendAsync(Guid id, string text) => Post<SendMessageResponse>($"api/conversations/{id}/send", new SendMessageRequest(text));
    public Task<ImportResponse> ImportPasteAsync(string text, string? source, string? tags, string? consentSource = null) => Post<ImportResponse>("api/import/paste", new PasteImportRequest(text, source, tags, consentSource));
    public Task<OperationResult> ImportOptOutsAsync(string text, string reason) => Post<OperationResult>("api/import/optouts", new OptOutImportRequest(text, reason));

    public async Task<ImportResponse> ImportFileAsync(string path, string? source, string? tags, string? consentSource = null)
    {
        await using var fs = File.OpenRead(path);
        return await ImportFileAsync(fs, Path.GetFileName(path), source, tags, consentSource);
    }

    /// <summary>Importação a partir de um fluxo (navegador: o arquivo escolhido não tem caminho local).</summary>
    public async Task<ImportResponse> ImportFileAsync(Stream fs, string fileName, string? source, string? tags, string? consentSource = null)
    {
        using var form = new MultipartFormDataContent { { new StreamContent(fs), "file", fileName } };
        if (source is not null) form.Add(new StringContent(source), "source");
        if (tags is not null) form.Add(new StringContent(tags), "tags");
        if (consentSource is not null) form.Add(new StringContent(consentSource), "consentSource");
        return await Read<ImportResponse>(await _http.PostAsync("api/import/file", form));
    }

    public Task<List<PlanDto>> PlansAsync() => Get<List<PlanDto>>("api/plans");
    public Task<PlanDto> NewPlanVersionAsync(PlanUpsert p) => Post<PlanDto>("api/plans", p);
    public Task<PlanDto> ApprovePlanAsync(Guid id) => Post<PlanDto>($"api/plans/{id}/approve", null);
    public Task<List<KnowledgeDto>> KnowledgeAsync() => Get<List<KnowledgeDto>>("api/knowledge");
    public Task<List<TemplateDto>> TemplatesAsync() => Get<List<TemplateDto>>("api/templates");

    public Task<AiSettingsDto> AiSettingsAsync() => Get<AiSettingsDto>("api/ai/settings");
    public Task<OperationResult> SaveAiSettingsAsync(AiSettingsUpdate u) => Send<OperationResult>(HttpMethod.Put, "api/ai/settings", u);
    public Task<ModelsResponse> AiModelsAsync() => Get<ModelsResponse>("api/ai/models");
    public Task<AiSimulateResponse> AiSimulateAsync(AiSimulateRequest r) => Post<AiSimulateResponse>("api/ai/simulate", r);
    public Task<AiSimulateResponse> SuggestAsync(Guid leadId) => Post<AiSimulateResponse>($"api/conversations/{leadId}/suggest", null);
    public Task<List<AgentDto>> AgentsAsync(AgentKind kind) => Get<List<AgentDto>>($"api/agents?kind={kind}");
    public Task<AgentDto> CreateAgentAsync(AgentUpsert a) => Post<AgentDto>("api/agents", a);
    public Task<AgentDto> UpdateAgentAsync(Guid id, AgentUpsert a) => Send<AgentDto>(HttpMethod.Put, $"api/agents/{id}", a);
    public Task<OperationResult> DeleteAgentAsync(Guid id) => Send<OperationResult>(HttpMethod.Delete, $"api/agents/{id}", null);
    public Task<List<IntentInfo>> IntentsAsync() => Get<List<IntentInfo>>("api/agents/intents");
    public Task<AgentTestResponse> TestAgentAsync(Guid? agentId, AgentTestRequest r) => Post<AgentTestResponse>(agentId is { } id ? $"api/agents/{id}/test" : "api/agents/global/test", r);
    public Task<List<QualificationQuestionDto>> QuestionsAsync(Guid? agentId) => Get<List<QualificationQuestionDto>>("api/qualification-questions" + (agentId is { } id ? "?agentId=" + id : ""));
    public Task<QualificationQuestionDto> CreateQuestionAsync(QualificationQuestionUpsert q) => Post<QualificationQuestionDto>("api/qualification-questions", q);
    public Task<QualificationQuestionDto> UpdateQuestionAsync(Guid id, QualificationQuestionUpsert q) => Send<QualificationQuestionDto>(HttpMethod.Put, $"api/qualification-questions/{id}", q);
    public Task<OperationResult> DeleteQuestionAsync(Guid id) => Send<OperationResult>(HttpMethod.Delete, $"api/qualification-questions/{id}", null);
    public Task<OperationResult> HandoffAsync(Guid leadId, string? reason) => Post<OperationResult>($"api/leads/{leadId}/handoff", new HandoffRequest(reason));
    public Task<OperationResult> ApproveKnowledgeAsync(Guid id, bool approve) => Post<OperationResult>($"api/knowledge/{id}/approve?approve={approve}", null);

    public Task<EvolutionSettingsDto> EvolutionSettingsAsync() => Get<EvolutionSettingsDto>("api/integrations/evolution");
    public Task<OperationResult> SaveEvolutionAsync(EvolutionSettingsUpdate u) => Send<OperationResult>(HttpMethod.Put, "api/integrations/evolution", u);
    public Task<EvolutionStatusDto> EvolutionStatusAsync() => Get<EvolutionStatusDto>("api/integrations/evolution/status");
    public Task<OperationResult> RegisterWebhookAsync() => Post<OperationResult>("api/integrations/evolution/webhook", null);
    public Task<OperationResult> TestMessageAsync(string phone, string text) => Post<OperationResult>("api/integrations/evolution/test-message", new TestMessageRequest(phone, text));
    public Task<List<WebhookEventDto>> WebhookEventsAsync() => Get<List<WebhookEventDto>>("api/integrations/evolution/events");

    // Fase 6
    public Task<OperationResult> SaveQualificationAsync(Guid id, QualificationUpdate q) => Send<OperationResult>(HttpMethod.Put, $"api/leads/{id}/qualification", q);
    public Task<OperationResult> AddFactAsync(Guid id, FactCreate f) => Post<OperationResult>($"api/leads/{id}/facts", f);
    public Task<MoveCardResponse> AcceptSuggestionAsync(Guid id, bool confirm) => Post<MoveCardResponse>($"api/leads/{id}/suggestion/accept?confirm={confirm}", null);
    public Task<OperationResult> RejectSuggestionAsync(Guid id) => Post<OperationResult>($"api/leads/{id}/suggestion/reject", null);
    // Fase 7
    public Task<UserDto> MeAsync() => Get<UserDto>("api/me");
    public Task<OperationResult> ChangePasswordAsync(string current, string next) => Post<OperationResult>("api/me/password", new ChangePasswordRequest(current, next));
    public Task<List<UserDto>> UsersAsync() => Get<List<UserDto>>("api/users");
    public Task<UserDto> CreateUserAsync(UserCreate u) => Post<UserDto>("api/users", u);
    public Task<OperationResult> UpdateUserAsync(Guid id, UserUpdate u) => Send<OperationResult>(HttpMethod.Put, $"api/users/{id}", u);
    public Task<OperationResult> AssignAsync(Guid leadId, Guid? userId) => Post<OperationResult>($"api/conversations/{leadId}/assign", new AssignRequest(userId));
    // Fase 8
    public Task<OperationResult> CreateTemplateAsync(TemplateCreate t) => Post<OperationResult>("api/templates", t);
    public Task<OperationResult> NewTemplateVersionAsync(Guid templateId, string body) => Post<OperationResult>($"api/templates/{templateId}/versions", new TemplateVersionCreate(body));
    public Task<OperationResult> ApproveTemplateAsync(Guid versionId, string? channelName) => Post<OperationResult>($"api/templates/versions/{versionId}/approve", new TemplateApprove(channelName));
    public Task<OperationResult> ArchiveTemplateAsync(Guid versionId) => Post<OperationResult>($"api/templates/versions/{versionId}/archive", null);
    public Task<TemplatePreview> PreviewTemplateAsync(string body) => Post<TemplatePreview>("api/templates/preview", new TemplatePreviewRequest(body, null));
    public Task<List<CampaignDto>> CampaignsAsync() => Get<List<CampaignDto>>("api/campaigns");
    public Task<CampaignDto> CreateCampaignAsync(CampaignUpsert c) => Post<CampaignDto>("api/campaigns", c);
    public Task<CampaignDto> UpdateCampaignAsync(Guid id, CampaignUpsert c) => Send<CampaignDto>(HttpMethod.Put, $"api/campaigns/{id}", c);
    public Task<CampaignValidation> ValidateCampaignAsync(Guid id) => Post<CampaignValidation>($"api/campaigns/{id}/validate", null);
    public Task<List<ApproachPreview>> PreviewCampaignAsync(Guid id) => Post<List<ApproachPreview>>($"api/campaigns/{id}/preview", null);
    public Task<OperationResult> CampaignActionAsync(Guid id, string action) => Post<OperationResult>($"api/campaigns/{id}/{action}", null);
    // Fase 9
    public Task<ReportDto> ReportAsync(DateTimeOffset from, DateTimeOffset to) =>
        Get<ReportDto>($"api/reports?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}");
    public Task<SentMessagesReportDto> SentMessagesReportAsync(DateTimeOffset from, DateTimeOffset to, string? campaign, string? author) =>
        Get<SentMessagesReportDto>($"api/reports/sent-messages?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}"
            + (string.IsNullOrEmpty(campaign) ? "" : "&campaign=" + Uri.EscapeDataString(campaign))
            + (string.IsNullOrEmpty(author) ? "" : "&author=" + Uri.EscapeDataString(author)));
    public async Task<byte[]> DownloadAsync(string url)
    {
        var res = await Wrap(() => _http.GetAsync(url));
        if (!res.IsSuccessStatusCode) await Read<OperationResult>(res);
        return await res.Content.ReadAsByteArrayAsync();
    }
    public Task<OperationResult> BackupAsync() => Post<OperationResult>("api/maintenance/backup", null);
    public Task<OperationResult> PurgeAsync(int days) => Post<OperationResult>($"api/maintenance/purge?days={days}", null);
    public Task<List<ReportRow>> BackupsAsync() => Get<List<ReportRow>>("api/maintenance/backups");

    // Base Dominius Play, Comercial e Laboratório (v0.4.1)
    public Task<List<KbEntryDto>> KbAsync(string? q = null, KbStatus? status = null) =>
        Get<List<KbEntryDto>>("api/kb?" + (string.IsNullOrWhiteSpace(q) ? "" : "q=" + Uri.EscapeDataString(q) + "&") + (status is null ? "" : "status=" + (int)status));
    public Task<List<string>> KbCategoriesAsync() => Get<List<string>>("api/kb/categories");
    public Task<List<ReportRow>> KbPlaceholdersAsync() => Get<List<ReportRow>>("api/kb/placeholders");
    public Task<KbEntryDto> CreateKbAsync(KbEntryUpsert r) => Post<KbEntryDto>("api/kb", r);
    public Task<KbEntryDto> UpdateKbAsync(Guid id, KbEntryUpsert r) => Send<KbEntryDto>(HttpMethod.Put, $"api/kb/{id}", r);
    public Task<OperationResult> SetKbStatusAsync(Guid id, KbStatus status) => Post<OperationResult>($"api/kb/{id}/status?status={(int)status}", null);
    public Task<OperationResult> DeleteKbAsync(Guid id) => Send<OperationResult>(HttpMethod.Delete, $"api/kb/{id}", null);
    public Task<List<KbRevisionDto>> KbHistoryAsync(Guid id) => Get<List<KbRevisionDto>>($"api/kb/{id}/history");
    public Task<KbTestResponse> TestKbAsync(KbTestRequest r) => Post<KbTestResponse>("api/kb/test", r);
    public Task<List<KbEntryUpsert>> ExportKbAsync() => Get<List<KbEntryUpsert>>("api/kb/export");
    public Task<KbImportResult> ImportKbAsync(KbImportRequest r) => Post<KbImportResult>("api/kb/import", r);
    public Task<KbAccessDto> KbAccessAsync() => Get<KbAccessDto>("api/kb/access");
    public Task<OperationResult> SaveKbAccessAsync(KbAccessDto r) => Send<OperationResult>(HttpMethod.Put, "api/kb/access", r);
    public Task<DominiusPlay.Domain.Services.CommercialConfig> CommercialAsync() => Get<DominiusPlay.Domain.Services.CommercialConfig>("api/commercial");
    public Task<OperationResult> SaveCommercialAsync(DominiusPlay.Domain.Services.CommercialConfig c) => Send<OperationResult>(HttpMethod.Put, "api/commercial", c);
    public Task<List<CommercialHistoryDto>> CommercialHistoryAsync() => Get<List<CommercialHistoryDto>>("api/commercial/history");
    public Task<List<LabScenarioDto>> LabScenariosAsync() => Get<List<LabScenarioDto>>("api/lab/scenarios");
    public Task<LabResponse> RunLabAsync(LabRequest r) => Post<LabResponse>("api/lab/run", r);

    private async Task<T> Get<T>(string url) => await Read<T>(await Wrap(() => _http.GetAsync(url)));
    private Task<T> Post<T>(string url, object? body) => Send<T>(HttpMethod.Post, url, body);
    private async Task<T> Send<T>(HttpMethod m, string url, object? body)
    {
        var res = await Wrap(() => _http.SendAsync(new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body, options: Json) }));
        return await Read<T>(res);
    }

    private static async Task<HttpResponseMessage> Wrap(Func<Task<HttpResponseMessage>> f)
    {
        try { return await f(); }
        catch (HttpRequestException ex) { throw new ApiException("Servidor do CRM inacessível: " + ex.Message); }
        catch (TaskCanceledException) { throw new ApiException("Tempo esgotado ao falar com o servidor do CRM."); }
    }

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ApiException("Sessão expirada. Entre novamente.");
        if (res.StatusCode == HttpStatusCode.Forbidden) throw new ApiException("Seu perfil não tem permissão para esta ação.");
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync();
            try { var op = JsonSerializer.Deserialize<OperationResult>(body, Json); if (op?.Message is { Length: > 0 }) throw new ApiException(op.Message + (op.Detail is null ? "" : $" ({op.Detail})")); }
            catch (JsonException) { }
            throw new ApiException($"Erro {(int)res.StatusCode} do servidor.");
        }
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }
}
