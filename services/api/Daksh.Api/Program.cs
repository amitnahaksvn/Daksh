using Daksh.Api.Auth;
using Daksh.Api.Hubs;
using Daksh.Api.Infrastructure;
using Daksh.Api.Modules;
using Daksh.Domain;
using Daksh.Integrations;
using FluentValidation;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });

builder.Services.AddSingleton(_ => MongoConnection.CreateDatabase(
    builder.Configuration["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Mongo:ConnectionString (Mongo__ConnectionString) is required."),
    builder.Configuration["Mongo:Database"] ?? throw new InvalidOperationException("Mongo:Database (Mongo__Database) is required.")));
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddSingleton<IEmailSender, GmailEmailSender>();
builder.Services.AddSingleton<UserTokenService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClaimsPrincipalAccessor>();
builder.Services.AddDakshIntegrations();
builder.Services.AddDakshWhatsAppIntegration(builder.Configuration);
builder.Services.AddSignalR();
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
app.MapDashboardApps();
app.MapHub<IntegrationsHub>("/hubs/integrations").RequireAuthorization(Daksh.Api.Auth.AuthSetup.UserScheme);

app.Run();

public partial class Program;
