# MindAttic.Vault

One IConfiguration-backed credentials and settings library for .NET 9 and 10: the same wiring reads API keys from a local file store on your laptop and from App Service settings or Azure Key Vault in production.

[![.NET](https://img.shields.io/badge/.NET-9.0%20and%2010.0-512BD4)](MindAttic.Vault/MindAttic.Vault.csproj) [![C#](https://img.shields.io/badge/language-C%23-239120)](MindAttic.Vault) [![Version](https://img.shields.io/badge/version-5.0.0-blue)](MindAttic.Vault/MindAttic.Vault.csproj) [![Tests](https://img.shields.io/badge/tests-NUnit%204-brightgreen)](MindAttic.Vault.Tests) [![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

```text
                    services.AddMindAtticVault(builder.Configuration)
                                        |
                                        v
   llm.GetKey("claude")  -->  LlmCredentialResolver  (CompositeCredentialStore)
                                        |
             +--------------------------+---------------------------+
             |  first non-blank value wins                          |
             v                                                      v
   IConfiguration  "MindAttic:Vault:LLM:claude:apiKey"     LlmCredentialStore
     a. AddAzureKeyVault(...)          production            %APPDATA%\MindAttic\LLM
     b. AddEnvironmentVariables()      App Service, CI       providers.json
     c. AddJsonFile("appsettings")     public defaults       (writable; settings UIs
     d. AddMindAtticVaultFiles()       local dev              land here)
```

Get it: the package is `MindAttic.Vault` (current version 5.0.0, built and packed from this repo; see [Building](#building)).

## Why

- Stop hand-rolling `Load()`, `Save()` and environment-overlay plumbing in every service; one library replaces it.
- Ship the same code to a developer laptop, Azure App Service, Azure Container Apps or AKS with no changes between environments.
- Define secrets once under `MindAttic:Vault` and have local files, environment variables, App Service settings and Key Vault all resolve into the same shape.
- Keep production read-only: configuration-backed stores throw on writes, so a deployed app never mutates its secrets.
- Keep existing `%APPDATA%` keyrings working; they surface as a first-class `IConfigurationSource`, so migrating is zero-risk.
- Take no Azure SDK dependency in the core package; wire Key Vault, AWS or GCP providers upstream and Vault reads them through `IConfiguration`.

## Features

### Credential resolution

- `LlmCredentialResolver` and `BrokerCredentialResolver` chain `IConfiguration` in front of a writable file store; reads walk config, then file, then null, and writes go to the file store only.
- `ConfigurationCredentialStore` is a read-only view over any configuration section; every write method throws `NotSupportedException`.
- `CompositeCredentialStore` chains any number of stores: first non-null read wins, writes target the first writable store.
- `AppScopedCredentialStore` gives each app its own key per provider (namespaced as `appId-provider`), falling back to the shared key when the app has none.
- `IRotatingKeyStore` lets a provider hold a pool of keys in priority order, each with an optional label, while the first key stays mirrored to the plain `apiKey` field for single-key callers.

### File stores

- `LlmCredentialStore`, `BrokerCredentialStore` and `FtpCredentialStore` read and write the canonical bucket files under `%APPDATA%\MindAttic\<Bucket>\`.
- `TokenStore` holds single-secret buckets such as GitHub or USPS tokens.
- Writes are atomic (temp file plus `File.Replace`, with a `.bak` kept), and malformed files are recovered from instead of crashing the host.

### Settings and paths

- `JsonSettingsStore<T>` loads, saves and updates per-app JSON settings, roaming or local, with sync and async APIs.
- `VaultPaths` replaces scattered `Path.Combine(APPDATA, "MindAttic", ...)` path math and works cross-platform.
- `EnvironmentOverlay` and `KeyResolver` compose environment-variable overlays and custom resolution chains for code without a DI host.

### Dependency injection

- `services.AddMindAtticVault()` for console and desktop apps (file stores only).
- `services.AddMindAtticVault(builder.Configuration)` for web and worker hosts (cloud-native resolvers).
- `services.AddVaultAppSettings<T>("MyApp")` for a roaming settings store.

Every public type ships XML doc comments (`MindAttic.Vault.xml` is emitted at build time), so IntelliSense explains behaviour, edge cases and exceptions inline.

## Quick start

Prerequisites: the .NET 9 or .NET 10 SDK and a reference to the `MindAttic.Vault` package (see [Building](#building) to pack it yourself).

```csharp
// Program.cs
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddMindAtticVaultFiles()                 // %APPDATA%\MindAttic\... single local source of truth
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddSingleton<MyService>();
```

```csharp
// MyService.cs
using MindAttic.Vault.Credentials;

public class MyService(LlmCredentialResolver llm, BrokerCredentialResolver brokers)
{
    public string? Claude      => llm.GetKey("claude");
    public string? AlpacaPaper => brokers.GetKey("alpaca-paper");
}
```

Set a secret once and every MindAttic project on the machine sees it. Edit the bucket file directly, or use the writable store API:

```csharp
LlmCredentialStore.Default.SetKey("claude", "sk-ant-...");           // LLM\providers.json
BrokerCredentialStore.Default.SetBrokerCreds("alpaca-paper",
    new BrokerCredentialStore.BrokerCreds("PK...", "S...", null));   // Brokers\providers.json
TokenStore.ForBucket("Tokens").Set("github", "ghp_...");             // Tokens\tokens.json
FtpCredentialStore.Default.Set(new FtpCredentialStore.FtpCreds(
    "ftp.example.com", 21, "user@example.com", "pw", true,
    "prod.example.net", null));                                      // Ftp\ftp.json
```

The equivalent `%APPDATA%\MindAttic\LLM\providers.json`:

```json
{ "claude": { "type": "anthropic", "apiKey": "sk-ant-..." } }
```

`FtpCredentialStore.Default.TryGetJson()` returns the exact flat JSON blob that MindAttic.Deploy's `MINDATTIC_FTP_JSON` environment variable expects, with no reshaping at the call site.

## Configuration schema

Every source (appsettings.json, the local APPDATA store, environment variables, App Service Application Settings, Azure Key Vault) surfaces the same shape under `MindAttic:Vault`:

```json
{
  "MindAttic": {
    "Vault": {
      "LLM": {
        "claude": { "type": "anthropic", "apiKey": "sk-ant-...", "model": "claude-sonnet-4-6", "maxTokens": 8192 },
        "gemini": { "type": "google",    "apiKey": "AIza..." },
        "grok":   { "type": "bearer",    "apiKey": "xai-..." }
      },
      "Brokers": {
        "alpaca-paper": { "type": "alpaca", "apiKey": "PK...", "secret": "...", "baseUrl": "https://paper-api.alpaca.markets" },
        "alpaca-live":  { "type": "alpaca", "apiKey": "AK...", "secret": "...", "baseUrl": "https://api.alpaca.markets" }
      },
      "Tokens": {
        "github": "ghp_...",
        "usps":   "USPS-..."
      }
    }
  }
}
```

How that schema appears in each source:

| Source | What you set | Notes |
| --- | --- | --- |
| appsettings.json | The nested object above | Use `appsettings.Development.json` for non-secret dev overrides; never check secrets into git. |
| Local dev (APPDATA) | `%APPDATA%\MindAttic\LLM\providers.json` (folder equals section) | The single local source of truth, surfaced through `IConfiguration` by `AddMindAtticVaultFiles()`. |
| Environment variables | `MindAttic__Vault__LLM__claude__apiKey=sk-ant-...` | Standard double-underscore to colon translation. App Service Application Settings arrive this way. |
| Azure Key Vault | Secret named `MindAttic--Vault--LLM--claude--apiKey` | The default `KeyVaultSecretManager` translates double-dash to colon. |
| App Service Key Vault references | App Setting value `@Microsoft.KeyVault(SecretUri=...)` | App Service resolves the reference into a plain environment variable before the app sees it. |

### Canonical buckets

MindAttic projects do not use User Secrets ([VLT-LAW-3](docs/BIBLE.md#VLT-LAW-3)). It would duplicate the writable APPDATA store and, because `AddUserSecrets` ranks above `AddMindAtticVaultFiles`, a stale value could silently mask a freshly rotated key on disk. Do not add `AddUserSecrets(...)` or `<UserSecretsId>` to MindAttic projects. Production stays on environment variables and Key Vault.

The on-disk layout follows one invariant: folder name equals config section equals `MindAttic:Vault:<Bucket>`, and each file is a faithful image of its config subtree.

| Bucket folder | File | Shape |
| --- | --- | --- |
| `LLM` | `providers.json` | `{ id: { type, apiKey, model, maxTokens } }` |
| `Brokers` | `providers.json` | `{ id: { type, apiKey, secret, baseUrl } }` |
| `Tokens` | `tokens.json` | `{ github: "...", "nuget-org": "..." }` (flat) |
| `Subtitles` | `providers.json` | `{ OpenSubtitles: { user, password } }` |
| `Notifications` | `providers.json` | `{ email:{ smtpHost, smtpPort, username, password, from }, toEmail:"..." }` |
| `AudioStore` | `providers.json` | `{ provider, container, connectionString }` |
| `Ftp` | `ftp.json` | `{ host, port, user, password, secure, servername }` (flat, single record) |

`MindAtticConfigurationSource` scans every bucket except `Ftp` by default and flattens each file (nested objects, arrays and top-level scalars) into `IConfiguration`. `Ftp` is a deliberate exception: it is a deploy-time credential for MindAttic.Deploy, never something App Service or Key Vault needs to surface, so it stays a file-only store (`FtpCredentialStore`). Pass `Buckets` explicitly to include it.

### Source precedence

With the recommended wiring, `GetKey("claude")` walks:

```text
1.  Explicit DI registration              (e.g. services.AddSingleton(myMockedStore))
2.  IConfiguration (highest-priority source among):
      a. AddAzureKeyVault(...)            <- prod, when you wire it directly
      b. AddEnvironmentVariables()        <- App Service, containers, CI
      c. AddJsonFile("appsettings.json")  <- non-secret defaults / public config
      d. AddMindAtticVaultFiles()         <- %APPDATA%\MindAttic (single local source of truth)
3.  LlmCredentialStore (file fallback)    <- writable; settings UI lands here
4.  return null
```

Any non-null trimmed value short-circuits the chain. `KeyResolver` exposes the same primitives so non-DI code can compose the chain by hand.

### Settings versus credentials

| What | Where | Roaming | Why |
| --- | --- | --- | --- |
| API keys and secrets (local dev) | `%APPDATA%\MindAttic\<Bucket>\` | yes | Single local source of truth, surfaced through `IConfiguration`. |
| API keys and secrets (prod) | `IConfiguration` (App Service settings, Key Vault) | n/a | Cloud-native standard; never written by app code in prod. |
| Per-app preferences (theme, layout, last opened file) | `%APPDATA%\MindAttic\<app>\settings.json` | yes | Follows the user across machines; not a secret. |
| Per-machine caches and data (SQL data, evidence files, large blobs) | `%LOCALAPPDATA%\MindAttic\<app>\` | no | Big, machine-specific, not worth roaming. |

## Deployment

### Azure App Service

In the Azure portal, open Configuration, then Application settings, and add:

| Name | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__LLM__claude__model` | `claude-sonnet-4-6` |
| `MindAttic__Vault__Brokers__alpaca-paper__apiKey` | `PK...` |
| `MindAttic__Vault__Brokers__alpaca-paper__secret` | `S...` |

App Service injects them as environment variables; `AddEnvironmentVariables()` converts the separators and the values flow into Vault unchanged. No code change from the local-dev wiring.

To use an App Service Key Vault reference, set the Application Setting value to:

```text
@Microsoft.KeyVault(SecretUri=https://my-vault.vault.azure.net/secrets/MindAttic--Vault--LLM--claude--apiKey)
```

App Service resolves the reference and surfaces the secret as a plain environment variable. Vault never knows Key Vault is involved.

### Azure Container Apps, AKS, or anywhere with Key Vault

To talk to Key Vault directly (not on App Service, or you want secrets to refresh without a restart):

```csharp
// Add the Azure SDK packages your host needs:
//   Azure.Extensions.AspNetCore.Configuration.Secrets
//   Azure.Identity

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddEnvironmentVariables()
    .AddAzureKeyVault(
        new Uri("https://my-vault.vault.azure.net"),
        new DefaultAzureCredential());

builder.Services.AddMindAtticVault(builder.Configuration);
```

Name secrets with `--` as the section separator, for example `MindAttic--Vault--LLM--claude--apiKey`. There is no custom code in Vault for this; the [LLM health dashboard](#llm-health-dashboard) in this repo is a working example of the pattern.

### Rotating a secret

- Dev: edit `%APPDATA%\MindAttic\LLM\providers.json` or call `LlmCredentialStore.Default.SetKey("claude", "new-key")`. Nothing ranks above it locally, so the new value takes effect immediately.
- Prod (App Service): edit the Application Setting in the portal and restart the app slot.
- Prod (Key Vault): create a new secret version. App Service Key Vault references re-resolve on restart; direct `AddAzureKeyVault(...)` calls reload on the cadence you configured.

## API

### Package contents

```text
MindAttic.Vault
├── Configuration/
│   ├── VaultConfigurationKeys                # Schema constants ("MindAttic:Vault:LLM" etc.)
│   ├── MindAtticConfigurationSource          # IConfigurationSource over %APPDATA%\MindAttic\*
│   ├── MindAtticConfigurationProvider        # The provider impl (internal)
│   └── ConfigurationBuilderExtensions        # builder.AddMindAtticVaultFiles()
├── Credentials/
│   ├── ICredentialStore                      # The contract (read + write)
│   ├── IRotatingKeyStore                     # Optional key-pool capability (CredentialPoolEntry)
│   ├── CredentialStore                       # Generic 3-tier file store
│   ├── LlmCredentialStore                    # File store at %APPDATA%\MindAttic\LLM
│   ├── BrokerCredentialStore                 # File store at %APPDATA%\MindAttic\Brokers
│   ├── FtpCredentialStore                    # File store at %APPDATA%\MindAttic\Ftp (flat, single record)
│   ├── TokenStore                            # Single-secret bucket (GitHub, USPS, ...)
│   ├── ConfigurationCredentialStore          # IConfiguration-backed read view (cloud-native)
│   ├── CompositeCredentialStore              # Chains stores; first non-null wins
│   ├── AppScopedCredentialStore              # Namespaces provider ids under "{appId}-"
│   ├── LlmCredentialResolver                 # Composite(Config -> File) for LLM
│   └── BrokerCredentialResolver              # Composite(Config -> File) for Brokers
├── DependencyInjection/
│   └── ServiceCollectionExtensions           # AddMindAtticVault() / AddMindAtticVault(IConfiguration)
├── Paths/
│   ├── VaultPaths                            # %APPDATA%\MindAttic + %LOCALAPPDATA%\MindAttic helpers
│   └── EnvironmentOverlay                    # Apply/ApplyAll for env-var overlays
├── Resolution/
│   └── KeyResolver                           # Chained resolver builder
└── Settings/
    └── JsonSettingsStore<T>                  # Generic Load/Save/Update for per-app JSON config
```

This section covers the shape of the surface; the XML docs are the line-level reference.

### VaultConfigurationKeys

Namespace `MindAttic.Vault.Configuration`. Schema constants; use these instead of hard-coded strings.

```csharp
VaultConfigurationKeys.RootSection;          // "MindAttic"
VaultConfigurationKeys.VaultSection;         // "MindAttic:Vault"
VaultConfigurationKeys.LlmSection;           // "MindAttic:Vault:LLM"
VaultConfigurationKeys.BrokersSection;       // "MindAttic:Vault:Brokers"
VaultConfigurationKeys.TokensSection;        // "MindAttic:Vault:Tokens"
VaultConfigurationKeys.SubtitlesSection;     // "MindAttic:Vault:Subtitles"
VaultConfigurationKeys.NotificationsSection; // "MindAttic:Vault:Notifications"
VaultConfigurationKeys.AudioStoreSection;    // "MindAttic:Vault:AudioStore"
```

`ProviderSection(bucketSection, providerId)` and `ProviderApiKeyPath(bucketSection, providerId)` build the colon-delimited path to a provider's section or its `apiKey` leaf (for example `MindAttic:Vault:LLM:claude:apiKey`). Both throw `ArgumentException` on null or whitespace.

### MindAtticConfigurationSource

Namespace `MindAttic.Vault.Configuration`. An `IConfigurationSource` that adapts the APPDATA bucket files into the standard schema:

```csharp
builder.Configuration.AddMindAtticVaultFiles(opt =>
{
    opt.Buckets        = new[] { "LLM", "Brokers", "Tokens" };  // optional narrow/override
    opt.RoamingRoot    = "/some/test/path";                     // optional override (tests)
    opt.ReloadOnChange = true;                                  // file watching
});
```

### LlmCredentialResolver and BrokerCredentialResolver

Namespace `MindAttic.Vault.Credentials`. Cloud-native composites; inject these from new code.

```csharp
public class MyService(LlmCredentialResolver llm)
{
    public string? Claude => llm.GetKey("claude");
}
```

Reads walk `IConfiguration`, then the file fallback, then null. Writes go to the file fallback only.

### LlmCredentialStore, BrokerCredentialStore and FtpCredentialStore

Namespace `MindAttic.Vault.Credentials`. File-only stores under `%APPDATA%\MindAttic\<bucket>\`. `LlmCredentialStore` and `BrokerCredentialStore` replace the legacy `MindAttic.Legion.MindAtticCredentialStore` and `IdiotProof.Engine.Settings.BrokerCredentialStore`. `FtpCredentialStore` is a single flat record at `Ftp\ftp.json`, deliberately excluded from `IConfiguration` projection.

All three implement `ICredentialStore`: `GetKey` and `SetKey`, `LoadAll` and `LoadAllRaw`, `ListProviders`, `SaveAllRaw` and `SaveRaw`, plus a `Default` singleton whose directory can be overridden for tests with `MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS` or `MINDATTIC_FTP_CREDENTIALS`.

### ConfigurationCredentialStore

Namespace `MindAttic.Vault.Credentials`. A read-only `ICredentialStore` over a fixed configuration section:

```csharp
ConfigurationCredentialStore.ForLlm(builder.Configuration);     // MindAttic:Vault:LLM
ConfigurationCredentialStore.ForBrokers(builder.Configuration); // MindAttic:Vault:Brokers
new ConfigurationCredentialStore(cfg, "MyApp:Custom:Bucket");   // arbitrary path
```

Every write method (`SetKey`, `SaveAllRaw`, `SaveRaw`) throws `NotSupportedException`. This is the type that enforces read-only production.

### CompositeCredentialStore

Namespace `MindAttic.Vault.Credentials`. Chains any number of stores. Reads walk in order; writes target the first writable store. Both resolvers are subclasses with two preset stores. It also implements `IRotatingKeyStore`, reading pools from the first store that has one and writing pools to the writable store.

### AppScopedCredentialStore

Namespace `MindAttic.Vault.Credentials`. The bring-your-own-key pattern every LLM-consuming MindAttic app shares: each app keeps its own key for a provider, so testing a key in one app never changes what another app resolves, and falls back to the shared id when it has none. Compose it in front of the shared store:

```csharp
var keys = new CompositeCredentialStore(
    new AppScopedCredentialStore("automata", LlmCredentialStore.Default),
    LlmCredentialStore.Default); // or LlmCredentialResolver for cloud-native reads

keys.GetKey("claude");            // "automata-claude" if set, else the shared "claude"
keys.SetKey("claude", "sk-...");  // always writes "automata-claude", never the shared entry
```

`LoadAll`, `ListProviders` and `LoadAllRaw` on the scoped instance surface only that app's own prefixed entries (prefix stripped). `SaveAllRaw` is a read-modify-write that replaces only those entries, leaving every other app's entries in the same file untouched. It replaces the one-off provider-id helpers each consumer used to hand-roll.

### IRotatingKeyStore

Namespace `MindAttic.Vault.Credentials`. An optional capability for stores that hold more than one key per provider. `GetKeys(providerId)` returns `CredentialPoolEntry(Key, Label)` records in priority order; `SetKeys(providerId, keys)` replaces the pool while preserving the provider's other fields. The first entry is always mirrored to the plain `apiKey` field, and a provider with exactly one key never gains an `apiKeys` array on disk. Implemented by `CredentialStore`, `CompositeCredentialStore` and `AppScopedCredentialStore`; read-only stores do not implement it.

### TokenStore

Namespace `MindAttic.Vault.Credentials`. A single-secret bucket for tokens that need no provider, key and secret triplet:

```csharp
var github = TokenStore.ForBucket("Tokens").Get("github");
TokenStore.ForBucket("Tokens").Set("github", "ghp_...");
TokenStore.ForBucket("Tokens").Remove("github");
```

### JsonSettingsStore

Namespace `MindAttic.Vault.Settings`. Per-app JSON settings, roaming under `%APPDATA%\MindAttic\<app>\settings.json` by default:

```csharp
var store = JsonSettingsStore<MySettings>.ForApp("MyApp");
var s = store.Load();
store.Save(s);
store.Update(s => s.Theme = "dark");

// For non-roaming local data (caches, evidence files, sql data):
JsonSettingsStore<MyData>.ForLocalApp("MyApp");
```

Writes are atomic (`.tmp` plus `File.Replace` with a `.bak` retained) and serialized under a per-instance `SemaphoreSlim`, so concurrent `Save` and `Update` calls in one process never tear the file. `LoadAsync`, `SaveAsync` and `UpdateAsync` mirror the sync API with `CancellationToken` support. Reads degrade to `new T()` on a missing or malformed file; a settings load never throws.

### VaultPaths

Namespace `MindAttic.Vault.Paths`.

```csharp
VaultPaths.RoamingRoot;                  // %APPDATA%\MindAttic
VaultPaths.LocalRoot;                    // %LOCALAPPDATA%\MindAttic
VaultPaths.RoamingBucket("LLM");         // %APPDATA%\MindAttic\LLM
VaultPaths.LocalApp("Prose");            // %LOCALAPPDATA%\MindAttic\Prose
VaultPaths.Ensure(path);                 // mkdir -p
```

Override either root for tests with `MINDATTIC_VAULT_ROAMING_ROOT` or `MINDATTIC_VAULT_LOCAL_ROOT`. On non-Windows hosts the same properties resolve through the standard `Environment.SpecialFolder` lookup (for example `~/.config/MindAttic`), and root resolution never throws ([VLT-LAW-7](docs/BIBLE.md#VLT-LAW-7)).

### EnvironmentOverlay

Namespace `MindAttic.Vault.Paths`.

```csharp
EnvironmentOverlay.Apply("MY_KEY", v => settings.Key = v);
EnvironmentOverlay.ApplyAll(new (string, Action<string>)[]
{
    ("CLAUDE_API_KEY",   v => s.ClaudeApiKey = v),
    ("ALPACA_KEY_ID",    v => s.AlpacaKeyId  = v),
});
```

### KeyResolver

Namespace `MindAttic.Vault.Resolution`.

```csharp
var resolver = KeyResolver
    .From(KeyResolver.Explicit("claude", explicitKey))                 // DI override
    .Then(KeyResolver.FromConfiguration(cfg, VaultConfigurationKeys.LlmSection))
    .Then(KeyResolver.EnvByConvention())                                // CLAUDE_API_KEY
    .Then(KeyResolver.FromStore(LlmCredentialStore.Default));          // file fallback

var key = resolver.Resolve("claude");
```

### ServiceCollectionExtensions

Namespace `MindAttic.Vault.DependencyInjection`.

```csharp
services.AddMindAtticVault();                          // file-only stores (console/desktop, no IConfiguration)
services.AddMindAtticVault(builder.Configuration);     // cloud-native: Composite(Config -> File)
services.AddVaultAppSettings<MySettings>("MyApp");     // roaming JsonSettingsStore<T>
```

`AddMindAtticVault()` registers the file-backed `LlmCredentialStore` and `BrokerCredentialStore` singletons and an `ICredentialStore` defaulting to LLM. `AddMindAtticVault(IConfiguration)` also registers the two resolvers. Inject the resolvers from new code, and the concrete stores only when legacy code needs the literal file path.

## How sibling repos consume Vault

Fifteen MindAttic repos reference the package: Automata, IdiotProof, JobHunt, MediaButler, MindAttic.Authentication, MindAttic.Deploy, MindAttic.Ideas, MindAttic.Launcher, MindAttic.Legion, MindAttic.Psst, OpenCredentials, Prose, TaxRateCollector, ThinkTank and Tutor. Two patterns cover most of them.

File-only credential lookup, for a console host with no DI. MindAttic.Launcher pushes an agent CLI's API key into a child process's environment just before launch, treating a missing entry as a no-op:

```csharp
using MindAttic.Vault.Credentials;

public static class ProviderCredentials
{
    public static void Apply(ProcessStartInfo psi, string providerKey, ICredentialStore? store = null)
    {
        var key = (store ?? LlmCredentialStore.Default).GetKey(providerKey);
        if (string.IsNullOrWhiteSpace(key)) return;
        psi.Environment["GEMINI_API_KEY"] = key; // (per-provider mapping in the real file)
    }
}
```

Per-app settings through `JsonSettingsStore<T>`. The launcher's settings land at `%APPDATA%\MindAttic\MindAttic.Launcher\settings.json`, seeded once from a legacy file if the Vault-managed file does not exist yet:

```csharp
public sealed class SettingsStore
{
    public const string AppBucket = "MindAttic.Launcher";
    private readonly JsonSettingsStore<AppSettings> store;

    public SettingsStore() : this(JsonSettingsStore<AppSettings>.ForApp(AppBucket), DefaultLegacySettingsPath) { }

    public AppSettings Load()
    {
        if (!store.Exists()) SeedFromLegacyIfPresent();
        return store.Load();
    }

    public void Save(AppSettings settings) => store.Save(settings);
    public AppSettings Update(Action<AppSettings> mutate) => store.Update(mutate);
}
```

Reach for the resolvers instead when the host is a web or worker app with a real `IConfiguration`.

### Consumers

Workspace projects that reference the package today (pinned version in brackets): Automata (5.0.0), Tutor (5.0.0), JobHunt (5.0.0), MindAttic.Legion (5.0.0), IdiotProof (4.0.0), MediaButler (4.0.0), Prose (4.0.0), TaxRateCollector (4.0.0), ThinkTank (4.0.0), MindAttic.Ideas (3.0.0), MindAttic.Deploy (2.0.0), MindAttic.Authentication (1.0.0), MindAttic.Launcher (1.0.0), MindAttic.Psst (1.0.0) and OpenCredentials (1.0.0). MindAttic.Mobile references the project source directly.

## LLM health dashboard

`MindAttic.Vault.Dashboard` is a standalone Blazor Server app (`net10.0`) that answers a question Vault itself cannot: are the LLM keys still good? A key can be revoked, run out of quota, or point at a deprecated model. The dashboard probes every keyed provider on a schedule and renders a traffic-light health view.

It is in progress (see [RFC 0001](docs/rfc/0001-llm-health-dashboard.md) and Epic D in the [user stories](docs/USER_STORIES.md)). It is not in `MindAttic.Vault.slnx`, has no dedicated test project, is not built by the normal `dotnet build` and `dotnet test` commands, and is never part of the published package.

- Dependencies: `MindAttic.Vault 1.0.0`, `MindAttic.Legion 22.0.0` (probing, diagnosis, live model discovery), `Azure.Identity` and `Azure.Extensions.AspNetCore.Configuration.Secrets`. It is the only place in this repo the Azure packages appear.
- Credentials: reads `AddMindAtticVaultFiles()` and environment variables, and adds Key Vault through managed identity when `MindAttic:Vault:KeyVaultUri` is set.
- Statuses: `Unknown`, `Healthy` (green), `Degraded` (amber: authenticated but drifted, or self-healed this sweep), `Down` (red: unreachable, key rejected, quota, or deprecated model).
- Options (`Monitor` config section): sweep `Interval` (default hourly), `ProbeTimeout` (default 30 seconds), `TrustedProviders` (`claude`, `openai`, `gemini`, `deepseek`), `MonitorAllKeyed`, `SelfHealModels`, and webhook or email alert targets.
- Services: `LlmHealthMonitor` probes, `HealthMonitorStore` holds the latest snapshot per provider, `MonitorBackgroundService` drives the sweep, `SelfHealer` repoints deprecated model ids, `AlertDispatcher` sends notifications on state change.
- UI: a verdict banner ("Trusted panel is ONLINE and queryable" or "DEGRADED") with a manual re-check button, then card grids for the trusted panel and all other keyed providers.

```csharp
builder.Configuration
    .AddMindAtticVaultFiles()      // %APPDATA%\MindAttic canonical files (dev)
    .AddEnvironmentVariables();    // App Service Application Settings

var keyVaultUri = builder.Configuration["MindAttic:Vault:KeyVaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());

MindAtticCredentialStore.UseConfiguration(builder.Configuration); // hands config to Legion's probes
```

Run it locally:

```powershell
cd MindAttic.Vault.Dashboard
dotnet run
# https://localhost:7242 or http://localhost:5242 (see Properties/launchSettings.json)
```

Leave `MindAttic:Vault:KeyVaultUri` empty in `appsettings.json` to use the local LLM bucket file; set it to a Key Vault URI to probe through managed identity.

## Project layout

```text
MindAttic.Vault/                     (repo root)
├── MindAttic.Vault/                 The published library (net9.0;net10.0, PackageId MindAttic.Vault)
├── MindAttic.Vault.Tests/           NUnit 4 suite (net10.0), one fixture per public type
│   └── TempDirectory.cs             Self-cleaning temp dir for file-store tests
├── MindAttic.Vault.Dashboard/       Blazor Server LLM health monitor (not in the solution, not packed)
│   ├── Components/                  Razor pages and layout (Home.razor is the dashboard)
│   └── Services/                    Monitor, store, background service, self-healer, alerts, models
├── MindAttic.Vault.slnx             Solution: MindAttic.Vault + MindAttic.Vault.Tests only
├── docs/                            Codex documentation (BIBLE, AMENDMENTS, USER_STORIES, rfc, digest)
├── tools/
│   ├── codex.ps1                    Codex CLI: doctor (validate docs) and digest (regenerate)
│   └── build-readme.ps1             Regenerates README.htm from this file
├── nuget.config                     Package sources: local family feed + nuget.org
├── package.json, index.htm          Node README-to-HTML renderer; index.htm is not deployed and is unrelated to the package
└── LICENSE                          MIT
```

## Testing

The NUnit suite covers every public type, including argument validation, malformed-input handling, atomic writes and the full cloud-native flow:

- `VaultPaths`: environment override, bucket and app combine, `Ensure`, defaults, constants.
- `EnvironmentOverlay`: apply, skip-empty, bulk apply, null tolerance.
- `CredentialStore`: three-tier precedence, malformed JSON, atomic write with `.bak`, sibling field preservation, constructor guards.
- `LlmCredentialStore`: type inference (anthropic, google, bearer), model and maxTokens preservation, malformed-file recovery.
- `BrokerCredentialStore`: full record I/O, partial-rotate preservation, alpaca type inference, wrong-type-field defence.
- `FtpCredentialStore`: flat-record I/O, legacy field-name compatibility, `TryGetJson()` shape, environment override.
- `TokenStore`: read, write, remove, case insensitivity, atomic swap, malformed and empty files.
- `JsonSettingsStore<T>`: round-trip, defaults on malformed input, `Update` semantics, factories, custom JSON options.
- `KeyResolver`: chaining, surviving a throwing step, every step builder, normalisation, custom suffixes.
- `MindAtticConfigurationSource` and provider: file to `IConfiguration` projection, custom buckets, scalar coercion, arrays, `ReloadOnChange`, malformed input, root fallback.
- `ConfigurationCredentialStore`: read-only contract, schema mapping, raw payload reconstruction.
- `CompositeCredentialStore`, `AppScopedCredentialStore` and key rotation: priority, write targeting, list union, prefix scoping, key pools.
- `ServiceCollectionExtensions`, `ConfigurationBuilderExtensions` and `VaultConfigurationKeys`: registration, fluent returns, every constant locked down.
- `CloudNativeIntegrationTests`: in-memory configuration plus temp file source plus environment overlay, through DI.

```powershell
dotnet test MindAttic.Vault.slnx
```

No real `%APPDATA%` is touched: every test redirects through environment variables (`MINDATTIC_VAULT_ROAMING_ROOT`, `MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS`, `MINDATTIC_FTP_CREDENTIALS`) or temp directories.

`LiveKeyValidationTests` (including `TrustedPanel_EveryKeyAuthenticatesLive`) hits real provider APIs with live keys, so it is marked `[Explicit]` and skipped by a normal `dotnet test`. It proves nothing offline.

Vault is a class library with no UI, so there is no browser end-to-end suite; consumer projects exercise the credential surface through their own UI tests.

## Building

```powershell
# Build both target frameworks
dotnet build MindAttic.Vault.slnx

# Run the full NUnit suite (must be green before packing)
dotnet test MindAttic.Vault.slnx

# Pack to the local NuGet feed configured in nuget.config
dotnet pack MindAttic.Vault\MindAttic.Vault.csproj -c Release -o C:\LocalNuGet

# Validate the docs/ Codex canon
powershell -File tools\codex.ps1 doctor

# Regenerate docs/BIBLE.digest.md after editing the bible
powershell -File tools\codex.ps1 digest
```

- Target frameworks: `net9.0` and `net10.0`, multi-targeted so consumers on either get a matching build.
- Dependencies: `Microsoft.Extensions.Configuration`, `Configuration.Abstractions`, `Configuration.Binder`, `DependencyInjection.Abstractions`, `Logging.Abstractions` and `Options`, all pinned at 9.0.0 for cross-framework compatibility.
- Versioning: whole-number, major-only (`4.0.0` to `5.0.0`, never `5.1.0`). The csproj `<Version>` is authoritative over any prose ([VLT-§4](docs/BIBLE.md#VLT-§4)).
- Release: bump the version whenever the public surface changes, keep `dotnet test` green, pack, then update each consumer's package reference when that consumer next needs it.
- nuget.org currently lists only early 0.x versions of the package; current versions are packed to the local family feed.

## Limitations

- The current package is not yet published to nuget.org; consumers outside the MindAttic workspace must pack it from source.
- Production writes are intentionally unsupported; the composite resolvers send writes to the file fallback, which should be locked down or absent in containers.
- There is no separate Azure package. Direct Key Vault access is one line of upstream wiring with `Azure.Extensions.AspNetCore.Configuration.Secrets`.
- Non-Azure clouds work through any `IConfiguration` provider (AWS Secrets Manager, GCP Secret Manager community providers), but only the Azure paths are exercised in this repo.
- The LLM health dashboard is in progress and not a supported package.

## Glossary

- Bucket: a credential category whose folder under `%APPDATA%\MindAttic\` matches the last segment of its config section (`LLM`, `Brokers`, `Tokens`, `Subtitles`, `Notifications`, `AudioStore`, `Ftp`).
- Provider: one keyed entry inside a bucket, such as `claude` in `LLM` or `alpaca-paper` in `Brokers`.
- Credential: the resolved secret value for a provider.
- Source: anything the chain reads from, an `IConfigurationSource` or a store passed through DI.
- Store: a concrete `ICredentialStore`, file-backed, configuration-backed (read-only) or chained.
- Resolver: a composite preset that chains configuration in front of a file store; what new DI-based code should inject.
- Setting: a non-secret per-app preference persisted by `JsonSettingsStore<T>`.
- Roaming versus local: `%APPDATA%` follows the Windows user across machines; `%LOCALAPPDATA%` stays on the current machine.
- Trusted panel: the dashboard's gating provider set whose combined health decides its overall verdict.

The authoritative glossary and domain model are in [docs/BIBLE.md](docs/BIBLE.md).

## Documentation

This repo follows the MindAttic Codex documentation standard: a fact lives in exactly one layer, and other layers link to it by stable ID.

- [docs/BIBLE.md](docs/BIBLE.md): what Vault is and is not, architecture, the Laws (VLT-LAW ids), verified state, glossary.
- [docs/AMENDMENTS.md](docs/AMENDMENTS.md): pending decisions not yet folded into the bible (normally empty).
- [User stories](docs/USER_STORIES.md): each completed one citing the NUnit test that proves it.
- [docs/rfc](docs/rfc): design notes for in-flight work, currently the dashboard RFC.
- [docs/BIBLE.digest.md](docs/BIBLE.digest.md): generated by `tools/codex.ps1 digest`; never hand-edit.
- [AGENTS.md](AGENTS.md): instructions for coding agents working in this repo.

After touching anything in docs, run `powershell -File tools\codex.ps1 doctor` and keep it exiting 0.

## License

MIT. See [LICENSE](LICENSE).

Part of [MindAttic](https://mindattic.com) — see more projects at [github.com/mindattic](https://github.com/mindattic). Related: [MindAttic.Legion](https://github.com/mindattic/MindAttic.Legion), [MindAttic.Launcher](https://github.com/mindattic/MindAttic.Launcher), [MindAttic.Psst](https://github.com/mindattic/MindAttic.Psst).
