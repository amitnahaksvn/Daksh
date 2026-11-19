using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class SelfCommandsTests
{
    private static SelfCommand P(string t) => SelfCommands.Parse(t);

    [Theory]
    [InlineData("busy for 2 hours", 120)] [InlineData("I'm busy for 30 min", 30)] [InlineData("busy 1 hour", 60)] [InlineData("im busy for an hour", 60)] [InlineData("busy for half an hour", 30)] [InlineData("busy", 120)]
    public void Busy_with_a_length(string text, double minutes)
    {
        var c = P(text);
        Assert.Equal(SelfCommandKind.Busy, c.Kind);
        Assert.Equal(minutes, c.Duration!.Value.TotalMinutes);
    }

    [Theory]
    [InlineData("busy until 5pm", "17:00")] [InlineData("I'm busy till 4:30 pm", "16:30")] [InlineData("busy until 9am", "09:00")] [InlineData("busy until 17:45", "17:45")] [InlineData("busy till 12am", "00:00")]
    public void Busy_until_a_time(string text, string clock)
    {
        var c = P(text);
        Assert.Equal(SelfCommandKind.Busy, c.Kind);
        Assert.Equal(clock, c.UntilClock);
    }

    [Theory]
    [InlineData("busy for 2 hours - in a meeting", "in a meeting")] [InlineData("busy until 5pm (driving)", "driving")] [InlineData("I'm busy for 30 min, on a call", "on a call")] [InlineData("busy till 6:30 pm : at the gym", "at the gym")]
    public void Busy_with_an_optional_note(string text, string note) => Assert.Equal(note, P(text).Note);

    [Fact] public void The_note_is_optional() => Assert.Null(P("busy for 2 hours").Note);

    [Theory] [InlineData("free")] [InlineData("I'm free now")] [InlineData("free now")] [InlineData("available")] [InlineData("I'm back")] [InlineData("not busy")]
    public void Free(string text) => Assert.Equal(SelfCommandKind.Free, P(text).Kind);

    [Theory]
    [InlineData("buy milk", "Shopping", "milk")] [InlineData("Buy 2 kg rice", "Shopping", "2 kg rice")]
    [InlineData("add headphones to amazon list", "Amazon", "headphones")] [InlineData("put sunscreen on my groceries list", "Groceries", "sunscreen")] [InlineData("add milk to shopping", "Shopping", "milk")]
    [InlineData("todo call the bank", "To-do", "call the bank")] [InlineData("todo: renew passport", "To-do", "renew passport")] [InlineData("remind me to pay rent", "To-do", "pay rent")]
    [InlineData("amazon: usb cable", "Amazon", "usb cable")]
    public void Adding_to_lists(string text, string list, string item)
    {
        var c = P(text);
        Assert.Equal(SelfCommandKind.AddItem, c.Kind);
        Assert.Equal(list, c.List);
        Assert.Equal(item, c.Text);
    }

    [Fact]
    public void Removing_an_item()
    {
        var c = P("remove milk");
        Assert.Equal(SelfCommandKind.RemoveItem, c.Kind);
        Assert.Equal("milk", c.Text);
        Assert.Null(c.List);
        var d = P("delete headphones from amazon list");
        Assert.Equal("Amazon", ListsModule.CleanName(d.List));
        Assert.Equal("headphones", d.Text);
    }

    [Theory] [InlineData("show my lists")] [InlineData("what's on my list")] [InlineData("show shopping")]
    public void Showing_lists(string text) => Assert.Equal(SelfCommandKind.ShowLists, P(text).Kind);

    [Theory]
    [InlineData("hello")] [InlineData("meeting at 5 tomorrow")] [InlineData("http://example.com/page")] [InlineData("note: remember this")] [InlineData("")] [InlineData("I am busy with work and also tired of it all today so call later")]
    public void Ordinary_notes_to_yourself_are_left_alone(string text) => Assert.Equal(SelfCommandKind.None, P(text).Kind);

    [Fact]
    public void Our_own_replies_are_never_read_as_commands() => Assert.Equal(SelfCommandKind.None, P(SelfCommands.ReplyMarker + "✅ Added \"buy milk\" to your Shopping list").Kind);

    [Fact]
    public void A_message_is_only_handled_once()
    {
        Assert.True(SelfCommandSeen.First("c1", "m1"));
        Assert.False(SelfCommandSeen.First("c1", "m1"));
        Assert.True(SelfCommandSeen.First("c1", "m2"));
    }

    [Theory]
    [InlineData("919876543210@s.whatsapp.net", "919876543210@s.whatsapp.net", true)]
    [InlineData("919876543210:12@s.whatsapp.net", "919876543210@s.whatsapp.net", true)]
    [InlineData("919876543210:12@s.whatsapp.net", "919000000000@s.whatsapp.net", false)]
    [InlineData(null, "919876543210@s.whatsapp.net", false)]
    public void Own_chat_detection(string? account, string chat, bool expected) => Assert.Equal(expected, IntegrationsModule.IsOwnChat(account, chat));
}

