---
codex: 1
project: MindAttic.Vault
code: M.VALT
layer: stories
status: living
updated: 2026-10-03
---
<!-- GENERATED from MindAttic.Tickets/data/Tickets.db by `tickets db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the tickets CLI. -->

# MindAttic.Vault — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test that proves it.
> "Consumer" = a .NET host that takes a dependency on the package (Program.cs author / service author).
> Verified 2026-10-03: `dotnet test MindAttic.Vault.slnx` → Failed: 0, Passed: 292, Total: 292 (exit 0).

## Epic A — Local credential resolution

- **M.VALT-US-A1 ✅** As a consumer, I can resolve an LLM key with `IConfiguration` winning over the
  on-disk file, so a production setting always beats a stale local file. *Given a key in both
  config and the APPDATA file, When I call `LlmCredentialResolver.GetKey`, Then the config value
  is returned.* *(verified by `LlmCredentialResolver_Reads_Configuration_First_File_Second`.)*
- **M.VALT-US-A2 ✅** As a consumer, I can write a rotated LLM key and have it land in the file
  fallback (never in read-only config), so dev rotation works without touching prod sources.
  *(verified by `LlmCredentialResolver_SetKey_Lands_In_File_Store`.)*
- **M.VALT-US-A3 ✅** As a consumer, I can resolve broker creds config-first then file, so paper/live
  Alpaca keys follow the same precedence as LLM keys. *(verified by
  `BrokerCredentialResolver_Reads_Configuration_First_File_Second`.)*
- **M.VALT-US-A4 ✅** As a consumer, the resolver writes the LLM key with the correct inferred
  `type` (anthropic for claude, google for gemini, bearer otherwise) and preserves `model`/
  `maxTokens`, so I never hand-set provider type. *(verified by `SetKey_Infers_Anthropic_Type_For_Claude`,
  `SetKey_Infers_Google_Type_For_Gemini`, `SetKey_Defaults_To_Bearer_For_Unknown_Provider`,
  `SetKey_Preserves_Model_And_MaxTokens`.)*
- **M.VALT-US-A5 ✅** As a consumer, I can read and partially rotate full broker records
  (apiKey/secret/baseUrl) without losing sibling fields. *(verified by
  `GetBrokerCreds_Reads_All_Fields`, `SetBrokerCreds_Persists_All_Fields`,
  `GetBrokerCreds_Returns_Null_When_ApiKey_Or_Secret_Empty`.)*
- **M.VALT-US-A6 ✅** As a consumer, I can store flat single-value tokens (github, nuget-org) with
  case-insensitive keys and atomic swaps. *(verified by `Set_Then_Get_Roundtrips`,
  `LoadAll_Is_Case_Insensitive`, `Set_Trims_Whitespace`.)*

## Epic B — Cloud-native projection & precedence

- **M.VALT-US-B1 ✅** As a consumer, I can register `AddMindAtticVaultFiles()` and have my APPDATA
  bucket files surface through `IConfiguration` under the standard schema. *(verified by
  `File_Source_Surfaces_Through_IConfiguration_When_Registered`,
  `Surfaces_Existing_Providers_Json_Under_MindAttic_Vault_Section`.)*
- **M.VALT-US-B2 ✅** As a consumer, when the same key exists in config and the APPDATA file, the
  configuration value wins end-to-end through DI. *(verified by
  `Configuration_Wins_Over_Existing_AppData_File`.)*
- **M.VALT-US-B3 ✅** As a consumer, the source flattens multiple buckets, coerces scalars
  (bool/int/double), projects arrays, and survives malformed/empty/non-object files without
  throwing. *(verified by `Reads_Multiple_Buckets`, `Empty_Tmp_Yields_No_Keys_Without_Throwing`,
  and the `MindAtticConfigurationSourceTests` fixture.)*
- **M.VALT-US-B4 ✅** As a consumer, I can read a fixed config section read-only and have all write
  paths throw, so production code can't accidentally mutate secrets. *(verified by
  `GetKey_Reads_Standard_Llm_Section`, and the read-only-contract cases in
  `ConfigurationCredentialStoreTests`.)*

## Epic C — DI wiring, paths & settings

- **M.VALT-US-C1 ✅** As a Program.cs author, `AddMindAtticVault(IConfiguration)` registers the LLM &
  broker resolvers and a default `ICredentialStore`, with full argument validation. *(verified by
  `AddMindAtticVault_Resolves_Llm_And_Broker_Stores`,
  `AddMindAtticVault_Resolves_Default_ICredentialStore_To_Llm_Store`,
  `AddMindAtticVault_Without_Configuration_Throws_For_Null_Services`.)*
- **M.VALT-US-C2 ✅** As a Program.cs author, `AddVaultAppSettings<T>("MyApp")` registers a roaming
  `JsonSettingsStore<T>` I can inject. *(verified by `AddVaultAppSettings_Registers_JsonSettingsStore`.)*
- **M.VALT-US-C3 ✅** As a service author, I can load/save/update per-app settings with defaults on a
  missing or malformed file, and overlay env vars after load. *(verified by
  `Load_Returns_Defaults_When_File_Missing`, `Save_Then_Load_Roundtrips`,
  `Load_Returns_Defaults_For_Malformed_Json`, `LoadWithOverlay_Applies_Overlay_After_Load`.)*
- **M.VALT-US-C4 ✅** As a service author, `VaultPaths` gives me APPDATA/LOCALAPPDATA path math with
  an env-var override for tests, replacing hand-rolled `Path.Combine`. *(verified by
  `RoamingRoot_Defaults_To_AppData_MindAttic_When_Env_Unset`, `RoamingRoot_Honours_Override_Env_Var`.)*
- **M.VALT-US-C5 ✅** As a service author, `KeyResolver` lets me compose an explicit chain that returns
  the first non-empty trimmed value and survives a throwing step. *(verified by
  `Resolve_Returns_First_Non_Empty_Step`, `Resolve_Trims_Returned_Value`,
  `Resolve_Survives_Throwing_Step`.)*
- **M.VALT-US-C6 ✅** As a maintainer, the full cloud-native path (in-memory config + temp file
  source + env overlay, in DI) is exercised end-to-end. *(verified by the
  `CloudNativeIntegrationTests` fixture.)*

- **M.VALT-US-C7 ✅** As an app author, Vault resolves its roots on **any** OS — Windows, Linux, macOS,
  iOS, Android — and never aborts my host at startup because the platform has no user profile.
  *`VaultPaths` walks an ordered chain (override → `SpecialFolder` → platform convention → `$HOME` →
  application base) and reports which rule won via `ResolveRoaming()`/`ResolveLocal()`/`Describe()`.
  See [M.VALT-LAW-7](BIBLE.md#M.VALT-LAW-7).* *(verified by `VaultPathsResolutionTests.Override_WinsAndIsUsedVerbatim`,
  `BlankOverride_IsTreatedAsUnset`, `SpecialFolder_IsPreferredWhenTheHostProvidesOne`,
  `SpecialFolder_ThatThrows_FallsThroughInsteadOfPropagating`, `Windows_FallsBackToAppDataVariables`,
  `Linux_UsesXdgWhenSet`, `Linux_FallsBackToTheXdgDefaultsUnderHome`,
  `Apple_UsesLibraryApplicationSupport`, `WindowsWithoutAppData_StillFindsTheUserProfile`,
  `NoUserProfileAtAll_ResolvesBesideTheBinariesInsteadOfThrowing`,
  `EveryBranchReturnsARootedNonBlankPath`, `PublicRootsResolveOnThisHostAndAreReportable` —
  every environment dependency is injected, so the Linux-container branch is covered from a Windows
  agent.)*
- **M.VALT-US-C8 ✅** As an app author, I can keep my own key for a provider in the shared keyring and
  fall back to the cross-app default, without ever changing what another app resolves. *Given
  `Composite(AppScopedCredentialStore("automata", shared), shared)`, When I read `claude`, Then
  `automata-claude` wins if set, else `claude`; writes land only under `automata-`.* *(verified by
  `Composite_Of_Scoped_Then_Shared_Prefers_Own_Key`,
  `Composite_Of_Scoped_Then_Shared_Falls_Back_When_No_Own_Key`,
  `Composite_Write_Lands_In_Scoped_Store_Not_Shared`,
  `SaveAllRaw_Replaces_Only_Own_Entries_Leaving_Others_Untouched`.)*
- **M.VALT-US-C9 ✅** As an app author, I can store an ordered pool of keys for one provider and rotate
  through it, while single-key callers keep working. *(verified by
  `SetKeys_Then_GetKeys_Round_Trips_Multiple_Keys_In_Order`,
  `SetKeys_Mirrors_First_Entry_Into_Plain_ApiKey_For_Back_Compat`,
  `SetKeys_With_Single_Entry_Omits_ApiKeys_Array_On_Disk`,
  `CompositeCredentialStore_SetKeys_Writes_To_Writable_Store`.)*
- **M.VALT-US-C10 ✅** As a deploy tool author, I can read and write the shared FTP(S) deploy record in
  MindAttic.Deploy's existing field shape. *(verified by `Get_Reads_All_Fields_From_Legacy_Shape`,
  `Set_Persists_All_Fields_And_Round_Trips`,
  `TryGetJson_Matches_Legacy_Field_Names_For_MINDATTIC_FTP_JSON`.)*

## Epic D — LLM Health Dashboard (frontier)

- **M.VALT-US-D1 ⬜** As an operator, I can open a dashboard that probes every keyed LLM provider in
  the Vault and shows a traffic-light health status per provider. *(The Dashboard app exists in the repo but is not in the solution or test tree — unproven.
  See [RFC 0001](rfc/0001-llm-health-dashboard.md).)*
- **M.VALT-US-D2 ⬜** As an operator, a trusted panel (`claude`, `openai`, `gemini`, `deepseek`) gates
  an overall confidence verdict. *(Live-auth test `TrustedPanel_EveryKeyAuthenticatesLive` is
  `[Explicit]` — requires real keys/network; not run by a normal `dotnet test`.)*
- **M.VALT-US-D3 ⬜** As an operator, I get an alert (email/webhook) when a provider changes state
  between sweeps, and deprecated-model pointers optionally self-heal within the sweep interval.
  *(`SelfHealer`/`AlertDispatcher` services exist in the Dashboard, untested here.)*

## Priority backlog
Dependency-ordered toward "publish to nuget.org and ship the health dashboard":
1. **M.VALT-US-X2 ⬜** Publish the current `MindAttic.Vault` to nuget.org (nuget.org lists only early
   0.x versions).
2. **M.VALT-US-D1 → D2 → D3 ⬜** Land the LLM Health Dashboard: add the project to the solution, add a
   test project for the monitor/self-healer, then promote D1–D3 to ✅ with named tests.


## Epic E — Vault as the ecosystem Settings Hub
- **M.VALT-US-E1 ⬜** As any MindAttic app, I have no settings page of my own -- my cog icon opens Vault Dashboard directly to my own tab.
- **M.VALT-US-E2 ⬜** As a developer adding a new app's settings, I write one tab component against a MindAttic.Components contract and it appears in Vault Dashboard with no Dashboard code change.
- **M.VALT-US-E3 ⬜** As an operator, Vault Dashboard is always reachable locally (service/tray-launched) so a cog click always has somewhere to go.
- **M.VALT-US-E4 ⬜** As the LLM Health Dashboard's existing audience, health monitoring survives as one tab among the others, not replaced.

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| M.VALT-T1 | 2026-10-12 | docs | closed | Start the permanent ticket log in USER_STORIES.md | — |
| M.VALT-T2 | 2026-10-14 | chore | closed | NUnit tests for Dashboard HealthMonitorStore (MindAttic.Vault.Dashboard/Services/HealthMonitorStore.cs; add a Dashboard ProjectReference to MindAttic.Vault.Tests first): RecordUptime running percentage per provider and case-insensitive ids, Previous returns null before first commit, Commit replaces snapshots and evicts providers missing from the fresh set, sets LastSweepUtc, clears SweepInProgress and fires OnChanged, Snapshots ordered trusted-first then alphabetical, TrustedPanelHealthy false when no trusted snapshots exist or any trusted one is not Healthy - this is the headline confidence verdict the dashboard leads with | MindAttic.Vault.Tests/HealthMonitorStoreTests.cs |
| M.VALT-T3 | 2026-10-14 | chore | open | NUnit tests for Dashboard AlertDispatcher (Services/AlertDispatcher.cs): with a fake IHttpClientFactory/HttpMessageHandler verify every non-blank webhook gets a JSON POST with provider, displayName, status, previous, diagnosis, detail, atUtc fields; blank webhook urls skipped; no channels configured means no work; a webhook that throws or returns non-2xx is logged and does not stop other webhooks or fault DispatchAsync; email channel skipped when Host is blank or To is empty, and an SMTP failure is swallowed - alerts must be best-effort and isolated so one bad channel never breaks a sweep | — |
| M.VALT-T4 | 2026-10-14 | chore | open | NUnit tests for Dashboard SelfHealer (Services/SelfHealer.cs) model auto-repoint: using a fake HTTP handler for LlmModelDiscovery and a temp Vault dir, cover discovery exception returns null, empty live-model list returns null, replacement preference High tier then Medium then catalog DefaultModel then any catalog-known live model, a live list with only non-catalog ids (image/embedding) returns null and writes nothing, success patches only the model field in providers.json preserving apiKey and other fields, missing providers.json entry or non-object JSON returns the FAILED to persist note - a wrong repoint silently breaks a trusted provider | — |
| M.VALT-T5 | 2026-10-14 | chore | open | NUnit tests for Dashboard LlmHealthMonitor sweep and MonitorBackgroundService (Services/LlmHealthMonitor.cs, MonitorBackgroundService.cs): with fake HTTP responses verify Classify (unhealthy is Down, self-healed or wrong reply is Degraded, else Healthy), ConsecutiveFailures increments across failing sweeps and resets to 0 on success, alerts fire only on transitions into or out of Down and never on the first sweep (previous Unknown) or steady state, self-heal only runs on NotFound when SelfHealModels is true, trusted ids always probed and MonitorAllKeyed adds keyed supported providers without duplicates, SweepInProgress reset even when the sweep throws, concurrent RunSweepAsync calls serialize; background service treats non-positive Interval as 1 hour and keeps looping after a sweep exception but stops on cancellation | — |
| M.VALT-T6 | 2026-10-14 | chore | open | Run Vault.Dashboard once so its carry-forward migrates the leftover rolled log %LOCALAPPDATA%\MindAttic\VaultDashboard\logs\MindAttic.Log.2026-10.db (61 rows) into the Vault database | — |
| M.VALT-T7 | 2026-10-15 | chore | closed | Remove duplicate command copies (/deploy, /quickload, /quicksave) now centralized at the user level (~/.claude/commands) | M.FLOW-T14 |
| M.VALT-T8 | 2026-10-16 | chore | closed | Tracked .githooks/commit-msg runs the shared MindAttic commit rule: every commit names an existing workflow ticket | M.FLOW agent-standard commit hook |
| M.VALT-T9 | 2026-10-16 | chore | closed | install.ps1 adds this repo's 8 .prose/commands stubs for the shared global commands, so any LLM client finds them | mindattic-agent-standard/install.ps1 run 2026-10-16 |
| M.VALT-T10 | 2026-10-16 | chore | open | Bug hunt: run an exhaustive /bughunt over the whole repo and fix every real bug found (each fix under its own fix ticket), then hunt again; close this only when a full pass finds nothing | user request 2026-10-16: approved repo sweep |
| M.VALT-T11 | 2026-10-16 | chore | open | Run the full unit test suite and fix every failure (each under its own fix ticket) until it passes; put the passing run in --ref. No test project: close this wont-do saying so | user request 2026-10-16: approved repo sweep |
| M.VALT-T12 | 2026-10-16 | chore | open | Clean up code and docs: remove dead code, stale comments and outdated or duplicated docs, and simplify where behaviour doesn't change (/simplify); tests stay green | user request 2026-10-16: approved repo sweep |
| M.VALT-T13 | 2026-10-16 | docs | open | Bring README.md up to date with the code as it is now (what it is, setup, usage, commands, current state), following codex-standard/README-STYLE.md; README.htm regenerated if the repo has one | user request 2026-10-16: approved repo sweep |
| M.VALT-T14 | 2026-10-16 | docs | closed | Update USER_STORIES.md | — |
| M.VALT-T15 | 2026-10-19 | chore | wont-do | Convert tracked source files to LF line endings via .gitattributes, per HOUSE-LAW-13 (low priority) | — |
| M.VALT-T16 | 2026-10-20 | docs | open | Publish MindAttic.Vault docs to GitHub Pages via the shared Agency DocFX pipeline: run adopt-docs-site.ps1 (README.md as home page, docs/*.md as articles, API reference only where the code builds in CI), enable Pages with build_type=workflow, set the repo homepage URL, retire tools/build-readme.ps1 and README.htm, confirm the docs Actions run is green and https://mindattic.github.io/MindAttic.Vault/ returns 200. Depends on MAT-T8. | — |
| M.VALT-T17 | 2026-10-21 | chore | closed | Update settings.json | — |
| M.VALT-T18 | 2026-10-22 | docs | closed | Update USER_STORIES.md | — |
| M.VALT-T19 | 2026-10-24 | docs | closed | Update USER_STORIES.md | — |
| M.VALT-T20 | 2026-10-30 | docs | wont-do | RFC 0002: expand Vault Dashboard's charter from LLM-health-only to ecosystem settings hub | superseded by Epic E redesign: Vault repo hosts two companion apps (Web + Desktop), not a widened single Dashboard |
| M.VALT-T21 | 2026-10-30 | feature | wont-do | Dashboard: add tab-host shell (nav rail, one route per app, deep-linkable by app id) | superseded by Epic E redesign: Vault repo hosts two companion apps (Web + Desktop), not a widened single Dashboard |
| M.VALT-T22 | 2026-10-30 | feature | wont-do | Dashboard: discover and register installed app settings-tab packages without per-app Dashboard code changes | superseded by Epic E redesign: Vault repo hosts two companion apps (Web + Desktop), not a widened single Dashboard |
| M.VALT-T23 | 2026-10-30 | feature | wont-do | Dashboard: document and implement the deep-link contract apps use to open a specific settings tab | superseded by Epic E redesign: Vault repo hosts two companion apps (Web + Desktop), not a widened single Dashboard |
| M.VALT-T24 | 2026-10-30 | chore | wont-do | Dashboard: give it a reliable always-on local-run story (background service or tray-launched Kestrel host) | superseded by Epic E redesign: Vault repo hosts two companion apps (Web + Desktop), not a widened single Dashboard |
| M.VALT-T25 | 2026-10-30 | chore | closed | Dashboard: wire the new logo.ico as its favicon | M.VALT-US-E1 |
| M.VALT-T26 | 2026-10-30 | docs | wont-do | RFC 0002: split Vault's UI story into MindAttic.Vault.Web and MindAttic.Vault.Desktop | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T27 | 2026-10-30 | chore | wont-do | Rename MindAttic.Vault.Dashboard project to MindAttic.Vault.Web | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T28 | 2026-10-30 | feature | wont-do | Scaffold MindAttic.Vault.Desktop (native WPF app) | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T29 | 2026-10-30 | feature | wont-do | Desktop: tab-host shell adapting MaTabControl for a dynamic, discoverable tab list | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T30 | 2026-10-30 | feature | wont-do | Desktop: tab discovery/registration without editing Desktop's own source per app | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T31 | 2026-10-30 | feature | wont-do | Desktop: single-instance launch-or-activate contract (the thing every app's cog calls) | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T32 | 2026-10-30 | feature | wont-do | Web: require real authentication + authorization before any settings-editing route ships | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T33 | 2026-10-30 | feature | wont-do | Audit every settings/credential change made through Web or Desktop, secrets masked by default | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T34 | 2026-10-30 | chore | wont-do | Web: lock down the web-exposed surface (headers, Key Vault RBAC, HSTS/antiforgery coverage) | withdrawn: plan rejected by owner 2026-10-31, see M.VALT-T26 |
| M.VALT-T35 | 2026-10-31 | docs | open | RFC 0003: MindAttic.Vault.Desktop as the single settings/secrets UI, with Profiles (Desktop, Azure, future) mapping identical per-app tabs to different storage backends; no web-exposed settings surface is ever built | M.VALT-US-E1, M.VALT-US-E2, M.VALT-US-E3, M.VALT-US-E4 |
| M.VALT-T36 | 2026-10-31 | feature | closed | Extract ISettingsStore<T> from JsonSettingsStore<T>: the Desktop Profile's implementation, unchanged behavior | 0f1aa27 SettingsStoreContractTests |
| M.VALT-T37 | 2026-10-31 | feature | closed | Scaffold MindAttic.Vault.Desktop (native WPF app) | 89ee8cc CompositionTests |
| M.VALT-T38 | 2026-10-31 | feature | closed | Desktop: tab-host shell (MaTabControl) plus a global Profile switcher (Desktop / Azure / future) | 8fab692 1fff27f TabHostTests AppSettingsTabTests DesktopProfileTests |
| M.VALT-T39 | 2026-10-31 | feature | closed | Desktop: single-instance launch-or-activate contract (named pipe or mutex) | 7a5d470 SingleInstanceTests |
| M.VALT-T40 | 2026-10-31 | feature | closed | Azure Profile: one-time per-app resource picker (Azure.Identity + Azure.ResourceManager) | e35e803 ResourcePickerTests |
| M.VALT-T41 | 2026-10-31 | feature | closed | Azure Profile: Key Vault-backed credential store for secret fields | 3ea37eb KeyVaultKeyBackendTests |
| M.VALT-T42 | 2026-10-31 | feature | closed | Azure Profile: Azure App Configuration-backed settings store for non-secret fields | b438802 AppConfigurationSettingsBackendTests |
| M.VALT-T43 | 2026-10-31 | feature | closed | Vault core: ProviderCatalog (ids, display names, categories, key-page URLs, aliases) so anthropic/claude, google/gemini and xai/grok keys merge into one list | 95d80a4 ProviderCatalogTests |
| M.VALT-T44 | 2026-10-31 | feature | closed | Vault core: settings manifest attributes ([SettingsApp], [Setting], [UsesCredential], Range/Choices/PathPicker/Url/Multiline) written to settings.manifest.json next to settings.json | 1b11c8e SettingsManifestTests |
| M.VALT-T45 | 2026-10-31 | feature | closed | Vault core: named keys (required unique Name + Disabled per pool entry) and per-app key assignments ({AppId}:Keys:{provider} = ordered key names, first active) | 230ebb2 NamedKeyStoreTests KeyAssignmentStoreTests |
| M.VALT-T46 | 2026-10-31 | feature | closed | Vault core: key health (key-health.json, keyed by provider + key name) and KeySelector failover: 401/403 marks failed, 429 cools down, network errors never blame the key | f5e444e KeySelectorTests |
| M.VALT-T47 | 2026-10-31 | feature | closed | Vault core: ConfigurationCredentialStore reads named key lists (Llm:{provider}:ApiKeys:*) and app assignments ({AppId}:Keys:{provider}) so selection and failover work the same from Azure App Configuration | 2482190 ConfigurationKeySourceTests |
| M.VALT-T48 | 2026-10-31 | feature | closed | Vault core: import existing per-app key copies (AppScopedCredentialStore, .key overrides, JF_API_KEY/ELEVENLABS_API_KEY, legion.json) into the shared named lists as {app} (imported) and set them as that app's active key | 3e8e21c KeyImporterTests |
| M.VALT-T49 | 2026-10-31 | chore | open | Publish the next whole-number major of MindAttic.Vault with the LAMP core (ISettingsStore, LoadOrSeed, manifest, named keys, KeySelector, catalog, import) | M.VALT-T35 |
| M.VALT-T50 | 2026-10-31 | feature | closed | Vault.Desktop: Keys tab (one section per provider: named keys in failover order, masked value, health light, last used, used by, Test, add/rename/disable/remove/reorder) and per-app Keys section (Active key picker + fallbacks) | f9e39a3 KeysTabTests ProviderKeyTesterTests |
| M.VALT-T51 | 2026-10-31 | feature | closed | Vault.Desktop: Get Started (BYOK) checklist shown while any required credential has no working key, with key-page links, Test and the offered key import | 078ce7a GetStartedTests |
| M.VALT-T52 | 2026-10-31 | feature | closed | Vault.Desktop: remember last Profile, tab, window bounds (clamped to an existing monitor), theme, collapsed groups and Azure environment mappings | 726034f DesktopStateTests |
| M.VALT-T53 | 2026-10-31 | feature | closed | Vault.Desktop: append-only audit log of every Save (time, Windows user / Azure UPN, Profile, app, field names; secrets only as changed) in local Vault\audit.db with insert-only triggers | afe728e 0777a80 AuditLogTests CanaryLeakTests |
| M.VALT-T54 | 2026-10-31 | feature | closed | Vault.Desktop: startup check that the Vault folders are not readable by other users or Everyone, with a warning and one-click fix | 5f053e8 FolderAccessTests |
| M.VALT-T55 | 2026-10-31 | feature | closed | Vault core: VaultConfiguration provider exposing every app settings.json, Global\settings.json and the key files as one merged IConfiguration tree, readable whole or in part | eadd89e VaultConfigurationTests |
| M.VALT-T56 | 2026-10-31 | feature | closed | Vault.Desktop: mindattic-vault:// protocol handler (per-user registration) so web apps can open Desktop at an app tab and setting; locally hosted web apps use the CurrentUserOnly pipe server-side; no network listener | a94bf05 ProtocolRegistrationTests |
| M.VALT-T57 | 2026-10-31 | chore | open | LAMP final verification: every app opens its Vault.Desktop tab and setting from its cog, no app keeps its own config screen, full test suites green in every touched repo, and after restarting MindAttic.Orchestrator.Desktop.Wpf.exe every app behaves exactly as before | M.VALT-T35 |
| M.VALT-T58 | 2026-10-31 | feature | closed | Vault core: VaultSettings.Open(appId, setting) + mindattic-vault:// link + single-instance pipe contract, usable from WPF, Blazor and console apps (moved from M.COMP-T157) | aa7c0ad VaultOpenRequestTests VaultSettingsOpenTests VaultDesktopPipeServerTests |
| M.VALT-T59 | 2026-10-31 | fix | closed | Undo mojibake (garbled em dashes and arrows) introduced into three Vault sources by a Windows PowerShell 5.1 Get-Content/Set-Content round-trip during LAMP edits | b384972 build |
| M.VALT-T60 | 2026-10-31 | feature | closed | Settings manifests support per-machine apps: [SettingsApp(Local = true)] keeps settings in %LOCALAPPDATA%\MindAttic\<AppId>; Discover and VaultConfiguration scan both roots | f0dbfd2 LocalScopeSettingsTests |
| M.VALT-T61 | 2026-10-31 | fix | closed | CredentialStore/TokenStore keep the previous file as providers.json.bak / tokens.json.bak on every save, so a replaced or removed key value stays on disk (RFC 0003 Security 5: keys are written nowhere else) | 2d1605c CredentialStoreTests TokenStoreTests CanaryLeakTests |
| M.VALT-T62 | 2026-10-31 | feature | closed | Vault.Desktop: Credentials section for structured credential groups (Notifications SMTP/Twilio, Subtitles, AudioStore, Brokers key+secret, Ftp, Tokens) rendered from each bucket's providers.json/tokens.json, secret fields masked with MaSecretBox, plain fields editable, atomic writes, audited | f99f3b9 f57c1c6 CredentialsTabTests CanaryLeakTests KeysTabTests |
| M.VALT-T63 | 2026-10-31 | feature | open | Vault.Desktop Azure Profile: edit credential groups (Notifications, Subtitles, AudioStore, Brokers, Ftp, Tokens, Hosting) as App Configuration keys MindAttic:Vault:<bucket>:<entry>:<field>, secret-looking fields as Key Vault references (KeyVaultKeyBackend patterns); today the Credentials tab is read-only on Azure (follow-up of M.VALT-T62) | M.VALT-T35 |
| M.VALT-T64 | 2026-10-31 | feature | closed | Vault.Desktop tab strip: every tab button is as wide as the longest tab name, computed on load and recalculated on resize and when tabs change, so tabs show as equal-width | 3a98fe3 EqualTabWidthsTests (5); Desktop suite 140/140; installed app: 25 tabs one width |
| M.VALT-T65 | 2026-10-31 | chore | open | Push every repo the LAMP rollout (M.VALT-T35) committed to: Vault, Components and all migrated apps | M.VALT-T35 |
| M.VALT-T66 | 2026-10-31 | fix | closed | Fails on main: MindAttic.Vault.Tests.VaultDesktopPipeServerTests.RealLauncher_SendReachesTheServer — Assert.That(r.AppId, Is.EqualTo("Tutor")) Expected string length 5 but was 6. Strings differ at index 0. Expected: "Tutor" But was: "Global" -----------^ (landed on main since the last green run: tickets M.VALT-T64, M.VALT-T62, M.VALT-T61, M.VALT-T37, M.VALT-T53, M.VALT-T54, M.VALT-T36, M.VALT-T52, M.VALT-T51, M.VALT-T38, M.VALT-T60, M.VALT-T50, M.VALT-T41, M.VALT-T42, M.VALT-T40, M.VALT-T59 in 3a98fe3 Vault.Desktop tabs are equal width: every tab is as wide as the longest name, measured on load, on resize and whenever tabs change (M.VALT-T64); f57c1c6 Vault.Desktop Credentials tab masks every field of a bucket found on disk without a known shape (Security's pepper.v1 / dp-kek, PackageSigning's pfx), and pepper, kek, pfx, private count as secret-looking names (M.VALT-T62); f99f3b9 Vault.Desktop Credentials tab: edit the shared credential groups (Notifications, Subtitles, AudioStore, Brokers, Ftp, Tokens, Hosting and any other bucket on disk) in place, secret fields masked, types and unknown fields kept, atomic writes, one audit row per save; Azure read-only for now (M.VALT-T62); 2d1605c Credential and token stores no longer keep a .bak of the previous file, so a replaced or removed key never stays on disk; the canary leak test drops its allowance (M.VALT-T61); cf5ae46 Vault.Desktop tests: rename the Samples.Main helper to Samples.Shell so it isn't mistaken for an entry point (CS0028) (M.VALT-T37); 38eec06 Vault.Desktop pins the patched native SQLite (SQLitePCLRaw.lib.e_sqlite3 3.53.3) under its audit log, as the workspace's other SQLite apps do (GHSA-2m69-gcr7-jv3q) (M.VALT-T53); 0777a80 Vault.Desktop canary leak test: a canary key pushed through Get Started, add, test (a refusing provider echoing it), rename, set value, assignment, Save, audit, the diagnostic log and remembered state appears nowhere on disk but the key store and in no message (M.VALT-T53); 5f053e8 Vault.Desktop checks at startup that %APPDATA%\MindAttic\LLM and \Vault and %LOCALAPPDATA%\MindAttic\Vault aren't readable by Everyone, Users, Authenticated Users or any other account, and warns with a one-click fix that removes them and disables inheritance, keeping the user, SYSTEM and Administrators (audited) (M.VALT-T54); da81525 LoadOrSeed returns the defaults instead of throwing when the folder can't be written, so a read-only deployed web app still starts (M.VALT-T36); 726034f Vault.Desktop remembers its state in %APPDATA%\MindAttic\Vault.Desktop\state.json: last Profile, last tab, window bounds clamped to a monitor that still exists, collapsed Keys groups and an opaque Azure environments slot; saved debounced on change and on exit (M.VALT-T52); 078ce7a Vault.Desktop Get Started: shown first while any app's required provider has no usable key, one row per provider naming the apps that need it with a key field, the key page link and an immediate test; offers environment, legion.json and per-app key copies for import on click only (audited by name); disappears once every required provider has a working key (M.VALT-T51); 1fff27f Vault.Desktop offers the Azure Profile in the picker (services.AddAzureProfile()) with its sign-in and resource picker as a setup tab, selected while the Profile isn't ready; signing in or picking an environment rechecks the Profile and reloads every tab (M.VALT-T38); 40ce3ad Vault.Desktop follows manifest Scope: the Desktop Profile loads and saves a Local-scope app's settings under %LOCALAPPDATA%\MindAttic\<AppId>, and the manifest catalog discovers and watches both roots (M.VALT-T60); f0dbfd2 Per-machine settings apps: [SettingsApp(Local = true)] keeps settings in %LOCALAPPDATA%\MindAttic\<AppId>; manifests carry Scope; Discover and the merged configuration scan both roots (M.VALT-T60); f9e39a3 Vault.Desktop Keys tab: one section per provider grouped by category, named keys in failover order with masked values, health lights, last used and used by; add, set value (resets health), rename (health and assignments follow), disable, remove (blocked while assigned, naming the apps), reorder and Test (one cheap authenticated call, classified like KeySelector, recorded in key health); every change audited by name (M.VALT-T50); 3ea37eb Azure Profile key backend: named keys as App Configuration Key Vault references to generated llm-{provider}-{slug}-{hash} secrets tagged name/provider, new version on SetValue, rename keeps the secret and rewrites order/disabled/assignments, remove refused while assigned; AzureProfile + AddAzureProfile; AzureLive suite (M.VALT-T41); b438802 Azure Profile settings backend: each app's document as flat App Configuration key-values named exactly as IConfiguration sees them, types rebuilt from the manifest kinds, only changed fields written, removed items deleted, ETag If-Match/If-None-Match -> ConcurrencyConflictException, 403 -> App Configuration Data Owner role message (M.VALT-T42); e35e803 Azure Profile resource picker: MindAttic.Vault.Desktop.Azure with one-time browser sign-in (persisted OS-encrypted MSAL cache), subscription -> resource group -> Key Vault + App Configuration picker, remembered environments and the themed setup view (M.VALT-T40); b384972 Restore the em dashes and arrows garbled by a PowerShell 5.1 re-encode in three Vault sources (M.VALT-T59); afe728e Vault.Desktop audit log: append-only SQLite at %LOCALAPPDATA%\MindAttic\Vault\audit.db (triggers abort UPDATE/DELETE), one row per Save and key assignment with time, actor, Profile, target, action and names only; write failures logged, never thrown (M.VALT-T53)) | passes on main@50625d0 |
| M.VALT-T67 | 2026-10-31 | chore | open | LAMP verification: run every migrated repo's full test suite, fix or ticket each failure, and add an ecosystem-wide Vault integration suite (all manifests, settings files, keys, links) proving the migration end to end | M.VALT-T35 |
| M.VALT-T68 | 2026-10-31 | fix | closed | Vault.Desktop warns the key folders (Roaming LLM, Roaming Vault, Local Vault) are readable by SNOWCRASH\CodexSandboxUsers; find where that access comes from and resolve it without breaking Codex | Applied Vault.Desktop Fix (user-approved): LLM, Roaming Vault, Local Vault now protected, only ryand/SYSTEM/Administrators; 0 child items readable by CodexSandboxUsers; banner gone |
| M.VALT-T69 | 2026-10-31 | fix | closed | VaultDesktopPipeServerTests.RealLauncher_SendReachesTheServer uses the real per-user pipe: with the live Vault.Desktop running it sends --app Tutor to the user's open Vault window and times out; give VaultDesktopLauncher a pipe-name overload and use a unique test pipe | 50625d0 |
| M.VALT-T70 | 2026-10-31 | fix | closed | ProviderCatalog lacks providers apps declare (Prose: stability, kling, runway; TaxRateCollector: usps) and the legacy claude-api id in LLM\providers.json shows as its own provider instead of merging into Claude | 9e52f68 ProviderCatalogTests.ProvidersAppsDeclareAreKnown, LegacyClaudeApiEntry_MergesIntoClaudesNamedList; Vault.Tests 476/476 |
| M.VALT-T71 | 2026-10-31 | fix | closed | Find and fix the errors the user reports after the LAMP rollout (app logs, startup, Vault.Desktop) | Errors found and fixed: M.ORCH-T1314 (PATH), M.ORCH-T1315 (MapVirtualKeyW), M.LNCH-T99/PL-T32 (raw CR in run.ps1); Orchestrator redeployed (M.ORCH-T1316) |
| M.VALT-T72 | 2026-10-31 | chore | open | Redeploy every app changed by the LAMP rollout (local C:\Apps / AppData installs via each repo's run.ps1) so the installed copies match main | M.VALT-T35 |
| M.VALT-T73 | 2026-10-31 | chore | closed | LAMP bug hunt: redeploy every app the Vault LAMP plan changed (each on its own), prove its settings come from Vault, and click its settings/cog to confirm Vault.Desktop opens with that app's tab selected; ticket and fix every bug found | Bug hunt complete across 26 apps: every app redeployed on its own, values proven from Vault with exact-byte restores, every settings/cog entry opens Vault on its own tab (Orchestrator by the main session). Fixed: M.ORCH-T1325/T1326, M.PSST-T28, M.BRWS-T24/T25, M.TIX-T270/T271, M.CAPT-T88, JH-T23, M.LNCH-T102, M.VALT-T74, TUT-T34/T35, M.IDEA-T33/T34. Open: M.ORCH-T1327 (burndown axis labels), IdiotProof CSP |
| M.VALT-T74 | 2026-10-31 | fix | closed | VaultConfigurationProvider ReloadOnChange watches only the roaming root, so a per-machine app's settings.json under %LOCALAPPDATA%\MindAttic (Tutor, Capture, Orchestrator, ...) saved in Vault.Desktop never reloads the IConfiguration tree (found by M.VALT-T73) | 7fc1517 LocalScopeSettingsTests.VaultConfiguration_ReloadOnChange_PicksUpALocalAppsSavedSettings |
| M.VALT-T75 | 2026-10-31 | feature | closed | Vault tabs are named after the project (repo): every [SettingsApp] DisplayName becomes its repo's name (MindAttic.Capture, MindAttic.Browser, MindAttic.Launcher, MindAttic.Tickets, Unstuck, ProcessLogger, MindAttic.Ideas, JellyFinPoster, MindAttic.Log, MindAttic.Orchestrator); re-verify every cog lands on that tab | Stopped by the user (friendly names are fine if derived from the repo name). Done before the stop: Capture 7027e57, Browser bd3b631, Launcher 5f2d009, Tickets 92240ad, Unstuck c686eaf, ProcessLogger decd9a4 now use the exact repo name (ProcessLogger's manifest refreshes on its next run). Ideas, JellyFinPoster, MindAttic.Log, Orchestrator unchanged. |
| M.VALT-T76 | 2026-10-31 | chore | closed | Full build and test run of every LAMP repo after the bug-hunt fixes (user-requested), one failure ticket per failure | M.VALT-T73 |
| M.VALT-T77 | 2026-10-31 | feature | closed | Vault's Global tab holds exactly the values every app should reach (shared across the ecosystem): find what Global has today, which per-app settings are really global, and give apps one way to read Global | f67d85d GlobalSettingsTests; Orchestrator M.ORCH-T1328 0bf38266 GlobalFallbackTests |
| M.VALT-T78 | 2026-10-31 | feature | closed | Every Vault app tab is named exactly after its repo, no friendly names: Ideas -> MindAttic.Ideas, Jellyfin Poster -> JellyFinPoster, Log Reader -> MindAttic.Log, MindAttic Orchestrator -> MindAttic.Orchestrator, and refresh ProcessLogger's manifest | Every app tab is now exactly its repo folder name (checked against D:\Projects\MindAttic; only Vault's own Global is exempt). Vault tests 490/141/414 green after f67d85d/7f86470. |
| M.VALT-T79 | 2026-10-31 | feature | closed | Vault.Desktop: order the app tabs alphabetically (A-Z, left to right, top to bottom) after Keys, Credentials and Global | 7f86470 TabHostTests (141/141); deployed, live order confirmed |
| M.VALT-T80 | 2026-10-31 | chore | closed | Redeploy Vault.Desktop (run.ps1) to ship the A-Z tab order (M.VALT-T79) | Deployed 7f86470 (+ f67d85d Global values); live tab order A-Z after Keys/Credentials/Global |
| M.VALT-T81 | 2026-10-31 | chore | closed | Add agent-session.json, favicon.ico, assets; Update App.razor, USER_STORIES.md | — |
| M.VALT-T82 | 2026-11-02 | docs | open | Adopt MindAttic.Agency docs-site pipeline, publish README to GitHub Pages (HOUSE-LAW-14) | — |
| M.VALT-T83 | 2026-11-02 | fix | closed | Bughunt sweep, two passes: find and fix real bugs across tracked source (user request 2026-11-02) | — |
| M.VALT-T84 | 2026-11-02 | fix | open | Fails on main: MindAttic.Vault.Ecosystem.Tests.DiscoveryTests.Discover_finds_every_expected_app — Missing apps: 1 problem(s): Ideas: no settings.manifest.json under either root (landed on main since the last green run: tickets M.VALT-T83 in 5e00055 Bughunt: store writes no longer wipe providers.json/tokens.json/key-assignments/settings when the existing file can't be read; watcher reloads can't crash the host; Desktop reads BOM'd JSON instead of showing (and saving over) defaults; recovery alerts no longer say DOWN (M.VALT-T83)) | main@5e00055 |
| M.VALT-T85 | 2026-11-02 | fix | open | Fails on main: MindAttic.Vault.Ecosystem.Tests.DesktopCatalogTests.Desktops_catalog_lists_every_discovered_app_from_the_real_roots — Desktop tabs: 1 problem(s): Ideas: no tab (landed on main since the last green run: tickets M.VALT-T83 in 5e00055 Bughunt: store writes no longer wipe providers.json/tokens.json/key-assignments/settings when the existing file can't be read; watcher reloads can't crash the host; Desktop reads BOM'd JSON instead of showing (and saving over) defaults; recovery alerts no longer say DOWN (M.VALT-T83)) | main@5e00055 |
| M.VALT-T86 | 2026-11-02 | fix | closed | Command pointer in .agent/commands said a literal $source instead of its legacy path (install.ps1 escaping bug fixed in MAT-T84); point it at the real source, or the global command when the project copy is gone | — |
| M.VALT-T87 | 2026-11-02 | fix | closed | Fix the open items the 2026-11-02 bughunt left for decision (strict settings load, corrupt-file copy-aside, catalog family routing, FTP .bak) | — |
| M.VALT-T88 | 2026-11-03 | chore | closed | Publish a MindAttic.Vault package with LoadStrict/TryLoad (M.VALT-T87) so consumers can adopt it | — |
| M.VALT-T89 | 2026-11-03 | fix | open | TokenStore reads swallow IO errors as an empty map (LoadAllLocked non-write path catch-all), so a Get that lands during another process's File.Replace swap reports the token missing; and the cross-process write is an unlocked read-modify-write that can lose another process's update. Seen as an intermittent 'stored Claude key not found right after Save' in IdiotProof's Cypress 03 (about 1 run in 4) | IP-T382 |
| M.VALT-T90 | 2026-11-06 | feature | open | Add a global 'Default to Desktop 1' setting so MindAttic windows/games never disturb virtual desktops 2+ | — |
