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
