using System.Text.Json;
using Daksh.Platform.Tools;

namespace Daksh.Platform.Tests.Tools;

public sealed class ToolPolicyTests
{
    private static ToolDescriptor Tool(string name, string group = "misc", ToolRisk risk = ToolRisk.Low) =>
        new(name, $"{name} description", group, JsonDefaults.Schema(new { type = "object" }), risk);

    [Fact]
    public void Evaluate_ToolInProfile_IsAllowed()
    {
        var spec = new ToolPolicySpec { Profile = "minimal" };
        var decisions = ToolPolicy.Evaluate([Tool("session_status")], spec, []);
        Assert.True(decisions.Single().Allowed);
    }

    [Fact]
    public void Evaluate_ToolNotInProfile_IsDenied()
    {
        var spec = new ToolPolicySpec { Profile = "minimal" };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web")], spec, []);
        Assert.False(decisions.Single().Allowed);
        Assert.Equal("not in agent policy", decisions.Single().Reason);
    }

    [Fact]
    public void Evaluate_AgentPolicyDeny_OverridesProfileAllow()
    {
        var spec = new ToolPolicySpec { Profile = "full", Deny = ["session_status"] };
        var decisions = ToolPolicy.Evaluate([Tool("session_status")], spec, []);
        Assert.False(decisions.Single().Allowed);
        Assert.Equal("denied by agent policy", decisions.Single().Reason);
    }

    [Fact]
    public void Evaluate_WorkspaceRuleDeny_OverridesProfileAllow()
    {
        var spec = new ToolPolicySpec { Profile = "full" };
        var rules = new[] { new ToolRule("session_status", "deny") };
        var decisions = ToolPolicy.Evaluate([Tool("session_status")], spec, rules);
        Assert.False(decisions.Single().Allowed);
        Assert.Equal("denied by workspace rule", decisions.Single().Reason);
    }

    [Fact]
    public void Evaluate_WorkspaceRuleDeny_OverridesWorkspaceRuleAllow()
    {
        // Same tool matched by both an "allow" rule and a "deny" rule: deny must still win.
        var spec = new ToolPolicySpec { Profile = "minimal" };
        var rules = new[]
        {
            new ToolRule("web_search", "allow"),
            new ToolRule("web_search", "deny"),
        };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web")], spec, rules);
        Assert.False(decisions.Single().Allowed);
    }

    [Fact]
    public void Evaluate_AgentPolicyDeny_OverridesAlsoAllow()
    {
        var spec = new ToolPolicySpec { Profile = "minimal", AlsoAllow = ["web_search"], Deny = ["web_search"] };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web")], spec, []);
        Assert.False(decisions.Single().Allowed);
    }

    [Fact]
    public void Evaluate_AgentPolicyDeny_OverridesExplicitAllowList()
    {
        var spec = new ToolPolicySpec { Allow = ["web_search"], Deny = ["web_search"] };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web")], spec, []);
        Assert.False(decisions.Single().Allowed);
    }

    [Fact]
    public void Evaluate_WorkspaceRuleDeny_OverridesExplicitAllowList()
    {
        var spec = new ToolPolicySpec { Allow = ["web_search"] };
        var rules = new[] { new ToolRule("web_search", "deny") };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web")], spec, rules);
        Assert.False(decisions.Single().Allowed);
    }

    [Fact]
    public void Evaluate_DenyWins_EvenWhenApproveRuleAlsoMatches()
    {
        // Deny must win over an approve rule too: a denied tool should never be surfaced as "needs approval".
        var spec = new ToolPolicySpec { Profile = "full", Deny = ["shell_exec"] };
        var rules = new[] { new ToolRule("shell_exec", "approve") };
        var decisions = ToolPolicy.Evaluate([Tool("shell_exec", "runtime")], spec, rules);
        var decision = decisions.Single();
        Assert.False(decision.Allowed);
        Assert.False(decision.RequiresApproval);
    }

    [Fact]
    public void Evaluate_AllowAndAlsoAllowBothSet_Throws()
    {
        var spec = new ToolPolicySpec { Allow = ["web_search"], AlsoAllow = ["memory_search"] };
        Assert.Throws<InvalidOperationException>(() => ToolPolicy.Evaluate([Tool("web_search", "web")], spec, []));
    }

