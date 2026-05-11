# MindAttic.Vault

> Universal credentials & settings library for the MindAttic family.
> Cloud-native first, dev-friendly always.

`MindAttic.Vault` is the one place every MindAttic application reads its credentials and per-app settings. It replaces the hand-rolled `Load() / Save() / OverlayFromEnvironment() / OverlayFromMindAtticCredentials()` code that was duplicated across nine projects and unifies the resolution chain so the same code works on a developer laptop, on Azure App Service, in an Azure Container App, or anywhere else .NET runs.

| Status | 0.2.0 — built, 88 tests green, packaged at `C:\LocalNuGet\MindAttic.Vault.0.2.0.nupkg`. **Not yet integrated** into any consumer; per-project plans live in [`IntegrationPlans/`](IntegrationPlans/). |
| --- | --- |
| Target framework | `net10.0` |
| Dependencies | `Microsoft.Extensions.Configuration.Abstractions`, `Configuration.Binder`, `DependencyInjection.Abstractions`, `Logging.Abstractions`, `Options` |

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
11. [Testing strategy](#testing-strategy)
12. [Integration plans (per-project rollout)](#integration-plans-per-project-rollout)
13. [Contributing & release process](#contributing--release-process)
14. [FAQ](#faq)

---

## Why this exists

A pre-Vault audit of `D:\Projects\MindAttic` found:

- **5 implementations** of `Load()` reading a JSON settings file from disk.
- **2 separate** "credential store" classes (one for LLM keys in Legion, one for broker keys in IdiotProof) implementing the same 3-tier (`.key` → `providers.json` → `credentials.json`) resolution.
- **9 different** invocations of `Path.Combine(APPDATA, "MindAttic", ...)` reinventing the same path math.
- **1 hand-rolled** `OverlayFromEnvironment()` that was repeated as a *concept* in every app even when not as a method.

Adding a new MindAttic app today means copy-pasting 60–200 lines of credential plumbing. Vault collapses that into one library and makes the same code Azure-deployable.

## Design principles

1. **Cloud-native first.** The primary credential source is `IConfiguration`. The same `services.AddMindAtticVault(builder.Configuration)` call resolves keys from User Secrets in dev, Azure App Service Application Settings in production, or Azure Key Vault directly — depending only on what the host has registered with `IConfigurationBuilder`.
2. **Backward compatible.** Existing developers with keys in `%APPDATA%\MindAttic\LLM\providers.json` lose nothing. The file source is exposed as a first-class `IConfigurationSource` so legacy keys flow into `IConfiguration` automatically.
3. **Settings stay roaming, secrets move into config.** Per-app preferences (theme, layout, last-opened-file) continue to live in `%APPDATA%\MindAttic\<app>\settings.json` because they should follow the user across machines. Secrets follow the .NET cloud-native convention and live in `IConfiguration`.
4. **Read-only in production.** `ConfigurationCredentialStore` doesn't write back to `IConfiguration`. Mutations from a settings UI land in the file-backed fallback; production deploys never write secrets at runtime.
5. **No Azure SDK in the core package.** The Azure path is "register `AddAzureKeyVault(...)` upstream and Vault reads from `IConfiguration`." Zero Azure-only dependencies in `MindAttic.Vault`.

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

## Standard configuration schema

Every cloud-native source — `appsettings.json`, User Secrets, env vars, App Service Application Settings, Azure Key Vault — surfaces the same shape under `MindAttic:Vault`:

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
| **User Secrets** (dev) | `dotnet user-secrets set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-..."` | Use the **shared User Secrets ID** (next section) for family-wide sharing. |
| **Env vars** | `MindAttic__Vault__LLM__claude__apiKey=sk-ant-...` | Standard `__` → `:` translation. App Service Application Settings inject as env vars. |
| **Azure Key Vault** | Secret named `MindAttic--Vault--LLM--claude--apiKey` | Standard `--` → `:` translation by the default `KeyVaultSecretManager`. |
| **App Service Key Vault references** | App Setting value `@Microsoft.KeyVault(SecretUri=...)` | App Service resolves the reference into a plain env var before the app sees it — Vault picks it up automatically. |
| **Legacy `%APPDATA%`** | `%APPDATA%\MindAttic\LLM\providers.json` | Surfaced through `IConfiguration` via `AddMindAtticVaultFiles()`. Preserves every existing dev install. |

### Shared User Secrets ID for family-wide dev sharing

Set the following in **every** MindAttic project's `.csproj`:

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>
```

That ID is exposed as `VaultConfigurationKeys.SharedUserSecretsId`. With it set, one `dotnet user-secrets set` command writes to a single shared `secrets.json` and every app sees the new key — same family-wide-sharing benefit you got from `%APPDATA%\MindAttic\LLM\providers.json` today, but the canonical .NET way.

> Want isolation per-project? Drop the shared ID and let `dotnet user-secrets init` mint a per-project GUID. You lose family sharing but gain blast-radius isolation. Pick whichever fits the project; the integration plans default to shared.

## Source precedence (read order)

When a Program.cs follows the recommended wiring, here's the order Vault walks for `GetKey("claude")`:

```
1.  Explicit DI registration              (e.g. services.AddSingleton(myMockedStore))
2.  IConfiguration:                       (whichever is highest-priority among:)
      a. AddAzureKeyVault(...)            ← prod, when you wire it directly
      b. AddEnvironmentVariables()        ← App Service, containers, CI
      c. AddUserSecrets<Program>()        ← dev laptop
      d. AddJsonFile("appsettings.json")  ← non-secret defaults / public config
      e. AddMindAtticVaultFiles()         ← legacy %APPDATA%\MindAttic
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
    .AddMindAtticVaultFiles()                 // %APPDATA%\MindAttic\... legacy keys
    .AddUserSecrets<Program>()                // dev secrets, family-wide via shared id
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

Set a secret once and every MindAttic project sees it:

```powershell
dotnet user-secrets set "MindAttic:Vault:LLM:claude:apiKey" "sk-ant-..."
dotnet user-secrets set "MindAttic:Vault:Brokers:alpaca-paper:apiKey" "PK..."
dotnet user-secrets set "MindAttic:Vault:Brokers:alpaca-paper:secret" "S..."
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
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .AddAzureKeyVault(
        new Uri("https://my-vault.vault.azure.net"),
        new DefaultAzureCredential());

builder.Services.AddMindAtticVault(builder.Configuration);
```

Name secrets in Key Vault using `--` as the section separator: `MindAttic--Vault--LLM--claude--apiKey`. The default `KeyVaultSecretManager` translates `--` to `:` so they land at the right spot in `IConfiguration`. **No custom code in Vault.**

## Reference — public types

Each major class has full XML doc comments; the highlights:

### `VaultConfigurationKeys` (`MindAttic.Vault.Configuration`)

Schema constants — use these instead of hard-coding strings.

```csharp
VaultConfigurationKeys.RootSection;       // "MindAttic"
VaultConfigurationKeys.VaultSection;      // "MindAttic:Vault"
VaultConfigurationKeys.LlmSection;        // "MindAttic:Vault:LLM"
VaultConfigurationKeys.BrokersSection;    // "MindAttic:Vault:Brokers"
VaultConfigurationKeys.TokensSection;     // "MindAttic:Vault:Tokens"
VaultConfigurationKeys.SharedUserSecretsId; // "mindattic-vault-shared"
```

### `MindAtticConfigurationSource` (`MindAttic.Vault.Configuration`)

`IConfigurationSource` that adapts `%APPDATA%\MindAttic\<bucket>\providers.json` into the standard schema:

```csharp
builder.Configuration.AddMindAtticVaultFiles(opt =>
{
    opt.Buckets        = new[] { "LLM", "Brokers", "GitHub" };  // optional override
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

### `LlmCredentialStore` / `BrokerCredentialStore` (`MindAttic.Vault.Credentials`)

File-only stores at `%APPDATA%\MindAttic\<bucket>\providers.json`. Drop-in replacements for the legacy `MindAttic.Legion.MindAtticCredentialStore` and `IdiotProof.Engine.Settings.BrokerCredentialStore`. Still injectable for code that genuinely wants the file path (rare).

### `ConfigurationCredentialStore` (`MindAttic.Vault.Credentials`)

Read-only `ICredentialStore` over a fixed configuration section. Construct via:

```csharp
ConfigurationCredentialStore.ForLlm(builder.Configuration);     // MindAttic:Vault:LLM
ConfigurationCredentialStore.ForBrokers(builder.Configuration); // MindAttic:Vault:Brokers
new ConfigurationCredentialStore(cfg, "MyApp:Custom:Bucket");   // arbitrary path
```

### `CompositeCredentialStore` (`MindAttic.Vault.Credentials`)

Chains any number of stores. Reads walk in order; writes target the first writable store. Both `LlmCredentialResolver` and `BrokerCredentialResolver` are subclasses of this with two preset stores.

### `TokenStore` (`MindAttic.Vault.Credentials`)

Single-secret bucket for tokens that don't need provider/key/secret triplets:

```csharp
var github = TokenStore.ForBucket("GitHub").Get("github");
TokenStore.ForBucket("GitHub").Set("github", "ghp_...");
TokenStore.ForBucket("GitHub").Remove("github");
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
VaultPaths.LocalApp("StreetSamurai");    // %LOCALAPPDATA%\MindAttic\StreetSamurai
VaultPaths.Ensure(path);                 // mkdir -p
```

Override either root for tests with `MINDATTIC_VAULT_ROAMING_ROOT` / `MINDATTIC_VAULT_LOCAL_ROOT`.

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

## Settings vs. credentials — where each lives

| What | Where | Roaming? | Why |
| --- | --- | --- | --- |
| **API keys / secrets** | `IConfiguration` (User Secrets / App Service / Key Vault) | n/a | Cloud-native standard; never written by app code in prod. |
| **Per-app preferences** (theme, layout, "last opened file") | `%APPDATA%\MindAttic\<app>\settings.json` | yes | Follows user across machines; not a secret. |
| **Per-machine caches & data** (SQL data dir, evidence files, large blobs) | `%LOCALAPPDATA%\MindAttic\<app>\` | no | Big, machine-specific, not worth roaming. |
| **Legacy LLM / broker keyrings** | `%APPDATA%\MindAttic\LLM\providers.json`, `%APPDATA%\MindAttic\Brokers\providers.json` | yes | Backward compat; still works, surfaced through `IConfiguration` via `AddMindAtticVaultFiles()`. |

## Testing strategy

**Unit & integration:** 223 NUnit tests covering every public type, including
argument validation, malformed-input handling, atomic-write behaviour, and the
full cloud-native end-to-end flow:

- `VaultPaths` — env override, bucket/app combine, `Ensure`, defaults, constants
- `EnvironmentOverlay` — apply, skip-empty, bulk apply, null-tolerance
- `CredentialStore` — 3-tier precedence, malformed JSON, atomic write + `.bak`, sibling field preservation, argument validation, constructor guards
- `LlmCredentialStore` — type inference (anthropic / google / bearer), model + maxTokens preservation, `Default` singleton, malformed-existing recovery
- `BrokerCredentialStore` — full record I/O, partial-rotate preservation, type inference (alpaca prefix), wrong-type-field defence, argument validation, `Default` singleton
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

**No real `%APPDATA%` is touched** — every test redirects via env vars (`MINDATTIC_VAULT_ROAMING_ROOT`, `MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS`) or temp directories.

**Documentation:** the package now ships an XML documentation file (`MindAttic.Vault.xml`) so consumers see IntelliSense for every public type and member.

**About Cypress / browser E2E:** Vault is a class library with no UI surface. Cypress (or Playwright) doesn't apply here — there is no DOM to drive. Each *consumer* project (Tutor, ThinkTank, IdiotProof, …) has its own Cypress suite that exercises the credential surface through its own UI; those suites continue to work unchanged after the swap because Vault preserves the on-disk shape and resolution semantics. The integration plan for each consumer calls out which Cypress specs to re-run. The `CloudNativeIntegrationTests` fixture in this repo is the equivalent end-to-end coverage at the library level.

## Integration plans (per-project rollout)

Per the user's instruction, **no consumer is integrated yet.** Each project gets its own diff-level plan; run them in this order so each consumer can be verified in isolation:

| # | Project | Plan | Notes |
| --- | --- | --- | --- |
| ✅ 1 | MindAttic.Legion | [`MindAttic.Legion.md`](IntegrationPlans/MindAttic.Legion.md) | **DONE.** Legion 2.1.0 published to nuget.org (commit `fed2a19`). |
| ✅ 2 | FractionsOfACent | [`FractionsOfACent.md`](IntegrationPlans/FractionsOfACent.md) | **DONE.** GitHubTokenProvider in place; priority chain verified end-to-end (commit `4c593e5`). |
| ✅ 3 | ThinkTank | [`ThinkTank.md`](IntegrationPlans/ThinkTank.md) | **DONE.** SettingsServiceVaultOverlay layered on existing factory; 252 tests pass (commit `05bbb30`). |
| ✅ 4 | Tutor | [`Tutor.md`](IntegrationPlans/Tutor.md) | **DONE.** Forward-looking DI wiring only; 338 tests pass (commit `5b33913`). |
| ✅ 5 | IdiotProof | [`IdiotProof.md`](IntegrationPlans/IdiotProof.md) | **DONE.** Duplicate BrokerCredentialStore deleted; OverlayFromConfiguration added; 105 tests pass (commit `b1e7dcf`). |
| ✅ 6 | StreetSamurai | [`StreetSamurai.md`](IntegrationPlans/StreetSamurai.md) | **DONE.** ResolveApiKey now consults VaultConfiguration first; 21 settings tests pass (commit `18b9993`). |
| ✅ 7 | TaxRateCollector | [`TaxRateCollector.md`](IntegrationPlans/TaxRateCollector.md) | **DONE.** Static-field IConfiguration injection + Save() leak protection; 29 settings tests pass (commit `bcefece`). |
| ⚪ 8 | GridGame2026 | [`GridGame2026.md`](IntegrationPlans/GridGame2026.md) | Documented skip — Unity, no creds. |

**Status:** all integrations applied. `MindAttic.Vault 0.2.0` and `MindAttic.Legion 2.1.0` are live on nuget.org so every consumer's GitHub Actions CI/CD now resolves the package without local-feed plumbing.

Every plan ends with a **rollback** section.

## Contributing & release process

- Bump the `<Version>` in `MindAttic.Vault.csproj` whenever public surface changes.
- `dotnet test` must be green before packaging.
- `dotnet pack -c Release -o C:\LocalNuGet` publishes to the family's local NuGet feed.
- After a version bump, update each consumer's `<PackageReference Version=...>` lazily — only when that project's integration plan is being executed.

## FAQ

**Q. Does the LLM/Broker file at `%APPDATA%` still work after the swap?**
Yes. `AddMindAtticVaultFiles()` keeps it as a configuration source so legacy keys flow into `IConfiguration` automatically. New keys should go into User Secrets / App Service Application Settings, but no migration is forced.

**Q. Can I write keys at runtime in production?**
You shouldn't. `ConfigurationCredentialStore` throws `NotSupportedException` on writes. The composite resolvers route writes to the file fallback, which is appropriate for a dev laptop but should be locked down (or unmounted) in containers.

**Q. What about non-Azure clouds?**
Anything that produces an `IConfiguration` works. AWS Secrets Manager and GCP Secret Manager both have community providers — register them upstream of `AddMindAtticVault(...)` and Vault picks the values up the same way.

**Q. Why didn't you ship `MindAttic.Vault.Azure`?**
Azure App Service Application Settings (with optional Key Vault references) cover ~95% of MindAttic's intended deployment targets and need zero Azure SDK. The remaining 5% (direct Key Vault SDK with Managed Identity) is one line of upstream wiring with the existing `Azure.Extensions.AspNetCore.Configuration.Secrets` package — not worth a separate Vault package. If a real production scenario emerges, we'll add a thin companion package then.

**Q. How do I rotate a secret?**
- Dev: `dotnet user-secrets set "MindAttic:Vault:LLM:claude:apiKey" "new-key"`.
- Prod (App Service): edit the Application Setting in the portal; restart the app slot.
- Prod (Key Vault): create a new secret version. App Service Key Vault references re-resolve on app restart; direct `AddAzureKeyVault(...)` calls re-load on the cadence you configured.
