using System.Text.Json;
using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using Daksh.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Api.Modules;

public sealed record CreateTaskRequest(string? Title, DateTimeOffset? DueAt);
public sealed record TaskDto(Guid Id, string Title, DateTimeOffset? DueAt, bool Completed);
public sealed record CreateReminderRequest(string? Text, DateTimeOffset RemindAt);
public sealed record ReminderDto(Guid Id, string Text, DateTimeOffset RemindAt);

public sealed class CreateTaskValidator : AbstractValidator<CreateTaskRequest>
{
    public CreateTaskValidator() => RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
}

public sealed class CreateReminderValidator : AbstractValidator<CreateReminderRequest>
{
    public CreateReminderValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(500);
        RuleFor(x => x.RemindAt).GreaterThan(DateTimeOffset.UtcNow.AddSeconds(-5))
            .WithMessage("RemindAt must not be in the past.");
    }
}

public static class TasksModule
{
    public static void MapTasksAndReminders(this IEndpointRouteBuilder app)
    {
        var t = app.MapGroup("/api/v1/tasks").RequireAuthorization().WithTags("Tasks");

        t.MapGet("/", async (HttpContext http, DakshDbContext db, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            return Results.Ok(await db.Tasks.Where(x => x.UserId == u).OrderBy(x => x.CreatedAt)
                .Select(x => new TaskDto(x.Id, x.Title, x.DueAt, x.Completed)).ToListAsync(ct));
        });

        t.MapPost("/", async (CreateTaskRequest req, HttpContext http, DakshDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            var task = new TaskItem { UserId = u, Title = req.Title!, DueAt = req.DueAt };
            db.Tasks.Add(task);
            audit.Record(u, "task.create", nameof(TaskItem), task.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/tasks/{task.Id}", new TaskDto(task.Id, task.Title, task.DueAt, task.Completed));
        }).Validate<CreateTaskRequest>();

        t.MapPost("/{id:guid}/complete", async (Guid id, HttpContext http, DakshDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == u, ct);
            if (task is null) return Results.NotFound();
            task.Completed = true;
            audit.Record(u, "task.complete", nameof(TaskItem), id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Ok(new TaskDto(task.Id, task.Title, task.DueAt, task.Completed));
        });

        var r = app.MapGroup("/api/v1/reminders").RequireAuthorization().WithTags("Reminders");

        r.MapGet("/", async (HttpContext http, DakshDbContext db, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            return Results.Ok(await db.Reminders.Where(x => x.UserId == u).OrderBy(x => x.RemindAt)
                .Select(x => new ReminderDto(x.Id, x.Text, x.RemindAt)).ToListAsync(ct));
        });

        // Creating a reminder also enqueues a durable job; the worker delivers the notification at RemindAt.
        r.MapPost("/", async (CreateReminderRequest req, HttpContext http, DakshDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            var reminder = new Reminder { UserId = u, Text = req.Text!, RemindAt = req.RemindAt };
            db.Reminders.Add(reminder);
            db.Jobs.Add(new Job
            {
                Type = JobTypes.ReminderDue,
                IdempotencyKey = $"reminder:{reminder.Id}",
                RunAt = req.RemindAt,
                Payload = JsonSerializer.Serialize(new { reminderId = reminder.Id })
            });
            audit.Record(u, "reminder.create", nameof(Reminder), reminder.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/reminders/{reminder.Id}", new ReminderDto(reminder.Id, reminder.Text, reminder.RemindAt));
        }).Validate<CreateReminderRequest>();
    }
}
