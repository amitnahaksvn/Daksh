using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class WhatsAppIntelTests
{
    private static List<string> Detect(string text, params string[] disabled) => WhatsAppIntel.Detect(text, disabled);

    [Fact]
    public void Detects_questions()
    {
        Assert.Contains("question", Detect("Are you coming tomorrow?"));
        Assert.Contains("question", Detect("kya hua bhai"));
        Assert.DoesNotContain("question", Detect("See you tomorrow."));
    }

    [Fact]
    public void Detects_otp_only_with_a_code_word_and_a_code()
    {
        Assert.Contains("otp", Detect("Your OTP is 482913. Do not share it."));
        Assert.DoesNotContain("otp", Detect("Call me at 5"));
        Assert.DoesNotContain("otp", Detect("What is the OTP?"));
    }

    [Fact]
    public void Detects_payment_requests_and_amounts()
    {
        Assert.Contains("payment", Detect("Please pay ₹2,500 by UPI"));
        Assert.Equal("₹2,500", WhatsAppIntel.ExtractValue("payment", "Please pay ₹2,500 by UPI"));
    }

    [Fact]
    public void Flags_shortened_and_phishing_links_but_not_plain_ones()
    {
        Assert.Contains("suspicious-link", Detect("Claim your prize https://bit.ly/abc123"));
        Assert.Contains("link", Detect("Docs here https://example.com/report"));
        Assert.DoesNotContain("suspicious-link", Detect("Docs here https://example.com/report"));
    }

    [Fact]
    public void Media_labels_count_as_attachments_and_documents()
    {
        Assert.Contains("attachment", Detect("📷 Photo"));
        var doc = Detect("📄 Document");
        Assert.Contains("document", doc);
        Assert.Contains("attachment", doc);
    }

    [Fact]
    public void Disabled_categories_are_never_tagged()
    {
        Assert.DoesNotContain("question", Detect("Are you coming?", "question"));
        Assert.Contains("question", Detect("Are you coming?", "payment"));
    }

    [Fact]
    public void Reminder_due_time_follows_the_message()
    {
        var inTwoHours = WhatsAppIntel.ReminderDue("remind me in 2 hours");
        Assert.InRange((inTwoHours - DateTime.UtcNow).TotalMinutes, 118, 122);
        Assert.InRange((WhatsAppIntel.ReminderDue("call tomorrow") - DateTime.UtcNow).TotalHours, 23, 25);
        Assert.InRange((WhatsAppIntel.ReminderDue("call him") - DateTime.UtcNow).TotalMinutes, 58, 62);
    }

    [Fact]
    public void Monitoring_filters_by_category_and_contact_type()
    {
        var s = new WhatsAppSettings { ConnectionId = "c", MonitorLinks = false, MonitorBusiness = false, MonitorUnknownNumbers = false };
        Assert.False(WhatsAppMonitoring.Passes(s, isGroup: false, "see https://example.com", isSavedContact: true));
        Assert.False(WhatsAppMonitoring.Passes(s, isGroup: false, "hello", isSavedContact: true, isBusiness: true));
        Assert.False(WhatsAppMonitoring.Passes(s, isGroup: false, "hello", isSavedContact: false));
        Assert.True(WhatsAppMonitoring.Passes(s, isGroup: false, "hello", isSavedContact: true));
        Assert.True(WhatsAppMonitoring.Passes(s, isGroup: true, "hello"));
    }
}
