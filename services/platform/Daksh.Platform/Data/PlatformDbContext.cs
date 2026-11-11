using System.Text.Json;
using Daksh.Platform.Domain;
using Daksh.Platform.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Daksh.Platform.Data;

public sealed class JsonValueConverter<T>() : ValueConverter<T, string>(
    v => JsonSerializer.Serialize(v, JsonDefaults.Options),
    v => JsonSerializer.Deserialize<T>(v, JsonDefaults.Options)!) where T : class;

public sealed class JsonValueComparer<T>() : ValueComparer<T>(
    (a, b) => JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options),
    v => JsonSerializer.Serialize(v, JsonDefaults.Options).GetHashCode(),
    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonDefaults.Options), JsonDefaults.Options)!) where T : class;

/// <summary>
/// All platform tables, in PostgreSQL schema "platform". Every <see cref="IWorkspaceScoped"/> entity is filtered by the
/// current <see cref="TenantContext"/> and checked again on save, so a query or write can only ever touch the
/// workspace the caller was authorised for.
/// </summary>
public class PlatformDbContext(DbContextOptions<PlatformDbContext> options, TenantContext tenant) : DbContext(options)
{
    public const string Schema = "platform";

    public TenantContext Tenant => tenant;
    private Guid? CurrentWorkspaceId => tenant.WorkspaceId;
    private bool IsSystem => tenant.IsSystem;

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentConfiguration> AgentConfigurations => Set<AgentConfiguration>();
    public DbSet<AgentInstruction> AgentInstructions => Set<AgentInstruction>();
    public DbSet<AgentDelegation> AgentDelegations => Set<AgentDelegation>();
    public DbSet<ModelProvider> ModelProviders => Set<ModelProvider>();
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionParticipant> SessionParticipants => Set<SessionParticipant>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<Execution> Executions => Set<Execution>();
    public DbSet<ExecutionEvent> ExecutionEvents => Set<ExecutionEvent>();
    public DbSet<ExecutionCheckpoint> ExecutionCheckpoints => Set<ExecutionCheckpoint>();
    public DbSet<ToolDefinition> ToolDefinitions => Set<ToolDefinition>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();
    public DbSet<ToolPermission> ToolPermissions => Set<ToolPermission>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<CostRecord> CostRecords => Set<CostRecord>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<Domain.Memory> Memories => Set<Domain.Memory>();
    public DbSet<MemoryEmbedding> MemoryEmbeddings => Set<MemoryEmbedding>();
    public DbSet<MemorySource> MemorySources => Set<MemorySource>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<SkillVersion> SkillVersions => Set<SkillVersion>();
    public DbSet<Domain.Plugin> Plugins => Set<Domain.Plugin>();
    public DbSet<PluginConfiguration> PluginConfigurations => Set<PluginConfiguration>();
    public DbSet<PluginLog> PluginLogs => Set<PluginLog>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelAccount> ChannelAccounts => Set<ChannelAccount>();
    public DbSet<ChannelConnection> ChannelConnections => Set<ChannelConnection>();
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();
    public DbSet<ChannelRoute> ChannelRoutes => Set<ChannelRoute>();
    public DbSet<ChannelIdentity> ChannelIdentities => Set<ChannelIdentity>();
    public DbSet<Domain.Automation> Automations => Set<Domain.Automation>();
    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();
    public DbSet<JobExecution> JobExecutions => Set<JobExecution>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DevicePairing> DevicePairings => Set<DevicePairing>();
    public DbSet<DeviceCapability> DeviceCapabilities => Set<DeviceCapability>();
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        void Json<T>() where T : class => c.Properties<T>().HaveConversion<JsonValueConverter<T>, JsonValueComparer<T>>().HaveColumnType("jsonb");
        Json<Dictionary<string, string>>();
        Json<List<string>>();
        Json<List<Guid>>();
        Json<ToolPolicySpec>();
        Json<AgentLimits>();
        Json<AgentMemorySettings>();
        Json<AgentSessionSettings>();
        Json<AgentApprovalPolicy>();
        Json<ModelCapabilities>();
        Json<ChannelPolicy>();
        Json<AutomationRetry>();
        c.Properties<decimal>().HavePrecision(18, 8);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<User>(e => { e.HasIndex(x => x.NormalizedEmail).IsUnique(); e.Property(x => x.Email).HasMaxLength(320); e.Property(x => x.NormalizedEmail).HasMaxLength(320); e.Property(x => x.DisplayName).HasMaxLength(200); });
        b.Entity<RefreshToken>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => x.FamilyId); e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<PasswordResetToken>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ApiKey>(e => { e.HasIndex(x => x.KeyHash).IsUnique(); e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Role>(e => { e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique(); e.Property(x => x.Name).HasMaxLength(64); e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Permission>(e => { e.HasKey(x => x.Key); e.Property(x => x.Key).HasMaxLength(64); });
        b.Entity<RolePermission>(e => { e.HasKey(x => new { x.RoleId, x.PermissionKey }); e.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionKey); });
        b.Entity<Workspace>(e => { e.HasIndex(x => x.Slug).IsUnique(); e.Property(x => x.Name).HasMaxLength(200); e.Property(x => x.Slug).HasMaxLength(80); e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<WorkspaceMember>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkspaceInvitation>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => new { x.WorkspaceId, x.Email }); });
        b.Entity<AuditLog>(e => { e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt }); e.HasIndex(x => new { x.UserId, x.CreatedAt }); e.Property(x => x.Action).HasMaxLength(100); });
        b.Entity<SystemSetting>(e => e.HasIndex(x => new { x.Scope, x.ScopeId, x.Key }).IsUnique());
        b.Entity<IdempotencyRecord>(e => { e.HasKey(x => new { x.Scope, x.Key }); e.Property(x => x.Scope).HasMaxLength(100); e.Property(x => x.Key).HasMaxLength(200); });

        b.Entity<Agent>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Slug }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Slug).HasMaxLength(80);
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne(x => x.Configuration).WithOne().HasForeignKey<AgentConfiguration>(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Instructions).WithOne().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AgentConfiguration>(e => e.HasIndex(x => x.AgentId).IsUnique());
        b.Entity<AgentInstruction>(e => e.HasIndex(x => new { x.WorkspaceId, x.AgentId, x.Order }));
        b.Entity<AgentDelegation>(e =>
        {
            e.HasIndex(x => new { x.AgentId, x.TargetAgentId }).IsUnique();
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.TargetAgentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ModelProvider>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique(); e.Property(x => x.Name).HasMaxLength(64); e.Property(x => x.Kind).HasMaxLength(40);
            e.HasMany(x => x.Credentials).WithOne().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Models).WithOne(m => m.Provider).HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Model>(e => { e.HasIndex(x => new { x.ProviderId, x.ModelId }).IsUnique(); e.Property(x => x.ModelId).HasMaxLength(200); });

        b.Entity<Session>(e =>
        {
            e.Ignore(x => x.Version);
            e.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique().HasFilter("\"Key\" IS NOT NULL AND \"DeletedAt\" IS NULL");
            e.HasIndex(x => new { x.WorkspaceId, x.OwnerUserId, x.LastMessageAt });
            e.HasIndex(x => new { x.WorkspaceId, x.AgentId });
            e.Property(x => x.Title).HasMaxLength(300); e.Property(x => x.Key).HasMaxLength(400);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SessionParticipant>(e => { e.HasIndex(x => new { x.SessionId, x.UserId }); e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Message>(e =>
        {
            e.HasIndex(x => new { x.SessionId, x.Seq }).IsUnique();
            e.HasIndex(x => x.ExecutionId);
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MessageAttachment>(e => e.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<Execution>(e =>
        {
            e.Ignore(x => x.Version);
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.HasIndex(x => x.ParentExecutionId);
            e.HasIndex(x => x.RootExecutionId);
            e.HasIndex(x => new { x.WorkspaceId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExecutionEvent>(e => { e.HasIndex(x => new { x.ExecutionId, x.Seq }).IsUnique(); e.HasIndex(x => new { x.SessionId, x.CreatedAt }); e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ExecutionCheckpoint>(e => { e.HasIndex(x => x.ExecutionId).IsUnique(); e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ToolDefinition>(e => { e.HasIndex(x => x.Name).IsUnique(); e.Property(x => x.Name).HasMaxLength(120); });
        b.Entity<ToolExecution>(e =>
        {
            e.HasIndex(x => new { x.ExecutionId, x.ToolCallId }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ToolPermission>(e => e.HasIndex(x => new { x.WorkspaceId, x.AgentId, x.UserId }));
        b.Entity<Approval>(e => { e.Property(x => x.Version).IsRowVersion(); e.HasIndex(x => new { x.WorkspaceId, x.Status, x.CreatedAt }); e.HasIndex(x => x.ExecutionId); });
        b.Entity<UsageRecord>(e => { e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt }); e.HasIndex(x => x.ExecutionId); e.HasIndex(x => x.RootExecutionId); });
        b.Entity<CostRecord>(e => { e.HasIndex(x => x.UsageRecordId).IsUnique(); e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt }); e.HasOne<UsageRecord>().WithMany().HasForeignKey(x => x.UsageRecordId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<StoredFile>(e => { e.ToTable("Files"); e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt }); e.HasIndex(x => x.Sha256); e.Property(x => x.Name).HasMaxLength(400); });

        b.Entity<Domain.Memory>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Scope, x.AgentId, x.UserId });
            e.HasIndex(x => new { x.WorkspaceId, x.ContentHash });
            e.HasIndex(x => x.ExpiresAt);
            e.HasMany(x => x.Sources).WithOne().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MemoryEmbedding>(e => { e.HasIndex(x => new { x.MemoryId, x.Model }).IsUnique(); e.HasOne<Domain.Memory>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Skill>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.AgentId, x.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL").AreNullsDistinct(false);
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SkillVersion>(e => e.HasIndex(x => new { x.SkillId, x.Version }).IsUnique());
        b.Entity<Domain.Plugin>(e => { e.HasIndex(x => x.Key).IsUnique(); e.Property(x => x.Key).HasMaxLength(64); });
        b.Entity<PluginConfiguration>(e => { e.HasIndex(x => new { x.PluginId, x.WorkspaceId }).IsUnique().AreNullsDistinct(false); e.HasOne<Domain.Plugin>().WithMany().HasForeignKey(x => x.PluginId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<PluginLog>(e => { e.HasIndex(x => new { x.PluginId, x.CreatedAt }); e.HasOne<Domain.Plugin>().WithMany().HasForeignKey(x => x.PluginId).OnDelete(DeleteBehavior.Cascade); });

        b.Entity<Channel>(e => { e.HasIndex(x => new { x.WorkspaceId, x.Kind, x.Name }).IsUnique(); e.HasMany(x => x.Accounts).WithOne(a => a.Channel).HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ChannelAccount>(e => { e.HasIndex(x => x.WebhookKey).IsUnique(); e.Property(x => x.WebhookKey).HasMaxLength(80); });
        b.Entity<ChannelConnection>(e => { e.HasIndex(x => x.ChannelAccountId).IsUnique(); e.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ChannelMessage>(e =>
        {
            e.HasIndex(x => new { x.ChannelAccountId, x.Direction, x.ExternalMessageId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.ExecutionId);
            e.Property(x => x.ExternalMessageId).HasMaxLength(200); e.Property(x => x.Direction).HasMaxLength(4);
            e.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ChannelRoute>(e => e.HasIndex(x => new { x.WorkspaceId, x.ChannelAccountId, x.Priority }));
        b.Entity<ChannelIdentity>(e => { e.HasIndex(x => new { x.ChannelAccountId, x.ExternalSenderId }).IsUnique(); e.Property(x => x.ExternalSenderId).HasMaxLength(200); e.HasOne<ChannelAccount>().WithMany().HasForeignKey(x => x.ChannelAccountId).OnDelete(DeleteBehavior.Cascade); });

        b.Entity<Domain.Automation>(e => { e.Property(x => x.Version).IsRowVersion(); e.HasIndex(x => new { x.WorkspaceId, x.Status }); e.HasIndex(x => new { x.Status, x.NextRunAt }); e.Property(x => x.Name).HasMaxLength(200); });
        b.Entity<ScheduledJob>(e =>
        {
            e.HasIndex(x => new { x.AutomationId, x.ScheduledFor }).IsUnique();
            e.HasIndex(x => new { x.Status, x.RunAfter });
            e.HasOne<Domain.Automation>().WithMany().HasForeignKey(x => x.AutomationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<JobExecution>(e => { e.HasIndex(x => new { x.AutomationId, x.StartedAt }); e.HasIndex(x => x.ExecutionId); e.HasOne<ScheduledJob>().WithMany().HasForeignKey(x => x.ScheduledJobId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Webhook>(e => { e.HasIndex(x => x.Key).IsUnique(); e.Property(x => x.Key).HasMaxLength(80); });
        b.Entity<WebhookDelivery>(e => { e.HasIndex(x => new { x.WebhookId, x.IdempotencyKey }).IsUnique(); e.Property(x => x.IdempotencyKey).HasMaxLength(200); e.HasOne<Webhook>().WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.Cascade); });

        b.Entity<Device>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasMany(x => x.Capabilities).WithOne().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<DevicePairing>(e => { e.HasIndex(x => x.CodeHash).IsUnique(); e.HasIndex(x => x.ClaimSecretHash); });
        b.Entity<DeviceCapability>(e => e.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique());
        b.Entity<DeviceEvent>(e => { e.HasIndex(x => new { x.DeviceId, x.CreatedAt }); e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade); });

        // Every workspace-scoped table gets a foreign key to its workspace and the tenant filter.
        foreach (var type in b.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(IWorkspaceScoped).IsAssignableFrom(t)).ToList())
            GetType().GetMethod(nameof(ConfigureTenant), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(type).Invoke(this, [b]);
        foreach (var type in b.Model.GetEntityTypes().Select(t => t.ClrType).Where(t => typeof(ISoftDelete).IsAssignableFrom(t)).ToList())
            GetType().GetMethod(nameof(ConfigureSoftDelete), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(type).Invoke(this, [b]);

        b.Entity<ExecutionEvent>().HasQueryFilter("tenant", e => IsSystem || e.WorkspaceId == CurrentWorkspaceId);
        b.Entity<AuditLog>().HasQueryFilter("tenant", e => IsSystem || e.WorkspaceId == CurrentWorkspaceId);
    }

    private void ConfigureTenant<T>(ModelBuilder b) where T : class, IWorkspaceScoped
    {
        b.Entity<T>().HasQueryFilter("tenant", e => IsSystem || e.WorkspaceId == CurrentWorkspaceId);
        b.Entity<T>().HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<T>().HasIndex(e => e.WorkspaceId);
    }

    private void ConfigureSoftDelete<T>(ModelBuilder b) where T : class, ISoftDelete =>
        b.Entity<T>().HasQueryFilter("softdelete", e => e.DeletedAt == null);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Guard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Guard();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Second line of tenant isolation: refuses to write a row of another workspace even if code forgot a check.</summary>
    private void Guard()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity is Entity e)
                e.UpdatedAt = now;
            if (entry.Entity is AuditLog && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit log rows cannot be changed or deleted.");
            if (entry.Entity is not IWorkspaceScoped scoped || entry.State is EntityState.Unchanged or EntityState.Detached) continue;
            if (tenant.IsSystem)
            {
                if (scoped.WorkspaceId == Guid.Empty) throw new TenantViolationException($"{entry.Entity.GetType().Name} was saved without a workspace.");
                continue;
            }
            var current = tenant.WorkspaceId ?? throw new TenantViolationException($"{entry.Entity.GetType().Name} cannot be saved without a selected workspace.");
            if (entry.State == EntityState.Added && scoped.WorkspaceId == Guid.Empty) scoped.WorkspaceId = current;
            if (scoped.WorkspaceId != current)
                throw new TenantViolationException($"{entry.Entity.GetType().Name} belongs to another workspace.");
            if (entry.State == EntityState.Modified && entry.Property(nameof(IWorkspaceScoped.WorkspaceId)).IsModified)
                throw new TenantViolationException("A row cannot be moved to another workspace.");
        }
    }
}
