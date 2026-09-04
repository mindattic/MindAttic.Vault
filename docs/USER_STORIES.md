---
codex: 1
project: MindAttic.Vault
code: VLT
layer: stories
status: living
updated: 2026-06-07
---

# MindAttic.Vault — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned · 🗑️ cut. Every ✅ cites the test that proves it.
> "Consumer" = a .NET host that takes a dependency on the package (Program.cs author / service author).
> Verified 2026-06-07: `dotnet test MindAttic.Vault.slnx` → Failed: 0, Passed: 241, Total: 241 (exit 0).

## Epic A — Local credential resolution

- **VLT-US-A1 ✅** As a consumer, I can resolve an LLM key with `IConfiguration` winning over the
  on-disk file, so a production setting always beats a stale local file. *Given a key in both
  config and the APPDATA file, When I call `LlmCredentialResolver.GetKey`, Then the config value
  is returned.* *(verified by `LlmCredentialResolver_Reads_Configuration_First_File_Second`.)*
- **VLT-US-A2 ✅** As a consumer, I can write a rotated LLM key and have it land in the file
  fallback (never in read-only config), so dev rotation works without touching prod sources.
  *(verified by `LlmCredentialResolver_SetKey_Lands_In_File_Store`.)*
- **VLT-US-A3 ✅** As a consumer, I can resolve broker creds config-first then file, so paper/live
  Alpaca keys follow the same precedence as LLM keys. *(verified by
  `BrokerCredentialResolver_Reads_Configuration_First_File_Second`.)*
- **VLT-US-A4 ✅** As a consumer, the resolver writes the LLM key with the correct inferred
  `type` (anthropic for claude, google for gemini, bearer otherwise) and preserves `model`/
  `maxTokens`, so I never hand-set provider type. *(verified by `SetKey_Infers_Anthropic_Type_For_Claude`,
  `SetKey_Infers_Google_Type_For_Gemini`, `SetKey_Defaults_To_Bearer_For_Unknown_Provider`,
  `SetKey_Preserves_Model_And_MaxTokens`.)*
- **VLT-US-A5 ✅** As a consumer, I can read and partially rotate full broker records
  (apiKey/secret/baseUrl) without losing sibling fields. *(verified by
  `GetBrokerCreds_Reads_All_Fields`, `SetBrokerCreds_Persists_All_Fields`,
  `GetBrokerCreds_Returns_Null_When_ApiKey_Or_Secret_Empty`.)*
- **VLT-US-A6 ✅** As a consumer, I can store flat single-value tokens (github, nuget-org) with
  case-insensitive keys and atomic swaps. *(verified by `Set_Then_Get_Roundtrips`,
  `LoadAll_Is_Case_Insensitive`, `Set_Trims_Whitespace`.)*

## Epic B — Cloud-native projection & precedence

- **VLT-US-B1 ✅** As a consumer, I can register `AddMindAtticVaultFiles()` and have my APPDATA
  bucket files surface through `IConfiguration` under the standard schema. *(verified by
  `File_Source_Surfaces_Through_IConfiguration_When_Registered`,
  `Surfaces_Existing_Providers_Json_Under_MindAttic_Vault_Section`.)*
- **VLT-US-B2 ✅** As a consumer, when the same key exists in config and the APPDATA file, the
  configuration value wins end-to-end through DI. *(verified by
  `Configuration_Wins_Over_Existing_AppData_File`.)*
- **VLT-US-B3 ✅** As a consumer, the source flattens multiple buckets, coerces scalars
  (bool/int/double), projects arrays, and survives malformed/empty/non-object files without
  throwing. *(verified by `Reads_Multiple_Buckets`, `Empty_Tmp_Yields_No_Keys_Without_Throwing`,
  and the `MindAtticConfigurationSourceTests` fixture.)*
- **VLT-US-B4 ✅** As a consumer, I can read a fixed config section read-only and have all write
  paths throw, so production code can't accidentally mutate secrets. *(verified by
  `GetKey_Reads_Standard_Llm_Section`, and the read-only-contract cases in
  `ConfigurationCredentialStoreTests`.)*

## Epic C — DI wiring, paths & settings

- **VLT-US-C1 ✅** As a Program.cs author, `AddMindAtticVault(IConfiguration)` registers the LLM &
  broker resolvers and a default `ICredentialStore`, with full argument validation. *(verified by
  `AddMindAtticVault_Resolves_Llm_And_Broker_Stores`,
  `AddMindAtticVault_Resolves_Default_ICredentialStore_To_Llm_Store`,
  `AddMindAtticVault_Without_Configuration_Throws_For_Null_Services`.)*
- **VLT-US-C2 ✅** As a Program.cs author, `AddVaultAppSettings<T>("MyApp")` registers a roaming
  `JsonSettingsStore<T>` I can inject. *(verified by `AddVaultAppSettings_Registers_JsonSettingsStore`.)*
