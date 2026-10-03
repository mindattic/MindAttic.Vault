---
codex: 1
project: MindAttic.Vault
code: VLT
layer: bible
status: living
updated: 2026-10-03
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
- **`%APPDATA%` keyrings are first-class.** `providers.json` keyrings are surfaced as an
  `IConfigurationSource`, so a dev machine's files and a production host's settings share one
  schema.
- **Read-only in production, writable on the laptop.** Configuration-backed stores throw on
  writes; settings UIs land in the file-backed fallback only.
- **One keyring, many apps.** Each app can hold its own key for a provider and fall back to the
  shared default, without ever changing what another app resolves.
- **Runs anywhere.** Root resolution works on Windows, Linux, macOS, iOS and Android and never
  aborts host startup.
- **Settings stay roaming, secrets stay cloud-native.** Per-app preferences roam via `%APPDATA%`;
  secrets follow the .NET cloud-native convention and live in `IConfiguration`.

## 3. What it is NOT {#VLT-§3}
- **NOT a secrets manager / vault server.** It does not store, encrypt, or serve secrets over a
  network. It is a *resolution and projection* layer over sources the host already owns.
- **NOT an Azure SDK wrapper.** The core package has zero Azure-only dependencies. Key Vault is
  reached by registering `AddAzureKeyVault(...)` upstream of Vault, not by Vault calling Azure.
