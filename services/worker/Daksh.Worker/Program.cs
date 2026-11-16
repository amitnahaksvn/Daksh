using Daksh.Domain;
using Daksh.Integrations;
using Daksh.Worker.Jobs;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; });
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.AddSingleton(_ => MongoConnection.CreateDatabase(
    builder.Configuration["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Mongo:ConnectionString (Mongo__ConnectionString) is required."),
    builder.Configuration["Mongo:Database"] ?? throw new InvalidOperationException("Mongo:Database (Mongo__Database) is required.")));
builder.Services.AddDakshIntegrations();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IJobHandler, ReminderDueHandler>();
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddHostedService<JobWorker>();

builder.Build().Run();
