using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class ReplyGateTests
{
    private static GateVerdict V(string text, bool group = false) => ReplyGate.Evaluate(text, group).Verdict;

    // ── The AI may answer only simple social messages ────────────────────────────────────────────
    [Theory]
    [InlineData("hi")] [InlineData("Hi!")] [InlineData("Hello")] [InlineData("hello sir")] [InlineData("Hey there")] [InlineData("hii")]
    [InlineData("Good morning")] [InlineData("good morning sir!")] [InlineData("Good night")] [InlineData("gm")]
    [InlineData("how are you?")] [InlineData("How r u")] [InlineData("hi, how are you?")] [InlineData("hello bhai kaise ho")]
    [InlineData("what's up")] [InlineData("namaste")] [InlineData("Namaste sir")] [InlineData("kya haal hai")]
    [InlineData("thanks")] [InlineData("Thank you so much!")] [InlineData("thanks sir")] [InlineData("dhanyavad")]
    [InlineData("Happy birthday!")] [InlineData("Happy Diwali")] [InlineData("congratulations")] [InlineData("all the best")]
    [InlineData("bye")] [InlineData("take care")]
    [InlineData("Hi\nhow are you")]
    public void Greetings_and_pleasantries_may_be_answered_by_the_AI(string text) => Assert.Equal(GateVerdict.AiOk, V(text));

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
}
