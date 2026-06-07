---
codex: 1
project: MindAttic.Vault
code: VLT
layer: bible
status: living
updated: 2026-06-07
---

# MindAttic.Vault — Project Bible
> Single source of truth for what MindAttic.Vault IS, is NOT, and the rules that keep it coherent.
> README.md says how to build/run and consume the package; this says how to think about the system.

## 1. The one sentence {#VLT-§1}
MindAttic.Vault is a cloud-native credentials-and-settings library for .NET that gives every
MindAttic host **one `IConfiguration`-backed pipeline** for API keys, broker tokens, and per-app
preferences — with the `%APPDATA%\MindAttic` file store as the single local source of truth and
Azure App Service Application Settings / Key Vault as the production source, with no code change
between environments.

## 2. The product promise {#VLT-§2}
- **One schema, every source.** Secrets are defined once under `MindAttic:Vault` and resolve
  identically from the local APPDATA store, environment variables, App Service Application
  Settings, and Azure Key Vault.
- **Cloud-native by default, zero Azure SDK in the core.** Vault reads through `IConfiguration`;
  callers wire `AddAzureKeyVault(...)` (or AWS/GCP equivalents) upstream and Vault picks the
  values up — no vendor lock-in in the package itself.
- **Backward-compatible with `%APPDATA%`.** Legacy `providers.json` keyrings keep working,
  surfaced as a first-class `IConfigurationSource`, so existing dev installs cut over with
  zero risk.
- **Read-only in production, writable on the laptop.** Configuration-backed stores throw on
  writes; settings UIs land in the file-backed fallback only.
- **Settings stay roaming, secrets stay cloud-native.** Per-app preferences roam via `%APPDATA%`;
  secrets follow the .NET cloud-native convention and live in `IConfiguration`.

## 3. What it is NOT {#VLT-§3}
- **NOT a secrets manager / vault server.** It does not store, encrypt, or serve secrets over a
  network. It is a *resolution and projection* layer over sources the host already owns.
- **NOT an Azure SDK wrapper.** The core package has zero Azure-only dependencies. Key Vault is
  reached by registering `AddAzureKeyVault(...)` upstream of Vault, not by Vault calling Azure.
