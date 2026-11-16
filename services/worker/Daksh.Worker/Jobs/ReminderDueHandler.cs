using Daksh.Domain;

namespace Daksh.Worker.Jobs;

// Pending MongoDB rebuild: previously looked up/updated the reminder via DakshDbContext.Reminders
// and sent it through IPushNotificationSender.
public sealed class ReminderDueHandler : IJobHandler
{
    public string Type => JobTypes.ReminderDue;

    public Task HandleAsync(Job job, CancellationToken ct) =>
        throw new NotImplementedException("Reminder delivery is unavailable: pending MongoDB rebuild.");
}