- **VLT-US-C3 ✅** As a service author, I can load/save/update per-app settings with defaults on a
  missing or malformed file, and overlay env vars after load. *(verified by
  `Load_Returns_Defaults_When_File_Missing`, `Save_Then_Load_Roundtrips`,
  `Load_Returns_Defaults_For_Malformed_Json`, `LoadWithOverlay_Applies_Overlay_After_Load`.)*
- **VLT-US-C4 ✅** As a service author, `VaultPaths` gives me APPDATA/LOCALAPPDATA path math with
  an env-var override for tests, replacing hand-rolled `Path.Combine`. *(verified by
  `RoamingRoot_Defaults_To_AppData_MindAttic_When_Env_Unset`, `RoamingRoot_Honours_Override_Env_Var`.)*
- **VLT-US-C5 ✅** As a service author, `KeyResolver` lets me compose an explicit chain that returns
  the first non-empty trimmed value and survives a throwing step. *(verified by
  `Resolve_Returns_First_Non_Empty_Step`, `Resolve_Trims_Returned_Value`,
  `Resolve_Survives_Throwing_Step`.)*
- **VLT-US-C6 ✅** As a maintainer, the full cloud-native path (in-memory config + temp file
  source + env overlay, in DI) is exercised end-to-end. *(verified by the
  `CloudNativeIntegrationTests` fixture.)*

## Epic D — LLM Health Dashboard (frontier)

- **VLT-US-D1 ⬜** As an operator, I can open a dashboard that probes every keyed LLM provider in
  the Vault and shows a traffic-light health status per provider. *(In-flight on
  `feat/llm-health-dashboard`; the Dashboard app is not in the solution or test tree — unproven.
  See [RFC 0001](rfc/0001-llm-health-dashboard.md).)*
- **VLT-US-D2 ⬜** As an operator, a trusted panel (`claude`, `openai`, `gemini`, `deepseek`) gates
  an overall confidence verdict. *(Live-auth test `TrustedPanel_EveryKeyAuthenticatesLive` is
  **skipped** — requires real keys/network; not run in CI.)*
- **VLT-US-D3 ⬜** As an operator, I get an alert (email/webhook) when a provider changes state
  between sweeps, and deprecated-model pointers optionally self-heal within the sweep interval.
  *(Planned; `SelfHealer`/`AlertDispatcher` services exist in the working tree, untested here.)*

- **VLT-US-C7 ✅** As an app author, Vault resolves its roots on **any** OS — Windows, Linux, macOS,
  iOS, Android — and never aborts my host at startup because the platform has no user profile.
  *`VaultPaths` walks an ordered chain (override → `SpecialFolder` → platform convention → `$HOME` →
  application base) and reports which rule won via `ResolveRoaming()`/`ResolveLocal()`/`Describe()`.
  See [VLT-A3](AMENDMENTS.md).* *(tests: `VaultPathsResolutionTests.Override_WinsAndIsUsedVerbatim`,
  `BlankOverride_IsTreatedAsUnset`, `SpecialFolder_IsPreferredWhenTheHostProvidesOne`,
  `SpecialFolder_ThatThrows_FallsThroughInsteadOfPropagating`, `Windows_FallsBackToAppDataVariables`,
  `Linux_UsesXdgWhenSet`, `Linux_FallsBackToTheXdgDefaultsUnderHome`,
  `Apple_UsesLibraryApplicationSupport`, `WindowsWithoutAppData_StillFindsTheUserProfile`,
  `NoUserProfileAtAll_ResolvesBesideTheBinariesInsteadOfThrowing`,
  `EveryBranchReturnsARootedNonBlankPath`, `PublicRootsResolveOnThisHostAndAreReportable` —
  every environment dependency is injected, so the Linux-container branch is covered from a Windows
  agent. 265 tests green.)*

## Priority backlog
Dependency-ordered toward "publish 1.0.0 and ship the health dashboard":
1. **VLT-US-X1 ✅** Reconcile README status prose (stale "0.3.0") with the authoritative
   `<Version>1.0.0</Version>` in the csproj ([HOUSE-LAW-1](../../MindAttic.HouseRules.md#HOUSE-LAW-1)).
   *(Resolved 2026-06-07: README Status row and Integration plans status line updated to `1.0.0`.)*
2. **VLT-US-X2 ⬜** Publish `MindAttic.Vault 1.0.0` to nuget.org (README's pending release step).
3. **VLT-US-D1 → D2 → D3 ⬜** Land the LLM Health Dashboard: add the project to the solution, add a
   test project for the monitor/self-healer, then promote D1–D3 to ✅ with named tests.

### Audit log
No story spec was changed; each entry below records a status promotion only.

- **2026-06-07 — VLT-US-X1 ⬜→✅** README Status row updated from "0.3.0" to "1.0.0"; Integration
  plans status line updated to `MindAttic.Vault 1.0.0`. Original spec: *Reconcile README status
  prose (stale "0.3.0") with the authoritative `<Version>1.0.0</Version>` in the csproj.*

Prior to 2026-06-07: this file was the first Codex stories file for the repo; migration source was
`README.md` (the de-facto bible) plus the real NUnit suite — no prior `user_stories.md` existed.