- **NOT a User-Secrets replacement that ranks above disk.** User Secrets is retired family-wide
  (see [VLT-LAW-3](#VLT-LAW-3)); the writable APPDATA store is the single local home so a stale
  CLI value can never mask a freshly-rotated key.
- **NOT a runtime secret writer in production.** `ConfigurationCredentialStore` throws on writes;
  production deploys never mutate secrets at runtime.
- **NOT a UI.** It is a class library with no DOM. The companion `MindAttic.Vault.Dashboard`
  (see [VLT-§7](#VLT-§7)) is a separate, in-flight app, not part of the published package.

## 4. Architecture canon {#VLT-§4}

```
                       consumer host (Program.cs)
                                 |
            builder.Configuration.AddMindAtticVaultFiles()  ... AddAzureKeyVault()
                                 |
                         IConfiguration  (composed source chain)
                                 |
        services.AddMindAtticVault(IConfiguration)   <-- DependencyInjection
                                 |
        +------------------------+-------------------------+
        |                        |                         |
  LlmCredentialResolver   BrokerCredentialResolver   JsonSettingsStore<T>
   (Composite)               (Composite)              (per-app settings)
        |                        |
   [ConfigurationCredentialStore]  -> reads IConfiguration  (cloud-native, read-only)
   [LlmCredentialStore/BrokerCredentialStore] -> %APPDATA%\MindAttic\<Bucket>\providers.json (writable)
                                 ^
        MindAtticConfigurationSource projects those same bucket files INTO IConfiguration
```

Read order for any `GetKey`: explicit DI registration → `IConfiguration` (Key Vault → env vars →
appsettings → `AddMindAtticVaultFiles`) → file fallback → null. First non-empty trimmed value
short-circuits.

### 4.1 Projects
- **`MindAttic.Vault`** — the published library (`net9.0;net10.0`, `<Version>1.0.0</Version>`,
  `PackageId=MindAttic.Vault`). The only thing in the NuGet artifact.
- **`MindAttic.Vault.Tests`** — NUnit suite (`net10.0`), `InternalsVisibleTo` target. Not packable.
- **`MindAttic.Vault.Dashboard`** — Blazor LLM-health-monitor app (`net10.0`, Sdk.Web), references
  the local Vault project + `MindAttic.Legion 3.0.0`. **In-flight on branch
  `feat/llm-health-dashboard`; NOT in `MindAttic.Vault.slnx`; NOT part of the package.**
  See [VLT-§7](#VLT-§7) and [RFC 0001](rfc/0001-llm-health-dashboard.md).

### 4.2 Domain model (NOUNS)
- **Bucket** — a credential category whose folder name equals its config section
  (`MindAttic:Vault:<Bucket>`): `LLM`, `Brokers`, `Tokens`, `Subtitles`, `Notifications`,
  `AudioStore`. Cataloged in [VLT-§9](#VLT-§9).
- **Provider** — a keyed entry inside a bucket (e.g. `claude`, `alpaca-paper`) holding a typed
  credential triplet/record.
- **Credential** — the resolved secret value (apiKey / secret / token) for a provider.
- **Source** — an `IConfigurationSource` or store the resolution chain walks (APPDATA file,
  env vars, appsettings, Key Vault, explicit DI).
- **Setting** — a per-app, non-secret preference object persisted as `settings.json` (roaming) or
  under `%LOCALAPPDATA%` (per-machine).

### 4.3 Key services (VERBS)
- **`MindAtticConfigurationSource` / `…Provider`** (`Configuration/`) — *project* APPDATA bucket
  files into `IConfiguration` under the standard schema; flatten objects/arrays/scalars; optional
  `ReloadOnChange`.
- **`ConfigurationBuilderExtensions.AddMindAtticVaultFiles()`** (`Configuration/`) — register the
  source on an `IConfigurationBuilder`.
- **`LlmCredentialStore` / `BrokerCredentialStore` / `CredentialStore` / `TokenStore`**
  (`Credentials/`) — *read/write* the file-backed APPDATA stores (3-tier precedence, atomic
  write with `.bak`, type inference).
- **`ConfigurationCredentialStore`** (`Credentials/`) — *read* a fixed config section; throws on write.
- **`CompositeCredentialStore`** + **`LlmCredentialResolver` / `BrokerCredentialResolver`**
  (`Credentials/`) — *chain* stores (config → file); writes target the file fallback.
- **`KeyResolver`** (`Resolution/`) — *compose* an explicit resolution chain for non-DI code.
- **`ServiceCollectionExtensions.AddMindAtticVault(...)` / `AddVaultAppSettings<T>(...)`**
  (`DependencyInjection/`) — *register* the resolvers/stores in DI.
- **`VaultPaths` / `EnvironmentOverlay`** (`Paths/`) — *compute* APPDATA/LOCALAPPDATA paths and
  *overlay* env vars onto settings.
- **`JsonSettingsStore<T>`** (`Settings/`) — *load/save/update* per-app JSON settings.

## 5. The Laws {#VLT-§5}
This bible **inherits the org-wide House Rules** at
[`../../MindAttic.HouseRules.md`](../../MindAttic.HouseRules.md) by reference — they are not restated
here. The directly load-bearing ones for Vault: whole-number versioning
([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)), credentials-through-Vault
([HOUSE-LAW-3](../../MindAttic.HouseRules.md#HOUSE-LAW-3)), provider-agnostic LLMs via Legion
([HOUSE-LAW-4](../../MindAttic.HouseRules.md#HOUSE-LAW-4)), and verified-not-asserted done
([HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8)).

Project-specific laws below:

### {#VLT-LAW-1} VLT-LAW-1 — One schema, every source
Every source surfaces the identical shape under `MindAttic:Vault`. Section names are constants in
`VaultConfigurationKeys` — never hard-code the strings. The same wiring resolves keys on a laptop,
on App Service, and via Key Vault with no code change.

### {#VLT-LAW-2} VLT-LAW-2 — Folder name == config section
The single on-disk invariant: a bucket's folder under `%APPDATA%\MindAttic\` **equals** its config
section's final segment (`MindAttic:Vault:<Bucket>`). Each file is a faithful image of its config
subtree. Never split one credential across two stores.

### {#VLT-LAW-3} VLT-LAW-3 — APPDATA is the single local source of truth; User Secrets is retired
The writable `%APPDATA%\MindAttic\<Bucket>\` store is the one local home for every credential. Do
**not** add `AddUserSecrets(...)` or `<UserSecretsId>` to any MindAttic project — User Secrets
ranked above the writable store and could silently mask a freshly-rotated key. Production stays
env vars / App Service Application Settings / Key Vault. (Sharpens
[HOUSE-LAW-3](../../MindAttic.HouseRules.md#HOUSE-LAW-3).)

### {#VLT-LAW-4} VLT-LAW-4 — Read-only in production
`ConfigurationCredentialStore` throws `NotSupportedException` on writes. Composite resolvers route
writes to the file fallback only — appropriate for a dev laptop, never for a production deploy.

### {#VLT-LAW-5} VLT-LAW-5 — No Azure SDK in the core package
The published `MindAttic.Vault` has zero Azure-only dependencies. Key Vault / AWS / GCP are reached
by registering their `IConfigurationSource` upstream of `AddMindAtticVault(...)`. Azure packages
appear only in the separate Dashboard app, never in the library.

### {#VLT-LAW-6} VLT-LAW-6 — Atomic writes, never touch real %APPDATA% in tests
File stores write atomically (temp + swap, `.bak` retained) and tolerate malformed/empty input by
falling back to defaults. Tests redirect every path via env vars
(`MINDATTIC_VAULT_ROAMING_ROOT`, `MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS`) or
temp directories — no test ever reads or writes the developer's real `%APPDATA%`.

## 6. Verified state {#VLT-§6}
Evidence captured 2026-06-07 on branch `feat/llm-health-dashboard`.

- **Build:** `dotnet build MindAttic.Vault.slnx -c Debug` → **exit 0, clean** (library builds for
  both `net9.0` and `net10.0`). ✅
- **Tests:** `dotnet test MindAttic.Vault.slnx` → **Failed: 0, Passed: 241, Total: 241** (exit 0).
  `TrustedPanel_EveryKeyAuthenticatesLive` is a live-network test skipped at runtime via
  `OneTimeSetUp`; the NUnit runner counts it within the 241 total and the suite exits clean. ✅
- **Coverage surface (proven):** every public type has NUnit coverage — `VaultPaths`,
  `EnvironmentOverlay`, `CredentialStore`, `LlmCredentialStore`, `BrokerCredentialStore`,
  `TokenStore`, `JsonSettingsStore<T>`, `KeyResolver`, `MindAtticConfigurationSource/Provider`,
  `ConfigurationCredentialStore`, `CompositeCredentialStore`, `ConfigurationBuilderExtensions`,
  `VaultConfigurationKeys`, `ServiceCollectionExtensions`, `Llm/BrokerCredentialResolver`, plus a
  `CloudNativeIntegrationTests` end-to-end fixture. ✅ (See [USER_STORIES](USER_STORIES.md).)
- **Versioning:** `MindAttic.Vault.csproj` `<Version>1.0.0</Version>` — whole-number compliant
  ([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)). README reconciled to `1.0.0` (VLT-US-X1
  resolved). ✅
- **Dashboard:** present in the working tree, references `MindAttic.Legion 3.0.0`, **not built by
  the solution and not covered by the test suite** — its status is unproven here. ⬜
  (See [VLT-§7](#VLT-§7).)

## 7. Active frontier {#VLT-§7}
- **LLM Health Dashboard** (`feat/llm-health-dashboard`) — a Blazor app that probes every keyed
  LLM provider in the Vault on a schedule, renders traffic-light health, alerts on state change,
  and optionally self-heals deprecated-model pointers. Tracked in
  [RFC 0001](rfc/0001-llm-health-dashboard.md) and
  [Epic D](USER_STORIES.md#epic-d-llm-health-dashboard-frontier). Not yet in
  the solution or test tree.
- **nuget.org publish** — the README notes publish-to-nuget.org as the pending release step
  (VLT-US-X2 in [USER_STORIES](USER_STORIES.md#priority-backlog)).

## 8. Quality bar {#VLT-§8}
A change is **done** ([HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8)) only when:
1. `dotnet build MindAttic.Vault.slnx` is clean for **both** `net9.0` and `net10.0`.
2. `dotnet test MindAttic.Vault.slnx` is green (live-network tests may be skipped, never failing).
3. Any new public type ships XML doc comments (the package emits `MindAttic.Vault.xml`).
4. New credentials/buckets honor [VLT-LAW-2](#VLT-LAW-2) (folder == section) and have a store/
   source projection test that redirects `%APPDATA%` ([VLT-LAW-6](#VLT-LAW-6)).
5. The user story carrying the change is `✅` only with its verifying test named in
   [USER_STORIES](USER_STORIES.md).

## 9. Glossary {#VLT-§9}
- **Bucket** — credential category; folder == `MindAttic:Vault:<Bucket>`. Canonical set:
  - `LLM` — `providers.json`: `{ id: { type, apiKey, model, maxTokens } }`.
  - `Brokers` — `providers.json`: `{ id: { type, apiKey, secret, baseUrl } }`.
  - `Tokens` — `tokens.json`: flat `{ github: "...", "nuget-org": "..." }`.
  - `Subtitles` — `providers.json`: `{ OpenSubtitles: { user, password } }`.
  - `Notifications` — `providers.json`: `{ twilio:{...}, email:{...}, to, toEmail }`.
  - `AudioStore` — `providers.json`: `{ provider, container, connectionString }`.
- **Provider** — a keyed entry in a bucket (`claude`, `alpaca-paper`, …).
- **Resolver** — a `CompositeCredentialStore` chaining config → file (e.g. `LlmCredentialResolver`).
- **Source** — an `IConfigurationSource`/store in the read chain.
- **Roaming vs local** — roaming settings live in `%APPDATA%`; per-machine caches/data in
  `%LOCALAPPDATA%`.
- **Trusted panel** — (Dashboard) the gating provider set (`claude`, `openai`, `gemini`,
  `deepseek`) whose votes decide the overall confidence verdict.
