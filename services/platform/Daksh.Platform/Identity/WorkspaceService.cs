using Daksh.Platform.Domain;

namespace Daksh.Platform.Identity;

public sealed record MembershipContext(WorkspaceMember Member, Role Role, IReadOnlySet<string> Permissions);

public sealed class NotAMemberException(string message) : Exception(message);

/// <summary>
/// Pending MongoDB rebuild: previously created workspaces with built-in roles and resolved membership
/// against PlatformDbContext. Every member throws until the Mongo-backed implementation is written.
/// </summary>
public sealed class WorkspaceService
{
    private static NotImplementedException Pending() => new("WorkspaceService is unavailable: pending MongoDB rebuild.");

    public Task<Workspace> CreateAsync(string name, Guid ownerUserId, CancellationToken ct) => throw Pending();
    public Task EnsureBuiltInRolesAsync(Guid workspaceId, CancellationToken ct) => throw Pending();
    public Task<MembershipContext> ResolveMembershipAsync(Guid workspaceId, Guid userId, CancellationToken ct) => throw Pending();
    public Task<List<WorkspaceMember>> ListMembershipsAsync(Guid userId, CancellationToken ct) => throw Pending();
}
