using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using FluentValidation;

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

// Pending MongoDB rebuild: previously persisted tasks/reminders/jobs via DakshDbContext.
public static class TasksModule
{
    public static void MapTasksAndReminders(this IEndpointRouteBuilder app)
    {
        var t = app.MapGroup("/api/v1/tasks").RequireAuthorization(AuthSetup.UserScheme).WithTags("Tasks");

        t.MapGet("/", (HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Tasks are unavailable: pending MongoDB rebuild."));

        t.MapPost("/", (CreateTaskRequest req, HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Tasks are unavailable: pending MongoDB rebuild.")).Validate<CreateTaskRequest>();

        t.MapPost("/{id:guid}/complete", (Guid id, HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Tasks are unavailable: pending MongoDB rebuild."));

        var r = app.MapGroup("/api/v1/reminders").RequireAuthorization(AuthSetup.UserScheme).WithTags("Reminders");

        r.MapGet("/", (HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Reminders are unavailable: pending MongoDB rebuild."));

        // Creating a reminder also enqueues a durable job; the worker delivers the notification at RemindAt.
        r.MapPost("/", (CreateReminderRequest req, HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Reminders are unavailable: pending MongoDB rebuild.")).Validate<CreateReminderRequest>();
    }
}