    [Fact]
    public void Evaluate_ApproveRule_AllowedAndRequiresApproval()
    {
        var spec = new ToolPolicySpec { Profile = "full" };
        var rules = new[] { new ToolRule("shell_exec", "approve") };
        var decisions = ToolPolicy.Evaluate([Tool("shell_exec", "runtime")], spec, rules);
        var decision = decisions.Single();
        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresApproval);
    }

    [Fact]
    public void Evaluate_PlainAllow_DoesNotRequireApproval()
    {
        var spec = new ToolPolicySpec { Profile = "full" };
        var decisions = ToolPolicy.Evaluate([Tool("session_status")], spec, []);
        var decision = decisions.Single();
        Assert.True(decision.Allowed);
        Assert.False(decision.RequiresApproval);
    }

    [Fact]
    public void Evaluate_ApproveRuleOnDisallowedTool_DoesNotForceApprovalFlag()
    {
        var spec = new ToolPolicySpec { Profile = "minimal" };
        var rules = new[] { new ToolRule("web_search", "approve") };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web")], spec, rules);
        var decision = decisions.Single();
        Assert.False(decision.Allowed);
        Assert.False(decision.RequiresApproval);
    }

    [Fact]
    public void Evaluate_GroupWildcard_MatchesAllToolsInGroup()
    {
        var spec = new ToolPolicySpec { Allow = ["group:web"] };
        var decisions = ToolPolicy.Evaluate([Tool("web_search", "web"), Tool("web_fetch", "web"), Tool("shell_exec", "runtime")], spec, []);
        Assert.True(decisions.First(d => d.Name == "web_search").Allowed);
        Assert.True(decisions.First(d => d.Name == "web_fetch").Allowed);
        Assert.False(decisions.First(d => d.Name == "shell_exec").Allowed);
    }

    [Fact]
    public void Matches_StarWildcard_MatchesEverything()
    {
        Assert.True(ToolPolicy.Matches("*", Tool("anything")));
    }

    [Fact]
    public void Matches_PrefixWildcard_MatchesByPattern()
    {
        Assert.True(ToolPolicy.Matches("web_*", Tool("web_search")));
        Assert.False(ToolPolicy.Matches("web_*", Tool("memory_search")));
    }

    [Theory]
    [InlineData(ToolRisk.Low, "medium", false)]
    [InlineData(ToolRisk.Medium, "medium", true)]
    [InlineData(ToolRisk.High, "medium", true)]
    [InlineData(ToolRisk.Critical, "medium", true)]
    public void NeedsApproval_RiskThreshold_GatesByRisk(ToolRisk risk, string threshold, bool expected)
    {
        var tool = Tool("t", risk: risk);
        var result = ToolPolicy.NeedsApproval("t", risk, threshold, [], [], ruleRequiresApproval: false, tool);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NeedsApproval_CriticalRisk_CannotBeAutoApprovedByConfiguration()
    {
        var tool = Tool("danger", risk: ToolRisk.Critical);
        var result = ToolPolicy.NeedsApproval("danger", ToolRisk.Critical, "never", [], autoApprove: ["danger"], ruleRequiresApproval: false, tool);
        Assert.True(result);
    }

    [Fact]
    public void NeedsApproval_AlwaysRequireList_ForcesApprovalRegardlessOfRisk()
    {
        var tool = Tool("safe_tool", risk: ToolRisk.Low);
        var result = ToolPolicy.NeedsApproval("safe_tool", ToolRisk.Low, "never", alwaysRequire: ["safe_tool"], [], ruleRequiresApproval: false, tool);
        Assert.True(result);
    }

    [Fact]
    public void NeedsApproval_RuleRequiresApproval_AlwaysTrue()
    {
        var tool = Tool("safe_tool", risk: ToolRisk.Low);
        var result = ToolPolicy.NeedsApproval("safe_tool", ToolRisk.Low, "never", [], [], ruleRequiresApproval: true, tool);
        Assert.True(result);
    }
}