- **NOT a User Secrets source.** MindAttic projects do not use User Secrets (see
  [VLT-LAW-3](#VLT-LAW-3)); the writable APPDATA store is the single local home so a stale CLI
  value can never mask a freshly-rotated key.
- **NOT a runtime secret writer in production.** `ConfigurationCredentialStore` throws on writes;
  production deploys never mutate secrets at runtime.
- **NOT a UI.** It is a class library with no DOM. The companion `MindAttic.Vault.Dashboard`
  (see [VLT-§7](#VLT-§7)) is a separate, in-progress app, not part of the published package.

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

  per-app BYOK:  Composite( AppScopedCredentialStore("<app>", shared), shared )
```

Read order for any `GetKey`: explicit DI registration → `IConfiguration` (Key Vault → env vars →
appsettings → `AddMindAtticVaultFiles`) → file fallback → null. First non-empty trimmed value
short-circuits.

### 4.1 Projects
- **`MindAttic.Vault`** — the published library (`net9.0;net10.0`, `<Version>5.0.0</Version>`,
  `PackageId=MindAttic.Vault`). The only thing in the NuGet artifact. The csproj `<Version>` is the
  authoritative version; prose never overrides it.
- **`MindAttic.Vault.Tests`** — NUnit suite (`net10.0`), `InternalsVisibleTo` target. Not packable.
- **`MindAttic.Vault.Dashboard`** — Blazor LLM-health-monitor app (`net10.0`, Sdk.Web), references
  the published `MindAttic.Vault 1.0.0` and `MindAttic.Legion 22.0.0` packages plus the Azure Key
  Vault configuration packages. **NOT in `MindAttic.Vault.slnx`; no test project; NOT part of the
  package.** See [VLT-§7](#VLT-§7) and [RFC 0001](rfc/0001-llm-health-dashboard.md).

### 4.2 Domain model (NOUNS)
- **Bucket** — a credential category whose folder name equals its config section
  (`MindAttic:Vault:<Bucket>`): `LLM`, `Brokers`, `Tokens`, `Subtitles`, `Notifications`,
  `AudioStore`, plus the file-only `Ftp`. Cataloged in [VLT-§9](#VLT-§9).
- **Provider** — a keyed entry inside a bucket (e.g. `claude`, `alpaca-paper`) holding a typed
  credential triplet/record. An app-scoped provider id is `{appId}-{provider}` (e.g.
  `automata-claude`).
- **Key pool** — an ordered list of keys for one provider (`CredentialPoolEntry`: key + optional
  label); the first entry is mirrored to the plain `apiKey` field.
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
- **`FtpCredentialStore`** (`Credentials/`) — *read/write* the single flat FTP(S) deploy record at
  `%APPDATA%\MindAttic\Ftp\ftp.json` (field names match MindAttic.Deploy's existing shape;
  `MINDATTIC_FTP_CREDENTIALS` overrides the directory). File-only; not projected into
  `IConfiguration`.
- **`ConfigurationCredentialStore`** (`Credentials/`) — *read* a fixed config section; throws on write.
- **`CompositeCredentialStore`** + **`LlmCredentialResolver` / `BrokerCredentialResolver`**
  (`Credentials/`) — *chain* stores (config → file); writes target the file fallback.
- **`AppScopedCredentialStore`** (`Credentials/`) — *namespace* every provider id under `{appId}-`
  so many apps share one keyring. Composed in front of the shared store it gives "this app's own
  key, else the shared default"; writes land only under the app's prefix (per-app BYOK).
- **`IRotatingKeyStore`** (`Credentials/`) — *get/set* a provider's key pool. Implemented by
  `CredentialStore` (and so the LLM/Broker stores), `CompositeCredentialStore` and
  `AppScopedCredentialStore`; a single-key provider never gains an `apiKeys` array on disk.
- **`KeyResolver`** (`Resolution/`) — *compose* an explicit resolution chain for non-DI code.
- **`ServiceCollectionExtensions.AddMindAtticVault(...)` / `AddVaultAppSettings<T>(...)`**
  (`DependencyInjection/`) — *register* the resolvers/stores in DI.
- **`VaultPaths` / `EnvironmentOverlay`** (`Paths/`) — *compute* the roaming/local roots and
  *overlay* env vars onto settings. Root resolution is an ordered chain that never throws
  ([VLT-LAW-7](#VLT-LAW-7)):
  1. `MINDATTIC_VAULT_ROAMING_ROOT` / `MINDATTIC_VAULT_LOCAL_ROOT`, used verbatim;
  2. the matching `Environment.SpecialFolder` (the normal answer on Windows, macOS, iOS, Android);
  3. the platform convention from the environment — `%APPDATA%`/`%LOCALAPPDATA%` on Windows,
     `~/Library/Application Support` on Apple, `$XDG_CONFIG_HOME`/`$XDG_DATA_HOME` (else
     `~/.config`, `~/.local/share`) on Linux and Android;
  4. `$HOME`/`%USERPROFILE%` → `.mindattic/{config,data}`;
  5. `{AppContext.BaseDirectory}/.mindattic/{config,data}`.

  `ResolveRoaming()` / `ResolveLocal()` return the path and the `VaultRootSource` that produced
  it; `Describe()` prints both for startup diagnostics.
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

### {#VLT-LAW-3} VLT-LAW-3 — APPDATA is the single local source of truth; no User Secrets
The writable `%APPDATA%\MindAttic\<Bucket>\` store is the one local home for every credential. Do
**not** add `AddUserSecrets(...)` or `<UserSecretsId>` to any MindAttic project — User Secrets
ranks above the writable store and could silently mask a freshly-rotated key. Production stays
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
(`MINDATTIC_VAULT_ROAMING_ROOT`, `MINDATTIC_LLM_CREDENTIALS`, `MINDATTIC_BROKER_CREDENTIALS`,
`MINDATTIC_FTP_CREDENTIALS`) or temp directories — no test ever reads or writes the developer's
real `%APPDATA%`.

### {#VLT-LAW-7} VLT-LAW-7 — Root resolution never throws
`VaultPaths` always returns a rooted, non-blank root via the chain in [VLT-§4](#VLT-§4). Vault sits
in the `IConfiguration` chain, so a throw there aborts host construction (e.g. on a Linux App
Service worker) before any application code runs. An explicit override env var always wins, and a
host that resolves through `Environment.SpecialFolder` never resolves anywhere else. Every
environment dependency is injectable so each platform branch is tested from any OS.

## 6. Verified state {#VLT-§6}
Evidence captured 2026-10-03 on `main`.

- **Build:** `dotnet build MindAttic.Vault.slnx` → clean for both `net9.0` and `net10.0`. ✅
- **Tests:** `dotnet test MindAttic.Vault.slnx` → **Failed: 0, Passed: 292, Total: 292** (exit 0).
  `LiveKeyValidationTests` (incl. `TrustedPanel_EveryKeyAuthenticatesLive`) is `[Explicit]` — it
  hits real provider APIs and is skipped by a normal run. ✅
- **Coverage surface (proven):** every public type has NUnit coverage — `VaultPaths` (incl. the
  root-resolution chain), `EnvironmentOverlay`, `CredentialStore`, `LlmCredentialStore`,
  `BrokerCredentialStore`, `FtpCredentialStore`, `TokenStore`, `JsonSettingsStore<T>`, `KeyResolver`,
  `MindAtticConfigurationSource/Provider`, `ConfigurationCredentialStore`, `CompositeCredentialStore`,
  `AppScopedCredentialStore`, key pools (`IRotatingKeyStore`), `ConfigurationBuilderExtensions`,
  `VaultConfigurationKeys`, `ServiceCollectionExtensions`, `Llm/BrokerCredentialResolver`, plus a
  `CloudNativeIntegrationTests` end-to-end fixture. ✅ (See [USER_STORIES](USER_STORIES.md).)
- **Versioning:** `MindAttic.Vault.csproj` `<Version>5.0.0</Version>` — whole-number compliant
  ([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)). Packed to the local family feed;
  nuget.org lists only early 0.x versions. ✅
- **Dashboard:** tracked in the repo, **not built by the solution and not covered by the test
  suite** — its status is unproven here. ⬜ (See [VLT-§7](#VLT-§7).)

## 7. Active frontier {#VLT-§7}
- **LLM Health Dashboard** (`MindAttic.Vault.Dashboard/`) — a Blazor app that probes every keyed
  LLM provider in the Vault on a schedule, renders traffic-light health, alerts on state change,
  and optionally self-heals deprecated-model pointers. Tracked in
  [RFC 0001](rfc/0001-llm-health-dashboard.md) and
  [Epic D](USER_STORIES.md#epic-d-llm-health-dashboard-frontier). Not yet in
  the solution or test tree.
- **nuget.org publish** — current versions are not yet on nuget.org
  (VLT-US-X2 in [USER_STORIES](USER_STORIES.md#priority-backlog)).

## 8. Quality bar {#VLT-§8}
A change is **done** ([HOUSE-LAW-8](../../MindAttic.HouseRules.md#HOUSE-LAW-8)) only when:
1. `dotnet build MindAttic.Vault.slnx` is clean for **both** `net9.0` and `net10.0`.
2. `dotnet test MindAttic.Vault.slnx` is green (live-network tests may be skipped, never failing).
3. Any new public type ships XML doc comments (the package emits `MindAttic.Vault.xml`).
4. New credentials/buckets honor [VLT-LAW-2](#VLT-LAW-2) (folder == section) and have a store/
   source projection test that redirects `%APPDATA%` ([VLT-LAW-6](#VLT-LAW-6)).
5. A public-surface change bumps the major version in the csproj
   ([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)).
6. The user story carrying the change is `✅` only with its verifying test named in
   [USER_STORIES](USER_STORIES.md).

## 9. Glossary {#VLT-§9}
- **Bucket** — credential category; folder == `MindAttic:Vault:<Bucket>`. Canonical set:
  - `LLM` — `providers.json`: `{ id: { type, apiKey, apiKeys?, model, maxTokens } }`.
  - `Brokers` — `providers.json`: `{ id: { type, apiKey, secret, baseUrl } }`.
  - `Tokens` — `tokens.json`: flat `{ github: "...", "nuget-org": "..." }`.
  - `Subtitles` — `providers.json`: `{ OpenSubtitles: { user, password } }`.
  - `Notifications` — `providers.json`: `{ email: { smtpHost, smtpPort, username, password, from }, toEmail }`.
  - `AudioStore` — `providers.json`: `{ provider, container, connectionString }`.
  - `Ftp` — `ftp.json`: one flat FTP(S) record; file-only, read by `FtpCredentialStore`, not
    projected into `IConfiguration`.
- **Provider** — a keyed entry in a bucket (`claude`, `alpaca-paper`, …).
- **App-scoped id** — `{appId}-{provider}` (e.g. `tutor-claude`), written by `AppScopedCredentialStore`.
- **Key pool** — the ordered keys for one provider (`IRotatingKeyStore`); entry 0 mirrors `apiKey`.
- **Resolver** — a `CompositeCredentialStore` chaining config → file (e.g. `LlmCredentialResolver`).
- **Source** — an `IConfigurationSource`/store in the read chain.
- **Roaming vs local** — roaming settings live in `%APPDATA%`; per-machine caches/data in
  `%LOCALAPPDATA%`.
- **Trusted panel** — (Dashboard) the gating provider set (`claude`, `openai`, `gemini`,
  `deepseek`) whose votes decide the overall confidence verdict.
