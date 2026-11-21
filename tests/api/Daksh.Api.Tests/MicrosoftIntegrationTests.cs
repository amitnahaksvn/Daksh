using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class MicrosoftIntegrationTests
{
    [Fact]
    public void Features_default_to_mail_calendar_and_onedrive_and_drop_unknown_names()
    {
        Assert.Equal(["mail", "calendar", "onedrive"], MicrosoftFeatures.Parse(null));
        Assert.Equal(["mail", "calendar", "onedrive"], MicrosoftFeatures.Parse("teams,bogus"));
        Assert.Equal(["devops", "mail"], MicrosoftFeatures.Parse("DevOps, mail, devops"));
    }

    [Fact]
    public void Scopes_are_read_only_by_default()
    {
        var scope = MicrosoftFeatures.ScopeFor(MicrosoftFeatures.Parse("mail,calendar,onedrive"));
        Assert.Contains("offline_access", scope);
        Assert.Contains("Mail.Read", scope);
        Assert.Contains("Calendars.Read", scope);
        Assert.Contains("Files.Read", scope);
        Assert.DoesNotContain("ReadWrite", scope);
        Assert.DoesNotContain("Mail.Send", scope);
    }

    [Fact]
    public void Full_access_adds_the_write_permissions_for_the_chosen_services_only()
    {
        var scope = MicrosoftFeatures.ScopeFor(["mail"], write: true);
        Assert.Contains("Mail.ReadWrite", scope);
        Assert.Contains("Mail.Send", scope);
        Assert.DoesNotContain("Files", scope);
        Assert.DoesNotContain("Calendars", scope);
        Assert.Contains("499b84ac-1321-427f-aa17-267ca6975798/user_impersonation", MicrosoftFeatures.ScopeFor(["devops"]));
    }

    [Fact]
    public void Admin_approval_covers_graph_permissions_only()
    {
        var read = MicrosoftFeatures.AdminScopeFor(["mail", "devops"], write: false);
        Assert.Contains("https://graph.microsoft.com/Mail.Read", read);
        Assert.DoesNotContain("499b84ac", read);
        Assert.DoesNotContain("offline_access", read);
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite", MicrosoftFeatures.AdminScopeFor(["mail"], write: true));
    }

    [Fact]
    public void Teams_is_not_requested()
    {
        var all = MicrosoftFeatures.ScopeFor(MicrosoftFeatures.All);
        Assert.DoesNotContain("Chat.", all);
        Assert.DoesNotContain("Channel", all);
    }

    [Fact]
    public void Html_is_stripped_to_readable_text()
    {
        var text = MicrosoftAssistant.StripHtml("<html><style>p{color:red}</style><body><p>Hello&nbsp;<b>Amit</b></p><script>alert(1)</script></body></html>");
        Assert.Equal("Hello Amit", text);
    }
}
