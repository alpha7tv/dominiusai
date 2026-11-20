using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using DominiusPlay.Infrastructure.Ai;
using DominiusPlay.Infrastructure.Data;
using DominiusPlay.Infrastructure.Evolution;
using DominiusPlay.Infrastructure.Import;
using DominiusPlay.Infrastructure.Security;
using DominiusPlay.Server.Endpoints;
using DominiusPlay.Server.Hubs;
using DominiusPlay.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "DominiusPlayCRM"); // roda como Serviço do Windows quando instalado assim

// Diretório de dados (banco SQLite, chaves de criptografia, logs)
var dataDir = builder.Configuration["DataDirectory"] is { Length: > 0 } d ? d
    : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DominiusPlay", "CRM")
    : Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

// Banco: "Sqlite" (uma máquina) ou "Postgres" (equipe, fonte única de verdade)
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var conn = builder.Configuration.GetConnectionString("Crm");
builder.Services.AddDbContext<CrmDbContext>(o =>
{
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)) o.UseNpgsql(conn);
    else o.UseSqlite(conn ?? $"Data Source={Path.Combine(dataDir, "crm.db")}");
});

builder.Services.AddDataProtection().SetApplicationName("DominiusPlay.Crm")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

var jwt = new JwtOptions { Key = AuthService.LoadOrCreateKey(dataDir) };
builder.Services.AddSingleton(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Issuer, IssuerSigningKey = jwt.Key,
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
    };
    // SignalR envia o token pela query string
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query["access_token"] is { Count: > 0 } t) ctx.Token = t;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Api.AdminPolicy, p => p.RequireRole("Administrador"))
    .AddPolicy(Api.SupervisorPolicy, p => p.RequireRole("Administrador", "Supervisor"));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddFixedWindowLimiter("webhook", l => { l.PermitLimit = 600; l.Window = TimeSpan.FromMinutes(1); l.QueueLimit = 100; });
});

builder.Services.AddSignalR();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConversationLocks>();
builder.Services.AddSingleton<InstanceStatusCache>();
builder.Services.AddHttpClient<IEvolutionApi, EvolutionApiClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<EvolutionGateway>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<LeadImportService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<InboundProcessor>();
builder.Services.AddScoped<OutboundQueueProcessor>();
builder.Services.AddSingleton<AiDispatcher>();
builder.Services.AddHttpClient<IAiProvider, OpenAiClient>(c => c.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddScoped<AiAgent>();
builder.Services.AddScoped<AiResponder>();
builder.Services.AddScoped<CampaignService>();
builder.Services.AddSingleton(new DataPaths(dataDir));
if (builder.Configuration.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService<OutboundQueueWorker>();
    builder.Services.AddHostedService<InstanceMonitorWorker>();
    builder.Services.AddHostedService<AiWorker>();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
    await db.Database.EnsureCreatedAsync();
    var upgraded = await SchemaUpgrader.UpgradeSqliteAsync(db); // banco de versão anterior: adiciona colunas/tabelas novas
    if (upgraded.Count > 0) app.Logger.LogInformation("Banco atualizado: {Changes}", string.Join(", ", upgraded));
    await SeedData.EnsureSeededAsync(db);
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapCrmApi();
app.MapAiApi();
app.MapPhasesApi();
app.MapHub<CrmHub>("/hubs/crm");
app.Run();

public partial class Program;
