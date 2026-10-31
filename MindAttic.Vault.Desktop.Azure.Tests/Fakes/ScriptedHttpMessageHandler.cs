using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MindAttic.Vault.Desktop.Azure.Tests.Fakes;

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> (copied from Automata.Tests.Fakes): records every request and returns a
/// scripted sequence of responses, one per call — the last entry repeats for any call beyond the scripted count.
/// Extended here with <see cref="Route"/> responders, tried in order before the script, so a fake service
/// (<see cref="FakeAzure"/>) can answer by method and path while a test still scripts one-off failures.
/// </summary>
public sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(HttpStatusCode Status, string Body, int? RetryAfterSeconds)> responses = new();
    private readonly List<JsonNode?> requestBodies = new();
    private readonly List<RecordedRequest> requests = new();
    private readonly List<Func<RecordedRequest, HttpResponseMessage?>> routes = new();
    private readonly object gate = new();
    private int callCount;

    public IReadOnlyList<JsonNode?> RequestBodies { get { lock (gate) return requestBodies.ToList(); } }
    public IReadOnlyList<RecordedRequest> Requests { get { lock (gate) return requests.ToList(); } }
    public int CallCount => callCount;

    public ScriptedHttpMessageHandler Respond(HttpStatusCode status, string body, int? retryAfterSeconds = null)
    {
        responses.Add((status, body, retryAfterSeconds));
        return this;
    }

    /// <summary>Adds a responder; returning null passes the request on to the next responder or the script.</summary>
    public ScriptedHttpMessageHandler Route(Func<RecordedRequest, HttpResponseMessage?> responder)
    {
        lock (gate) routes.Insert(0, responder);
        return this;
    }

    /// <summary>Adds a responder tried after every existing one (a service fallback).</summary>
    public ScriptedHttpMessageHandler Fallback(Func<RecordedRequest, HttpResponseMessage?> responder)
    {
        lock (gate) routes.Add(responder);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bodyText = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            try { json = JsonNode.Parse(bodyText); } catch { json = null; }
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!, headers, bodyText, json);

        List<Func<RecordedRequest, HttpResponseMessage?>> snapshot;
        lock (gate)
        {
            requests.Add(recorded);
            requestBodies.Add(json);
            snapshot = routes.ToList();
        }

        foreach (var route in snapshot)
            if (route(recorded) is { } routed)
            {
                Interlocked.Increment(ref callCount);
                routed.RequestMessage = request;
                return routed;
            }

        if (responses.Count == 0)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json"), RequestMessage = request };

        var index = Math.Min(callCount, responses.Count - 1);
        var (status, respBody, retryAfter) = responses[index];
        Interlocked.Increment(ref callCount);

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(respBody, Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
        if (retryAfter is { } seconds)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
}

/// <summary>A request as the handler saw it (headers and body captured before the SDK disposes the message).</summary>
public sealed record RecordedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body, JsonNode? Json)
{
    public string Path => Uri.AbsolutePath;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public string? Query(string name)
    {
        foreach (var part in Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                return eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    public override string ToString() => $"{Method} {Uri}";
}
