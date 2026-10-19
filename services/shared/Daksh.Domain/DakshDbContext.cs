using Microsoft.EntityFrameworkCore;

namespace Daksh.Domain;

public class DakshDbContext(DbContextOptions<DakshDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<PrivacySettings> PrivacySettings => Set<PrivacySettings>();
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Conversation>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ConversationMessage>(e => e.Property(x => x.Content).HasMaxLength(8000));
        b.Entity<TaskItem>(e => { e.ToTable("tasks"); e.HasIndex(x => x.UserId); e.Property(x => x.Title).HasMaxLength(300); });
        b.Entity<Reminder>(e => { e.HasIndex(x => x.UserId); e.Property(x => x.Text).HasMaxLength(500); });
        b.Entity<AuditEvent>(e => { e.HasIndex(x => new { x.UserId, x.OccurredAt }); e.Property(x => x.Action).HasMaxLength(100); });
        b.Entity<PrivacySettings>(e => e.HasKey(x => x.UserId));
        b.Entity<Job>(e =>
        {
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.RunAt });
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.IdempotencyKey).HasMaxLength(200);
        });
    }
}
