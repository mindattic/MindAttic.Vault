using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Configuration;

/// <summary>
/// Reads named keys and app assignments from IConfiguration (the deployed-app path: App Configuration
/// with Key Vault references, or <see cref="VaultConfigurationSource"/> locally). Falls back to the
/// older <c>apiKeys</c> array and single <c>apiKey</c> shapes so every existing source keeps working.
/// </summary>
public sealed class ConfigurationKeySource : IKeySource
{
    private readonly IConfiguration configuration;

    public ConfigurationKeySource(IConfiguration configuration) =>
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public IReadOnlyList<NamedKey> GetKeys(string providerId)
    {
        var info = ProviderCatalog.Get(providerId);
        var result = new List<NamedKey>();
        foreach (var id in ProviderCatalog.IdsFor(info.Id))
        {
            var section = configuration.GetSection($"{VaultConfigurationKeys.VaultSection}:{info.Bucket}:{id}");
            foreach (var key in ReadProvider(section))
            {
                if (result.Any(k => k.Value == key.Value)) continue;
                var name = key.Name;
                for (var n = 2; result.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{key.Name} ({n})";
                result.Add(key with { Name = name });
            }
        }
        return result;
    }

    public IReadOnlyList<string> GetAssignment(string appId, string providerId) =>
        Ordered(configuration.GetSection(LampNames.Assignment(appId, providerId)))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .ToList();

    internal static IReadOnlyList<NamedKey> ReadProvider(IConfigurationSection section)
    {
        var keys = section.GetSection(LampNames.KeysSegment).GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!.Trim(), StringComparer.OrdinalIgnoreCase);
        if (keys.Count > 0)
        {
            var order = Ordered(section.GetSection(LampNames.KeyOrderSegment)).Where(n => n is not null && keys.ContainsKey(n)).Select(n => n!).ToList();
            foreach (var name in keys.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                if (!order.Contains(name, StringComparer.OrdinalIgnoreCase)) order.Add(name);
            var disabled = new HashSet<string>(Ordered(section.GetSection(LampNames.DisabledKeysSegment)).Where(n => n is not null)!, StringComparer.OrdinalIgnoreCase);
            return order.Select(n => new NamedKey(keys.Keys.First(k => string.Equals(k, n, StringComparison.OrdinalIgnoreCase)), keys[n], disabled.Contains(n))).ToList();
        }

        var entries = new List<CredentialPoolEntry>();
        foreach (var item in section.GetSection("apiKeys").GetChildren().OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue))
        {
            var value = item["key"];
            if (string.IsNullOrWhiteSpace(value)) continue;
            entries.Add(new CredentialPoolEntry(value.Trim(), item["label"], bool.TryParse(item["disabled"], out var d) && d));
        }
        if (entries.Count == 0 && section[VaultConfigurationKeys.ApiKeyProperty] is { } single && !string.IsNullOrWhiteSpace(single))
            entries.Add(new CredentialPoolEntry(single.Trim()));
        return NamedKeyStore.Name(entries);
    }

    private static IEnumerable<string?> Ordered(IConfigurationSection section) =>
        section.GetChildren()
            .OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue)
            .Select(c => c.Value);
}

/// <summary>Key selection over configuration.</summary>
public static class ConfigurationKeySelectorExtensions
{
    /// <summary>A <see cref="KeySelector"/> reading keys from configuration first, then the local files.</summary>
    public static KeySelector CreateKeySelector(this IConfiguration configuration,
        Func<Exception, KeyFailureException?>? classifier = null) =>
        new(new CompositeKeySource(new ConfigurationKeySource(configuration), new FileKeySource(NamedKeyStore.ForRoamingRoot())),
            KeyHealthStore.ForLocalRoot(), classifier: classifier);
}
