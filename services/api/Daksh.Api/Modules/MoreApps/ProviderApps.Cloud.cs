using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Daksh.Api.Modules;

// Batch 3, part 1: cloud and infrastructure. AWS, Microsoft Azure, Google Cloud, Kubernetes, 1Password (Connect), Obsidian and MCP servers.

public static partial class ProviderApps
{
    private static readonly Regex Guid36 = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex K8sName = new(@"^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$", RegexOptions.Compiled);

    // ============================================================================================
    // AWS (an IAM user's access key; read-only)
    // ============================================================================================
    private static readonly Regex AwsRegion = new(@"^[a-z]{2}(-[a-z]+)+-\d$", RegexOptions.Compiled);

    private static XDocument Xml(string text) { try { return XDocument.Parse(text); } catch (System.Xml.XmlException) { throw new AppException(502, "AWS sent a reply Daksha could not read."); } }
    private static IEnumerable<XElement> XAll(XDocument d, string local) => d.Descendants().Where(e => e.Name.LocalName == local);
    private static string? X(XElement e, string local) => e.Elements().FirstOrDefault(x => x.Name.LocalName == local)?.Value;

    /// <summary>One signed AWS request. Returns the reply text (XML or JSON).</summary>
    private static async Task<string> AwsAsync(ProviderCtx c, string service, string host, string method, string path, IDictionary<string, string>? query = null, string body = "", string contentType = "")
    {
        var region = c.Cred.GetValueOrDefault("region") is { Length: > 0 } r ? r : "us-east-1";
        var now = DateTimeOffset.UtcNow;
        var q = query ?? new Dictionary<string, string>();
        var headers = new Dictionary<string, string> { ["host"] = host, ["x-amz-date"] = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'") };
        if (service == "s3") headers["x-amz-content-sha256"] = AwsSigV4.Sha256Hex(body);
        if (contentType.Length > 0) headers["content-type"] = contentType;
        var auth = AwsSigV4.Authorization(method, path, q, headers, body, c.Cred["accessKeyId"], c.Cred["secretAccessKey"], region, service, now);
        var send = new Dictionary<string, string> { ["x-amz-date"] = headers["x-amz-date"], ["Authorization"] = auth };
        if (service == "s3") send["x-amz-content-sha256"] = headers["x-amz-content-sha256"];
        var url = $"https://{host}{path}" + (q.Count > 0 ? "?" + string.Join('&', q.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")) : "");
        return await c.RawAsync(new HttpMethod(method), url, body.Length == 0 ? null : body, contentType.Length > 0 ? contentType : "text/plain", send);
    }

    private static string AwsRegionOf(ProviderCtx c) => c.Cred.GetValueOrDefault("region") is { Length: > 0 } r ? r : "us-east-1";

    private static ProviderApp Aws() => new()
    {
        Key = "aws", Name = "AWS", Icon = "mono:AWS", Color = "#FF9900", Account = "Servers, storage, functions and alarms",
        ConnectTitle = "Connect AWS", ConnectLabel = "Connect AWS",
        ConnectBlurb = "Create an IAM user with read-only access and paste its access key. Daksha lists your servers, buckets, functions and alarms. It never changes anything.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#aws",
        Ask = Rx("aws|amazon web services|ec2|s3 buckets?|lambda|cloudwatch|servers?"),
        Setup = ["Nothing to set on the server. Each person creates an **IAM user** with the managed policy `ReadOnlyAccess` (IAM → Users → Create user), then creates an **access key** for it.", "In Daksha they enter the access key id, the secret access key and a default region (for example `ap-south-1`)."],
        Notes = "Requests are signed with AWS Signature Version 4. Use a dedicated read-only user, never your root account.",
        Credentials = new()
        {
            Fields = [F("accessKeyId", "Access key id", placeholder: "AKIA…"), F("secretAccessKey", "Secret access key", "secret"), F("region", "Region", placeholder: "ap-south-1", def: "ap-south-1")],
            Prepare = (v, _) => Regex.IsMatch(v["accessKeyId"], @"^(AKIA|ASIA)[A-Z0-9]{12,20}$") && AwsRegion.IsMatch(v["region"]) ? Task.CompletedTask : throw new AppException(400, "Check the access key id (it starts with AKIA) and the region (like ap-south-1)."),
            Probe = async c => { var r = AwsRegionOf(c); var xml = Xml(await AwsAsync(c, "sts", $"sts.{r}.amazonaws.com", "POST", "/", null, "Action=GetCallerIdentity&Version=2011-06-15", "application/x-www-form-urlencoded"));
                var acct = XAll(xml, "Account").FirstOrDefault()?.Value ?? throw new AppException(502, "AWS did not say who this key belongs to."); return new ProfileInfo(acct, $"AWS {acct} ({XAll(xml, "Arn").FirstOrDefault()?.Value.Split('/').Last()})"); },
        },
        Views =
        [
            new ViewSpec { Id = "instances", Label = "EC2 servers", Dashboard = true, Load = async (c, _) => { var r = AwsRegionOf(c);
                var xml = Xml(await AwsAsync(c, "ec2", $"ec2.{r}.amazonaws.com", "GET", "/", new Dictionary<string, string> { ["Action"] = "DescribeInstances", ["Version"] = "2016-11-15", ["MaxResults"] = "50" }));
                return XAll(xml, "instancesSet").SelectMany(s => s.Elements()).Select(i => new AppItem(X(i, "instanceId") ?? "", i.Descendants().Where(t => t.Name.LocalName == "item" && X(t, "key") == "Name").Select(t => X(t, "value")).FirstOrDefault() is { Length: > 0 } n ? n : X(i, "instanceId") ?? "Server",
                    $"{X(i, "instanceType")} · {X(i, "privateIpAddress")} · {r}", null, X(i.Elements().First(e => e.Name.LocalName == "instanceState"), "name"))).Take(50).ToList(); } },
            new ViewSpec { Id = "buckets", Label = "S3 buckets", Load = async (c, _) => { var r = AwsRegionOf(c); var xml = Xml(await AwsAsync(c, "s3", $"s3.{r}.amazonaws.com", "GET", "/"));
                return XAll(xml, "Bucket").Select(b => new AppItem(X(b, "Name") ?? "", X(b, "Name") ?? "Bucket", J.When(X(b, "CreationDate")))).Take(100).ToList(); } },
            new ViewSpec { Id = "functions", Label = "Lambda functions", Load = async (c, _) => { var r = AwsRegionOf(c); using var d = JsonDocument.Parse(await AwsAsync(c, "lambda", $"lambda.{r}.amazonaws.com", "GET", "/2015-03-31/functions/", new Dictionary<string, string> { ["MaxItems"] = "50" }));
                return J.Items(d.RootElement, "Functions", f => new AppItem(J.S(f, "FunctionName") ?? "", J.S(f, "FunctionName") ?? "Function", $"{J.S(f, "Runtime")} · {J.When(J.S(f, "LastModified"))}")); } },
            new ViewSpec { Id = "alarms", Label = "Alarms firing", Dashboard = true, Empty = "No alarms are firing.", Load = async (c, _) => { var r = AwsRegionOf(c);
                var xml = Xml(await AwsAsync(c, "monitoring", $"monitoring.{r}.amazonaws.com", "GET", "/", new Dictionary<string, string> { ["Action"] = "DescribeAlarms", ["Version"] = "2010-08-01", ["StateValue"] = "ALARM" }));
                return XAll(xml, "MetricAlarms").SelectMany(a => a.Elements()).Select(a => new AppItem(X(a, "AlarmName") ?? "", X(a, "AlarmName") ?? "Alarm", J.Clip(X(a, "StateReason"), 120), null, X(a, "StateValue"))).Take(50).ToList(); } },
        ],
    };

    // ============================================================================================
    // Microsoft Azure (Azure Resource Manager through a Microsoft sign-in; read-only)
    // ============================================================================================
    private const string Arm = "https://management.azure.com/";

    private static ProviderApp Azure() => new()
    {
        Key = "microsoft-azure", Name = "Microsoft Azure", Icon = "SiMicrosoftazure", Color = "#0078D4", Account = "Subscriptions, resource groups and virtual machines",
        ConnectTitle = "Connect Microsoft Azure", ConnectLabel = "Continue with Microsoft",
        ConnectBlurb = "Sign in with the Microsoft account that owns your Azure subscriptions. Daksha lists subscriptions, resource groups and virtual machines, and never changes anything.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#microsoft-azure",
        Ask = Rx("azure|virtual machines?|vms?|resource groups?|subscriptions?"),
        Setup = ["Reuses the Microsoft app registration (`Microsoft__OAuth__ClientId` / `ClientSecret`), or set `AzureCloud__OAuth__*` for a separate one.", "In Entra ID → App registrations → your app → API permissions → add **Azure Service Management → user_impersonation** (delegated)."],
        Notes = "Daksha only calls read endpoints. The Azure permission itself is broad (user_impersonation), so use a dedicated app registration if that matters to you.",
        OAuth = new()
        {
            Section = "AzureCloud", Fallbacks = ["Microsoft"], AuthUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize", TokenUrl = "https://login.microsoftonline.com/organizations/oauth2/v2.0/token",
            ReadScopes = ["https://management.azure.com/user_impersonation", "offline_access", "openid", "profile"], AuthExtra = new() { ["prompt"] = "select_account" },
        },
        Profile = async c => { using var d = await c.GetAsync(Arm + "tenants?api-version=2022-12-01"); var t = J.Arr(d.RootElement, "value").FirstOrDefault(); if (t.ValueKind != JsonValueKind.Object) throw new AppException(400, "This Microsoft account has no Azure tenant.");
            return new ProfileInfo(J.S(t, "tenantId") ?? "", J.First(t, "displayName", "defaultDomain", "tenantId") ?? "Azure"); },
        Views =
        [
            new ViewSpec { Id = "subscriptions", Label = "Subscriptions", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(Arm + "subscriptions?api-version=2022-12-01");
                return J.Items(d.RootElement, "value", s => new AppItem(J.S(s, "subscriptionId") ?? "", J.S(s, "displayName") ?? "Subscription", J.S(s, "subscriptionId"), $"https://portal.azure.com/#@/resource/subscriptions/{J.S(s, "subscriptionId")}", J.S(s, "state"), null, "groups")); } },
            new ViewSpec { Id = "groups", Label = "Resource groups", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!Guid36.IsMatch(i["arg"])) throw new AppException(400, "That subscription is not valid.");
                using var d = await c.GetAsync(Arm + $"subscriptions/{i["arg"]}/resourcegroups?api-version=2021-04-01"); return J.Items(d.RootElement, "value", g => new AppItem(J.S(g, "id") ?? "", J.S(g, "name") ?? "Group", J.S(g, "location"), null, J.S(g, "properties.provisioningState")), 100); } },
            new ViewSpec { Id = "vms", Label = "Virtual machines", Inputs = [new("subscription", "Subscription", "select-view", null, true, null, "subscriptions")], Load = async (c, i) => { if (!Guid36.IsMatch(i["subscription"])) throw new AppException(400, "Choose a subscription.");
                using var d = await c.GetAsync(Arm + $"subscriptions/{i["subscription"]}/providers/Microsoft.Compute/virtualMachines?api-version=2024-03-01"); return J.Items(d.RootElement, "value", v => new AppItem(J.S(v, "id") ?? "", J.S(v, "name") ?? "VM", $"{J.S(v, "location")} · {J.S(v, "properties.hardwareProfile.vmSize")}", null, J.S(v, "properties.provisioningState")), 100); } },
        ],
    };

