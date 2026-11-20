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
    [InlineData("Daksha add kids jacket in buy list", "Shopping", "kids jacket")] [InlineData("Daksha, add kids jacket to my buy list", "Shopping", "kids jacket")] [InlineData("hey daksha buy milk", "Shopping", "milk")]
    [InlineData("add kids jacket to shopping list", "Shopping", "kids jacket")] [InlineData("add pay bills to my to do list", "To-do", "pay bills")] [InlineData("add kids jacket to to buy list", "Shopping", "kids jacket")]
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

public class AssistantMailContextTests
{
    [Fact]
    public void Asking_about_mail_codes_or_importance_looks_at_mail()
    {
        Assert.True(AssistantIntent.Detect("any important email?", []).Mail);
        Assert.True(AssistantIntent.Detect("did I get an OTP?", []).Codes);
        Assert.True(AssistantIntent.Detect("did I get an OTP?", []).Mail);
    }

    [Fact]
    public void Words_that_only_mean_look_at_my_mail_are_not_used_as_search_terms()
    {
        var words = AssistantIntent.Keywords("any important email from Flipkart about my invoice").Where(w => !AssistantIntent.MailWords.Contains(w)).ToList();
        Assert.Contains("Flipkart", words);
        Assert.Contains("invoice", words);
        Assert.DoesNotContain("email", words, StringComparer.OrdinalIgnoreCase);
    }
}

public class SelfAssistantTests
{
    [Theory]
    [InlineData("what's pending?")] [InlineData("any important email?")] [InlineData("Any OTP in my mail")] [InlineData("daksha what is on my amazon list")]
    [InlineData("Hey Daksha, summarize my day")] [InlineData("did Ravi reply about the party?")] [InlineData("show my pending tasks")] [InlineData("who messaged me today")]
    public void Questions_for_Daksha(string text) => Assert.True(SelfAssistant.LooksLikeQuestion(text));

    [Theory]
    [InlineData("buy milk")] [InlineData("busy for 2 hours")] [InlineData("free")] [InlineData("remember to call mom tomorrow")] [InlineData("https://example.com/page")] [InlineData("ok")] [InlineData("")]
    public void Notes_and_commands_are_not_questions(string text) => Assert.False(SelfAssistant.LooksLikeQuestion(text));

    [Theory]
    [InlineData("{\"intent\":\"ignore\"}")] [InlineData("not json")] [InlineData("")] [InlineData("{\"intent\":\"dance\"}")] [InlineData("{\"intent\":\"add\",\"text\":null}")]
    public void Unclear_intents_are_ignored(string raw) => Assert.Equal("ignore", SelfAssistant.ParseIntent(raw).Name);

    [Fact] public void A_question_intent_is_read_even_with_chatter_around_it() => Assert.Equal("question", SelfAssistant.ParseIntent("Sure: {\"intent\":\"question\"} done").Name);

    [Fact]
    public void An_add_intent_becomes_a_list_command()
    {
        var c = SelfAssistant.ParseIntent("{\"intent\":\"add\",\"list\":\"amazon\",\"text\":\"headphones\"}").Command!;
        Assert.Equal(SelfCommandKind.AddItem, c.Kind); Assert.Equal("headphones", c.Text);
    }

    [Fact] public void An_add_intent_without_a_list_leaves_it_unset() => Assert.Null(SelfAssistant.ParseIntent("{\"intent\":\"add\",\"list\":null,\"text\":\"wardrobe organiser\"}").Command!.List);

    [Fact]
    public void Event_calendar_and_send_intents_keep_their_details()
    {
        var e = SelfAssistant.ParseIntent("{\"intent\":\"event\",\"title\":\"Dentist\",\"start\":\"2026-11-21T17:00\",\"minutes\":45}");
        Assert.Equal("event", e.Name); Assert.Equal("Dentist", e.Arg("title")); Assert.Equal("2026-11-21T17:00", e.Arg("start")); Assert.Equal("45", e.Arg("minutes"));
        var s = SelfAssistant.ParseIntent("{\"intent\":\"send\",\"to\":\"Ravi\",\"text\":\"Running late\"}");
        Assert.Equal("send", s.Name); Assert.Equal("Ravi", s.Arg("to")); Assert.Equal("Running late", s.Arg("text"));
        Assert.Equal("calendar", SelfAssistant.ParseIntent("{\"intent\":\"calendar\",\"from\":\"2026-11-21\"}").Name);
    }

    [Fact]
    public void A_busy_intent_carries_its_length_and_note()
    {
        var c = SelfAssistant.ParseIntent("{\"intent\":\"busy\",\"minutes\":90,\"note\":\"in a meeting\"}").Command!;
        Assert.Equal(TimeSpan.FromMinutes(90), c.Duration); Assert.Equal("in a meeting", c.Note);
        Assert.Equal("17:30", SelfAssistant.ParseIntent("{\"intent\":\"busy\",\"until\":\"17:30\"}").Command!.UntilClock);
    }

    [Theory] [InlineData("")] [InlineData("   ")]
    public void Empty_messages_are_not_candidates(string text) => Assert.False(SelfAssistant.IsCandidate(text));

    [Theory] [InlineData("a")] [InlineData("https://example.com")] [InlineData("remember the wifi password is abc")]
    public void Everything_else_in_your_own_chat_is_for_Daksha(string text) => Assert.True(SelfAssistant.IsCandidate(text));

