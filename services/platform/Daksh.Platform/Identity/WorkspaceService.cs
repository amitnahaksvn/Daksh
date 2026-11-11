using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Platform.Identity;

public sealed record MembershipContext(WorkspaceMember Member, Role Role, IReadOnlySet<string> Permissions);

public sealed class NotAMemberException(string message) : Exception(message);

/// <summary>Creates workspaces with built-in roles and resolves a user's membership for setting <see cref="Data.TenantContext"/>.</summary>
public sealed class WorkspaceService(PlatformDbContext db)
{
    public async Task<Workspace> CreateAsync(string name, Guid ownerUserId, CancellationToken ct)
    {
        var slug = await UniqueSlugAsync(name, ct);
        var workspace = new Workspace { Name = name, Slug = slug, OwnerUserId = ownerUserId };
        db.Tenant.SetWorkspaceAsSystem(Guid.Empty); // placeholder scope; corrected to the new id right below
        workspace.Id = Guid.CreateVersion7();
        db.Tenant.SetWorkspaceAsSystem(workspace.Id);
        db.Workspaces.Add(workspace);
        await EnsureBuiltInRolesAsync(workspace.Id, ct);
        var ownerRole = await db.Roles.FirstAsync(r => r.WorkspaceId == workspace.Id && r.Name == "Owner", ct);
        db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = ownerUserId, RoleId = ownerRole.Id });
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task EnsureBuiltInRolesAsync(Guid workspaceId, CancellationToken ct)
    {
        var existing = await db.Roles.Where(r => r.WorkspaceId == workspaceId).Select(r => r.Name).ToListAsync(ct);
        foreach (var template in Permissions.BuiltInRoles.Where(t => !existing.Contains(t.Name)))
        {
            var role = new Role { WorkspaceId = workspaceId, Name = template.Name, Description = template.Description, Rank = template.Rank, IsSystem = true };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
            foreach (var p in template.Permissions) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = p });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<MembershipContext> ResolveMembershipAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        var member = await db.WorkspaceMembers.IgnoreQueryFilters()
            .Include(m => m.Role).ThenInclude(r => r!.Permissions)
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct)
            ?? throw new NotAMemberException("You are not a member of this workspace.");
        var permissions = member.Role!.Permissions.Select(p => p.PermissionKey).ToHashSet();
        return new MembershipContext(member, member.Role, permissions);
    }

    public Task<List<WorkspaceMember>> ListMembershipsAsync(Guid userId, CancellationToken ct) =>
        db.WorkspaceMembers.IgnoreQueryFilters().Where(m => m.UserId == userId).ToListAsync(ct);

    private async Task<string> UniqueSlugAsync(string name, CancellationToken ct)
    {
        var baseSlug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        while (baseSlug.Contains("--")) baseSlug = baseSlug.Replace("--", "-");
        if (baseSlug.Length == 0) baseSlug = "workspace";
        if (baseSlug.Length > 60) baseSlug = baseSlug[..60];
        var slug = baseSlug;
        var i = 1;
        while (await db.Workspaces.IgnoreQueryFilters().AnyAsync(w => w.Slug == slug, ct)) slug = $"{baseSlug}-{++i}";
        return slug;
    }
}