    // ============================================================================================
    // Google Cloud (Google sign-in, read-only)
    // ============================================================================================
    private static readonly Regex GcpProject = new(@"^[a-z][a-z0-9\-]{4,29}$", RegexOptions.Compiled);

    private static ProviderApp GoogleCloud() => new()
    {
        Key = "google-cloud", Name = "Google Cloud", Icon = "SiGooglecloud", Color = "#4285F4", Account = "Projects, virtual machines and Cloud Run",
        ConnectTitle = "Connect Google Cloud", ConnectLabel = "Continue with Google",
        ConnectBlurb = "Sign in with the Google account that owns your cloud projects. Daksha lists projects, virtual machines and Cloud Run services, and never changes anything.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#google-cloud",
        Ask = Rx("google cloud|gcp|cloud run|compute engine|gce"),
        Setup = ["Reuses the Google client (`Gmail__OAuth__*`). In Google Cloud Console enable the **Cloud Resource Manager**, **Compute Engine** and **Cloud Run Admin** APIs and add the scope `.../auth/cloud-platform.read-only` to the consent screen."],
        OAuth = new()
        {
            Section = "GoogleCloud", Fallbacks = ["Gmail", "Google"], AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token",
            ReadScopes = ["openid", "email", "https://www.googleapis.com/auth/cloud-platform.read-only"], AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "false" },
        },
        Profile = async c => { using var d = await c.GetAsync("https://openidconnect.googleapis.com/v1/userinfo"); return new ProfileInfo(J.S(d.RootElement, "sub") ?? "", J.S(d.RootElement, "email") ?? "Google account"); },
        Views =
        [
            new ViewSpec { Id = "projects", Label = "Projects", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync("https://cloudresourcemanager.googleapis.com/v1/projects?pageSize=50&filter=lifecycleState%3AACTIVE");
                return J.Items(d.RootElement, "projects", p => new AppItem(J.S(p, "projectId") ?? "", J.S(p, "name") ?? J.S(p, "projectId") ?? "Project", J.S(p, "projectId"), $"https://console.cloud.google.com/home/dashboard?project={J.S(p, "projectId")}", J.S(p, "lifecycleState"), null, "instances")); } },
            new ViewSpec { Id = "instances", Label = "Virtual machines", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!GcpProject.IsMatch(i["arg"])) throw new AppException(400, "That project is not valid.");
                using var d = await c.GetAsync($"https://compute.googleapis.com/compute/v1/projects/{i["arg"]}/aggregated/instances?maxResults=100");
                return d.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                    ? items.EnumerateObject().SelectMany(z => J.Arr(z.Value, "instances")).Select(v => new AppItem(J.S(v, "id") ?? "", J.S(v, "name") ?? "VM", $"{(J.S(v, "zone") ?? "").Split('/').Last()} · {(J.S(v, "machineType") ?? "").Split('/').Last()}", null, J.S(v, "status"))).Take(100).ToList() : []; } },
            new ViewSpec { Id = "cloud-run", Label = "Cloud Run", Inputs = [new("project", "Project", "select-view", null, true, null, "projects")], Load = async (c, i) => { if (!GcpProject.IsMatch(i["project"])) throw new AppException(400, "Choose a project.");
                using var d = await c.GetAsync($"https://run.googleapis.com/v2/projects/{i["project"]}/locations/-/services?pageSize=50"); return J.Items(d.RootElement, "services", s => new AppItem(J.S(s, "name") ?? "", (J.S(s, "name") ?? "").Split('/').Last(), J.S(s, "uri"), J.S(s, "uri"), J.S(s, "terminalCondition.state"))); } },
        ],
    };

    // ============================================================================================
    // Kubernetes (API server address and a service-account token; an optional private CA)
    // ============================================================================================
    /// <summary>A client that trusts the cluster's own CA, only when one was given. Clusters with a public certificate use the shared client.</summary>
    private static HttpClient? K8sClient(ProviderCtx c)
    {
        var pem = c.Cred.GetValueOrDefault("ca") ?? "";
        if (!pem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)) return null;
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(30), ConnectTimeout = TimeSpan.FromSeconds(10) };
        {
            var root = X509Certificate2.CreateFromPem(pem);
            // Trust only the cluster's own CA (not the system store) for this connection.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static ProviderApp Kubernetes() => new()
    {
        Key = "kubernetes", Name = "Kubernetes", Icon = "SiKubernetes", Color = "#326CE5", Account = "Namespaces, pods and deployments",
        ConnectTitle = "Connect a Kubernetes cluster", ConnectLabel = "Connect cluster",
        ConnectBlurb = "Enter the API server address and a service-account token. Daksha lists namespaces, pods and deployments. Use a service account with read-only permissions.",
        WriteLabel = "Allow Daksh to scale deployments", WriteHint = "Full access lets Daksha change a deployment's replica count (the token needs permission to do that).",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#kubernetes",
        Ask = Rx("kubernetes|k8s|pods?|deployments?|cluster|namespaces?"),
        Client = K8sClient,
        Setup = ["Nothing to set on the server. Create a **service account** with the built-in `view` ClusterRole, then create a token for it: `kubectl create token <name> --duration=8760h`.", "In Daksha enter the API server address (`https://…:6443`), the token and, if the cluster uses a private certificate authority, its certificate (PEM).", "The Daksha server must be able to reach the API server."],
        Notes = "A private CA is trusted only for that cluster's connection. Public cloud clusters with a public certificate need no CA.",
        Credentials = new()
        {
            Fields = [F("url", "API server address", "url", "https://k8s.example.com:6443"), F("token", "Service-account token", "secret"), F("ca", "Cluster CA certificate (PEM)", "textarea", required: false, hint: "Only for a private certificate authority.")],
            Prepare = async (v, ct) => { await PrepareUrl("url", allowHttp: false)(v, ct); if (v["ca"].Length > 0) { try { using var _ = X509Certificate2.CreateFromPem(v["ca"]); } catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException) { throw new AppException(400, "That does not look like a PEM certificate."); } } },
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["url"] + "/version"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, $"Kubernetes {J.S(d.RootElement, "gitVersion")} at {new Uri(c.Cred["url"]).Host}"); },
        },
        Views =
        [
            new ViewSpec { Id = "namespaces", Label = "Namespaces", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["url"] + "/api/v1/namespaces?limit=100");
                return J.Items(d.RootElement, "items", n => new AppItem(J.S(n, "metadata.name") ?? "", J.S(n, "metadata.name") ?? "Namespace", J.When(J.S(n, "metadata.creationTimestamp")), null, J.S(n, "status.phase")), 100); } },
            new ViewSpec { Id = "pods", Label = "Pods", Dashboard = true, Inputs = [F("namespace", "Namespace", required: false, def: "default")], Load = async (c, i) => { var ns = i.GetValueOrDefault("namespace", "default"); if (ns.Length == 0) ns = "default"; if (!K8sName.IsMatch(ns)) throw new AppException(400, "That is not a valid namespace name."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(c.Cred["url"] + $"/api/v1/namespaces/{ns}/pods?limit=100");
                return J.Items(d.RootElement, "items", p => new AppItem(J.S(p, "metadata.name") ?? "", J.S(p, "metadata.name") ?? "Pod", $"{J.S(p, "spec.nodeName")} · {J.Arr(p, "status.containerStatuses").Sum(s => int.TryParse(J.S(s, "restartCount"), out var n) ? n : 0)} restarts", null, J.S(p, "status.phase")), 100); } },
            new ViewSpec { Id = "deployments", Label = "Deployments", Inputs = [F("namespace", "Namespace", required: false, def: "default")], Load = async (c, i) => { var ns = i.GetValueOrDefault("namespace", "default"); if (ns.Length == 0) ns = "default"; if (!K8sName.IsMatch(ns)) throw new AppException(400, "That is not a valid namespace name."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(c.Cred["url"] + $"/apis/apps/v1/namespaces/{ns}/deployments?limit=100");
                return J.Items(d.RootElement, "items", x => new AppItem(J.S(x, "metadata.name") ?? "", J.S(x, "metadata.name") ?? "Deployment", $"{J.S(x, "status.readyReplicas") ?? "0"} of {J.S(x, "spec.replicas") ?? "0"} ready", null, null), 100); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "scale", Label = "Scale a deployment", Inputs = [F("namespace", "Namespace", def: "default"), F("name", "Deployment"), F("replicas", "Replicas", "number")],
                Run = async (c, i) => { if (!K8sName.IsMatch(i["namespace"]) || !K8sName.IsMatch(i["name"]) || !int.TryParse(i["replicas"], out var n) || n is < 0 or > 200) throw new AppException(400, "Check the namespace, the deployment name and a replica count between 0 and 200.");
                    c.Token = c.Cred["token"]; await c.RawAsync(HttpMethod.Patch, c.Cred["url"] + $"/apis/apps/v1/namespaces/{i["namespace"]}/deployments/{i["name"]}/scale", JsonSerializer.Serialize(new { spec = new { replicas = n } }), "application/merge-patch+json");
                    return new ActionResult($"Scaled {i["name"]} to {n}."); } },
        ],
    };

    // ============================================================================================
    // 1Password (Connect server: item titles only, never secrets)
    // ============================================================================================
    private static readonly Regex OpId = new("^[a-z0-9]{26}$", RegexOptions.Compiled);

    private static ProviderApp OnePassword() => new()
    {
        Key = "1password", Name = "1Password", Icon = "mono:1P", Color = "#0572EC", Account = "Vaults and item titles (never passwords)",
        ConnectTitle = "Connect 1Password", ConnectLabel = "Connect 1Password",
        ConnectBlurb = "Uses a 1Password Connect server. Daksha lists vaults and the titles of items so you can find them. It never reads or shows passwords, codes or any secret field.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads titles.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#1password",
        Ask = Rx("1password|vaults?|password manager"),
        Setup = ["Nothing to set on the server. Each person runs a **1Password Connect server** (1password.com → Developer → Secrets Automation) and creates a token with read access to the vaults to share.", "In Daksha they enter the Connect server address and the token."],
        Notes = "By design Daksha asks only for the item list, which contains titles, categories and dates, never field values.",
        Credentials = new()
        {
            Fields = [F("url", "Connect server address", "url", "https://connect.example.com"), F("token", "Connect token", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["url"] + "/v1/vaults"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, $"1Password Connect ({J.Arr(d.RootElement, "").Count()} vaults)"); },
        },
        Views =
        [
            new ViewSpec { Id = "vaults", Label = "Vaults", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["url"] + "/v1/vaults");
                return J.Items(d.RootElement, "", v => new AppItem(J.S(v, "id") ?? "", J.S(v, "name") ?? "Vault", $"{J.S(v, "items") ?? "0"} items", null, null, null, "items")); } },
            new ViewSpec { Id = "items", Label = "Items", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!OpId.IsMatch(i["arg"])) throw new AppException(400, "That vault is not valid."); c.Token = c.Cred["token"];
                using var d = await c.GetAsync(c.Cred["url"] + $"/v1/vaults/{i["arg"]}/items"); return J.Items(d.RootElement, "", x => new AppItem(J.S(x, "id") ?? "", J.S(x, "title") ?? "Item", $"{J.S(x, "category")?.Replace('_', ' ').ToLowerInvariant()} · {J.When(J.S(x, "updatedAt"))}"), 100); } },
        ],
    };

    // ============================================================================================
    // Obsidian (the "Local REST API" community plugin)
    // ============================================================================================
    private static string ObsPath(string path)
    {
        var p = path.Trim().TrimStart('/');
        if (p.Length == 0 || p.Length > 300 || p.Contains("..", StringComparison.Ordinal) || p.Contains('\\') || p.Any(char.IsControl)) throw new AppException(400, "Enter a note path inside the vault, like Inbox/Idea.md.");
        return string.Join('/', p.Split('/').Select(Uri.EscapeDataString));
    }

    private static ProviderApp Obsidian() => new()
    {
        Key = "obsidian", Name = "Obsidian", Icon = "mono:OB", Color = "#7C3AED", Account = "Notes in your vault",
        ConnectTitle = "Connect Obsidian", ConnectLabel = "Connect Obsidian",
        ConnectBlurb = "Install the “Local REST API” community plugin in Obsidian, then enter its address and API key. Daksha can search your notes and add text to them.",
        WriteLabel = "Allow Daksh to add to notes", WriteHint = "Full access lets Daksha append text to a note or to today's daily note.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#obsidian",
        Ask = Rx("obsidian|vault|my notes?"),
        Decorate = (r, c) => { },
        Setup = ["Nothing to set on the server. In Obsidian install the **Local REST API** plugin, enable it and copy its API key.", "Enter its address (`https://127.0.0.1:27124`, or `http://127.0.0.1:27123` with the non-encrypted server enabled) and the key. The Daksha server must be able to reach Obsidian (use a private tunnel for a cloud server)."],
        Notes = "The plugin uses a self-signed certificate; use the plain-HTTP port on a trusted network or a tunnel with a proper certificate.",
        Credentials = new()
        {
            Fields = [F("url", "Obsidian address", "url", "http://127.0.0.1:27123"), F("key", "API key", "secret")],
            Prepare = PrepareUrl("url"),
            Probe = async c => { c.Token = c.Cred["key"]; using var d = await c.GetAsync(c.Cred["url"] + "/"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, "Obsidian vault"); },
        },
        Views =
        [
            new ViewSpec { Id = "files", Label = "Vault", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["key"]; using var d = await c.GetAsync(c.Cred["url"] + "/vault/");
                return J.Arr(d.RootElement, "files").Select(f => f.GetString() ?? "").Where(f => f.Length > 0).Select(f => new AppItem(f, f.TrimEnd('/'), f.EndsWith('/') ? "Folder" : null, null, null, null, f.EndsWith('/') ? "folder" : "note")).Take(100).ToList(); } },
            new ViewSpec { Id = "folder", Label = "Folder", Hidden = true, TakesArg = true, Load = async (c, i) => { c.Token = c.Cred["key"]; var path = ObsPath(i["arg"].TrimEnd('/')); using var d = await c.GetAsync(c.Cred["url"] + $"/vault/{path}/");
                return J.Arr(d.RootElement, "files").Select(f => f.GetString() ?? "").Where(f => f.Length > 0).Select(f => new AppItem($"{i["arg"].TrimEnd('/')}/{f}", f.TrimEnd('/'), f.EndsWith('/') ? "Folder" : null, null, null, null, f.EndsWith('/') ? "folder" : "note")).Take(100).ToList(); } },
            new ViewSpec { Id = "note", Label = "Note", Hidden = true, TakesArg = true, Load = async (c, i) => { c.Token = c.Cred["key"]; var path = ObsPath(i["arg"]);
                var text = await c.RawAsync(HttpMethod.Get, c.Cred["url"] + $"/vault/{path}", null, headers: new Dictionary<string, string> { ["Accept"] = "text/markdown" });
                return [new AppItem(i["arg"], i["arg"].Split('/').Last(), null, null, null, null, null, J.Clip(text, 8000))]; } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search your notes")], Load = async (c, i) => { c.Token = c.Cred["key"]; using var d = await c.PostAsync(c.Cred["url"] + $"/search/simple/?query={J.E(i["q"])}&contextLength=80");
                return J.Items(d.RootElement, "", m => new AppItem(J.S(m, "filename") ?? "", J.S(m, "filename") ?? "Note", J.Clip(J.S(m, "matches[0].context"), 100), null, null, null, "note")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "append", Label = "Add to a note", Inputs = [F("path", "Note path", placeholder: "Inbox/Ideas.md"), F("text", "Text to add", "textarea")],
                Run = async (c, i) => { c.Token = c.Cred["key"]; await c.RawAsync(HttpMethod.Post, c.Cred["url"] + $"/vault/{ObsPath(i["path"])}", "\n" + i["text"] + "\n", "text/markdown"); return new ActionResult("Added to the note."); } },
            new ActionSpec { Id = "daily", Label = "Add to today's daily note", Inputs = [F("text", "Text to add", "textarea")],
                Run = async (c, i) => { c.Token = c.Cred["key"]; await c.RawAsync(HttpMethod.Post, c.Cred["url"] + "/periodic/daily/", "\n" + i["text"] + "\n", "text/markdown"); return new ActionResult("Added to today's daily note."); } },
        ],
    };

    // ============================================================================================
    // MCP servers (Model Context Protocol over streamable HTTP)
    // ============================================================================================
    private sealed record McpReply(string? SessionId, JsonDocument? Message);

    private static async Task<McpReply> McpPostAsync(ProviderCtx c, string? session, object payload, bool expectReply)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, c.Cred["url"]) { Content = JsonContent.Create(payload) };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (c.Cred.GetValueOrDefault("token") is { Length: > 0 } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
        if (session is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-03-26");
        using var res = await c.Clients.CreateClient("provider-apps").SendAsync(req, c.Ct);
        if (!res.IsSuccessStatusCode) throw new AppException(res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? 409 : 502, res.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "The MCP server refused the token. Reconnect." : $"The MCP server answered HTTP {(int)res.StatusCode}.");
        var sid = res.Headers.TryGetValues("Mcp-Session-Id", out var v) ? v.FirstOrDefault() : session;
        if (!expectReply) return new McpReply(sid, null);
        var text = await res.Content.ReadAsStringAsync(c.Ct);
        if ((res.Content.Headers.ContentType?.MediaType ?? "").Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            text = string.Join('\n', text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).Where(l => l.Length > 0 && l.Contains("\"result\"") || l.Contains("\"error\"")).Take(1));
        try { return new McpReply(sid, JsonDocument.Parse(text)); } catch (JsonException) { throw new AppException(502, "The MCP server sent a reply Daksha could not read."); }
    }

    /// <summary>Starts a session, runs one request and returns its result element (the document must be disposed by the caller).</summary>
    private static async Task<JsonDocument> McpCallAsync(ProviderCtx c, string method, object? parameters)
    {
        var init = await McpPostAsync(c, null, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "daksha", version = "1.0" } } }, true);
        init.Message?.Dispose();
        await McpPostAsync(c, init.SessionId, new { jsonrpc = "2.0", method = "notifications/initialized" }, false);
        var reply = await McpPostAsync(c, init.SessionId, new { jsonrpc = "2.0", id = 2, method, @params = parameters ?? new { } }, true);
        var doc = reply.Message ?? throw new AppException(502, "The MCP server sent no reply.");
        if (J.At(doc.RootElement, "error").ValueKind == JsonValueKind.Object) { var m = J.S(doc.RootElement, "error.message"); doc.Dispose(); throw new AppException(400, $"The MCP server said: {m}"); }
        return doc;
    }

    private static ProviderApp McpServers() => new()
    {
        Key = "mcp-servers", Name = "MCP servers", Icon = "mono:MCP", Color = "#64748B", Account = "Tools offered by a Model Context Protocol server",
        ConnectTitle = "Connect an MCP server", ConnectLabel = "Connect server",
        ConnectBlurb = "Enter the address of an MCP server that speaks streamable HTTP (and a bearer token if it needs one). Daksha lists its tools and can call them.",
        WriteLabel = "Allow Daksh to call tools", WriteHint = "Tools can do anything the server allows, so calling one needs full access.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#mcp-servers",
        Ask = Rx("mcp|mcp servers?|tools?"),
        Setup = ["Nothing to set on the server. Each person enters the **address of an MCP server** (streamable HTTP, for example `https://mcp.example.com/mcp`) and, if required, a bearer token.", "Listing tools is read-only; calling a tool needs the full-access switch."],
        Notes = "Only the streamable-HTTP transport is supported (not stdio). Each call opens a short-lived session.",
        Credentials = new()
        {
            Fields = [F("url", "MCP server address", "url", "https://mcp.example.com/mcp"), F("token", "Bearer token", "secret", required: false)],
            Prepare = PrepareUrl("url"),
            Probe = async c => { using var d = await McpCallAsync(c, "tools/list", null); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, $"MCP server at {new Uri(c.Cred["url"]).Host}"); },
        },
        Views =
        [
            new ViewSpec { Id = "tools", Label = "Tools", Dashboard = true, Load = async (c, _) => { using var d = await McpCallAsync(c, "tools/list", null);
                return J.Items(d.RootElement, "result.tools", t => new AppItem(J.S(t, "name") ?? "", J.S(t, "name") ?? "Tool", J.Clip(J.S(t, "description"), 140), null, null, null, null, J.At(t, "inputSchema").ValueKind == JsonValueKind.Object ? "Arguments: " + J.Clip(J.At(t, "inputSchema.properties").GetRawText(), 600) : null), 100); } },
            new ViewSpec { Id = "resources", Label = "Resources", Empty = "This server offers no resources.", Load = async (c, _) => { using var d = await McpCallAsync(c, "resources/list", null);
                return J.Items(d.RootElement, "result.resources", r => new AppItem(J.S(r, "uri") ?? "", J.S(r, "name") ?? J.S(r, "uri") ?? "Resource", J.Clip(J.S(r, "description"), 140), null, J.S(r, "mimeType")), 100); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "call", Label = "Call a tool", Hint = "Arguments as JSON, for example {\"query\": \"hello\"}.", Inputs = [new("tool", "Tool", "select-view", null, true, null, "tools"), F("arguments", "Arguments (JSON)", "textarea", required: false, def: "{}")],
                Run = async (c, i) =>
                {
                    JsonElement args; try { args = JsonDocument.Parse(i["arguments"].Length == 0 ? "{}" : i["arguments"]).RootElement.Clone(); } catch (JsonException) { throw new AppException(400, "The arguments must be valid JSON."); }
                    if (args.ValueKind != JsonValueKind.Object) throw new AppException(400, "The arguments must be a JSON object.");
                    using var d = await McpCallAsync(c, "tools/call", new { name = i["tool"], arguments = args });
                    var text = string.Join("\n", J.Arr(d.RootElement, "result.content").Select(p => J.S(p, "text")).Where(t => t is not null));
                    if (J.S(d.RootElement, "result.isError") == "true") throw new AppException(400, $"The tool reported an error: {J.Clip(text, 300)}");
                    return new ActionResult(J.Clip(text.Length > 0 ? text : "Done.", 800));
                } },
        ],
    };
}
