using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class ReplyGateTests
{
    private static GateVerdict V(string text, bool group = false) => ReplyGate.Evaluate(text, group).Verdict;

    // ── The AI may answer only simple social messages ────────────────────────────────────────────
    [Theory]
    [InlineData("hi")] [InlineData("Hi!")] [InlineData("Hello")] [InlineData("hello sir")] [InlineData("Hey there")] [InlineData("hii")]
    [InlineData("Good morning")] [InlineData("good morning sir!")] [InlineData("gm")]
    [InlineData("how are you?")] [InlineData("How r u")] [InlineData("hi, how are you?")] [InlineData("hello bhai kaise ho")]
    [InlineData("what's up")] [InlineData("namaste")] [InlineData("Namaste sir")] [InlineData("kya haal hai")]
    [InlineData("Happy birthday!")] [InlineData("Happy Diwali")] [InlineData("congratulations")] [InlineData("all the best")]
    [InlineData("Hi\nhow are you")]
    public void Greetings_and_pleasantries_may_be_answered_by_the_AI(string text) => Assert.Equal(GateVerdict.AiOk, V(text));

    // ── Ending the conversation gets a fixed, friendly sign-off ──────────────────────────────────────
    [Theory]
    [InlineData("thanks")] [InlineData("Thank you so much!")] [InlineData("thanks sir")] [InlineData("dhanyavad")]
    [InlineData("bye")] [InlineData("take care")] [InlineData("thanks, bye")] [InlineData("ok thanks")] [InlineData("Good night")]
    public void Ending_the_conversation_gets_a_thank_you(string text) => Assert.Equal(GateVerdict.Closing, V(text));

    [Fact]
    public void Thanks_followed_by_a_request_is_still_a_request() => Assert.Equal(GateVerdict.Boss, V("thanks, can you send me the invoice?"));

    // ── Reels, images, videos, voice notes, stickers, emoji and plain acknowledgements get no reply ─────
    [Theory]
    [InlineData("📷 Photo")] [InlineData("🎥 Video")] [InlineData("🎤 Voice message")] [InlineData("📄 Document")] [InlineData("📍 Location")]
    [InlineData("Sticker")] [InlineData("👤 Contact: Ravi")] [InlineData("📞 Missed voice call")] [InlineData("📹 Missed video call")]
    [InlineData("https://www.instagram.com/reel/Cx12345AbCd/")] [InlineData("check this https://www.instagram.com/reel/Cx12345AbCd/?igsh=abc")]
    [InlineData("https://youtube.com/shorts/abc123")] [InlineData("https://youtu.be/dQw4w9WgXcQ")] [InlineData("https://vm.tiktok.com/ZMabc/")]
    [InlineData("https://fb.watch/abc/")] [InlineData("https://www.facebook.com/reel/12345")]
    [InlineData("reel")] [InlineData("Reels")] [InlineData("real")] [InlineData("image")] [InlineData("photo")] [InlineData("pic")] [InlineData("video")]
    [InlineData("see this reel")] [InlineData("look at this photo")] [InlineData("sent you a screenshot")] [InlineData("funny meme")]
    [InlineData("👍")] [InlineData("😂😂😂")] [InlineData("🙏")] [InlineData("❤️")] [InlineData("...")] [InlineData("   ")] [InlineData("")]
    [InlineData("ok")] [InlineData("okay")] [InlineData("okk")] [InlineData("k")] [InlineData("hmm")] [InlineData("noted")] [InlineData("got it")]
    [InlineData("theek hai")] [InlineData("haan")] [InlineData("lol")] [InlineData("cool")] [InlineData("ok 👍")]
    public void Media_reels_emoji_and_acknowledgements_get_no_reply(string text) => Assert.Equal(GateVerdict.Silent, V(text));

    // ── Anything needing a decision or information is held for the boss ──────────────────────────────
    [Theory]
    [InlineData("what time is the meeting tomorrow?")]
    [InlineData("please send me the invoice")]
    [InlineData("can you pay 5000 rupees by evening")]
    [InlineData("are you free today for a call")]
    [InlineData("I need the price quote for 100 units")]
    [InlineData("Can you confirm the booking for Friday?")]
    [InlineData("Where is my order? It was supposed to arrive yesterday")]
    [InlineData("hi, can you send me the document?")]
    [InlineData("hello sir, I want to discuss the contract terms with you")]
    [InlineData("we received your proposal and would like to schedule a meeting next week")]
    [InlineData("send me ₹2000 on UPI")]
    [InlineData("why was my payment rejected")]
    [InlineData("kab aa rahe ho office me")]
    [InlineData("paisa kab bhejoge")]
    [InlineData("mujhe 500 rupaye chahiye")]
    [InlineData("I am facing a problem with the delivery and nobody is responding")]
    [InlineData("Do you accept the offer or not?")]
    [InlineData("are you coming to the party on Saturday?")]
    [InlineData("tell me your bank account number")]
    [InlineData("What do you think about the new plan")]
    [InlineData("I will visit your shop tomorrow morning")]
    public void Questions_requests_money_time_and_decisions_are_held_for_the_boss(string text)
    {
        var verdict = V(text);
        Assert.True(verdict is GateVerdict.Boss or GateVerdict.BossAskMore, $"\"{text}\" was {verdict}");
    }

    [Theory]
    [InlineData("call me")] [InlineData("urgent")] [InlineData("need help")] [InlineData("please call")] [InlineData("are you there")]
    [InlineData("can we talk")] [InlineData("?")] [InlineData("???")]
    public void A_request_with_no_detail_asks_the_sender_for_more_information(string text) => Assert.Equal(GateVerdict.BossAskMore, V(text));

    [Fact]
    public void Hello_with_a_question_mark_is_still_just_a_greeting() => Assert.Equal(GateVerdict.AiOk, V("hello?"));

    [Fact]
    public void A_detailed_request_is_passed_on_without_asking_again() =>
        Assert.Equal(GateVerdict.Boss, V("please send me the March invoice for order 1042 before Friday"));

    [Fact]
    public void A_greeting_followed_by_a_request_is_not_small_talk()
    {
        Assert.NotEqual(GateVerdict.AiOk, V("hi sir, can you approve my leave request?"));
        Assert.NotEqual(GateVerdict.AiOk, V("good morning, what is the status of my file"));
    }

    [Fact]
    public void Unrecognised_messages_default_to_the_boss_never_to_the_AI() =>
        Assert.Equal(GateVerdict.Boss, V("The quarterly numbers for the northern region look different this time around"));

    [Fact]
    public void Asking_for_a_photo_is_a_request_not_a_shared_photo() =>
        Assert.NotEqual(GateVerdict.Silent, V("can you send me the photo of the invoice?"));

    // ── Groups: never spam a group with "I'll ask my boss" ───────────────────────────────────────────
    [Theory]
    [InlineData("what time is the meeting tomorrow?")]
    [InlineData("please send the report")]
    public void In_a_group_messages_needing_a_decision_get_no_reply_at_all(string text) => Assert.Equal(GateVerdict.Silent, V(text, group: true));

    [Fact]
    public void A_group_greeting_is_still_small_talk() => Assert.Equal(GateVerdict.AiOk, V("Good morning everyone", group: true));

    // ── The wording of holding replies ───────────────────────────────────────────────────────────────
    [Theory]
    [InlineData(GateVerdict.Boss)]
    [InlineData(GateVerdict.BossAskMore)]
    public void Holding_replies_speak_as_an_assistant_and_name_the_boss(GateVerdict verdict)
    {
        foreach (var seed in new[] { "a", "b", "c", "d", "e", "f", "g" })
        {
            var reply = ReplyGate.HoldingReply(verdict, null, seed);
            Assert.Contains("my boss", reply);
            Assert.Contains("AI assistant", reply);
            Assert.DoesNotContain("NEED_BOSS", reply);
            Assert.True(reply.Length < 300, reply);
        }
    }

    [Fact]
    public void Asking_for_more_information_actually_asks_a_question()
    {
        for (var i = 0; i < 6; i++) Assert.Contains("?", ReplyGate.HoldingReply(GateVerdict.BossAskMore, "my boss", $"seed{i}"));
        for (var i = 0; i < 6; i++) Assert.DoesNotContain("could you share", ReplyGate.HoldingReply(GateVerdict.Boss, "my boss", $"seed{i}"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_boss_can_be_given_another_name_and_the_wording_stays_deterministic()
    {
        var a = ReplyGate.HoldingReply(GateVerdict.Boss, "Amit sir", "chat1:message");
        Assert.Contains("Amit sir", a);
        Assert.DoesNotContain("my boss", a);
        Assert.Equal(a, ReplyGate.HoldingReply(GateVerdict.Boss, "Amit sir", "chat1:message"));
        Assert.Equal("my boss", ReplyGate.NormaliseBoss("  "));
        Assert.Equal(60, ReplyGate.NormaliseBoss(new string('x', 200)).Length);
    }

    [Theory]
    [InlineData("NEED_BOSS", true)] [InlineData("need_boss", true)] [InlineData("NEED_BOSS.", true)] [InlineData("Hello! How can I help?", false)] [InlineData("I need my boss to see this", false)]
    public void The_models_own_NEED_BOSS_signal_is_recognised(string reply, bool expected) => Assert.Equal(expected, ReplyGate.ModelSaidNeedBoss(reply));

    [Fact]
    public void The_prompt_makes_the_AI_an_assistant_that_defers_decisions()
    {
        var prompt = IntegrationsModule.BuildAutoReplyPrompt(new WhatsAppSettings { ConnectionId = "c", BossReference = "Amit sir" }, "123@s.whatsapp.net", "Ravi", "hello", false);
        Assert.Contains("AI assistant", prompt);
        Assert.Contains("Amit sir", prompt);
        Assert.Contains("NEED_BOSS", prompt);
        Assert.Contains("NEVER make a decision", prompt);
        Assert.DoesNotContain("You ARE the owner", prompt);
        Assert.DoesNotContain("answer naturally and confidently", prompt);
    }

    [Fact]
    public void The_gate_defaults_are_safe()
    {
        var s = new WhatsAppSettings { ConnectionId = "c" };
        Assert.True(s.ReplyGateEnabled);
        Assert.Equal("my boss", s.BossReference);
        Assert.Equal(240, s.GateHoldingCooldownMinutes);
    }

    [Fact]
    public void A_short_fragment_in_a_busy_conversation_goes_to_the_owner_instead_of_asking_for_detail()
    {
        // "Office party" alone looks vague, but in the middle of a conversation it is a question about plans.
        Assert.Equal(GateVerdict.BossAskMore, ReplyGate.Evaluate("Office party", false, inConversation: false).Verdict);
        Assert.Equal(GateVerdict.Boss, ReplyGate.Evaluate("Office party", false, inConversation: true).Verdict);
        Assert.Equal(GateVerdict.Boss, ReplyGate.Evaluate("?", false, inConversation: true).Verdict);
    }

    [Fact]
    public void The_assistant_stays_out_of_a_chat_the_owner_just_wrote_in()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(IntegrationsModule.IsOwnerActive(now.AddMinutes(-3), now));
        Assert.False(IntegrationsModule.IsOwnerActive(now.AddMinutes(-16), now));
        Assert.False(IntegrationsModule.IsOwnerActive(null, now));
    }

    [Fact]
    public void The_prompt_tells_the_model_to_read_the_conversation_and_hand_over_questions_about_plans()
    {
        var prompt = IntegrationsModule.BuildAutoReplyPrompt(new WhatsAppSettings { ConnectionId = "c" }, "1@s.whatsapp.net", "Yaashicca", "Office party", false);
        Assert.Contains("read them first", prompt);
        Assert.Contains("NEED_BOSS", prompt);
        Assert.Contains("plans", prompt);
    }

    [Fact]
    public void The_name_saved_in_the_phone_beats_the_name_the_person_chose_and_our_own_name_is_never_used()
    {
        var saved = new Dictionary<string, string> { ["1@lid"] = "Yashika (saved)" };
        var self = new Dictionary<string, string> { ["1@lid"] = "Yaashicca", ["2@lid"] = "Rohan K" };
        var msgs = new[] { (FromMe: true, ChatName: (string?)"Aj"), (FromMe: false, ChatName: (string?)"Pushname") };
        Assert.Equal("Yashika (saved)", IntegrationsModule.PickChatName("1@lid", saved, self, msgs));
        Assert.Equal("Rohan K", IntegrationsModule.PickChatName("2@lid", saved, self, msgs));
        Assert.Equal("Pushname", IntegrationsModule.PickChatName("3@lid", saved, self, msgs));
        Assert.Null(IntegrationsModule.PickChatName("3@lid", saved, self, new[] { (FromMe: true, ChatName: (string?)"Aj") })); // never our own name
        Assert.Null(IntegrationsModule.PickChatName("120@g.us", saved, self, msgs)); // a group message carries the sender's name
    }

    [Theory]
    [InlineData("image/jpeg", "image")]
    [InlineData("video/mp4", "video")]
    [InlineData("audio/ogg", "audio")]
    [InlineData("application/pdf", "document")]
    public void Files_are_sent_as_the_right_kind_of_attachment(string mime, string kind) =>
        Assert.Equal(kind, WhatsAppFeaturesModule.KindFor(mime));

    [Fact]
    public void Chat_assist_builds_a_labelled_transcript_and_parses_three_suggestions()
    {
        var t0 = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var transcript = ChatAssistModule.Transcript([(false, "Are you coming?", t0.AddMinutes(1)), (true, "Maybe", t0)]);
        Assert.Equal("Me: Maybe\nThem: Are you coming?", transcript); // oldest first, who said what
        Assert.Equal(["Sounds good", "Let me check", "Can't make it"], ChatAssistModule.ParseSuggestions("1. Sounds good\n2) \"Let me check\"\n- Can't make it\nextra four"));
        Assert.Empty(ChatAssistModule.ParseSuggestions(null));
        Assert.Contains("must not promise", ChatAssistModule.PromptFor("suggest", "Them: hi", null, null));
        Assert.Contains("French", ChatAssistModule.PromptFor("translate", "", "Bonjour", "French"));
        Assert.Throws<ArgumentException>(() => ChatAssistModule.PromptFor("nope", "", null, null));
    }

    [Fact]
    public void A_message_document_with_fields_the_api_does_not_know_still_loads()
    {
        // The WhatsApp service adds bookkeeping fields (read marker, deleted time, and whatever comes next). Reading a chat must never
        // fail because of a field this class has not heard of -- that once made every opened chat show no messages.
        var doc = new MongoDB.Bson.BsonDocument
        {
            { "connectionId", "c" }, { "messageId", "m" }, { "chatId", "1@lid" }, { "fromMe", false }, { "text", "hi" },
            { "timestamp", DateTime.UtcNow }, { "readSent", true }, { "deletedAt", DateTime.UtcNow }, { "someFutureField", 7 },
        };
        var message = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<WhatsAppMessage>(doc);
        Assert.True(message.ReadSent);
        Assert.Equal("hi", message.Text);
    }

    [Fact]
    public void Dashboard_tab_names_blocks_and_default_names_follow_the_rules()
    {
        Assert.Equal("My board", DashboardTabs.CleanName("  My    board \n"));
        Assert.Null(DashboardTabs.CleanName("   "));
        Assert.Null(DashboardTabs.CleanName(null));
        Assert.Equal(40, DashboardTabs.CleanName(new string('a', 100))!.Length);
        Assert.Equal(["command", "apps"], DashboardTabs.CleanWidgets(["apps", "bogus", "command", "apps"])); // known blocks only, once each, in catalog order
        Assert.Empty(DashboardTabs.CleanWidgets(null));
        Assert.Equal("Dashboard", DashboardTabs.NextDefaultName([]));
        Assert.Equal("Dashboard 2", DashboardTabs.NextDefaultName(["Dashboard"]));
        Assert.Equal("Dashboard 3", DashboardTabs.NextDefaultName(["dashboard", "Dashboard 2"]));
    }

    [Fact]
    public void A_real_question_with_no_app_named_still_searches_the_apps_for_its_words()
    {
        Assert.Equal(["coming", "party", "Saturday"], AssistantIntent.Keywords("Who is coming to the party on Saturday?"));
        Assert.Empty(AssistantIntent.Keywords("what did you say"));
        Assert.True(AssistantIntent.IsSubstantive("Who is coming to the party?"));
        Assert.False(AssistantIntent.IsSubstantive("hi"));
        Assert.False(AssistantIntent.IsSubstantive("ok thanks"));
    }

    [Fact]
    public void The_assistant_works_out_which_apps_a_question_is_about()
    {
        var names = new[] { "Yaashicca", "Rohan Kumar", "PSW Advertisements", "Mom" };
        var chat = AssistantIntent.Detect("What did Rohan say on WhatsApp?", names);
        Assert.True(chat.Messages);
        Assert.Equal(["Rohan Kumar"], chat.Names); // a first name is enough
        Assert.True(AssistantIntent.Detect("Any new emails from the bank in my inbox?", names).Mail);
        Assert.True(AssistantIntent.Detect("What tasks are overdue?", names).Tasks);
        Assert.True(AssistantIntent.Detect("Catch me up on today", names).Overview);
        Assert.False(AssistantIntent.Detect("What is the capital of France?", names).AnyApp);
        Assert.Equal(["Mom"], AssistantIntent.Detect("what did mom say?", names).Names); // a chat really named Mom matches
        Assert.Empty(AssistantIntent.Detect("the team group", ["Team Alpha"]).Names); // a generic first word alone does not pick a chat
        Assert.Equal(["Yaashicca"], AssistantIntent.Detect("did yaashicca reply?", names).Names); // case does not matter
        Assert.Empty(AssistantIntent.Detect("the roha situation", names).Names); // only whole words match
    }

    [Fact]
    public void Chat_context_is_one_labelled_line_per_message_oldest_first_and_long_text_is_clipped()
    {
        var t = new DateTime(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);
        var text = AssistantContextBuilder.FormatChat("Rohan", [(t.AddMinutes(5), false, "Rohan", "Still on tonight?"), (t, true, "Me", new string('x', 500))]);
        var lines = text.Split('\n');
        Assert.Equal("Chat with Rohan:", lines[0]);
        Assert.Contains("Me:", lines[1]); // the older one first
        Assert.Contains("Rohan: Still on tonight?", lines[2]);
        Assert.True(lines[1].Length < 360);
        Assert.Equal("a b", AssistantContextBuilder.Clip("  a\n\n b ", 10));
        Assert.Equal("abc…", AssistantContextBuilder.Clip("abcdef", 3));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, "ai_key_invalid")]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "ai_key_invalid")]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, "ai_rate_limited")]
    [InlineData(System.Net.HttpStatusCode.NotFound, "ai_model_missing")]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "ai_unavailable")]
    public void An_ai_provider_failure_is_explained_in_terms_a_person_can_act_on(System.Net.HttpStatusCode status, string code)
    {
        var (title, actual) = AssistantModule.DescribeAiFailure(new HttpRequestException("x", null, status));
        Assert.Equal(code, actual);
        Assert.False(string.IsNullOrWhiteSpace(title));
        Assert.Equal("ai_unavailable", AssistantModule.DescribeAiFailure(new InvalidOperationException()).Code);
    }
}
