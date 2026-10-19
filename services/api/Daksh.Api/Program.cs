using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using Daksh.Api.Modules;
using Daksh.Domain;
using Daksh.Integrations;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });

builder.Services.AddDbContext<DakshDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Daksh")));
builder.Services.AddScoped<IAuditLog, AuditLog>();
builder.Services.AddDakshIntegrations();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddDakshAuth(builder.Configuration, builder.Environment);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<DakshDbContext>("postgres", tags: ["ready"]);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

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

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup") && app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<DakshDbContext>().Database.Migrate();
}

app.Run();

public partial class Program;
