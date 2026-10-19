using Daksh.Domain;
using Daksh.Integrations;
using Daksh.Worker.Jobs;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.AddDbContext<DakshDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Daksh")));
builder.Services.AddDakshIntegrations();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IJobHandler, ReminderDueHandler>();
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddHostedService<JobWorker>();

builder.Build().Run();
