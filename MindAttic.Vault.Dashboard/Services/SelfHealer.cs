using System.Text.Json.Nodes;
using MindAttic.Legion;

namespace MindAttic.Vault.Dashboard.Services;

/// <summary>
/// The "fix itself inside an hour" worker for the one failure mode that is
/// safely auto-fixable: a deprecated model (HTTP 404 / <see cref="LlmHealthDiagnosis.NotFound"/>).
/// A dead/expired KEY cannot be auto-minted — that path alerts instead.
///
/// <para>On a NotFound, it asks the provider for its live model inventory
/// (<see cref="LlmModelDiscovery"/>), picks a sensible replacement, and writes it
/// into the writable Vault store (<c>providers.json</c>'s per-provider <c>model</c>
/// field) so the next call uses a model that actually exists.</para>
/// </summary>
public sealed class SelfHealer
{
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<SelfHealer> log;

    public SelfHealer(IHttpClientFactory httpFactory, ILogger<SelfHealer> log)
    {
        this.httpFactory = httpFactory;
        this.log = log;
    }

    /// <summary>
    /// Attempt to repoint <paramref name="providerId"/> to a live model.
    /// Returns a human-readable note describing what happened, or null when no
    /// change was made (no live models, write failed, etc.).
    /// </summary>
    public async Task<string?> TryRepointModelAsync(string providerId, TimeSpan timeout, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        var discovery = new LlmModelDiscovery(http);

        LlmModelDiscoveryResult result;
        try
        {
            result = await discovery.DiscoverOneAsync(providerId, timeout, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "self-heal: model discovery failed for {Provider}", providerId);
            return null;
        }

        if (result.LiveModels.Count == 0)
        {
            log.LogWarning("self-heal: {Provider} returned no live models — cannot repoint", providerId);
            return null;
        }

        var replacement = PickReplacement(providerId, result.LiveModels);
        if (string.IsNullOrWhiteSpace(replacement))
        {
            // No catalog-vetted live model to fall back to. Repointing to an
            // arbitrary live id risks landing on a non-chat model (an image /
            // embedding / rerank endpoint), which would keep the chat probe
            // failing forever. Leave it DOWN so the alert path handles it.
            log.LogWarning("self-heal: {Provider} has no catalog-known live model — leaving DOWN for manual review", providerId);
            return null;
        }

        if (!TryWriteModel(providerId, replacement, out var error))
        {
            log.LogWarning("self-heal: could not persist new model for {Provider}: {Error}", providerId, error);
            return $"auto-repoint to '{replacement}' FAILED to persist ({error}) — manual fix needed";
        }

        log.LogInformation("self-heal: repointed {Provider} to live model '{Model}'", providerId, replacement);
        return $"auto-repointed to live model '{replacement}'";
    }

    /// <summary>
    /// Pick a replacement model that is BOTH live at the provider AND vetted in
    /// Legion's catalog for that provider — so self-heal can never repoint a chat
    /// provider onto a random non-chat endpoint (image / embedding / rerank) that
    /// merely happens to appear first in the live list. Prefers the tiered
    /// High → Medium → Default model, then any other catalog-known live model.
    /// Returns null when nothing safe is available (caller leaves it DOWN).
    /// </summary>
    private static string? PickReplacement(string providerId, IReadOnlyList<string> liveModels)
    {
        bool Live(string? m) => !string.IsNullOrWhiteSpace(m)
            && liveModels.Contains(m!, StringComparer.OrdinalIgnoreCase);

        var high   = LlmProviderCatalog.GetTieredModel(providerId, ModelTier.High);
        var medium = LlmProviderCatalog.GetTieredModel(providerId, ModelTier.Medium);
        var dflt   = LlmProviderCatalog.Get(providerId)?.DefaultModel;

        if (Live(high))   return high!;
        if (Live(medium)) return medium!;
        if (Live(dflt))   return dflt!;

        // Any live model the catalog already knows for this provider is safe.
        return liveModels.FirstOrDefault(m => LlmProviderCatalog.IsKnownModel(providerId, m));
    }

    /// <summary>
    /// Patch the <c>model</c> field on the provider's entry in the writable Vault
    /// store, preserving every other field (apiKey, type, …). Returns false (with
    /// a reason) when there's no entry or the store is read-only in this host.
    /// </summary>
    private static bool TryWriteModel(string providerId, string model, out string error)
    {
        error = "";
        try
        {
            var all = MindAtticCredentialStore.LoadAllRaw();
            if (!all.TryGetValue(providerId, out var rawJson) || string.IsNullOrWhiteSpace(rawJson))
            {
                error = "no providers.json entry";
                return false;
            }

            var node = JsonNode.Parse(rawJson)?.AsObject();
            if (node is null) { error = "entry was not a JSON object"; return false; }

            node["model"] = model;
            MindAtticCredentialStore.SaveRaw(providerId, node.ToJsonString());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
