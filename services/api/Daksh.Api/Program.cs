using Daksh.Api.Auth;
using Daksh.Api.Hubs;
using Daksh.Api.Infrastructure;
using Daksh.Api.Modules;
using Daksh.Domain;
using Daksh.Integrations;
using Daksh.Platform.Security;
using FluentValidation;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });

builder.Services.AddSingleton(_ => MongoConnection.CreateDatabase(
    builder.Configuration["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Mongo:ConnectionString (Mongo__ConnectionString) is required."),
    builder.Configuration["Mongo:Database"] ?? throw new InvalidOperationException("Mongo:Database (Mongo__Database) is required.")));
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddSingleton<IEmailSender, GmailEmailSender>();
builder.Services.AddSingleton<UserTokenService>();
builder.Services.Configure<SecretProtectorOptions>(builder.Configuration.GetSection("Security:Encryption"));
builder.Services.AddSingleton<ISecretProtector>(sp => new SecretProtector(sp.GetRequiredService<IOptions<SecretProtectorOptions>>().Value));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClaimsPrincipalAccessor>();
builder.Services.AddDakshIntegrations(builder.Configuration);
builder.Services.AddDakshWhatsAppIntegration(builder.Configuration);
builder.Services.AddGmailIntegration();
builder.Services.AddTelegramIntegration(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddHostedService<WhatsAppReminderService>();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddDakshAuth(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

await AccountModule.EnsureAccountIndexesAsync(app.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>());
await IntegrationsModule.EnsureIntegrationIndexesAndCatalogAsync(app.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>());
await GmailIntegrationModule.EnsureGmailIndexesAsync(app.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>());
await GmailIntegrationModule.EnsureGmailManagementIndexesAsync(app.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>());
await TelegramIntegrationModule.EnsureTelegramIndexesAsync(app.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>());
await WhatsAppSettingsModule.EnsureIndexesAsync(app.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>());

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (!app.Environment.IsProduction())
    app.MapOpenApi(); // OpenAPI document at /openapi/v1.json

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapAssistant();
app.MapTasksAndReminders();
app.MapPlatform();
app.MapAccount();
app.MapIntegrations();
app.MapGmailIntegration();
app.MapTelegramIntegration();
app.MapChatAssist();
app.MapDashboardApps();
app.MapDashboardTabs();
app.MapWhatsAppSettings();
app.MapWhatsAppAutomation();
app.MapHub<IntegrationsHub>("/hubs/integrations").RequireAuthorization(Daksh.Api.Auth.AuthSetup.UserScheme);

app.Run();

public partial class Program;
