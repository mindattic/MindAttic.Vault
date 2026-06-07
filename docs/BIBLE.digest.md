---
codex: 1
project: MindAttic.Vault
code: VLT
layer: digest
status: generated
generatedFrom: VLT-§1,VLT-§3,VLT-§5,VLT-§9
updated: 2026-06-07
---

# MindAttic.Vault — BIBLE digest
AUTHORITATIVE — full detail in docs/BIBLE.md

## The one sentence
MindAttic.Vault is a cloud-native credentials-and-settings library for .NET that gives every
MindAttic host **one `IConfiguration`-backed pipeline** for API keys, broker tokens, and per-app
preferences — with the `%APPDATA%\MindAttic` file store as the single local source of truth and
Azure App Service Application Settings / Key Vault as the production source, with no code change
between environments.

## What it is NOT
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

## The Laws
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

## Glossary
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

## Status index (from USER_STORIES.md)
- done: 19 | partial: 1 | planned: 7 | cut: 1

## Latest amendment
- VLT-A2 — Whole-number versioning; csproj is authoritative over README prose (supersedes —)
