using System.Text.Json;
using Daksh.Domain;
using Daksh.Integrations;

namespace Daksh.Worker.Jobs;

public sealed class ReminderDueHandler(IPushNotificationSender push) : IJobHandler
{
    public string Type => JobTypes.ReminderDue;

    public async Task HandleAsync(Job job, DakshDbContext db, CancellationToken ct)
    {
        var id = JsonDocument.Parse(job.Payload).RootElement.GetProperty("reminderId").GetGuid();
        var reminder = await db.Reminders.FindAsync([id], ct);
        if (reminder is null || reminder.NotifiedAt is not null) return; // deleted or already delivered

        await push.SendAsync(new PushNotification(reminder.UserId, "Reminder", reminder.Text), ct);
        reminder.NotifiedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEvent { UserId = reminder.UserId, Action = "reminder.notified", EntityType = nameof(Reminder), EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
    }
}
