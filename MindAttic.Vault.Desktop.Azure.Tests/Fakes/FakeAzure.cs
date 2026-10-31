using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Core.Pipeline;

namespace MindAttic.Vault.Desktop.Azure.Tests.Fakes;

/// <summary>A token credential that never signs anyone in.</summary>
public sealed class FakeTokenCredential : TokenCredential
{
    public int Calls;

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}

/// <summary>
/// In-memory Azure for the real SDK clients: an App Configuration store, a Key Vault (with the challenge
/// handshake, versions, tags and soft-delete) and ARM listings (paged), all served through one
/// <see cref="ScriptedHttpMessageHandler"/> plugged in with <c>ClientOptions.Transport</c>.
/// </summary>
public sealed class FakeAzure
{
    public const string StoreHost = "teststore.azconfig.io";
    public const string VaultHost = "testvault.vault.azure.net";
    public const string ArmHost = "management.azure.com";

    public static readonly Uri StoreEndpoint = new($"https://{StoreHost}");
    public static readonly Uri VaultUri = new($"https://{VaultHost}/");

    private readonly object gate = new();
    private int etagCounter;

    public FakeAzure()
    {
        Handler = new ScriptedHttpMessageHandler();
        Handler.Fallback(HandleAppConfiguration).Fallback(HandleKeyVault).Fallback(HandleArm);
        Credential = new FakeTokenCredential();
        Clients = new AzureClientFactory(() => Credential, new HttpClientTransport(Handler), maxRetries: 0);
    }

    public ScriptedHttpMessageHandler Handler { get; }
    public FakeTokenCredential Credential { get; }
    public AzureClientFactory Clients { get; }

    /// <summary>App Configuration key-values by exact key (null label only).</summary>
    public SortedDictionary<string, Kv> Settings { get; } = new(StringComparer.Ordinal);

