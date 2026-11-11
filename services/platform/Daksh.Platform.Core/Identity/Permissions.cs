namespace Daksh.Platform.Identity;

public static class Permissions
{
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceManage = "workspace.manage";
    public const string WorkspaceDelete = "workspace.delete";
    public const string MembersRead = "members.read";
    public const string MembersManage = "members.manage";
    public const string AgentsRead = "agents.read";
    public const string AgentsWrite = "agents.write";
    public const string SessionsRead = "sessions.read";
    public const string SessionsWrite = "sessions.write";
    /// <summary>Read sessions owned by other members.</summary>
    public const string SessionsReadAll = "sessions.read_all";
    public const string ChatSend = "chat.send";
    public const string ProvidersRead = "providers.read";
    public const string ProvidersManage = "providers.manage";
    public const string ChannelsRead = "channels.read";
    public const string ChannelsManage = "channels.manage";
    public const string ToolsRead = "tools.read";
    public const string ToolsManage = "tools.manage";
    public const string SkillsRead = "skills.read";
    public const string SkillsManage = "skills.manage";
    public const string PluginsRead = "plugins.read";
    public const string PluginsManage = "plugins.manage";
    public const string MemoryRead = "memory.read";
    public const string MemoryWrite = "memory.write";
    /// <summary>Read and delete memories of other members and agents.</summary>
    public const string MemoryManage = "memory.manage";
    public const string AutomationsRead = "automations.read";
    public const string AutomationsWrite = "automations.write";
    public const string DevicesRead = "devices.read";
    public const string DevicesManage = "devices.manage";
    public const string ApprovalsRead = "approvals.read";
    public const string ApprovalsDecide = "approvals.decide";
    public const string FilesRead = "files.read";
    public const string FilesWrite = "files.write";
    public const string UsageRead = "usage.read";
    public const string AuditRead = "audit.read";
    public const string LogsRead = "logs.read";
    public const string SettingsManage = "settings.manage";
    public const string ApiKeysManage = "apikeys.manage";

    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        [WorkspaceRead] = "See the workspace", [WorkspaceManage] = "Rename and configure the workspace",
        [WorkspaceDelete] = "Delete the workspace or transfer ownership",
        [MembersRead] = "See members", [MembersManage] = "Invite, remove and change roles of members",
        [AgentsRead] = "See agents", [AgentsWrite] = "Create, edit and delete agents",
        [SessionsRead] = "See own sessions", [SessionsWrite] = "Create, rename, archive and delete own sessions",
        [SessionsReadAll] = "See sessions of other members",
        [ChatSend] = "Send messages to agents and stop runs",
        [ProvidersRead] = "See model providers and models", [ProvidersManage] = "Configure providers, credentials and models",
        [ChannelsRead] = "See channels", [ChannelsManage] = "Configure channels, routes and pairing",
        [ToolsRead] = "See tools and tool history", [ToolsManage] = "Change tool rules",
        [SkillsRead] = "See skills", [SkillsManage] = "Install, edit and remove skills",
        [PluginsRead] = "See plugins", [PluginsManage] = "Install, configure and remove plugins",
        [MemoryRead] = "See own and shared memories", [MemoryWrite] = "Create, edit and delete own memories",
        [MemoryManage] = "See and delete any memory in the workspace",
        [AutomationsRead] = "See automations", [AutomationsWrite] = "Create, edit, pause and delete automations and webhooks",
        [DevicesRead] = "See devices", [DevicesManage] = "Pair, authorise and revoke devices",
        [ApprovalsRead] = "See approval requests", [ApprovalsDecide] = "Approve or reject requests",
        [FilesRead] = "Download files", [FilesWrite] = "Upload and delete files",
        [UsageRead] = "See usage and costs", [AuditRead] = "See the audit log", [LogsRead] = "See execution logs",
        [SettingsManage] = "Change workspace settings", [ApiKeysManage] = "Create and revoke own API keys",
    };

    public sealed record RoleTemplate(string Name, string Description, int Rank, string[] Permissions);

    private static readonly string[] ReadOnlySet =
    [
        WorkspaceRead, MembersRead, AgentsRead, SessionsRead, ProvidersRead, ChannelsRead, ToolsRead, SkillsRead, PluginsRead,
        MemoryRead, AutomationsRead, DevicesRead, ApprovalsRead, FilesRead, UsageRead
    ];

    private static readonly string[] MemberSet =
        [.. ReadOnlySet, SessionsWrite, ChatSend, MemoryWrite, FilesWrite, ApiKeysManage, AutomationsWrite];

    public static readonly IReadOnlyList<RoleTemplate> BuiltInRoles =
    [
        new("Owner", "Full control, including deleting the workspace and transferring ownership.", 100, [.. All.Keys]),
        new("Administrator", "Full control except deleting the workspace or transferring ownership.", 80,
            [.. All.Keys.Where(k => k != WorkspaceDelete)]),
        new("Operator", "Runs the workspace day to day: channels, devices, approvals, automations and all sessions.", 60,
            [.. MemberSet, SessionsReadAll, ChannelsManage, DevicesManage, ApprovalsDecide, LogsRead, AuditRead, MemoryManage]),
        new("Developer", "Builds agents: agents, tools, skills, plugins and providers.", 50,
            [.. MemberSet, AgentsWrite, ToolsManage, SkillsManage, PluginsManage, ProvidersManage, LogsRead, ApprovalsDecide]),
        new("Member", "Uses agents, own sessions, own memories and own automations.", 30, MemberSet),
        new("Read-only", "Can look but not change anything or talk to agents.", 10, ReadOnlySet),
    ];
}