public class OtpExtractorTests
{
    [Theory]
    [InlineData("Your verification code", "Your verification code is 482915. It expires in 10 minutes.", "482915")]
    [InlineData("Login", "482915 is your login code for Acme.", "482915")]
    [InlineData("OTP", "Use OTP 7731 to sign in. Do not share it.", "7731")]
    [InlineData("Security code", "Your one-time code: 123-456", "123456")]
    [InlineData("Your code", "Enter this verification code: 90210 to continue", "90210")]
    public void Finds_the_code(string subject, string body, string code) => Assert.Equal(code, OtpExtractor.Extract(subject, body));

    [Theory]
    [InlineData("Your order", "Order 123456 shipped on 2024. Total 4999.")]
    [InlineData("Newsletter", "Our 2025 plans are here")]
    public void Ordinary_numbers_are_not_codes(string subject, string body) => Assert.Null(OtpExtractor.Extract(subject, body));

    [Fact] public void Recognises_otp_emails() { Assert.True(OtpExtractor.LooksLikeOtp("Your OTP", "...")); Assert.False(OtpExtractor.LooksLikeOtp("Lunch?", "Are you free")); }
}

public class HumanReplyTests
{
    private static readonly WhatsAppSettings S = new() { ConnectionId = "c", BossReference = "Amit sir" };

    [Fact]
    public void A_saved_contact_prompt_forbids_introducing_the_AI()
    {
        var p = HumanReply.BuildPrompt(S, GateVerdict.Boss, savedContact: true, introduced: false, "Ravi", "when is the meeting?");
        Assert.Contains("Do NOT say you are an AI", p);
        Assert.DoesNotContain("AI assistant", p.Replace("Do NOT say you are an AI or an assistant", ""));
        Assert.Contains("Amit sir", p);
    }

    [Fact]
    public void A_stranger_is_told_once_that_it_is_an_AI_assistant()
    {
        Assert.Contains("AI assistant", HumanReply.BuildPrompt(S, GateVerdict.Boss, false, false, "+91 98", "send me the invoice"));
        Assert.Contains("do not introduce yourself again", HumanReply.BuildPrompt(S, GateVerdict.Boss, false, true, "+91 98", "send me the invoice"));
    }

    [Fact]
    public void The_prompt_forbids_stock_phrases_answers_and_leaks()
    {
        var p = HumanReply.BuildPrompt(S, GateVerdict.BossAskMore, true, false, "Ravi", "call me");
        Assert.Contains("Never use stock phrases", p);
        Assert.Contains("do NOT answer the question", p);
        Assert.Contains("ONE short, specific question", p);
        Assert.Contains("PRIVACY", p);
    }

    [Theory]
    [InlineData("Haan bhai, main bata deta hoon sir ko, wo aapko call karenge.", true)]
    [InlineData("NEED_BOSS", false)]
    [InlineData("", false)]
    [InlineData("As an AI language model I cannot do that", false)]
    public void Only_short_human_looking_text_is_accepted(string text, bool ok) => Assert.Equal(ok, HumanReply.Clean(text) is not null);

    [Fact] public void Detects_that_it_already_introduced_itself() =>
        Assert.True(HumanReply.AlreadyIntroduced([("user", "hi"), ("assistant", "Hi, I'm Amit's AI assistant.")]));
}