    /// <summary>Key Vault secrets by name (case-insensitive like Key Vault), versions oldest first.</summary>
    public Dictionary<string, List<SecretVersion>> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Soft-deleted secret names.</summary>
    public HashSet<string> DeletedSecrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ARM: subscription id → display name; resource groups; vaults; stores. Page size for every list.</summary>
    public List<(string Id, string Name)> Subscriptions { get; } = new();
    public Dictionary<string, List<string>> ResourceGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> KeyVaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> AppConfigurationStores { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ArmPageSize { get; set; } = 1;
    public int AppConfigurationPageSize { get; set; } = 100;

    public IEnumerable<RecordedRequest> AppConfigurationWrites =>
        Handler.Requests.Where(r => r.Uri.Host == StoreHost && r.Method is "PUT" or "DELETE");

    public IEnumerable<RecordedRequest> KeyVaultRequests => Handler.Requests.Where(r => r.Uri.Host == VaultHost);

    public sealed record Kv(string Key, string? Value, string? ContentType, string ETag);
    public sealed record SecretVersion(string Version, string Value, Dictionary<string, string> Tags);

    public string Seed(string key, string? value, string? contentType = null)
    {
        lock (gate)
        {
            var kv = new Kv(key, value, contentType, NextETag());
            Settings[key] = kv;
            return kv.ETag;
        }
    }

    public string SeedSecret(string name, string value, Dictionary<string, string>? tags = null)
    {
        lock (gate)
        {
            if (!Secrets.TryGetValue(name, out var versions)) Secrets[name] = versions = new();
            var v = Guid.NewGuid().ToString("N");
            versions.Add(new SecretVersion(v, value, tags ?? new()));
            return v;
        }
    }

    /// <summary>The resolved tree a deployed app's IConfiguration would see (Key Vault references → latest secret value).</summary>
    public Dictionary<string, string?> ResolvedConfiguration()
    {
        lock (gate)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Settings.Values)
            {
                if (kv.ContentType?.StartsWith("application/vnd.microsoft.appconfig.keyvaultref+json", StringComparison.Ordinal) == true)
                {
                    var uri = new Uri(JsonNode.Parse(kv.Value!)!["uri"]!.GetValue<string>());
                    var name = uri.AbsolutePath.Trim('/').Split('/')[1];
                    result[kv.Key] = Secrets.TryGetValue(name, out var versions) && !DeletedSecrets.Contains(name) ? versions[^1].Value : null;
                }
                else result[kv.Key] = kv.Value;
            }
            return result;
        }
    }

    private string NextETag() => "etag-" + Interlocked.Increment(ref etagCounter).ToString("D6");

    // ------------------------------------------------------------------ App Configuration

    private HttpResponseMessage? HandleAppConfiguration(RecordedRequest r)
    {
        if (r.Uri.Host != StoreHost) return null;
        var path = r.Uri.AbsolutePath;
        lock (gate)
        {
            if (r.Method == "GET" && path.TrimEnd('/') == "/kv") return ListSettings(r);
            if (!path.StartsWith("/kv/", StringComparison.Ordinal)) return Json(HttpStatusCode.NotFound, "{}");
            var key = Uri.UnescapeDataString(r.Uri.AbsolutePath.Substring(4));
            if (r.Uri.OriginalString.Contains("/kv/", StringComparison.Ordinal))
            {
                var raw = r.Uri.OriginalString;
                var start = raw.IndexOf("/kv/", StringComparison.Ordinal) + 4;
                var end = raw.IndexOf('?', start);
                key = Uri.UnescapeDataString(end < 0 ? raw[start..] : raw[start..end]);
            }
            Settings.TryGetValue(key, out var existing);
            var ifMatch = Unquote(r.Header("If-Match"));
            var ifNoneMatch = Unquote(r.Header("If-None-Match"));

            switch (r.Method)
            {
                case "GET":
                    return existing is null ? Json(HttpStatusCode.NotFound, "{}") : SettingResponse(HttpStatusCode.OK, existing);

                case "PUT":
                {
                    if (ifMatch is not null && (existing is null || (ifMatch != "*" && ifMatch != existing.ETag))) return PreconditionFailed();
                    if (ifNoneMatch == "*" && existing is not null) return PreconditionFailed();
                    var body = r.Json as JsonObject ?? new JsonObject();
                    var kv = new Kv(key, body["value"]?.GetValue<string>(), body["content_type"]?.GetValue<string>(), NextETag());
                    Settings[key] = kv;
                    return SettingResponse(HttpStatusCode.OK, kv);
                }

                case "DELETE":
                {
                    if (existing is null) return ifMatch is null ? new HttpResponseMessage(HttpStatusCode.NoContent) : PreconditionFailed();
                    if (ifMatch is not null && ifMatch != "*" && ifMatch != existing.ETag) return PreconditionFailed();
                    Settings.Remove(key);
                    return SettingResponse(HttpStatusCode.OK, existing);
                }
            }
            return Json(HttpStatusCode.MethodNotAllowed, "{}");
        }
    }

    private HttpResponseMessage ListSettings(RecordedRequest r)
    {
        var filters = SplitFilter(r.Query("key") ?? "*");
        var matching = Settings.Values.Where(kv => filters.Any(f => Matches(f, kv.Key))).ToList();
        var after = r.Query("after");
        var start = after is null ? 0 : int.Parse(after);
        var page = matching.Skip(start).Take(AppConfigurationPageSize).ToList();
        var items = new JsonArray(page.Select(kv => (JsonNode)SettingJson(kv)).ToArray());
        var body = new JsonObject { ["items"] = items };
        var response = Json(HttpStatusCode.OK, "");
        if (start + page.Count < matching.Count)
        {
            var next = $"/kv?key={Uri.EscapeDataString(r.Query("key") ?? "*")}&api-version={r.Query("api-version")}&after={start + page.Count}";
            body["@nextLink"] = next;
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
        }
        response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/vnd.microsoft.appconfig.kvset+json");
        return response;
    }

    /// <summary>Splits an App Configuration key filter on unescaped commas and unescapes each part.</summary>
    internal static List<(string Literal, bool Prefix)> SplitFilter(string filter)
    {
        var result = new List<(string, bool)>();
        var sb = new StringBuilder();
        var prefix = false;
        for (var i = 0; i < filter.Length; i++)
        {
            var c = filter[i];
            if (c == '\\' && i + 1 < filter.Length) { sb.Append(filter[++i]); continue; }
            if (c == ',') { result.Add((sb.ToString(), prefix)); sb.Clear(); prefix = false; continue; }
            if (c == '*' && i == filter.Length - 1 || c == '*' && filter[i + 1] == ',') { prefix = true; continue; }
            sb.Append(c);
        }
        result.Add((sb.ToString(), prefix));
        return result;
    }

    private static bool Matches((string Literal, bool Prefix) f, string key) =>
        f.Prefix ? key.StartsWith(f.Literal, StringComparison.Ordinal) : key == f.Literal;

    private static JsonObject SettingJson(Kv kv) => new()
    {
        ["etag"] = kv.ETag,
        ["key"] = kv.Key,
        ["label"] = null,
        ["content_type"] = kv.ContentType,
        ["value"] = kv.Value,
        ["tags"] = new JsonObject(),
        ["locked"] = false,
        ["last_modified"] = "2026-10-31T12:00:00+00:00",
    };

    private static HttpResponseMessage SettingResponse(HttpStatusCode status, Kv kv)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(SettingJson(kv).ToJsonString(), Encoding.UTF8, "application/vnd.microsoft.appconfig.kv+json"),
        };
        response.Headers.TryAddWithoutValidation("ETag", $"\"{kv.ETag}\"");
        return response;
    }

    private static HttpResponseMessage PreconditionFailed() =>
        Json(HttpStatusCode.PreconditionFailed, "{\"type\":\"https://azconfig.io/errors/precondition-failed\",\"title\":\"Precondition failed\",\"status\":412}");

    // ------------------------------------------------------------------ Key Vault

    private HttpResponseMessage? HandleKeyVault(RecordedRequest r)
    {
        if (r.Uri.Host != VaultHost) return null;
        if (r.Header("Authorization") is null)
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"code\":\"Unauthorized\",\"message\":\"AKV10000\"}}", Encoding.UTF8, "application/json") };
            challenge.Headers.TryAddWithoutValidation("WWW-Authenticate",
                "Bearer authorization=\"https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001\", resource=\"https://vault.azure.net\"");
            return challenge;
        }

        var segments = r.Uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2 || segments[0] != "secrets") return KvError(HttpStatusCode.NotFound, "NotFound", "No such path.");
        var name = segments[1];
        var version = segments.Length > 2 ? segments[2] : null;

        lock (gate)
        {
            Secrets.TryGetValue(name, out var versions);
            var live = versions is not null && !DeletedSecrets.Contains(name);
            switch (r.Method)
            {
                case "PUT":
                {
                    if (DeletedSecrets.Contains(name))
                        return KvError(HttpStatusCode.Conflict, "Conflict", $"Secret {name} is currently in a deleted but recoverable state.");
                    var body = r.Json as JsonObject ?? new JsonObject();
                    var tags = (body["tags"] as JsonObject)?.ToDictionary(t => t.Key, t => t.Value!.GetValue<string>()) ?? new();
                    if (versions is null) Secrets[name] = versions = new();
                    var v = new SecretVersion(Guid.NewGuid().ToString("N"), body["value"]!.GetValue<string>(), tags);
                    versions.Add(v);
                    return Json(HttpStatusCode.OK, SecretJson(name, v, includeValue: true).ToJsonString());
                }

                case "GET" when version == "versions":
                {
                    if (!live) return KvError(HttpStatusCode.NotFound, "SecretNotFound", $"A secret with (name/id) {name} was not found in this key vault.");
                    var items = new JsonArray(versions!.Select(v => (JsonNode)SecretJson(name, v, includeValue: false)).ToArray());
                    return Json(HttpStatusCode.OK, new JsonObject { ["value"] = items, ["nextLink"] = null }.ToJsonString());
                }

                case "GET":
                {
                    var match = !live ? null : string.IsNullOrEmpty(version) ? versions![^1] : versions!.FirstOrDefault(v => v.Version == version);
                    return match is null
                        ? KvError(HttpStatusCode.NotFound, "SecretNotFound", $"A secret with (name/id) {name} was not found in this key vault.")
                        : Json(HttpStatusCode.OK, SecretJson(name, match, includeValue: true).ToJsonString());
                }

                case "PATCH":
                {
                    var match = !live ? null : string.IsNullOrEmpty(version) ? versions![^1] : versions!.FirstOrDefault(v => v.Version == version);
                    if (match is null) return KvError(HttpStatusCode.NotFound, "SecretNotFound", "Not found.");
                    if ((r.Json as JsonObject)?["tags"] is JsonObject tags)
                    {
                        match.Tags.Clear();
                        foreach (var (k, v) in tags) match.Tags[k] = v!.GetValue<string>();
                    }
                    return Json(HttpStatusCode.OK, SecretJson(name, match, includeValue: false).ToJsonString());
                }

                case "DELETE":
                {
                    if (!live) return KvError(HttpStatusCode.NotFound, "SecretNotFound", "Not found.");
                    DeletedSecrets.Add(name);
                    var json = SecretJson(name, versions![^1], includeValue: false);
                    json["recoveryId"] = $"https://{VaultHost}/deletedsecrets/{name}";
                    json["deletedDate"] = 1793448000;
                    json["scheduledPurgeDate"] = 1801224000;
                    return Json(HttpStatusCode.OK, json.ToJsonString());
                }
            }
        }
        return KvError(HttpStatusCode.MethodNotAllowed, "BadRequest", "Unsupported.");
    }

    private static JsonObject SecretJson(string name, SecretVersion v, bool includeValue)
    {
        var json = new JsonObject
        {
            ["id"] = $"https://{VaultHost}/secrets/{name}/{v.Version}",
            ["attributes"] = new JsonObject { ["enabled"] = true, ["created"] = 1793448000, ["updated"] = 1793448000, ["recoveryLevel"] = "Recoverable+Purgeable" },
            ["tags"] = new JsonObject(v.Tags.Select(t => new KeyValuePair<string, JsonNode?>(t.Key, t.Value))),
        };
        if (includeValue) json["value"] = v.Value;
        return json;
    }

    private static HttpResponseMessage KvError(HttpStatusCode status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString());

    // ------------------------------------------------------------------ ARM

    private HttpResponseMessage? HandleArm(RecordedRequest r)
    {
        if (r.Uri.Host != ArmHost || r.Method != "GET") return null;
        var segments = r.Uri.AbsolutePath.Trim('/').Split('/');
        var skip = int.TryParse(r.Query("$skiptoken"), out var s) ? s : 0;
        lock (gate)
        {
            if (segments is ["subscriptions"])
                return Page(r, skip, Subscriptions.Select(sub => (JsonNode)new JsonObject
                {
                    ["id"] = $"/subscriptions/{sub.Id}",
                    ["subscriptionId"] = sub.Id,
                    ["displayName"] = sub.Name,
                    ["state"] = "Enabled",
                    ["tenantId"] = "00000000-0000-0000-0000-000000000001",
                }).ToList());

            if (segments.Length == 3 && segments[0] == "subscriptions" && segments[2].Equals("resourcegroups", StringComparison.OrdinalIgnoreCase))
                return Page(r, skip, (ResourceGroups.TryGetValue(segments[1], out var groups) ? groups : new()).Select(g => (JsonNode)new JsonObject
                {
                    ["id"] = $"/subscriptions/{segments[1]}/resourceGroups/{g}",
                    ["name"] = g,
                    ["type"] = "Microsoft.Resources/resourceGroups",
                    ["location"] = "eastus",
                    ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" },
                }).ToList());

            if (segments.Length == 7 && segments[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) && segments[4] == "providers")
            {
                var rgKey = $"{segments[1]}/{segments[3]}";
                var idBase = $"/subscriptions/{segments[1]}/resourceGroups/{segments[3]}/providers/{segments[5]}/{segments[6]}";
                if (segments[5].Equals("Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase))
                    return Page(r, skip, (KeyVaults.TryGetValue(rgKey, out var vaults) ? vaults : new()).Select(v => (JsonNode)new JsonObject
                    {
                        ["id"] = $"{idBase}/{v}",
                        ["name"] = v,
                        ["type"] = "Microsoft.KeyVault/vaults",
                        ["location"] = "eastus",
                        ["properties"] = new JsonObject
                        {
                            ["tenantId"] = "00000000-0000-0000-0000-000000000001",
                            ["sku"] = new JsonObject { ["family"] = "A", ["name"] = "standard" },
                            ["vaultUri"] = $"https://{v}.vault.azure.net/",
                        },
                    }).ToList());
                if (segments[5].Equals("Microsoft.AppConfiguration", StringComparison.OrdinalIgnoreCase))
                    return Page(r, skip, (AppConfigurationStores.TryGetValue(rgKey, out var stores) ? stores : new()).Select(a => (JsonNode)new JsonObject
                    {
                        ["id"] = $"{idBase}/{a}",
                        ["name"] = a,
                        ["type"] = "Microsoft.AppConfiguration/configurationStores",
                        ["location"] = "eastus",
                        ["sku"] = new JsonObject { ["name"] = "standard" },
                        ["properties"] = new JsonObject { ["endpoint"] = $"https://{a}.azconfig.io", ["provisioningState"] = "Succeeded" },
                    }).ToList());
            }
        }
        return Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"NotFound\",\"message\":\"No route.\"}}");
    }

    private HttpResponseMessage Page(RecordedRequest r, int skip, List<JsonNode> all)
    {
        var page = all.Skip(skip).Take(ArmPageSize).ToList();
        var body = new JsonObject { ["value"] = new JsonArray(page.ToArray()) };
        if (skip + page.Count < all.Count)
            body["nextLink"] = $"https://{ArmHost}{r.Uri.AbsolutePath}?api-version={r.Query("api-version")}&$skiptoken={skip + page.Count}";
        return Json(HttpStatusCode.OK, body.ToJsonString());
    }

    // ------------------------------------------------------------------ helpers

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string? Unquote(string? etag) => etag?.Trim().Trim('"');
}