    [Theory]
    [InlineData("https://youtu.be/abc123", "link")] [InlineData("www.example.com/page", "link")] [InlineData("📷 Photo", "photo")] [InlineData("🎥 Video", "video")]
    [InlineData("🎤 Voice message", "voice note")] [InlineData("🎵 Audio", "voice note")] [InlineData("📄 Document", "file")]
    public void A_link_or_media_with_no_instruction_is_a_drop(string text, string kind) => Assert.Equal(kind, SelfAssistant.DropKind(text));

    [Theory] [InlineData("save https://example.com to reading list")] [InlineData("what is on my calendar")] [InlineData("Photo of the receipt for Ravi")]
    public void Text_with_an_instruction_or_caption_is_not_a_drop(string text) => Assert.Null(SelfAssistant.DropKind(text));

    [Theory] [InlineData("hi")] [InlineData("Hello!")] [InlineData("hey daksha")] [InlineData("good morning")] [InlineData("are you there?")] [InlineData("test")]
    public void Greetings_are_for_the_assistant(string text) { Assert.True(SelfAssistant.IsGreeting(text)); Assert.True(SelfAssistant.LooksLikeQuestion(text)); }

    [Theory] [InlineData("hi mom is coming at 5")] [InlineData("buy milk")] [InlineData("high protein diet plan")]
    public void Other_text_is_not_a_greeting(string text) => Assert.False(SelfAssistant.IsGreeting(text));

    [Fact] public void Our_own_replies_are_not_candidates() => Assert.False(SelfAssistant.IsCandidate(SelfCommands.ReplyMarker + "hello there"));

    [Fact] public void Our_own_replies_are_never_questions() => Assert.False(SelfAssistant.LooksLikeQuestion(SelfCommands.ReplyMarker + "what's pending?"));
    [Fact] public void The_address_is_stripped() => Assert.Equal("what's pending?", SelfAssistant.StripAddress("Hey Daksha, what's pending?"));
}

public class AutoReplyVoiceTests
{
    [Theory]
    [InlineData("I am Daksh, your personal AI operator. It looks like you've pasted a system prompt or test message.")]
    [InlineData("Let me know how I can help you with your connected apps (WhatsApp, Telegram, Gmail, tasks).")]
    [InlineData("Hi, this is Daksha!")]
    public void Replies_in_Dakshas_own_voice_are_caught(string text) => Assert.True(HumanReply.LooksWrong(text));

    [Theory]
    [InlineData("Hey! Kaise ho?")] [InlineData("Hello! Good to hear from you.")] [InlineData("Haan bhai, main bata deta hoon sir ko.")]
    public void Normal_replies_pass(string text) { Assert.False(HumanReply.LooksWrong(text)); Assert.NotNull(HumanReply.Clean(text)); }

    [Fact]
    public void The_auto_reply_instructions_and_the_senders_message_are_kept_apart()
    {
        var (instructions, message) = IntegrationsModule.BuildAutoReplyParts(new WhatsAppSettings { ConnectionId = "c", BossReference = "Amit sir" }, "1@s.whatsapp.net", "Ravi", "Hello", false);
        Assert.Equal("Hello", message);
        Assert.DoesNotContain("Hello", instructions.Replace("greetings", ""));
        Assert.Contains("on behalf of the account owner", instructions);
    }
}

public class SelfSendTests
{
    private sealed class FakeApp(params string[] names) : ISelfMessenger
    {
        public string AppName => "FakeChat";
        public List<(string Chat, string Text)> Sent { get; } = [];
        public bool Fail { get; set; }
        public Task<IReadOnlyList<ChatTarget>> FindAsync(string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatTarget>>(names.Where(n => n.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(n => new ChatTarget("id:" + n, n)).ToList());
        public Task SendAsync(string chatId, string text, CancellationToken ct) { if (Fail) throw new InvalidOperationException(); Sent.Add((chatId, text)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task One_match_is_sent_to_on_any_app()
    {
        var app = new FakeApp("Ravi Kumar", "Meera");
        var reply = await SelfSend.RunAsync(app, "ravi", "Running late", default);
        Assert.Equal([("id:Ravi Kumar", "Running late")], app.Sent);
        Assert.StartsWith("✅", reply); Assert.Contains("FakeChat", reply);
    }

    [Fact]
    public async Task An_exact_name_wins_over_longer_matches()
    {
        var app = new FakeApp("Ravi", "Ravi Kumar");
        await SelfSend.RunAsync(app, "Ravi", "hi", default);
        Assert.Equal("id:Ravi", Assert.Single(app.Sent).Chat);
    }

    [Fact]
    public async Task Several_matches_or_none_send_nothing()
    {
        var app = new FakeApp("Ravi Kumar", "Ravi Shah");
        Assert.Contains("more than one", await SelfSend.RunAsync(app, "Ravi", "hi", default));
        Assert.Contains("couldn't find", await SelfSend.RunAsync(app, "Zed", "hi", default));
        Assert.Empty(app.Sent);
    }

    [Fact] public async Task A_failed_send_is_reported_not_thrown() => Assert.StartsWith("ℹ️", await SelfSend.RunAsync(new FakeApp("Ravi") { Fail = true }, "Ravi", "hi", default));
}
