using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// Batch 4: AI model providers, voice and search tools, and SharePoint.
// Every model provider is one row of a table (which API style it speaks, where its keys come from). Each lists the models the person's key can use and,
// with full access (it spends their credits), sends a short test prompt.

public static partial class ProviderApps
{
    private enum ModelApi { OpenAi, Anthropic, Gemini, Cohere, Ollama, GithubModels, Bedrock, Vertex, CloudflareGateway }

    private sealed record ModelSpec(string Key, string Name, string Icon, string Color, ModelApi Api, string Blurb, string[] Setup, string? Base = null, bool UserUrl = false, bool KeyRequired = true, bool ModelsOptional = false, string Notes = "", string? DefaultUrl = null);

    private static readonly Regex ModelId = new(@"^[\w.\-:@/]{1,160}$", RegexOptions.Compiled);

    private static string ModelBase(ProviderCtx c, ModelSpec s) => s.UserUrl ? c.Cred["url"].TrimEnd('/') : s.Base!;

    private static void ModelHeaders(HttpRequestMessage r, ProviderCtx c, ModelSpec s)
    {
        var key = c.Cred.GetValueOrDefault("apiKey") ?? "";
        switch (s.Api)
        {
            case ModelApi.Anthropic: r.Headers.TryAddWithoutValidation("x-api-key", key); r.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01"); break;
            case ModelApi.Gemini: r.Headers.TryAddWithoutValidation("x-goog-api-key", key); break;
            case ModelApi.GithubModels: r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key); r.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json"); r.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28"); break;
            case ModelApi.CloudflareGateway:
                r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                if (c.Cred.GetValueOrDefault("gatewayToken") is { Length: > 0 } g) r.Headers.TryAddWithoutValidation("cf-aig-authorization", "Bearer " + g);
                break;
            default: if (key.Length > 0) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key); break;
        }
    }

    private static string GatewayBase(ProviderCtx c) => $"https://gateway.ai.cloudflare.com/v1/{c.Cred["accountId"]}/{c.Cred["gatewayId"]}/openai";

    private static async Task<IReadOnlyList<AppItem>> ListModelsAsync(ProviderCtx c, ModelSpec s)
    {
        try
        {
            switch (s.Api)
            {
                case ModelApi.Anthropic:
                { using var d = await c.GetAsync(s.Base + "/models?limit=100"); return J.Items(d.RootElement, "data", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "display_name") ?? J.S(m, "id") ?? "Model", $"{J.S(m, "id")} · {J.When(J.S(m, "created_at"))}"), 100); }
                case ModelApi.Gemini:
                { using var d = await c.GetAsync(s.Base + "/models?pageSize=100"); return J.Items(d.RootElement, "models", m => new AppItem((J.S(m, "name") ?? "").Replace("models/", ""), J.S(m, "displayName") ?? J.S(m, "name") ?? "Model", $"{(J.S(m, "name") ?? "").Replace("models/", "")} · {J.S(m, "inputTokenLimit")} tokens in", null, J.Arr(m, "supportedGenerationMethods").Any(x => x.GetString() == "generateContent") ? "chat" : null), 100); }
                case ModelApi.Cohere:
                { using var d = await c.GetAsync(s.Base + "/v1/models?page_size=100"); return J.Items(d.RootElement, "models", m => new AppItem(J.S(m, "name") ?? "", J.S(m, "name") ?? "Model", $"{J.S(m, "context_length")} tokens · {string.Join(", ", J.Arr(m, "endpoints").Select(e => e.GetString()))}"), 100); }
                case ModelApi.Ollama:
                { using var d = await c.GetAsync(ModelBase(c, s) + "/api/tags"); return J.Items(d.RootElement, "models", m => new AppItem(J.S(m, "name") ?? "", J.S(m, "name") ?? "Model", $"{J.S(m, "details.parameter_size")} · {(long.TryParse(J.S(m, "size"), out var b) ? b / 1_048_576 + " MB" : "")} · {J.When(J.S(m, "modified_at"))}"), 100); }
                case ModelApi.GithubModels:
                { using var d = await c.GetAsync("https://models.github.ai/catalog/models"); return J.Items(d.RootElement, "", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "name") ?? J.S(m, "id") ?? "Model", $"{J.S(m, "publisher")} · {J.Clip(J.S(m, "summary"), 90)}"), 100); }
                case ModelApi.Bedrock:
                { using var d = JsonDocument.Parse(await AwsAsync(c, "bedrock", $"bedrock.{AwsRegionOf(c)}.amazonaws.com", "GET", "/foundation-models")); return J.Items(d.RootElement, "modelSummaries", m => new AppItem(J.S(m, "modelId") ?? "", J.S(m, "modelName") ?? J.S(m, "modelId") ?? "Model", $"{J.S(m, "providerName")} · {J.S(m, "modelId")}"), 100); }
                case ModelApi.Vertex:
                    return [new AppItem("start", "Anthropic models on Vertex AI", "Enter a model id such as the one shown in Vertex AI Model Garden when you send a prompt.")];
                default:
                { var url = (s.Api == ModelApi.CloudflareGateway ? GatewayBase(c) : ModelBase(c, s)) + "/models"; using var d = await c.GetAsync(url);
                  var root = d.RootElement; var path = root.ValueKind == JsonValueKind.Array ? "" : "data";
                  return J.Items(root, path, m => new AppItem(J.S(m, "id") ?? "", J.S(m, "id") ?? "Model", $"{J.S(m, "owned_by") ?? J.S(m, "organization")}{(J.S(m, "context_length") is { } cl ? " · " + cl + " tokens" : "")}"), 200); }
            }
        }
        catch (AppException ex) when (s.ModelsOptional && ex.StatusCode is 404 or 400) { return [new AppItem("n/a", $"{s.Name} does not list its models", "You can still send a prompt by typing the model name.")]; }
    }

    private static async Task<string> ChatAsync(ProviderCtx c, ModelSpec s, string model, string prompt)
    {
        if (!ModelId.IsMatch(model)) throw new AppException(400, "Enter a model name, like gpt-4o-mini.");
        switch (s.Api)
        {
            case ModelApi.Anthropic:
            { using var d = await c.PostAsync(s.Base + "/messages", new { model, max_tokens = 400, messages = new[] { new { role = "user", content = prompt } } }); return J.S(d.RootElement, "content[0].text") ?? ""; }
            case ModelApi.Gemini:
            { using var d = await c.PostAsync(s.Base + $"/models/{Uri.EscapeDataString(model)}:generateContent", new { contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } } }); return J.S(d.RootElement, "candidates[0].content.parts[0].text") ?? ""; }
            case ModelApi.Cohere:
            { using var d = await c.PostAsync(s.Base + "/v2/chat", new { model, messages = new[] { new { role = "user", content = prompt } } }); return J.S(d.RootElement, "message.content[0].text") ?? ""; }
            case ModelApi.Ollama:
            { using var d = await c.PostAsync(ModelBase(c, s) + "/api/chat", new { model, stream = false, messages = new[] { new { role = "user", content = prompt } } }); return J.S(d.RootElement, "message.content") ?? ""; }
            case ModelApi.Bedrock:
            { using var d = JsonDocument.Parse(await AwsAsync(c, "bedrock", $"bedrock-runtime.{AwsRegionOf(c)}.amazonaws.com", "POST", $"/model/{Uri.EscapeDataString(model)}/converse",
                  null, JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = new[] { new { text = prompt } } } } }), "application/json")); return J.S(d.RootElement, "output.message.content[0].text") ?? ""; }
            case ModelApi.Vertex:
            {
                var (project, region) = (c.Setting("project").Trim(), c.Setting("region").Trim());
                if (!Regex.IsMatch(project, @"^[a-z][a-z0-9\-]{4,29}$") || !Regex.IsMatch(region, @"^[a-z]+-[a-z]+\d$")) throw new AppException(400, "Set your Google Cloud project and region in Manage → Preferences first.");
                using var d = await c.PostAsync($"https://{region}-aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/publishers/anthropic/models/{Uri.EscapeDataString(model)}:rawPredict", new { anthropic_version = "vertex-2023-10-16", max_tokens = 400, messages = new[] { new { role = "user", content = prompt } } });
                return J.S(d.RootElement, "content[0].text") ?? "";
            }
            default:
            { var url = s.Api == ModelApi.GithubModels ? "https://models.github.ai/inference" : s.Api == ModelApi.CloudflareGateway ? GatewayBase(c) : ModelBase(c, s);
              using var d = await c.PostAsync(url + "/chat/completions", new { model, messages = new[] { new { role = "user", content = prompt } } }); return J.S(d.RootElement, "choices[0].message.content") ?? ""; }
        }
    }

    private static ProviderApp ModelApp(ModelSpec s)
    {
        var fields = new List<InputSpec>();
        if (s.Api == ModelApi.Bedrock) fields.AddRange([F("accessKeyId", "Access key id", placeholder: "AKIA…"), F("secretAccessKey", "Secret access key", "secret"), F("region", "Region", placeholder: "us-east-1", def: "us-east-1")]);
        else if (s.Api == ModelApi.Vertex) { }
        else
        {
            if (s.Api == ModelApi.CloudflareGateway) fields.AddRange([F("accountId", "Cloudflare account id"), F("gatewayId", "Gateway name")]);
            if (s.UserUrl) fields.Add(F("url", s.Api == ModelApi.Ollama ? "Ollama address" : "Server address", "url", s.DefaultUrl ?? "http://localhost:8000/v1", def: s.DefaultUrl));
            fields.Add(F("apiKey", s.KeyRequired ? "API key" : "API key (if the server needs one)", "secret", required: s.KeyRequired));
            if (s.Api == ModelApi.CloudflareGateway) fields.Add(F("gatewayToken", "Gateway token (if authenticated)", "secret", required: false));
        }
        var chatInputs = new List<InputSpec> { s.Api == ModelApi.Vertex ? F("model", "Model", placeholder: "claude-…@…") : new("model", "Model", "select-view", null, true, null, "models"), F("prompt", "Your prompt", "textarea") };
        if (s.Api != ModelApi.Vertex && s.Api != ModelApi.Bedrock && s.ModelsOptional) chatInputs[0] = F("model", "Model", placeholder: "model name");

        return new ProviderApp
        {
            Key = s.Key, Name = s.Name, Icon = s.Icon, Color = s.Color, Account = "Models your key can use",
            ConnectTitle = $"Connect {s.Name}", ConnectLabel = s.Api == ModelApi.Vertex ? "Continue with Google" : $"Connect {s.Name}", ConnectBlurb = s.Blurb,
            WriteLabel = "Allow Daksh to send prompts", WriteHint = $"Sending a prompt uses your {s.Name} credits, so it needs full access. Listing models is free.",
            Auth = s.Api == ModelApi.Vertex ? AuthKind.OAuth : AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = $"Amit.md#{Regex.Replace(s.Name.ToLowerInvariant().Replace(".", ""), @"[^a-z0-9]+", "-").Trim('-')}",
            Ask = Rx($"{Regex.Escape(s.Name.ToLowerInvariant())}|models?"),
            NoBearer = s.Api != ModelApi.Vertex, Decorate = (r, c) => ModelHeaders(r, c, s), // Vertex signs in with Google, so its token is the bearer
            Setup = s.Setup, Notes = s.Notes,
            ExtraSettings = s.Api == ModelApi.Vertex ? [new("project", "Google Cloud project", "text", "The project that has Claude enabled in Vertex AI.", "my-project-123", null, ""), new("region", "Region", "text", "For example us-east5.", "us-east5", null, "us-east5")] : [],
            OAuth = s.Api != ModelApi.Vertex ? null : new()
            {
                Section = "Vertex", Fallbacks = ["GoogleCloud", "Gmail", "Google"], AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token",
                ReadScopes = ["openid", "email", "https://www.googleapis.com/auth/cloud-platform"], AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false" },
            },
            Profile = s.Api != ModelApi.Vertex ? null : async c => { using var d = await c.GetAsync("https://openidconnect.googleapis.com/v1/userinfo"); return new ProfileInfo(J.S(d.RootElement, "sub") ?? "", J.S(d.RootElement, "email") ?? "Google account"); },
            Credentials = s.Api == ModelApi.Vertex ? null : new()
            {
                Fields = [.. fields],
                Prepare = async (v, ct) =>
                {
                    if (s.Api == ModelApi.Bedrock) { if (!Regex.IsMatch(v["accessKeyId"], @"^(AKIA|ASIA)[A-Z0-9]{12,20}$") || !AwsRegion.IsMatch(v["region"])) throw new AppException(400, "Check the access key id (it starts with AKIA) and the region (like us-east-1)."); return; }
                    if (s.UserUrl)
                    {
                        if (v["url"].Length == 0 && s.DefaultUrl is not null) v["url"] = s.DefaultUrl;
                        await PrepareUrl("url")(v, ct);
                        if (s.Api == ModelApi.OpenAi && !v["url"].EndsWith("/v1", StringComparison.Ordinal) && !Regex.IsMatch(v["url"], @"/v\d+$")) v["url"] += "/v1";
                    }
                    if (s.Api == ModelApi.CloudflareGateway && (!Regex.IsMatch(v["accountId"], "^[0-9a-f]{32}$") || !Regex.IsMatch(v["gatewayId"], @"^[\w\-]{1,64}$"))) throw new AppException(400, "Check the account id (32 characters) and the gateway name.");
                },
                Probe = async c =>
                {
                    var items = await ListModelsAsync(c, s);
                    var id = s.UserUrl ? new Uri(c.Cred["url"]).Authority : s.Api == ModelApi.Bedrock ? c.Cred["accessKeyId"][..8] : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred.GetValueOrDefault("apiKey") ?? "")))[..12];
                    return new ProfileInfo(id, $"{s.Name} ({(s.UserUrl ? new Uri(c.Cred["url"]).Host : items.Count + " models")})");
                },
            },
            Views = [new ViewSpec { Id = "models", Label = "Models", Dashboard = true, Load = (c, _) => ListModelsAsync(c, s) }],
            Actions = [new ActionSpec { Id = "chat", Label = "Send a test prompt", Hint = "Uses your credits. The answer is shown here and not stored.", Inputs = [.. chatInputs], Run = async (c, i) => { var text = await ChatAsync(c, s, i["model"], i["prompt"]); return new ActionResult(J.Clip(text.Length > 0 ? text : "(the model sent an empty answer)", 900)); } }],
        };
    }

    private static readonly ModelSpec[] ModelSpecs =
    [
        new("openai", "OpenAI", "SiOpenai", "#10A37F", ModelApi.OpenAi, "Enter an OpenAI API key and Daksha lists the models it can use. With full access it can send a short test prompt.", ["Nothing to set on the server. Each person creates an **API key** at platform.openai.com/api-keys."], Base: "https://api.openai.com/v1"),
        new("anthropic", "Anthropic", "SiAnthropic", "#D97757", ModelApi.Anthropic, "Enter an Anthropic API key and Daksha lists the Claude models it can use.", ["Nothing to set on the server. Each person creates an **API key** at console.anthropic.com/settings/keys."], Base: "https://api.anthropic.com/v1"),
        new("google-gemini", "Google Gemini", "SiGooglegemini", "#4285F4", ModelApi.Gemini, "Enter a Gemini API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at aistudio.google.com/apikey."], Base: "https://generativelanguage.googleapis.com/v1beta"),
        new("mistral", "Mistral", "mono:MI", "#FA520F", ModelApi.OpenAi, "Enter a Mistral API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at console.mistral.ai/api-keys."], Base: "https://api.mistral.ai/v1"),
        new("deepseek", "DeepSeek", "mono:DS", "#4D6BFE", ModelApi.OpenAi, "Enter a DeepSeek API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at platform.deepseek.com/api_keys."], Base: "https://api.deepseek.com"),
        new("qwen", "Qwen", "mono:QW", "#615CED", ModelApi.OpenAi, "Enter an Alibaba Cloud Model Studio (DashScope) API key and Daksha lists the Qwen models.", ["Nothing to set on the server. Each person creates an **API key** in Alibaba Cloud Model Studio (bailian.console.alibabacloud.com → API key). The international endpoint is used."], Base: "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", Notes: "Mainland China accounts use dashscope.aliyuncs.com instead; use the Custom compatible API app for that."),
        new("moonshot-kimi", "Moonshot / Kimi", "mono:KM", "#000000", ModelApi.OpenAi, "Enter a Moonshot API key and Daksha lists the Kimi models.", ["Nothing to set on the server. Each person creates an **API key** at platform.moonshot.ai."], Base: "https://api.moonshot.ai/v1"),
        new("minimax", "MiniMax", "mono:MM", "#F23F5D", ModelApi.OpenAi, "Enter a MiniMax API key. You can then send a test prompt by typing a model name.", ["Nothing to set on the server. Each person creates an **API key** at platform.minimax.io."], Base: "https://api.minimax.io/v1", ModelsOptional: true, Notes: "MiniMax does not always list its models; type the model name when sending a prompt."),
        new("zai", "Z.AI", "mono:ZA", "#2D2D2D", ModelApi.OpenAi, "Enter a Z.AI API key and Daksha lists the GLM models.", ["Nothing to set on the server. Each person creates an **API key** at z.ai (API keys page)."], Base: "https://api.z.ai/api/paas/v4"),
        new("cohere", "Cohere", "mono:CO", "#39594D", ModelApi.Cohere, "Enter a Cohere API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at dashboard.cohere.com/api-keys."], Base: "https://api.cohere.com"),
        new("groq", "Groq", "mono:GQ", "#F55036", ModelApi.OpenAi, "Enter a Groq API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at console.groq.com/keys."], Base: "https://api.groq.com/openai/v1"),
        new("cerebras", "Cerebras", "mono:CB", "#F15A29", ModelApi.OpenAi, "Enter a Cerebras API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at cloud.cerebras.ai."], Base: "https://api.cerebras.ai/v1"),
        new("together-ai", "Together AI", "mono:TA", "#0F6FFF", ModelApi.OpenAi, "Enter a Together AI API key and Daksha lists the open models it can use.", ["Nothing to set on the server. Each person creates an **API key** at api.together.ai/settings/api-keys."], Base: "https://api.together.xyz/v1"),
        new("fireworks", "Fireworks", "mono:FW", "#6720FF", ModelApi.OpenAi, "Enter a Fireworks API key and Daksha lists the models it can use.", ["Nothing to set on the server. Each person creates an **API key** at fireworks.ai/account/api-keys."], Base: "https://api.fireworks.ai/inference/v1"),
        new("nvidia", "NVIDIA", "mono:NV", "#76B900", ModelApi.OpenAi, "Enter an NVIDIA API key (build.nvidia.com) and Daksha lists the hosted models.", ["Nothing to set on the server. Each person generates an **API key** at build.nvidia.com."], Base: "https://integrate.api.nvidia.com/v1"),
        new("hugging-face", "Hugging Face", "SiHuggingface", "#FFD21E", ModelApi.OpenAi, "Enter a Hugging Face access token and Daksha lists the models available through Inference Providers.", ["Nothing to set on the server. Each person creates a **fine-grained token** at huggingface.co/settings/tokens with “Make calls to Inference Providers”."], Base: "https://router.huggingface.co/v1"),
        new("openrouter", "OpenRouter", "mono:OR", "#6467F2", ModelApi.OpenAi, "Enter an OpenRouter API key and Daksha lists the hundreds of models it can route to.", ["Nothing to set on the server. Each person creates an **API key** at openrouter.ai/keys."], Base: "https://openrouter.ai/api/v1"),
        new("litellm", "LiteLLM", "mono:LL", "#64748B", ModelApi.OpenAi, "Enter the address of your LiteLLM proxy and a virtual key.", ["Nothing to set on the server. Each person enters the address of their own **LiteLLM proxy** and a virtual key. The Daksha server must be able to reach the proxy."], UserUrl: true, DefaultUrl: "https://litellm.example.com"),
        new("amazon-bedrock", "Amazon Bedrock", "mono:BR", "#FF9900", ModelApi.Bedrock, "Enter an IAM user's access key with Bedrock permissions and Daksha lists the foundation models in a region.", ["Nothing to set on the server. Each person creates an **IAM user** with `AmazonBedrockReadOnly` (add `bedrock:InvokeModel` to send prompts), an access key for it, and enables model access in the Bedrock console."], Notes: "Requests are signed with AWS Signature Version 4."),
        new("anthropic-vertex", "Anthropic on Vertex", "mono:AV", "#4285F4", ModelApi.Vertex, "Sign in with Google and set your project in Manage. Daksha can send a prompt to Claude on Vertex AI.", ["Reuses the Google client (`Gmail__OAuth__*`). Add the scope `.../auth/cloud-platform` to the consent screen, enable the **Vertex AI API** and enable the Claude models in Vertex AI Model Garden for your project.", "After connecting, set the project and region in Manage → Preferences."], Notes: "Vertex has no list of Anthropic models, so you type the model id when sending a prompt."),
        new("github-copilot", "GitHub Copilot", "SiGithubcopilot", "#000000", ModelApi.GithubModels, "Enter a GitHub token and Daksha lists the models available through GitHub Models.", ["Nothing to set on the server. Each person creates a **fine-grained personal access token** at github.com/settings/tokens with the `models:read` permission."], Notes: "This uses GitHub Models (models.github.ai). GitHub does not offer a public API to the Copilot chat service itself."),
        new("cloudflare-ai-gateway", "Cloudflare AI Gateway", "mono:CF", "#F38020", ModelApi.CloudflareGateway, "Enter your Cloudflare account id, gateway name and the provider key the gateway forwards (OpenAI-compatible).", ["Nothing to set on the server. In Cloudflare create an **AI Gateway** (AI → AI Gateway) and note the account id and gateway name.", "Enter them with the OpenAI-compatible provider key, and a gateway token if the gateway is authenticated."]),
        new("vercel-ai-gateway", "Vercel AI Gateway", "mono:VG", "#000000", ModelApi.OpenAi, "Enter a Vercel AI Gateway key and Daksha lists the models it can route to.", ["Nothing to set on the server. Each person creates an **AI Gateway API key** in the Vercel dashboard (AI Gateway → API Keys)."], Base: "https://ai-gateway.vercel.sh/v1"),
        new("ollama", "Ollama", "SiOllama", "#000000", ModelApi.Ollama, "Enter the address of your Ollama server and Daksha lists the models you have pulled.", ["Nothing to set on the server. Run Ollama where the Daksha server can reach it (default `http://localhost:11434`; for a cloud server use a private tunnel)."], UserUrl: true, KeyRequired: false, DefaultUrl: "http://localhost:11434"),
        new("lm-studio", "LM Studio", "mono:LM", "#5B5BD6", ModelApi.OpenAi, "Enter the address of LM Studio's local server and Daksha lists the loaded models.", ["Nothing to set on the server. In LM Studio start the **local server** (default `http://localhost:1234`). The Daksha server must be able to reach it."], UserUrl: true, KeyRequired: false, DefaultUrl: "http://localhost:1234"),
        new("vllm", "vLLM", "mono:VL", "#30A2FF", ModelApi.OpenAi, "Enter the address of your vLLM server and Daksha lists the models it serves.", ["Nothing to set on the server. Run `vllm serve <model>` (OpenAI-compatible, default port 8000) where the Daksha server can reach it. Pass `--api-key` if you protect it."], UserUrl: true, KeyRequired: false, DefaultUrl: "http://localhost:8000"),
        new("custom-openai-api", "Custom compatible API", "mono:API", "#64748B", ModelApi.OpenAi, "Enter the address and key of any OpenAI-compatible API and Daksha lists its models.", ["Nothing to set on the server. Each person enters the **base address** of an OpenAI-compatible service (for example `https://api.example.com/v1`) and its key."], UserUrl: true, KeyRequired: false, DefaultUrl: "https://api.example.com/v1"),
    ];

    private static IEnumerable<ProviderApp> ModelApps() => ModelSpecs.Select(ModelApp);

    // ============================================================================================
    // Voice, search and media tools
    // ============================================================================================
    private static ProviderApp ElevenLabs() => new()
    {
        Key = "elevenlabs", Name = "ElevenLabs", Icon = "mono:EL", Color = "#000000", Account = "Voices and character usage",
        ConnectTitle = "Connect ElevenLabs", ConnectLabel = "Connect ElevenLabs", ConnectBlurb = "Enter an ElevenLabs API key. Daksha shows your voices and how much of your plan you have used.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#elevenlabs",
        Ask = Rx("elevenlabs|voices?|text to speech|tts"),
        NoBearer = true, Decorate = HeaderFromCred("xi-api-key", "apiKey"),
        Setup = ["Nothing to set on the server. Each person creates an **API key** at elevenlabs.io (Profile → API keys)."],
        Credentials = new() { Fields = [F("apiKey", "API key", "secret")], Probe = async c => { using var d = await c.GetAsync("https://api.elevenlabs.io/v1/user/subscription"); return new ProfileInfo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred["apiKey"])))[..12], $"ElevenLabs ({J.S(d.RootElement, "tier") ?? "account"})"); } },
        Views =
        [
            new ViewSpec { Id = "usage", Label = "Usage", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync("https://api.elevenlabs.io/v1/user/subscription");
                return [new AppItem("chars", "Characters used", $"Plan: {J.S(d.RootElement, "tier")}", null, $"{J.S(d.RootElement, "character_count")} / {J.S(d.RootElement, "character_limit")}")]; } },
            new ViewSpec { Id = "voices", Label = "Voices", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync("https://api.elevenlabs.io/v1/voices");
                return J.Items(d.RootElement, "voices", v => new AppItem(J.S(v, "voice_id") ?? "", J.S(v, "name") ?? "Voice", $"{J.S(v, "category")} · {J.S(v, "labels.accent")} {J.S(v, "labels.gender")}".Trim(), J.S(v, "preview_url")), 100); } },
        ],
    };

    private static ProviderApp Deepgram() => new()
    {
        Key = "deepgram", Name = "Deepgram", Icon = "mono:DG", Color = "#13EF93", Account = "Projects and balance",
        ConnectTitle = "Connect Deepgram", ConnectLabel = "Connect Deepgram", ConnectBlurb = "Enter a Deepgram API key. Daksha shows your projects and remaining balance.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#deepgram",
        Ask = Rx("deepgram|speech to text|transcri\\w+"),
        NoBearer = true, Decorate = HeaderFromCred("Authorization", "apiKey", "Token "),
        Setup = ["Nothing to set on the server. Each person creates an **API key** in the Deepgram console (API Keys) with the Member role."],
        Credentials = new() { Fields = [F("apiKey", "API key", "secret")], Probe = async c => { using var d = await c.GetAsync("https://api.deepgram.com/v1/projects"); return new ProfileInfo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred["apiKey"])))[..12], $"Deepgram ({J.Arr(d.RootElement, "projects").Count()} projects)"); } },
        Views =
        [
            new ViewSpec { Id = "projects", Label = "Projects", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync("https://api.deepgram.com/v1/projects");
                return J.Items(d.RootElement, "projects", p => new AppItem(J.S(p, "project_id") ?? "", J.S(p, "name") ?? "Project", J.S(p, "company"), null, null, null, "balances")); } },
            new ViewSpec { Id = "balances", Label = "Balance", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Guid36.IsMatch(i["arg"])) throw new AppException(400, "That project is not valid."); using var d = await c.GetAsync($"https://api.deepgram.com/v1/projects/{i["arg"]}/balances");
                return J.Items(d.RootElement, "balances", b => new AppItem(J.S(b, "balance_id") ?? "", $"{J.S(b, "amount")} {J.S(b, "units")}", J.S(b, "purchase_order_id"))); } },
        ],
    };

    private static ProviderApp AzureSpeech() => new()
    {
        Key = "azure-speech", Name = "Azure Speech", Icon = "mono:AZ", Color = "#0078D4", Account = "Voices for text to speech",
        ConnectTitle = "Connect Azure Speech", ConnectLabel = "Connect Azure Speech", ConnectBlurb = "Enter the key and region of your Azure Speech resource. Daksha lists the voices you can use.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#azure-speech",
        Ask = Rx("azure speech|voices?|text to speech|tts"),
        NoBearer = true, Decorate = HeaderFromCred("Ocp-Apim-Subscription-Key", "key"),
        Setup = ["Nothing to set on the server. In the Azure portal create a **Speech** resource and copy one of its **keys** and its **region** (for example `centralindia`)."],
        Credentials = new()
        {
            Fields = [F("key", "Speech key", "secret"), F("region", "Region", placeholder: "centralindia")],
            Prepare = (v, _) => Regex.IsMatch(v["region"], @"^[a-z0-9]{3,30}$") ? Task.CompletedTask : throw new AppException(400, "A region is lowercase letters and digits, like centralindia."),
            Probe = async c => { using var d = await c.GetAsync($"https://{c.Cred["region"]}.tts.speech.microsoft.com/cognitiveservices/voices/list"); return new ProfileInfo($"{c.Cred["region"]}-{c.Cred["key"][..Math.Min(6, c.Cred["key"].Length)]}", $"Azure Speech ({c.Cred["region"]})"); },
        },
        Views =
        [
            new ViewSpec { Id = "voices", Label = "Voices", Dashboard = true, Inputs = [F("locale", "Language filter", placeholder: "en-IN, hi-IN", required: false)], Load = async (c, i) => { using var d = await c.GetAsync($"https://{c.Cred["region"]}.tts.speech.microsoft.com/cognitiveservices/voices/list");
                var f = i.GetValueOrDefault("locale", "").Trim();
                return J.Arr(d.RootElement, "").Where(v => f.Length == 0 || (J.S(v, "Locale") ?? "").StartsWith(f, StringComparison.OrdinalIgnoreCase)).Select(v => new AppItem(J.S(v, "ShortName") ?? "", J.S(v, "LocalName") ?? J.S(v, "ShortName") ?? "Voice", $"{J.S(v, "ShortName")} · {J.S(v, "Locale")}", null, J.S(v, "Gender"))).Take(60).ToList(); } },
        ],
    };

    private static ProviderApp PerplexitySearch() => new()
    {
        Key = "perplexity-search", Name = "Perplexity Search", Icon = "mono:PX", Color = "#20808D", Account = "Web search",
        ConnectTitle = "Connect Perplexity Search", ConnectLabel = "Connect Perplexity", ConnectBlurb = "Enter a Perplexity API key and Daksha can search the web through the Perplexity Search API.",
        WriteLabel = "Full access", WriteHint = "Searching only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#perplexity-search",
        Ask = Rx("perplexity|search the web|web search"),
        Setup = ["Nothing to set on the server. Each person creates an **API key** at perplexity.ai/account/api (Perplexity API)."],
        Notes = "Each search is billed to the person's Perplexity account.",
        Credentials = new() { Fields = [F("apiKey", "API key", "secret", placeholder: "pplx-…")], Prepare = (v, _) => v["apiKey"].Length >= 16 ? Task.CompletedTask : throw new AppException(400, "That does not look like a Perplexity API key."), Probe = c => Task.FromResult(new ProfileInfo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred["apiKey"])))[..12], "Perplexity Search")) },
        Views =
        [
            new ViewSpec { Id = "start", Label = "About", Dashboard = true, Load = (_, _) => Task.FromResult<IReadOnlyList<AppItem>>([new AppItem("search", "Search the web", "Open the Search tab, or ask Daksha a question.")]) },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search the web")], Load = async (c, i) => { c.Token = c.Cred["apiKey"]; using var d = await c.PostAsync("https://api.perplexity.ai/search", new { query = i["q"], max_results = 10 });
                return J.Items(d.RootElement, "results", r => new AppItem(J.S(r, "url") ?? "", J.S(r, "title") ?? "Result", J.Clip(J.S(r, "snippet"), 160), J.S(r, "url"), J.When(J.S(r, "date")))); } },
        ],
    };

    private static ProviderApp Runway() => new()
    {
        Key = "runway", Name = "Runway", Icon = "mono:RW", Color = "#000000", Account = "Credits and plan",
        ConnectTitle = "Connect Runway", ConnectLabel = "Connect Runway", ConnectBlurb = "Enter a Runway API key. Daksha shows your credit balance and plan limits.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#runway",
        Ask = Rx("runway|video generation|credits"),
        Decorate = (r, _) => r.Headers.TryAddWithoutValidation("X-Runway-Version", "2024-11-06"),
        Setup = ["Nothing to set on the server. Each person creates an **API key** in the Runway developer portal (dev.runwayml.com)."],
        Credentials = new() { Fields = [F("apiKey", "API key", "secret")], Probe = async c => { c.Token = c.Cred["apiKey"]; using var d = await c.GetAsync("https://api.dev.runwayml.com/v1/organization"); return new ProfileInfo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred["apiKey"])))[..12], "Runway"); } },
        Views =
        [
            new ViewSpec { Id = "organization", Label = "Credits", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["apiKey"]; using var d = await c.GetAsync("https://api.dev.runwayml.com/v1/organization");
                return [new AppItem("credits", "Credit balance", $"Monthly spend limit {J.S(d.RootElement, "tier.maxMonthlyCreditSpend")}", null, J.S(d.RootElement, "creditBalance"))]; } },
        ],
    };

    private static ProviderApp Fal() => new()
    {
        Key = "fal", Name = "fal", Icon = "mono:FL", Color = "#7C3AED", Account = "Image generation",
        ConnectTitle = "Connect fal", ConnectLabel = "Connect fal", ConnectBlurb = "Enter a fal API key. With full access Daksha can generate an image from a prompt (it uses your fal credits).",
        WriteLabel = "Allow Daksh to generate images", WriteHint = "Each image uses your fal credits, so it needs full access.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#fal",
        Ask = Rx("fal|generate (an )?images?|image generation"),
        NoBearer = true, Decorate = HeaderFromCred("Authorization", "apiKey", "Key "),
        Setup = ["Nothing to set on the server. Each person creates an **API key** at fal.ai/dashboard/keys."],
        Notes = "fal cannot check a key without spending credits, so the key is only checked the first time an image is generated.",
        Credentials = new() { Fields = [F("apiKey", "API key", "secret")], Prepare = (v, _) => v["apiKey"].Length >= 16 ? Task.CompletedTask : throw new AppException(400, "That does not look like a fal API key."), Probe = c => Task.FromResult(new ProfileInfo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred["apiKey"])))[..12], "fal")) },
        Views = [new ViewSpec { Id = "start", Label = "About", Dashboard = true, Load = (_, _) => Task.FromResult<IReadOnlyList<AppItem>>([new AppItem("generate", "Generate images", "Turn on full access, then use Do something → Generate an image.")]) }],
        Actions =
        [
            new ActionSpec { Id = "image", Label = "Generate an image", Inputs = [F("prompt", "Describe the image", "textarea")],
                Run = async (c, i) => { using var d = await c.PostAsync("https://fal.run/fal-ai/flux/schnell", new { prompt = i["prompt"], num_images = 1 }); var url = J.S(d.RootElement, "images[0].url") ?? throw new AppException(502, "fal returned no image."); return new ActionResult("Image ready.", url); } },
        ],
    };

    private static ProviderApp ComfyUi() => new()
    {
        Key = "comfyui", Name = "ComfyUI", Icon = "mono:CU", Color = "#64748B", Account = "Queue and recent images",
        ConnectTitle = "Connect ComfyUI", ConnectLabel = "Connect ComfyUI", ConnectBlurb = "Enter the address of your ComfyUI server. Daksha shows its status, queue and recent images.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#comfyui",
        Ask = Rx("comfyui|comfy"),
        Setup = ["Nothing to set on the server. Run ComfyUI where the Daksha server can reach it (default `http://127.0.0.1:8188`; use a private tunnel for a cloud server)."],
        Credentials = new() { Fields = [F("url", "ComfyUI address", "url", "http://127.0.0.1:8188", def: "http://127.0.0.1:8188")], Prepare = PrepareUrl("url"), Probe = async c => { using var d = await c.GetAsync(c.Cred["url"] + "/system_stats"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, $"ComfyUI {J.S(d.RootElement, "system.comfyui_version")}".Trim()); } },
        Views =
        [
            new ViewSpec { Id = "status", Label = "Status", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/system_stats"); using var q = await c.GetAsync(c.Cred["url"] + "/queue");
                return [new AppItem("queue", "Queue", $"{J.Arr(q.RootElement, "queue_running").Count()} running, {J.Arr(q.RootElement, "queue_pending").Count()} waiting"), .. J.Arr(d.RootElement, "devices").Select(x => new AppItem(J.S(x, "name") ?? "Device", J.S(x, "name") ?? "Device", $"{(long.TryParse(J.S(x, "vram_free"), out var f) ? f / 1_048_576 : 0)} of {(long.TryParse(J.S(x, "vram_total"), out var t) ? t / 1_048_576 : 0)} MB free", null, J.S(x, "type")))]; } },
            new ViewSpec { Id = "history", Label = "Recent images", Load = async (c, _) => { using var d = await c.GetAsync(c.Cred["url"] + "/history?max_items=10");
                return Props(d.RootElement).Select(p => new AppItem(p.Name, p.Name[..Math.Min(8, p.Name.Length)], string.Join(", ", Props(J.At(p.Value, "outputs")).SelectMany(o => J.Arr(o.Value, "images")).Select(im => J.S(im, "filename")).Where(f => f is not null).Take(3)), null, J.S(p.Value, "status.status_str"))).ToList(); } },
        ],
    };

    // ============================================================================================
    // SharePoint (Microsoft Graph; read-only)
    // ============================================================================================
    private const string Graph = "https://graph.microsoft.com/v1.0/";

    private static ProviderApp SharePoint() => new()
    {
        Key = "sharepoint", Name = "SharePoint", Icon = "SiMicrosoftsharepoint", Color = "#0078D4", Account = "Sites and documents",
        ConnectTitle = "Connect SharePoint", ConnectLabel = "Continue with Microsoft",
        ConnectBlurb = "Sign in with your work Microsoft account. Daksha lists the sites you follow and the documents in them. Read-only.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#sharepoint",
        Ask = Rx("sharepoint|sites?|team documents?"),
        Setup = ["Reuses the Microsoft app registration (`Microsoft__OAuth__ClientId` / `ClientSecret`), or set `SharePoint__OAuth__*`. Add the delegated Graph permissions **Sites.Read.All** and **Files.Read.All** (an administrator may need to consent)."],
        OAuth = new()
        {
            Section = "SharePoint", Fallbacks = ["Microsoft"], AuthUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize", TokenUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
            ReadScopes = ["https://graph.microsoft.com/Sites.Read.All", "https://graph.microsoft.com/Files.Read.All", "offline_access", "openid", "profile"], AuthExtra = new() { ["prompt"] = "select_account" },
        },
        Profile = async c => { using var d = await c.GetAsync(Graph + "me"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.First(d.RootElement, "displayName", "userPrincipalName") ?? "Microsoft account"); },
        Views =
        [
            new ViewSpec { Id = "sites", Label = "Sites", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(Graph + "sites?search=*&$top=25");
                return J.Items(d.RootElement, "value", s => new AppItem(J.S(s, "id") ?? "", J.S(s, "displayName") ?? J.S(s, "name") ?? "Site", J.S(s, "webUrl"), J.S(s, "webUrl"), null, null, "documents")); } },
            new ViewSpec { Id = "documents", Label = "Documents", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Regex.IsMatch(i["arg"], @"^[\w.,\-]{1,300}$")) throw new AppException(400, "That site is not valid.");
                using var d = await c.GetAsync(Graph + $"sites/{Uri.EscapeDataString(i["arg"])}/drive/root/children?$top=50&$orderby=lastModifiedDateTime desc".Replace(" ", "%20"));
                return J.Items(d.RootElement, "value", f => new AppItem(J.S(f, "id") ?? "", J.S(f, "name") ?? "Item", $"{(J.At(f, "folder").ValueKind == JsonValueKind.Object ? "Folder" : $"{(long.TryParse(J.S(f, "size"), out var b) ? Math.Max(1, b / 1024) : 0)} KB")} · {J.When(J.S(f, "lastModifiedDateTime"))}", J.S(f, "webUrl")), 50); } },
            new ViewSpec { Id = "recent", Label = "Recent files", Load = async (c, _) => { using var d = await c.GetAsync(Graph + "me/drive/recent?$top=25");
                return J.Items(d.RootElement, "value", f => new AppItem(J.S(f, "id") ?? "", J.S(f, "name") ?? "File", J.When(J.S(f, "lastModifiedDateTime")), J.S(f, "webUrl"))); } },
        ],
    };
}
