using System.Text;
using Daksh.Gateway.Auth;
using Daksh.Gateway.Hubs;
using Daksh.Gateway.Modules;
using Daksh.Platform;
using Daksh.Platform.Data;
using Daksh.Platform.Identity;
using Daksh.Platform.Runtime;
using Daksh.Platform.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.AddDakshPlatform(builder.Configuration);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    var jwt = builder.Configuration.GetSection("Auth:Jwt");
    var signingKey = jwt["SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey))
        throw new InvalidOperationException("Auth:Jwt:SigningKey must be configured (base64, at least 32 bytes). See .env.example.");
    // Without this, the handler silently renames "sub" to the long legacy ClaimTypes.NameIdentifier URI on
    // validation, which would not match the short claim names TokenService issues and TenantMiddleware reads.
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt["Issuer"], ValidAudience = jwt["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(signingKey)),
        ClockSkew = TimeSpan.FromSeconds(30),
    };
    // Lets the SignalR hub authenticate from a query-string access_token, the only option for WebSocket clients.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var accessToken = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(accessToken) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = accessToken;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
    if (origins is { Length: > 0 }) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

builder.Services.AddSignalR();
builder.Services.AddSingleton<Daksh.Platform.Runtime.IExecutionEventSink, SignalRExecutionEventSink>();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PlatformDbContext>("postgres", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("daksh-gateway"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(Daksh.Platform.Providers.OpenAiProvider.HttpClientName))
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation());

var app = builder.Build();

app.UseExceptionHandler(a => a.Run(async ctx =>
{
    var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (error is not null) ctx.RequestServices.GetRequiredService<ILogger<Program>>().LogError(error, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { title = "An unexpected error occurred.", traceId = ctx.TraceIdentifier });
}));
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() => { ctx.Response.Headers["X-Correlation-Id"] = ctx.TraceIdentifier; return Task.CompletedTask; });
    await next();
});
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

if (!app.Environment.IsProduction()) app.MapOpenApi();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
app.MapGet("/api/v1/system/info", () => Results.Ok(new { name = "Daksh Gateway", version = "0.1.0", protocolVersion = "1.0" }));

app.MapAuth();
app.MapWorkspacesAndProviders();
app.MapAgents();
app.MapSessions();
app.MapApprovalsAndAutomations();
app.MapChannels();
app.MapHub<GatewayHub>("/hubs/gateway");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    db.Tenant.SetSystem();
    if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup")) await db.Database.MigrateAsync();

    // Seed the permission catalog: RolePermissions has a foreign key to this table, so every permission a
    // built-in role template grants (see Daksh.Platform.Identity.Permissions) must exist here first.
    var existingPermissions = await db.Permissions.Select(p => p.Key).ToListAsync();
    foreach (var key in Daksh.Platform.Identity.Permissions.All.Keys.Except(existingPermissions))
        db.Permissions.Add(new Daksh.Platform.Domain.Permission { Key = key, Description = Daksh.Platform.Identity.Permissions.All[key] });
    await db.SaveChangesAsync();
    await scope.ServiceProvider.GetRequiredService<ToolRegistry>().SyncDefinitionsAsync(db, scope.ServiceProvider, CancellationToken.None);
}

app.Run();

public partial class Program;
