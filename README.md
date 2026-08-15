# MindAttic.Vault

> **One credential pipeline. Every .NET host.**
> A cloud-native credentials & settings library for .NET that gives every app one `IConfiguration`-backed pipeline for API keys, broker tokens, and per-app preferences — and keeps your legacy `%APPDATA%` keyrings working while you migrate.

Stop hand-rolling `Load()` / `Save()` / `OverlayFromEnvironment()` plumbing in every service. `MindAttic.Vault` collapses nine flavours of credential-loading code into one library and unifies the resolution chain so the same wiring runs on a developer laptop, on Azure App Service, in an Azure Container App, on AKS, or anywhere else .NET runs.

**Why MindAttic.Vault**

- **One schema, every source.** Define your secrets once under `MindAttic:Vault` — the local APPDATA store, environment variables, App Service Application Settings, and Azure Key Vault all resolve into the same shape with no code changes between environments.
- **Cloud-native by default, zero Azure SDK in the core.** Vault reads through `IConfiguration`, so you wire `AddAzureKeyVault(...)` (or AWS Secrets Manager, or GCP Secret Manager) upstream and Vault picks up the values automatically. No vendor lock-in.
- **Backward-compatible with `%APPDATA%`.** Legacy `providers.json` keyrings keep working — they're surfaced as a first-class `IConfigurationSource`, so the cutover is zero-risk for existing dev installs.
- **Read-only in production, writable on the laptop.** Configuration-backed stores throw on writes; production deploys never mutate secrets at runtime. Settings UIs land safely in the file-backed fallback.
- **Settings stay roaming, secrets stay cloud-native.** Per-app preferences (theme, layout, last-opened-file) keep following the user across machines via `%APPDATA%`; secrets follow the .NET cloud-native convention and live in `IConfiguration`.
- **Battle-tested.** 252 NUnit tests cover every public type — atomic writes, malformed-input recovery, source precedence, scalar coercion, and full cloud-native end-to-end DI flows.

