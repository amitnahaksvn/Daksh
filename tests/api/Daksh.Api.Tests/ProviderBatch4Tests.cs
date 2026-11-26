using System.Net;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;

namespace Daksh.Api.Tests;

public class ProviderBatch4Tests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }

    private static ProviderCtx Ctx(string key, Handler handler, Dictionary<string, string>? cred = null, Dictionary<string, string>? settings = null, string? token = null) => new()
    {
        App = ProviderApps.Get(key), Db = null!, Config = new ConfigurationBuilder().Build(), Clients = new Factory(handler), Token = token, Cred = cred ?? [], Settings = settings ?? [],
        Account = new AppAccountDoc { ConnectionId = "c", UserId = "u", AppKey = key, AccountId = "acct" },
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static Task<IReadOnlyList<AppItem>> Load(ProviderCtx c, string view, params (string, string)[] input) => ProviderApps.Get(c.App.Key).Views.Single(v => v.Id == view).Load(c, input.ToDictionary(i => i.Item1, i => i.Item2));
    private static Task<ActionResult> Run(ProviderCtx c, string action, params (string, string)[] input) => ProviderApps.Get(c.App.Key).Actions.Single(a => a.Id == action).Run(c, input.ToDictionary(i => i.Item1, i => i.Item2));

    private static readonly string[] ModelKeys =
    [
        "openai", "anthropic", "google-gemini", "mistral", "deepseek", "qwen", "moonshot-kimi", "minimax", "zai", "cohere", "groq", "cerebras", "together-ai", "fireworks", "nvidia", "hugging-face", "openrouter", "litellm",
        "amazon-bedrock", "anthropic-vertex", "github-copilot", "cloudflare-ai-gateway", "vercel-ai-gateway", "ollama", "lm-studio", "vllm", "custom-openai-api",
    ];

    [Fact]
    public void All_twenty_seven_model_providers_exist_and_every_one_offers_a_gated_test_prompt()
    {
        foreach (var key in ModelKeys)
        {
            var app = ProviderApps.Get(key);
            Assert.True(app.HasWrite, key);
            Assert.Contains(app.Actions, a => a.Id == "chat");
            Assert.Contains(app.Views, v => v.Id == "models" && v.Dashboard);
            Assert.False(app.WriteNeedsSignIn, key); // a Daksha-side switch, not a new permission
        }
    }

    [Fact]
    public async Task OpenAI_style_providers_list_models_with_a_bearer_key_and_chat_through_completions()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"data":[{"id":"gpt-4o-mini","owned_by":"openai"},{"id":"gpt-4o","owned_by":"openai"}]}""") : Json("""{"choices":[{"message":{"content":"Hello there"}}]}"""));
        var c = Ctx("openai", h, cred: new() { ["apiKey"] = "sk-test" });
        var models = await Load(c, "models");
        Assert.Equal(["gpt-4o-mini", "gpt-4o"], models.Select(m => m.Id));
        Assert.Equal("https://api.openai.com/v1/models", h.Seen[0].Request.RequestUri!.ToString());
        Assert.Equal("sk-test", h.Seen[0].Request.Headers.Authorization!.Parameter);
        var r = await Run(c, "chat", ("model", "gpt-4o-mini"), ("prompt", "Hi"));
        Assert.Equal("Hello there", r.Message);
        Assert.EndsWith("/chat/completions", h.Seen[1].Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"role\":\"user\"", h.Seen[1].Body);
    }

    [Fact]
    public async Task Together_returns_a_bare_array_and_that_is_read_too()
    {
        var h = new Handler(_ => Json("""[{"id":"meta-llama/Llama-3-8b","context_length":8192}]"""));
        var models = await Load(Ctx("together-ai", h, cred: new() { ["apiKey"] = "k" }), "models");
        Assert.Equal("meta-llama/Llama-3-8b", Assert.Single(models).Id); Assert.Contains("8192 tokens", models[0].Subtitle);
    }

    [Fact]
    public async Task Anthropic_uses_its_own_headers_and_message_shape()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"data":[{"id":"claude-x","display_name":"Claude X","created_at":"2026-01-01T00:00:00Z"}]}""") : Json("""{"content":[{"type":"text","text":"Bonjour"}]}"""));
        var c = Ctx("anthropic", h, cred: new() { ["apiKey"] = "sk-ant" });
        Assert.Equal("Claude X", (await Load(c, "models")).Single().Title);
        Assert.Equal("sk-ant", h.Seen[0].Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", h.Seen[0].Request.Headers.GetValues("anthropic-version").Single());
        Assert.Null(h.Seen[0].Request.Headers.Authorization);
        Assert.Equal("Bonjour", (await Run(c, "chat", ("model", "claude-x"), ("prompt", "Hi"))).Message);
        Assert.Contains("max_tokens", h.Seen[1].Body);
    }

    [Fact]
    public async Task Gemini_puts_the_key_in_a_header_and_escapes_the_model_name()
    {
        var h = new Handler(r => r.Method == HttpMethod.Get ? Json("""{"models":[{"name":"models/gemini-2.0-flash","displayName":"Gemini Flash","inputTokenLimit":1048576,"supportedGenerationMethods":["generateContent"]}]}""") : Json("""{"candidates":[{"content":{"parts":[{"text":"Namaste"}]}}]}"""));
        var c = Ctx("google-gemini", h, cred: new() { ["apiKey"] = "AIza" });
        var models = await Load(c, "models");
        Assert.Equal("gemini-2.0-flash", models[0].Id); Assert.Equal("chat", models[0].Badge);
        Assert.Equal("AIza", h.Seen[0].Request.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("AIza", h.Seen[0].Request.RequestUri!.ToString());
        Assert.Equal("Namaste", (await Run(c, "chat", ("model", "gemini-2.0-flash"), ("prompt", "Hi"))).Message);
        Assert.EndsWith("/models/gemini-2.0-flash:generateContent", h.Seen[1].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Cohere_and_Ollama_and_GitHub_Models_each_use_their_own_routes()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => Json("""{"models":[{"name":"command-r","context_length":128000,"endpoints":["chat"]}]}"""),
            "/v2/chat" => Json("""{"message":{"content":[{"text":"cohere says hi"}]}}"""),
            "/api/tags" => Json("""{"models":[{"name":"llama3:8b","size":4661224676,"details":{"parameter_size":"8B"},"modified_at":"2026-01-01T00:00:00Z"}]}"""),
            "/api/chat" => Json("""{"message":{"content":"ollama says hi"}}"""),
            "/catalog/models" => Json("""[{"id":"openai/gpt-4o","name":"GPT-4o","publisher":"OpenAI","summary":"A model"}]"""),
            _ => Json("""{"choices":[{"message":{"content":"github says hi"}}]}"""),
        });
        Assert.Equal("command-r", (await Load(Ctx("cohere", h, cred: new() { ["apiKey"] = "k" }), "models")).Single().Id);
        Assert.Equal("cohere says hi", (await Run(Ctx("cohere", h, cred: new() { ["apiKey"] = "k" }), "chat", ("model", "command-r"), ("prompt", "hi"))).Message);
        var ollama = Ctx("ollama", h, cred: new() { ["url"] = "http://localhost:11434" });
        Assert.Equal("llama3:8b", (await Load(ollama, "models")).Single().Id);
        Assert.Equal("ollama says hi", (await Run(ollama, "chat", ("model", "llama3:8b"), ("prompt", "hi"))).Message);
        Assert.Null(h.Seen.Last(s => s.Request.RequestUri!.AbsolutePath == "/api/chat").Request.Headers.Authorization); // no key needed
        var gh = Ctx("github-copilot", h, cred: new() { ["apiKey"] = "ghp_x" });
        Assert.Equal("openai/gpt-4o", (await Load(gh, "models")).Single().Id);
        Assert.Equal("2022-11-28", h.Seen.Last().Request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Equal("github says hi", (await Run(gh, "chat", ("model", "openai/gpt-4o"), ("prompt", "hi"))).Message);
        Assert.Equal("https://models.github.ai/inference/chat/completions", h.Seen.Last().Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Bedrock_signs_the_request_and_escapes_the_model_id_in_the_path()
    {
        var h = new Handler(r => r.RequestUri!.Host.StartsWith("bedrock-runtime") ? Json("""{"output":{"message":{"content":[{"text":"hi from bedrock"}]}}}""") : Json("""{"modelSummaries":[{"modelId":"anthropic.claude-3-haiku-20240307-v1:0","modelName":"Claude 3 Haiku","providerName":"Anthropic"}]}"""));
        var c = Ctx("amazon-bedrock", h, cred: new() { ["accessKeyId"] = "AKIAEXAMPLE12345678", ["secretAccessKey"] = "sec", ["region"] = "us-east-1" });
        var models = await Load(c, "models");
        Assert.Equal("Claude 3 Haiku", models.Single().Title);
        Assert.Equal("bedrock.us-east-1.amazonaws.com", h.Seen[0].Request.RequestUri!.Host);
        Assert.StartsWith("AWS4-HMAC-SHA256", h.Seen[0].Request.Headers.GetValues("Authorization").Single());
        Assert.Equal("hi from bedrock", (await Run(c, "chat", ("model", "anthropic.claude-3-haiku-20240307-v1:0"), ("prompt", "hi"))).Message);
        Assert.EndsWith("/model/anthropic.claude-3-haiku-20240307-v1%3A0/converse", h.Seen[1].Request.RequestUri!.AbsolutePath.Length > 0 ? h.Seen[1].Request.RequestUri!.AbsoluteUri.Split('?')[0] : "");
        Assert.Equal("application/json", h.Seen[1].Request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Cloudflare_gateway_builds_its_address_from_the_account_and_gateway_and_can_add_a_gateway_token()
    {
        var h = new Handler(_ => Json("""{"data":[{"id":"gpt-4o-mini"}]}"""));
        var acct = new string('a', 32);
        var c = Ctx("cloudflare-ai-gateway", h, cred: new() { ["accountId"] = acct, ["gatewayId"] = "my-gw", ["apiKey"] = "sk", ["gatewayToken"] = "cfg" });
        await Load(c, "models");
        Assert.Equal($"https://gateway.ai.cloudflare.com/v1/{acct}/my-gw/openai/models", h.Seen[0].Request.RequestUri!.ToString());
        Assert.Equal("Bearer cfg", h.Seen[0].Request.Headers.GetValues("cf-aig-authorization").Single());
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.Get("cloudflare-ai-gateway").Credentials!.Prepare!(new Dictionary<string, string> { ["accountId"] = "short", ["gatewayId"] = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task Self_hosted_servers_get_a_v1_suffix_only_when_they_speak_the_openai_style_and_link_local_is_refused()
    {
        var lm = ProviderApps.Get("lm-studio").Credentials!.Prepare!;
        var v = new Dictionary<string, string> { ["url"] = "localhost:1234", ["apiKey"] = "" };
        await lm(v, CancellationToken.None);
        Assert.Equal("http://localhost:1234/v1", v["url"]);
        var keep = new Dictionary<string, string> { ["url"] = "https://203.0.113.9/v2", ["apiKey"] = "" };
        await ProviderApps.Get("custom-openai-api").Credentials!.Prepare!(keep, CancellationToken.None);
        Assert.Equal("https://203.0.113.9/v2", keep["url"]);
        var oll = new Dictionary<string, string> { ["url"] = "http://localhost:11434", ["apiKey"] = "" };
        await ProviderApps.Get("ollama").Credentials!.Prepare!(oll, CancellationToken.None);
        Assert.Equal("http://localhost:11434", oll["url"]); // Ollama has its own routes, no /v1
        await Assert.ThrowsAsync<AppException>(() => lm(new Dictionary<string, string> { ["url"] = "http://169.254.169.254/", ["apiKey"] = "" }, CancellationToken.None));
    }

    [Fact]
    public async Task MiniMax_without_a_model_list_still_connects_and_says_so()
    {
        var h = new Handler(_ => Json("{}", HttpStatusCode.NotFound));
        var models = await Load(Ctx("minimax", h, cred: new() { ["apiKey"] = "k" }), "models");
        Assert.Contains("does not list", Assert.Single(models).Title);
    }

    [Fact]
    public async Task A_model_name_cannot_smuggle_extra_path_or_odd_characters()
    {
        var h = new Handler(_ => Json("{}"));
        var c = Ctx("openai", h, cred: new() { ["apiKey"] = "k" });
        await Assert.ThrowsAsync<AppException>(() => Run(c, "chat", ("model", "gpt 4; drop"), ("prompt", "x")));
        await Assert.ThrowsAsync<AppException>(() => Run(Ctx("google-gemini", h, cred: new() { ["apiKey"] = "k" }), "chat", ("model", "../../x?y"), ("prompt", "x")));
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Vertex_needs_the_project_and_region_set_before_it_sends_anything()
    {
        var h = new Handler(_ => Json("""{"content":[{"text":"vertex says hi"}]}"""));
        await Assert.ThrowsAsync<AppException>(() => Run(Ctx("anthropic-vertex", h, token: "T"), "chat", ("model", "claude-x@20250101"), ("prompt", "hi")));
        var r = await Run(Ctx("anthropic-vertex", h, token: "T", settings: new() { ["project"] = "my-project-123", ["region"] = "us-east5" }), "chat", ("model", "claude-x@20250101"), ("prompt", "hi"));
        Assert.Equal("vertex says hi", r.Message);
        Assert.Equal("us-east5-aiplatform.googleapis.com", h.Seen[0].Request.RequestUri!.Host);
        Assert.Contains("/projects/my-project-123/locations/us-east5/publishers/anthropic/models/claude-x%4020250101:rawPredict", h.Seen[0].Request.RequestUri!.AbsolutePath + "");
        Assert.Equal("T", h.Seen[0].Request.Headers.Authorization!.Parameter);
    }

    // ---- voice, search and media ------------------------------------------------------------------

    [Fact]
    public async Task ElevenLabs_Deepgram_and_Azure_Speech_each_send_their_own_key_header()
    {
        var h = new Handler(r => r.RequestUri!.Host.EndsWith("elevenlabs.io") ? Json("""{"tier":"creator","character_count":120,"character_limit":100000,"voices":[{"voice_id":"v1","name":"Rachel","category":"premade","labels":{"accent":"american"}}]}""")
            : r.RequestUri!.Host == "api.deepgram.com" ? Json("""{"projects":[{"project_id":"11111111-2222-3333-4444-555555555555","name":"Main"}],"balances":[{"balance_id":"b","amount":12.5,"units":"usd"}]}""")
            : Json("""[{"ShortName":"en-IN-NeerjaNeural","LocalName":"Neerja","Locale":"en-IN","Gender":"Female"},{"ShortName":"hi-IN-SwaraNeural","LocalName":"Swara","Locale":"hi-IN","Gender":"Female"}]"""));
        Assert.Equal("Rachel", (await Load(Ctx("elevenlabs", h, cred: new() { ["apiKey"] = "xi" }), "voices")).Single().Title);
        Assert.Equal("xi", h.Seen[0].Request.Headers.GetValues("xi-api-key").Single());
        Assert.Equal("Main", (await Load(Ctx("deepgram", h, cred: new() { ["apiKey"] = "dg" }), "projects")).Single().Title);
        Assert.Equal("Token dg", h.Seen[1].Request.Headers.GetValues("Authorization").Single());
        await Assert.ThrowsAsync<AppException>(() => Load(Ctx("deepgram", h, cred: new() { ["apiKey"] = "dg" }), "balances", ("arg", "../x")));
        var az = Ctx("azure-speech", h, cred: new() { ["key"] = "azkey", ["region"] = "centralindia" });
        Assert.Equal(["Swara"], (await Load(az, "voices", ("locale", "hi"))).Select(v => v.Title));
        Assert.Equal(2, (await Load(az, "voices")).Count);
        Assert.Equal("azkey", h.Seen.Last().Request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        await Assert.ThrowsAsync<AppException>(() => ProviderApps.Get("azure-speech").Credentials!.Prepare!(new Dictionary<string, string> { ["region"] = "evil.com/x", ["key"] = "k" }, CancellationToken.None));
    }

    [Fact]
    public async Task Perplexity_search_posts_the_query_and_lists_links()
    {
        var h = new Handler(_ => Json("""{"results":[{"title":"Result one","url":"https://example.com/1","snippet":"A snippet","date":"2026-12-01"}]}"""));
        var items = await Load(Ctx("perplexity-search", h, cred: new() { ["apiKey"] = "pplx-aaaaaaaaaaaaaaaa" }), "search", ("q", "what is daksha"));
        Assert.Equal("https://example.com/1", Assert.Single(items).Url);
        Assert.Equal("Bearer", h.Seen[0].Request.Headers.Authorization!.Scheme);
        Assert.Contains("\"query\":\"what is daksha\"", h.Seen[0].Body);
    }

    [Fact]
    public async Task Runway_sends_its_version_header_and_fal_generates_with_the_Key_scheme()
    {
        var h = new Handler(r => r.RequestUri!.Host == "fal.run" ? Json("""{"images":[{"url":"https://v3.fal.media/files/abc.png"}]}""") : Json("""{"creditBalance":250,"tier":{"maxMonthlyCreditSpend":1000}}"""));
        var credits = await Load(Ctx("runway", h, cred: new() { ["apiKey"] = "rk" }), "organization");
        Assert.Equal("250", credits.Single().Badge); Assert.Equal("2024-11-06", h.Seen[0].Request.Headers.GetValues("X-Runway-Version").Single());
        var r = await Run(Ctx("fal", h, cred: new() { ["apiKey"] = "fal-key-0123456789" }), "image", ("prompt", "a red kite"));
        Assert.Equal("https://v3.fal.media/files/abc.png", r.Url);
        Assert.Equal("Key fal-key-0123456789", h.Seen[1].Request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ComfyUI_shows_the_queue_and_the_recent_image_names()
    {
        var h = new Handler(r => r.RequestUri!.AbsolutePath switch
        {
            "/system_stats" => Json("""{"system":{"comfyui_version":"0.3.1"},"devices":[{"name":"cuda:0 RTX","type":"cuda","vram_total":8589934592,"vram_free":4294967296}]}"""),
            "/queue" => Json("""{"queue_running":[[1]],"queue_pending":[[2],[3]]}"""),
            _ => Json("""{"abc12345-xyz":{"outputs":{"9":{"images":[{"filename":"out_00001.png"}]}},"status":{"status_str":"success"}}}"""),
        });
        var c = Ctx("comfyui", h, cred: new() { ["url"] = "http://127.0.0.1:8188" });
        var status = await Load(c, "status");
        Assert.Equal("1 running, 2 waiting", status[0].Subtitle); Assert.Contains("4096 of 8192 MB free", status[1].Subtitle);
        var history = await Load(c, "history");
        Assert.Contains("out_00001.png", history.Single().Subtitle); Assert.Equal("success", history[0].Badge);
    }

    [Fact]
    public async Task SharePoint_validates_the_site_id_before_calling_graph()
    {
        var h = new Handler(_ => Json("""{"value":[{"id":"contoso.sharepoint.com,guid1,guid2","displayName":"Team site","webUrl":"https://contoso.sharepoint.com/sites/team"}]}"""));
        var sites = await Load(Ctx("sharepoint", h, token: "T"), "sites");
        Assert.Equal("documents", Assert.Single(sites).Drill);
        await Assert.ThrowsAsync<AppException>(() => Load(Ctx("sharepoint", h, token: "T"), "documents", ("arg", "../me/drive")));
        Assert.Single(h.Seen);
    }
}