| Status | **2.0.0** — Added `FtpCredentialStore` (`%APPDATA%\MindAttic\Ftp\ftp.json`) for MindAttic.Deploy/MindAttic.Bob. APPDATA is the single local source of truth (folder == `MindAttic:Vault:<Bucket>`). Packed to `C:\LocalNuGet`; **publish to nuget.org is the pending release step**. 252 NUnit tests green. All consumers stripped of `AddUserSecrets`/`<UserSecretsId>`. |
| --- | --- |
| Target frameworks | `net9.0` and `net10.0` (multi-targeted; consumers on either TFM get a matching build) |
| Dependencies (all pinned `9.0.0` for cross-TFM compatibility) | `Microsoft.Extensions.Configuration`, `Configuration.Abstractions`, `Configuration.Binder`, `DependencyInjection.Abstractions`, `Logging.Abstractions`, `Options` |
| Package | [`MindAttic.Vault`](https://github.com/mindattic/MindAttic.Vault) on the family's local NuGet feed (`C:\LocalNuGet`); nuget.org publish pending |

---

## Table of contents

1. [Why this exists](#why-this-exists)
2. [Design principles](#design-principles)
3. [What's in the package](#whats-in-the-package)
4. [Standard configuration schema](#standard-configuration-schema)
5. [Source precedence (read order)](#source-precedence-read-order)
6. [Quickstart — local dev](#quickstart--local-dev)
7. [Quickstart — Azure App Service](#quickstart--azure-app-service)
8. [Quickstart — Azure Container Apps / AKS / anywhere with Key Vault](#quickstart--azure-container-apps--aks--anywhere-with-key-vault)
9. [Reference — public types](#reference--public-types)
10. [Settings vs. credentials — where each lives](#settings-vs-credentials--where-each-lives)
11. [Repository layout](#repository-layout)
12. [MindAttic.Vault.Dashboard — LLM health monitor](#mindatticvaultdashboard--llm-health-monitor)
13. [How sibling repos consume Vault](#how-sibling-repos-consume-vault)
14. [Testing strategy](#testing-strategy)
15. [Build, test, and pack](#build-test-and-pack)
16. [Integration plans (per-project rollout)](#integration-plans-per-project-rollout)
17. [Contributing & release process](#contributing--release-process)
18. [Documentation map (Codex layers)](#documentation-map-codex-layers)
19. [Glossary](#glossary)
20. [FAQ](#faq)

---

## Why this exists

A pre-Vault audit of `D:\Projects\MindAttic` found:

- **5 implementations** of `Load()` reading a JSON settings file from disk.
- **2 separate** "credential store" classes (one for LLM keys in Legion, one for broker keys in IdiotProof) implementing the same 3-tier (`.key` → `providers.json` → `credentials.json`) resolution.
- **9 different** invocations of `Path.Combine(APPDATA, "MindAttic", ...)` reinventing the same path math.
- **1 hand-rolled** `OverlayFromEnvironment()` that was repeated as a *concept* in every app even when not as a method.

Adding a new MindAttic app today means copy-pasting 60–200 lines of credential plumbing. Vault collapses that into one library and makes the same code Azure-deployable.

## Design principles

1. **Cloud-native first.** The primary credential source is `IConfiguration`. The same `services.AddMindAtticVault(builder.Configuration)` call resolves keys from the local APPDATA store in dev, Azure App Service Application Settings in production, or Azure Key Vault directly — depending only on what the host has registered with `IConfigurationBuilder`.
2. **Backward compatible.** Existing developers with keys in `%APPDATA%\MindAttic\LLM\providers.json` lose nothing. The file source is exposed as a first-class `IConfigurationSource` so legacy keys flow into `IConfiguration` automatically.
3. **Settings stay roaming, secrets move into config.** Per-app preferences (theme, layout, last-opened-file) continue to live in `%APPDATA%\MindAttic\<app>\settings.json` because they should follow the user across machines. Secrets follow the .NET cloud-native convention and live in `IConfiguration`.
4. **Read-only in production.** `ConfigurationCredentialStore` doesn't write back to `IConfiguration`. Mutations from a settings UI land in the file-backed fallback; production deploys never write secrets at runtime.
5. **No Azure SDK in the core package.** The Azure path is "register `AddAzureKeyVault(...)` upstream and Vault reads from `IConfiguration`." Zero Azure-only dependencies in `MindAttic.Vault`. (The one place Azure packages *do* appear in this repo is the standalone `MindAttic.Vault.Dashboard` app — see [§12](#mindatticvaultdashboard--llm-health-monitor) — which is never part of the published package.)

## What's in the package

```
MindAttic.Vault
├── Configuration/
│   ├── VaultConfigurationKeys                # Schema constants ("MindAttic:Vault:LLM" etc.)
│   ├── MindAtticConfigurationSource          # IConfigurationSource over %APPDATA%\MindAttic\*
│   ├── MindAtticConfigurationProvider        # The provider impl (internal)
│   └── ConfigurationBuilderExtensions        # builder.AddMindAtticVaultFiles()
├── Credentials/
│   ├── ICredentialStore                      # The contract (read + write)
│   ├── CredentialStore                       # Generic 3-tier file store
│   ├── LlmCredentialStore                    # File store at %APPDATA%\MindAttic\LLM
│   ├── BrokerCredentialStore                 # File store at %APPDATA%\MindAttic\Brokers
│   ├── FtpCredentialStore                    # File store at %APPDATA%\MindAttic\Ftp (flat, single record)
│   ├── TokenStore                            # Single-secret bucket (GitHub, USPS, ...)
│   ├── ConfigurationCredentialStore          # IConfiguration-backed read view (cloud-native)
│   ├── CompositeCredentialStore              # Chains stores; first non-null wins
│   ├── LlmCredentialResolver                 # Composite(Config → File) for LLM
│   └── BrokerCredentialResolver              # Composite(Config → File) for Brokers
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

Every public type ships XML doc comments (`MindAttic.Vault.xml` is emitted at build time), so IntelliSense in a consuming project explains behaviour, edge cases, and exceptions inline — this README covers the *shape* of the surface; the XML docs are the line-level reference.

## Standard configuration schema

Every source — `appsettings.json`, the local APPDATA store, env vars, App Service Application Settings, Azure Key Vault — surfaces the same shape under `MindAttic:Vault`:

```jsonc
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
| `appsettings.json` | The nested object above | Use `appsettings.Development.json` for non-secret dev overrides; never check secrets into git. |
| **Local dev (APPDATA)** | `%APPDATA%\MindAttic\LLM\providers.json` (folder == section) | **The single local source of truth.** Surfaced through `IConfiguration` via `AddMindAtticVaultFiles()`. Edit the file directly or use the writable store API (`LlmCredentialStore.SetKey`). |
| **Env vars** | `MindAttic__Vault__LLM__claude__apiKey=sk-ant-...` | Standard `__` → `:` translation. App Service Application Settings inject as env vars. |
| **Azure Key Vault** | Secret named `MindAttic--Vault--LLM--claude--apiKey` | Standard `--` → `:` translation by the default `KeyVaultSecretManager`. |
| **App Service Key Vault references** | App Setting value `@Microsoft.KeyVault(SecretUri=...)` | App Service resolves the reference into a plain env var before the app sees it — Vault picks it up automatically. |

### Canonical bucket convention (single local source of truth)

User Secrets is **retired**. It duplicated the writable APPDATA store and — because
`AddUserSecrets` ranks *above* `AddMindAtticVaultFiles` — a stale `dotnet user-secrets`
value could silently mask a freshly-rotated key on disk. The APPDATA store is now the
one local home for every credential; do **not** add `AddUserSecrets(...)` or
`<UserSecretsId>` to MindAttic projects. Production stays env vars / Key Vault.

The on-disk layout follows one invariant — **folder name == config section ==
`MindAttic:Vault:<Bucket>`**, and each file is a faithful image of its config subtree:

| Bucket (`%APPDATA%\MindAttic\<Bucket>\`) | File | Shape |
| --- | --- | --- |
| `LLM` | `providers.json` | `{ id: { type, apiKey, model, maxTokens } }` |
| `Brokers` | `providers.json` | `{ id: { type, apiKey, secret, baseUrl } }` |
| `Tokens` | `tokens.json` | `{ github: "...", "nuget-org": "..." }` (flat) |
| `Subtitles` | `providers.json` | `{ OpenSubtitles: { user, password } }` |
| `Notifications` | `providers.json` | `{ twilio:{...}, email:{...}, to:"...", toEmail:"..." }` |
| `AudioStore` | `providers.json` | `{ provider, container, connectionString }` |
| `Ftp` | `ftp.json` | `{ host, port, user, password, secure, servername }` (flat, single record) |

`MindAtticConfigurationSource` scans every bucket above **except `Ftp`** by default and
flattens each file (nested objects, arrays, and top-level scalars) into `IConfiguration`,
so the same keys resolve whether they came from disk, env vars, or Key Vault. `Ftp` is a
deliberate exception — it's a deploy-time credential for MindAttic.Deploy (and read
directly by MindAttic.Bob), never something an Azure App Service/Key Vault needs to
surface, so it stays a plain file-only store (`FtpCredentialStore`) with no
`IConfiguration`/cloud-native resolver path. Pass `Buckets` explicitly to include it if a
future consumer needs otherwise.

## Source precedence (read order)

When a Program.cs follows the recommended wiring, here's the order Vault walks for `GetKey("claude")`:

```
1.  Explicit DI registration              (e.g. services.AddSingleton(myMockedStore))
2.  IConfiguration:                       (whichever is highest-priority among:)
      a. AddAzureKeyVault(...)            ← prod, when you wire it directly
      b. AddEnvironmentVariables()        ← App Service, containers, CI
      c. AddJsonFile("appsettings.json")  ← non-secret defaults / public config
      d. AddMindAtticVaultFiles()         ← %APPDATA%\MindAttic (single local source of truth)
3.  LlmCredentialStore (file fallback)    ← writable; settings UI lands here
4.  return null
```

Any non-null trimmed value short-circuits the chain. `KeyResolver` exposes the same primitives so non-DI code paths can compose the chain manually.

## Quickstart — local dev

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
    public string? Claude       => llm.GetKey("claude");
    public string? AlpacaPaper  => brokers.GetKey("alpaca-paper");
}
```

Set a secret once and every MindAttic project sees it — write the canonical APPDATA
bucket file (folder == section). Edit it directly, or use the writable store API:

```csharp
LlmCredentialStore.Default.SetKey("claude", "sk-ant-...");           // LLM\providers.json
BrokerCredentialStore.Default.SetBrokerCreds("alpaca-paper",
    new BrokerCredentialStore.BrokerCreds("PK...", "S...", null));   // Brokers\providers.json
TokenStore.ForBucket("Tokens").Set("github", "ghp_...");             // Tokens\tokens.json
FtpCredentialStore.Default.Set(new FtpCredentialStore.FtpCreds(
    "ftp.example.com", 21, "user@example.com", "pw", true,
    "prod.example.net", null));                                      // Ftp\ftp.json
```

`FtpCredentialStore.Default.TryGetJson()` hands back the exact flat JSON blob
MindAttic.Deploy's `MINDATTIC_FTP_JSON` env var expects — a direct drop-in, no
reshaping needed at the call site.

Equivalently, `%APPDATA%\MindAttic\LLM\providers.json`:

```jsonc
{ "claude": { "type": "anthropic", "apiKey": "sk-ant-..." } }
```

## Quickstart — Azure App Service

In the Azure portal → **Configuration** → **Application settings**, add:

| Name | Value |
| --- | --- |
| `MindAttic__Vault__LLM__claude__apiKey` | `sk-ant-...` |
| `MindAttic__Vault__LLM__claude__model` | `claude-sonnet-4-6` |
| `MindAttic__Vault__Brokers__alpaca-paper__apiKey` | `PK...` |
| `MindAttic__Vault__Brokers__alpaca-paper__secret` | `S...` |

App Service injects them as env vars; `AddEnvironmentVariables()` converts `__` to `:` and the values flow into Vault unchanged. **No code change vs. the local-dev wiring above** — drop the User Secrets line in production and you're done.

### Using App Service Key Vault references

Set the Application Setting value to:

```
@Microsoft.KeyVault(SecretUri=https://my-vault.vault.azure.net/secrets/MindAttic--Vault--LLM--claude--apiKey)
```

App Service resolves the reference and surfaces the secret as a plain env var. Vault still works unchanged — it never knows Key Vault is involved.

## Quickstart — Azure Container Apps / AKS / anywhere with Key Vault

If you want to talk to Key Vault directly (e.g. you're not on App Service, or you want secrets to refresh without restart):

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

Name secrets in Key Vault using `--` as the section separator: `MindAttic--Vault--LLM--claude--apiKey`. The default `KeyVaultSecretManager` translates `--` to `:` so they land at the right spot in `IConfiguration`. **No custom code in Vault.** (`MindAttic.Vault.Dashboard` in this repo is a real, working example of this pattern — see [§12](#mindatticvaultdashboard--llm-health-monitor).)

## Reference — public types

Each major class has full XML doc comments; the highlights:

### `VaultConfigurationKeys` (`MindAttic.Vault.Configuration`)

Schema constants — use these instead of hard-coding strings.

```csharp
VaultConfigurationKeys.RootSection;       // "MindAttic"
VaultConfigurationKeys.VaultSection;      // "MindAttic:Vault"
VaultConfigurationKeys.LlmSection;           // "MindAttic:Vault:LLM"
VaultConfigurationKeys.BrokersSection;       // "MindAttic:Vault:Brokers"
VaultConfigurationKeys.TokensSection;        // "MindAttic:Vault:Tokens"
VaultConfigurationKeys.SubtitlesSection;     // "MindAttic:Vault:Subtitles"
VaultConfigurationKeys.NotificationsSection; // "MindAttic:Vault:Notifications"
VaultConfigurationKeys.AudioStoreSection;    // "MindAttic:Vault:AudioStore"
```

`ProviderSection(bucketSection, providerId)` and `ProviderApiKeyPath(bucketSection, providerId)` build the colon-delimited paths to a specific provider's section / `apiKey` leaf (e.g. `MindAttic:Vault:LLM:claude:apiKey`) — both argument-validated (throw `ArgumentException` on null/whitespace).

### `MindAtticConfigurationSource` (`MindAttic.Vault.Configuration`)

`IConfigurationSource` that adapts `%APPDATA%\MindAttic\<bucket>\providers.json` into the standard schema:

```csharp
builder.Configuration.AddMindAtticVaultFiles(opt =>
{
    opt.Buckets        = new[] { "LLM", "Brokers", "Tokens" };  // optional narrow/override
    opt.RoamingRoot    = "/some/test/path";                     // optional override (tests)
    opt.ReloadOnChange = true;                                  // file watching
});
```

### `LlmCredentialResolver` / `BrokerCredentialResolver` (`MindAttic.Vault.Credentials`)

Cloud-native composites. Inject these from new code:

```csharp
public class MyService(LlmCredentialResolver llm)
{
    public string? Claude => llm.GetKey("claude");
}
```

Reads walk: `IConfiguration` → file fallback → null. Writes go to the file fallback only.

### `LlmCredentialStore` / `BrokerCredentialStore` / `FtpCredentialStore` (`MindAttic.Vault.Credentials`)

File-only stores at `%APPDATA%\MindAttic\<bucket>\`. `LlmCredentialStore`/`BrokerCredentialStore` are drop-in replacements for the legacy `MindAttic.Legion.MindAtticCredentialStore` and `IdiotProof.Engine.Settings.BrokerCredentialStore`. `FtpCredentialStore` is the newest addition (2.0.0) — a single flat record at `Ftp\ftp.json`, deliberately excluded from `IConfiguration` projection (see the bucket table above). All three implement the same `ICredentialStore` contract: `GetKey` / `SetKey`, `LoadAll` / `LoadAllRaw`, `ListProviders`, `SaveAllRaw` / `SaveRaw`, plus a `Default` singleton that honors an env-var directory override for tests (`MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS`, `MINDATTIC_FTP_CREDENTIALS`). Still injectable for code that genuinely wants the file path (rare).

### `ConfigurationCredentialStore` (`MindAttic.Vault.Credentials`)

Read-only `ICredentialStore` over a fixed configuration section. Construct via:

```csharp
ConfigurationCredentialStore.ForLlm(builder.Configuration);     // MindAttic:Vault:LLM
ConfigurationCredentialStore.ForBrokers(builder.Configuration); // MindAttic:Vault:Brokers
new ConfigurationCredentialStore(cfg, "MyApp:Custom:Bucket");   // arbitrary path
```

Every write method (`SetKey`, `SaveAllRaw`, `SaveRaw`) throws `NotSupportedException` — this is the type that enforces "read-only in production."

### `CompositeCredentialStore` (`MindAttic.Vault.Credentials`)

Chains any number of stores. Reads walk in order; writes target the first writable store. Both `LlmCredentialResolver` and `BrokerCredentialResolver` are subclasses of this with two preset stores.

### `TokenStore` (`MindAttic.Vault.Credentials`)

Single-secret bucket for tokens that don't need provider/key/secret triplets:

```csharp
var github = TokenStore.ForBucket("Tokens").Get("github");
TokenStore.ForBucket("Tokens").Set("github", "ghp_...");
TokenStore.ForBucket("Tokens").Remove("github");
```

### `JsonSettingsStore<T>` (`MindAttic.Vault.Settings`)

Per-app JSON settings. Roaming under `%APPDATA%\MindAttic\<app>\settings.json` by default:

```csharp
var store = JsonSettingsStore<MySettings>.ForApp("MyApp");
var s = store.Load();
store.Save(s);
store.Update(s => s.Theme = "dark");

// For non-roaming local data (caches, evidence files, sql data):
JsonSettingsStore<MyData>.ForLocalApp("MyApp");
```

Writes are atomic (`.tmp` + `File.Replace` with a `.bak` retained) and serialized under a per-instance `SemaphoreSlim`, so concurrent `Save`/`Update` calls from the same process never tear the file. `LoadAsync` / `SaveAsync` / `UpdateAsync` mirror the sync API with `CancellationToken` support. Reads always degrade to `new T()` on a missing or malformed file — a settings load never throws or crashes a host.

Register from DI:

```csharp
builder.Services.AddVaultAppSettings<MySettings>("MyApp");
```

### `VaultPaths` (`MindAttic.Vault.Paths`)

Path math — replaces `Path.Combine(Environment.GetFolderPath(...), "MindAttic", ...)` everywhere.

```csharp
VaultPaths.RoamingRoot;                  // %APPDATA%\MindAttic
VaultPaths.LocalRoot;                    // %LOCALAPPDATA%\MindAttic
VaultPaths.RoamingBucket("LLM");         // %APPDATA%\MindAttic\LLM
VaultPaths.LocalApp("Prose");    // %LOCALAPPDATA%\MindAttic\Prose
VaultPaths.Ensure(path);                 // mkdir -p
```

Override either root for tests with `MINDATTIC_VAULT_ROAMING_ROOT` / `MINDATTIC_VAULT_LOCAL_ROOT`. On non-Windows hosts the same properties resolve to `~/.config/MindAttic` and `~/.local/share/MindAttic/<app>` via the standard `Environment.SpecialFolder` lookup.

### `EnvironmentOverlay` (`MindAttic.Vault.Paths`)

```csharp
EnvironmentOverlay.Apply("MY_KEY", v => settings.Key = v);
EnvironmentOverlay.ApplyAll(new (string, Action<string>)[]
{
    ("CLAUDE_API_KEY",   v => s.ClaudeApiKey = v),
    ("ALPACA_KEY_ID",    v => s.AlpacaKeyId  = v),
});
```

### `KeyResolver` (`MindAttic.Vault.Resolution`)

```csharp
var resolver = KeyResolver
    .From(KeyResolver.Explicit("claude", explicitKey))                 // DI override
    .Then(KeyResolver.FromConfiguration(cfg, VaultConfigurationKeys.LlmSection))
    .Then(KeyResolver.EnvByConvention())                                // CLAUDE_API_KEY
    .Then(KeyResolver.FromStore(LlmCredentialStore.Default));          // file fallback

var key = resolver.Resolve("claude");
```

### `ServiceCollectionExtensions` (`MindAttic.Vault.DependencyInjection`)

```csharp
services.AddMindAtticVault();                          // file-only stores (console/desktop, no IConfiguration)
services.AddMindAtticVault(builder.Configuration);      // cloud-native: Composite(Config → File)
services.AddVaultAppSettings<MySettings>("MyApp");      // roaming JsonSettingsStore<T>
```

`AddMindAtticVault()` registers the file-backed `LlmCredentialStore`/`BrokerCredentialStore` singletons and an `ICredentialStore` defaulting to LLM. `AddMindAtticVault(IConfiguration)` additionally registers `LlmCredentialResolver`/`BrokerCredentialResolver` (config-first, file-fallback composites) — inject the resolvers from new code, the concrete stores only when legacy code needs the literal file path.

## Settings vs. credentials — where each lives

| What | Where | Roaming? | Why |
| --- | --- | --- | --- |
| **API keys / secrets** (local dev) | `%APPDATA%\MindAttic\<Bucket>\` (folder == `MindAttic:Vault:<Bucket>`) | yes | Single local source of truth; surfaced through `IConfiguration` via `AddMindAtticVaultFiles()`. |
| **API keys / secrets** (prod) | `IConfiguration` (App Service Application Settings / Key Vault) | n/a | Cloud-native standard; never written by app code in prod. |
| **Per-app preferences** (theme, layout, "last opened file") | `%APPDATA%\MindAttic\<app>\settings.json` | yes | Follows user across machines; not a secret. |
| **Per-machine caches & data** (SQL data dir, evidence files, large blobs) | `%LOCALAPPDATA%\MindAttic\<app>\` | no | Big, machine-specific, not worth roaming. |

## Repository layout

This repo ships more than the NuGet package. Top level (tests, docs and node/tooling `bin`/`obj`/`node_modules` omitted):

```
MindAttic.Vault/                     (repo root)
├── MindAttic.Vault/                 The published library — see "What's in the package" above.
│   └── MindAttic.Vault.csproj       net9.0;net10.0, PackageId=MindAttic.Vault, <Version>2.0.0</Version>
├── MindAttic.Vault.Tests/           NUnit 4 suite (net10.0), InternalsVisibleTo target, not packable.
│   ├── *Tests.cs                    One fixture per public type (see "Testing strategy").
│   └── TempDirectory.cs             Test helper — a self-cleaning temp dir for file-store tests.
├── MindAttic.Vault.Dashboard/       Blazor Server LLM-health-monitor app — see §12. NOT in the
│   │                                 solution, NOT part of the published package.
│   ├── Components/                 Razor pages/layout (Home.razor is the dashboard UI).
│   └── Services/                   LlmHealthMonitor, HealthMonitorStore, MonitorBackgroundService,
│                                    SelfHealer, AlertDispatcher, HealthModels (records/enums).
├── MindAttic.Vault.slnx             Solution file — lists ONLY MindAttic.Vault + MindAttic.Vault.Tests.
├── IntegrationPlans/                 Historical, diff-level per-consumer rollout plans (§16).
├── docs/                             Codex documentation layers — see §18.
│   ├── BIBLE.md                     L0 — architecture, Laws, verified state, glossary.
│   ├── AMENDMENTS.md                 L1 — append-only change log; an amendment wins over the bible.
│   ├── USER_STORIES.md               L2 — test-cited user stories.
│   ├── BIBLE.digest.md               GENERATED by tools/codex.ps1 digest — never hand-edit.
│   └── rfc/0001-llm-health-dashboard.md  Design note for the Dashboard (§12).
├── tools/
│   └── codex.ps1                     Codex CLI: `doctor` (validate docs/) and `digest` (regenerate).
├── nuget.config                      Package sources: local family feed (C:\LocalNuGet) + nuget.org.
├── package.json / node_modules/      A SEPARATE toolchain (marked + highlight.js) that renders
│                                      index.htm — the mindattic.com marketing landing page. Unrelated
│                                      to the package or to README.htm; do not confuse the two.
├── index.htm                         Rendered landing page (own pipeline; not touched by README tooling).
├── README.md                          This file.
└── LICENSE                            MIT.
```

## MindAttic.Vault.Dashboard — LLM health monitor

`MindAttic.Vault.Dashboard` is a standalone Blazor Server app (`net10.0`, `Microsoft.NET.Sdk.Web`) that answers a question Vault itself can't: *are the LLM keys the family holds still good?* A key can be revoked, hit a quota, or point at a model id that's been deprecated upstream — Vault has no opinion on any of that, it only resolves what's on disk/in config. The Dashboard probes every keyed provider on a schedule and renders a traffic-light health view.

**Status:** in-flight (see [RFC 0001](docs/rfc/0001-llm-health-dashboard.md) and [Epic D](docs/USER_STORIES.md#epic-d-llm-health-dashboard-frontier)). It is **not** listed in `MindAttic.Vault.slnx`, has no dedicated test project, and is not built or tested by the normal `dotnet build`/`dotnet test` commands in this repo — build/run it directly from its own project file. It is never part of the published `MindAttic.Vault` NuGet package (that would violate the "no Azure SDK in the core" design principle).

**What it depends on** (per `MindAttic.Vault.Dashboard.csproj`): `MindAttic.Vault 1.0.0` (the published NuGet package — this app is a real consumer of Vault, not a fork of it), `MindAttic.Legion 22.0.0` (for probing/diagnosis/live model discovery), and `Azure.Identity` + `Azure.Extensions.AspNetCore.Configuration.Secrets` (direct Key Vault wiring — the one place in this repo those packages appear).

**How it resolves credentials** (`Program.cs`):

```csharp
builder.Configuration
    .AddMindAtticVaultFiles()      // %APPDATA%\MindAttic canonical files (dev)
    .AddEnvironmentVariables();    // App Service Application Settings

var keyVaultUri = builder.Configuration["MindAttic:Vault:KeyVaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());

MindAtticCredentialStore.UseConfiguration(builder.Configuration); // hands config to Legion's probes
```

This is the same wiring shown in [§8](#quickstart--azure-container-apps--aks--anywhere-with-key-vault), applied for real: locally it reads `%APPDATA%\MindAttic\LLM\providers.json`; in Azure App Service with `MindAttic:Vault:KeyVaultUri` set, it resolves straight from Key Vault via managed identity.

**Domain model** (`Services/HealthModels.cs`):
- **`ProviderStatus`** — `Unknown` / `Healthy` (green) / `Degraded` (amber — authenticated but drifted, or self-healed this sweep) / `Down` (red — unreachable, key rejected, quota, or deprecated model).
- **`ProviderSnapshot`** — an immutable point-in-time record per provider: status, diagnosis, HTTP code, latency, consecutive-failure count, uptime %, and an optional `SelfHealNote`.
- **`MonitorOptions`** (bound from the `Monitor` config section) — sweep `Interval` (default hourly), `ProbeTimeout` (default 30s), the `TrustedProviders` panel (`claude`, `openai`, `gemini`, `deepseek` by default), `MonitorAllKeyed` (probe every other keyed provider too, informational only), `SelfHealModels` (auto-repoint a deprecated model id), and `Webhooks[]` / `Email` alert targets.
- **`AlertEvent`** — raised when a provider's status changes between sweeps; consumed by `AlertDispatcher`.

**Services:** `LlmHealthMonitor` runs the actual probes; `HealthMonitorStore` holds the latest snapshot per provider and the overall "trusted panel healthy" verdict; `MonitorBackgroundService` is the `IHostedService` driving the scheduled sweep; `SelfHealer` repoints deprecated-model pointers; `AlertDispatcher` sends email/webhook notifications on state change.

**UI (`Home.razor`):** a verdict banner at the top ("Trusted panel is ONLINE & queryable" / "DEGRADED — action needed", green/red) with a manual "Re-check now" button, then two card grids — the trusted voting panel first, then all other keyed providers grouped as informational.

**Running it locally:**

```powershell
cd MindAttic.Vault.Dashboard
dotnet run
# https://localhost:7242 or http://localhost:5242 (see Properties/launchSettings.json)
```

Configuration lives in `appsettings.json` / `appsettings.Development.json`: leave `MindAttic:Vault:KeyVaultUri` empty to use the local `%APPDATA%\MindAttic\LLM\providers.json` file; set it to an Azure Key Vault URI to probe via managed identity in App Service.

**Trusted panel:** the live-network test `TrustedPanel_EveryKeyAuthenticatesLive` exists conceptually for this app but is skipped in CI (it needs real keys and network access) — it proves nothing offline and should never be cited as evidence the Dashboard epic is done. See [RFC 0001](docs/rfc/0001-llm-health-dashboard.md) for the full phased plan (add to the solution, add an offline-tested project, then promote the Epic D stories).

## How sibling repos consume Vault

At least 17 repos in the workspace reference the `MindAttic.Vault` package, including `MindAttic.Launcher`, `MindAttic.Legion`, `MindAttic.Authentication`, `MindAttic.Deploy.Cli`, `MindAttic.Mobile`, `MindAttic.Psst`, `MediaButler`, `IdiotProof.Engine`, `Prose.Core`/`Prose.LlmCli`, `TaxRateCollector.Infrastructure`, `ThinkTank.Core`, `Tutor.Core`, and `OpenCredentials.Shared` — a superset of the projects tracked in [`IntegrationPlans/`](IntegrationPlans/), which records the original, diff-level rollout for the first wave of consumers (§16).

A concrete example — `MindAttic.Launcher` consumes two different corners of Vault for two different jobs:

**File-only credential lookup** (`MindAttic.Launcher/Services/ProviderCredentials.cs`) — pushing an agent CLI's API key into a child process's environment right before launch, with a missing/blank Vault entry treated as a no-op (the CLI falls back to however it's already configured):

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

Note this consumer only needs the writable file store directly (`LlmCredentialStore.Default`) — it's a console launcher with no `IConfiguration`/DI host, so it skips the cloud-native resolver entirely and calls the file-backed `ICredentialStore` contract straight.

**Per-app settings via `JsonSettingsStore<T>`** (`MindAttic.Launcher/Services/SettingsStore.cs`) — wraps `JsonSettingsStore<AppSettings>.ForApp("MindAttic.Launcher")`, so the launcher's settings land at `%APPDATA%\MindAttic\MindAttic.Launcher\settings.json`, with a one-time seed from a legacy pre-Vault settings file if the Vault-managed file doesn't exist yet:

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

Both patterns — direct file-store injection for CLIs/console apps with no DI host, and `JsonSettingsStore<T>.ForApp(...)` for roaming per-app settings — are the two most common ways a MindAttic host takes a dependency on Vault without needing the full cloud-native `IConfiguration` chain. Reach for the resolvers (`LlmCredentialResolver`/`BrokerCredentialResolver`) instead when the host is a web/worker app with a real `IConfiguration` (see [§6](#quickstart--local-dev)–[§8](#quickstart--azure-container-apps--aks--anywhere-with-key-vault) and the Dashboard in [§12](#mindatticvaultdashboard--llm-health-monitor)).

## Testing strategy

**Unit & integration:** 252 NUnit tests covering every public type, including
argument validation, malformed-input handling, atomic-write behaviour, and the
full cloud-native end-to-end flow:

- `VaultPaths` — env override, bucket/app combine, `Ensure`, defaults, constants
- `EnvironmentOverlay` — apply, skip-empty, bulk apply, null-tolerance
- `CredentialStore` — 3-tier precedence, malformed JSON, atomic write + `.bak`, sibling field preservation, argument validation, constructor guards
- `LlmCredentialStore` — type inference (anthropic / google / bearer), model + maxTokens preservation, `Default` singleton, malformed-existing recovery
- `BrokerCredentialStore` — full record I/O, partial-rotate preservation, type inference (alpaca prefix), wrong-type-field defence, argument validation, `Default` singleton
- `FtpCredentialStore` — flat-record I/O, legacy `secrets/ftp.json` field-name compatibility (`servername`, `_rejectUnauthorized`), `TryGetJson()` shape for `MINDATTIC_FTP_JSON`, `Default` singleton, env var override, argument validation
- `TokenStore` — read/write/remove, case insensitivity, atomic swap (`.bak`), `ForBucket`, malformed/empty file handling, argument validation
- `JsonSettingsStore<T>` — round-trip, defaults on malformed, `Update` semantics, factories (`ForApp` / `ForLocalApp` / `ForBucket`), custom JSON options, argument validation
- `KeyResolver` — chain, throw-survive, every step builder (`Explicit` / `Env` / `EnvByConvention` / `FromStore` / `FromConfiguration`), normalisation, custom suffixes, argument validation
- `MindAtticConfigurationSource` / `…Provider` — file → IConfiguration projection, custom buckets, scalar coercion (bool/int/double), array projection, `ReloadOnChange` watcher hooks, malformed/empty/non-object resilience, `EffectiveRoot` fallback
- `ConfigurationCredentialStore` — read-only contract (`SetKey`, `SaveAllRaw`, `SaveRaw` all throw), schema mapping, raw payload reconstruction, scalar coercion
- `CompositeCredentialStore` — priority, write-targeting, list union, raw layering, throwing-inner-store survival, null-store filtering
- `ConfigurationBuilderExtensions` — argument validation, fluent return, `configure` callback semantics
- `VaultConfigurationKeys` — every constant locked down, every path-builder argument-validated
- `ServiceCollectionExtensions` — DI registration (file-only + cloud-native), `AddVaultAppSettings<T>` factory, fluent return, full argument validation
- `LlmCredentialResolver` / `BrokerCredentialResolver` — cloud-native end-to-end
- `CloudNativeIntegrationTests` — full flow: in-memory IConfiguration + temp file source + env-var overlay, in DI

Run them:

```powershell
dotnet test D:\Projects\MindAttic\MindAttic.Vault\MindAttic.Vault.slnx
```

Confirmed locally: `dotnet test MindAttic.Vault.slnx` → `Passed: 252, Failed: 0, Skipped: 0, Total: 252` (net10.0). The suite also builds and would run identically against `net9.0` — the two TFMs share the same test project.

**No real `%APPDATA%` is touched** — every test redirects via env vars (`MINDATTIC_VAULT_ROAMING_ROOT`, `MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS`, `MINDATTIC_FTP_CREDENTIALS`) or temp directories (see `TempDirectory.cs`).

**Documentation:** the package ships an XML documentation file (`MindAttic.Vault.xml`) so consumers see IntelliSense for every public type and member. `CS1591` (missing XML doc warning) is suppressed only for the few internal-but-public surfaces already covered by a type-level summary.

**About Cypress / browser E2E:** Vault is a class library with no UI surface. Cypress (or Playwright) doesn't apply here — there is no DOM to drive. Each *consumer* project (Tutor, ThinkTank, IdiotProof, …) has its own Cypress suite that exercises the credential surface through its own UI; those suites continue to work unchanged after the swap because Vault preserves the on-disk shape and resolution semantics. The integration plan for each consumer calls out which Cypress specs to re-run. The `CloudNativeIntegrationTests` fixture in this repo is the equivalent end-to-end coverage at the library level. `MindAttic.Vault.Dashboard` has no Cypress/Playwright coverage either — see the caveats in [§12](#mindatticvaultdashboard--llm-health-monitor).

## Build, test, and pack

```powershell
# Build both target frameworks
dotnet build MindAttic.Vault.slnx

# Run the full NUnit suite (must be green before packing)
dotnet test MindAttic.Vault.slnx

# Pack to the family's local NuGet feed (see nuget.config)
dotnet pack MindAttic.Vault\MindAttic.Vault.csproj -c Release -o C:\LocalNuGet

# Validate the docs/ Codex canon (front-matter, IDs, links, cited tests/paths, digest freshness)
powershell -File tools\codex.ps1 doctor

# Regenerate docs/BIBLE.digest.md after editing BIBLE.md §1/§3/§5/§9 or the latest amendment
powershell -File tools\codex.ps1 digest
```

**Versioning:** whole-number, major-only — `MindAttic.Vault.csproj`'s `<Version>` goes `1.0.0` → `2.0.0` → `3.0.0`, never `2.1.0`. The csproj is the single authoritative version source; if any prose elsewhere in the repo disagrees, the csproj wins (see [`VLT-A2`](docs/AMENDMENTS.md#vlt-a2--whole-number-versioning-csproj-is-authoritative-over-readme-prose-supersedes)).

**`MindAttic.Vault.Dashboard`** is not part of `MindAttic.Vault.slnx` and is not covered by `dotnet build`/`dotnet test` above — build and run it directly from its own project (`dotnet build` / `dotnet run` inside `MindAttic.Vault.Dashboard/`), per [§12](#mindatticvaultdashboard--llm-health-monitor).

## Integration plans (per-project rollout)

> **Historical.** These plans describe the original 0.2.0 rollout, which wired `AddUserSecrets` + `<UserSecretsId>` into each consumer. **0.3.0 retired User Secrets family-wide** — ignore the User-Secrets steps in the plans below; the current rule is the [canonical bucket convention](#canonical-bucket-convention-single-local-source-of-truth) (APPDATA only). The plans are kept as a record of the initial integration.

Every applicable consumer has now been integrated. Each project's diff-level plan ran in this order so each consumer could be verified in isolation:

| # | Project | Plan | Notes |
| --- | --- | --- | --- |
| ✅ 1 | MindAttic.Legion | [`MindAttic.Legion.md`](IntegrationPlans/MindAttic.Legion.md) | **DONE.** Legion 2.1.0 published to nuget.org (commit `fed2a19`). |
| ✅ 2 | FractionsOfACent | [`FractionsOfACent.md`](IntegrationPlans/FractionsOfACent.md) | **DONE.** GitHubTokenProvider in place; priority chain verified end-to-end (commit `4c593e5`). |
| ✅ 3 | ThinkTank | [`ThinkTank.md`](IntegrationPlans/ThinkTank.md) | **DONE.** SettingsServiceVaultOverlay layered on existing factory; 252 tests pass (commit `05bbb30`). |
| ✅ 4 | Tutor | [`Tutor.md`](IntegrationPlans/Tutor.md) | **DONE.** Forward-looking DI wiring only; 338 tests pass (commit `5b33913`). |
| ✅ 5 | IdiotProof | [`IdiotProof.md`](IntegrationPlans/IdiotProof.md) | **DONE.** Duplicate BrokerCredentialStore deleted; OverlayFromConfiguration added; 105 tests pass (commit `b1e7dcf`). |
| ✅ 6 | Prose | [`Prose.md`](IntegrationPlans/Prose.md) | **DONE.** ResolveApiKey now consults VaultConfiguration first; 21 settings tests pass (commit `18b9993`). |
| ✅ 7 | TaxRateCollector | [`TaxRateCollector.md`](IntegrationPlans/TaxRateCollector.md) | **DONE.** Static-field IConfiguration injection + Save() leak protection; 29 settings tests pass (commit `bcefece`). |
| ⚪ 8 | GridGame2026 | [`GridGame2026.md`](IntegrationPlans/GridGame2026.md) | Documented skip — Unity, no creds. |
| ✅ 9 | MindAttic.Deploy | [`MindAttic.Deploy.md`](IntegrationPlans/MindAttic.Deploy.md) | **DONE.** New `FtpCredentialStore`; `DeployRunner` bridges Vault → `MINDATTIC_FTP_JSON` for the Node pipeline. |

**Status:** all integrations applied. `MindAttic.Vault 2.0.0` is packed to `C:\LocalNuGet`; publish to nuget.org is the pending release step (see [Contributing & release process](#contributing--release-process)). Several more repos have since taken a dependency on the published package outside this historical table — see [§13](#how-sibling-repos-consume-vault) for the current, verified list.

Every plan ends with a **rollback** section.

## Contributing & release process

- Bump the `<Version>` in `MindAttic.Vault.csproj` whenever public surface changes — whole-number major bumps only (`1.0.0` → `2.0.0` → `3.0.0`), never a minor/patch bump.
- `dotnet test` must be green before packaging.
- `dotnet pack -c Release -o C:\LocalNuGet` publishes to the family's local NuGet feed.
- After a version bump, update each consumer's `<PackageReference Version=...>` lazily — only when that project's integration plan is being executed.
- If you touch `docs/BIBLE.md`, `docs/AMENDMENTS.md`, or `docs/USER_STORIES.md`, run `powershell -File tools/codex.ps1 doctor` afterward and make sure it exits 0 — see [§18](#documentation-map-codex-layers).

## Documentation map (Codex layers)

This repo follows the MindAttic **Codex** documentation standard: a fact lives in exactly one layer, and other layers link to it by stable ID rather than restating it. This README is the practical "how to build/run/consume" layer; the canon below is "how to think about the system":

| Layer | File | What it's for |
| --- | --- | --- |
| **L0 — Bible** | [`docs/BIBLE.md`](docs/BIBLE.md) | What Vault IS / is NOT, architecture, the Laws (`VLT-LAW-*`), verified state, glossary. Stable section IDs `{#VLT-§N}`. |
| **L1 — Amendments** | [`docs/AMENDMENTS.md`](docs/AMENDMENTS.md) | Append-only change log (`VLT-A<n>`). An amendment **wins** over the bible — never rewritten, only superseded. |
| **L2 — User stories** | [`docs/USER_STORIES.md`](docs/USER_STORIES.md) | Stories `VLT-US-<Epic><n>`; every `✅` cites the NUnit test that proves it. |
| **RFC** | [`docs/rfc/`](docs/rfc/) | Design notes for in-flight work (currently just [RFC 0001](docs/rfc/0001-llm-health-dashboard.md), the Dashboard). Graduates into L0 + L2 once shipped. |
| **Generated** | [`docs/BIBLE.digest.md`](docs/BIBLE.digest.md) | Produced by `tools/codex.ps1 digest`. **Never hand-edit** — regenerate it instead. |

Validate the whole canon any time with `powershell -File tools/codex.ps1 doctor` (checks front-matter, unique IDs, resolvable cross-refs, cited tests/paths, and digest freshness; must exit 0).

## Glossary

A short, practical glossary for reading this README and the Vault source. For the authoritative, longer-form version (including the full domain model), see [`docs/BIBLE.md` §9](docs/BIBLE.md#VLT-§9).

- **Bucket** — a credential category whose folder name under `%APPDATA%\MindAttic\` is identical to its config section's last segment (`LLM`, `Brokers`, `Tokens`, `Subtitles`, `Notifications`, `AudioStore`, `Ftp`).
- **Provider** — one keyed entry inside a bucket, e.g. `claude` inside `LLM`, or `alpaca-paper` inside `Brokers`.
- **Credential** — the resolved secret value for a provider (an `apiKey`, a broker `secret`, a bare token).
- **Source** — anything the resolution chain can read from: an `IConfigurationSource` (Key Vault, env vars, appsettings, the APPDATA file projection) or a store passed explicitly via DI.
- **Store** — a concrete `ICredentialStore` implementation: file-backed (`CredentialStore` and its `LlmCredentialStore`/`BrokerCredentialStore`/`FtpCredentialStore` specializations), configuration-backed (`ConfigurationCredentialStore`, read-only), or chained (`CompositeCredentialStore`).
- **Resolver** — a `CompositeCredentialStore` preset that chains configuration in front of a file store (`LlmCredentialResolver`, `BrokerCredentialResolver`) — what new DI-based code should inject.
- **Setting** — a non-secret, per-app preference persisted via `JsonSettingsStore<T>`, as opposed to a credential.
- **Roaming vs. local** — roaming data lives under `%APPDATA%` and follows the Windows user across machines; local data lives under `%LOCALAPPDATA%` and stays on the current machine (caches, evidence files, large blobs).
- **Trusted panel** — (Dashboard-specific) the gating provider set (`claude`, `openai`, `gemini`, `deepseek` by default) whose combined health decides the Dashboard's overall confidence verdict.

## FAQ

**Q. Where do local dev secrets live now that User Secrets is retired?**
In the canonical APPDATA bucket files (`%APPDATA%\MindAttic\<Bucket>\providers.json|tokens.json`, where the folder equals the `MindAttic:Vault:<Bucket>` section). `AddMindAtticVaultFiles()` surfaces them through `IConfiguration` automatically — it's the single local source of truth. Production secrets come from App Service Application Settings / Key Vault.

**Q. Can I write keys at runtime in production?**
You shouldn't. `ConfigurationCredentialStore` throws `NotSupportedException` on writes. The composite resolvers route writes to the file fallback, which is appropriate for a dev laptop but should be locked down (or unmounted) in containers.

**Q. What about non-Azure clouds?**
Anything that produces an `IConfiguration` works. AWS Secrets Manager and GCP Secret Manager both have community providers — register them upstream of `AddMindAtticVault(...)` and Vault picks the values up the same way.

**Q. Why didn't you ship `MindAttic.Vault.Azure`?**
Azure App Service Application Settings (with optional Key Vault references) cover ~95% of MindAttic's intended deployment targets and need zero Azure SDK. The remaining 5% (direct Key Vault SDK with Managed Identity) is one line of upstream wiring with the existing `Azure.Extensions.AspNetCore.Configuration.Secrets` package — not worth a separate Vault package. `MindAttic.Vault.Dashboard` (§12) is exactly that remaining 5%, built as its own app rather than a Vault companion package.

**Q. How do I rotate a secret?**
- Dev: edit the APPDATA bucket file (`%APPDATA%\MindAttic\LLM\providers.json`) or call `LlmCredentialStore.Default.SetKey("claude", "new-key")`. Because nothing ranks above it locally, the new value takes effect immediately — no stale store can mask it.
- Prod (App Service): edit the Application Setting in the portal; restart the app slot.
- Prod (Key Vault): create a new secret version. App Service Key Vault references re-resolve on app restart; direct `AddAzureKeyVault(...)` calls re-load on the cadence you configured.

**Q. Is `MindAttic.Vault.Dashboard` something I can depend on?**
Not yet as a package — it isn't published and isn't in the solution. It's a working example of Vault + Key Vault + Legion wired together in a real Blazor app; read its `Program.cs` for a concrete Azure Container Apps-style wiring, but treat its own feature set (health probing, alerting, self-healing) as in-flight per [RFC 0001](docs/rfc/0001-llm-health-dashboard.md).
