# LAMP app migration playbook

How one MindAttic app moves its app-level settings and keys into MindAttic.Vault 7 so that
**MindAttic.Vault.Desktop.Wpf** is the only place they are edited. Plan: [RFC 0003](rfc/0003-lamp.md).
Each app has one migration ticket, `part-of` M.VALT-T35. The app must behave exactly as before when
it's done: same effective values, same keys used, same startup.

## Names (final)

| What | Name in files, App Configuration and IConfiguration |
|---|---|
| App setting | `MindAttic:Settings:{AppId}:{property}` (camelCase, nested objects add `:{child}`) |
| Cross-app setting | `MindAttic:Settings:Global:{property}` (`MindAtticGlobalSettings`) |
| Named key | `MindAttic:Vault:LLM:{provider}:keys:{keyName}` (brokers: `MindAttic:Vault:Brokers:...`) |
| Key order / disabled | `...:{provider}:keyOrder:{i}`, `...:{provider}:disabledKeys:{i}` |
| App's keys | `MindAttic:Vault:Assignments:{AppId}:{provider}:{i}` (first = active) |

`{AppId}` is the `[SettingsApp]` id **and** the app's folder under `%APPDATA%\MindAttic\` (M.VALT-LAW-2).
If the app already has a folder there, use that exact name.

## Steps

1. **Package.** Every project in the repo that references `MindAttic.Vault` moves to `7.0.0`
   (local feed `C:\LocalNuGet`). Fix any breaking changes from older majors.
2. **One settings class.** All app-level settings (scope: the RFC's section 0) go in one class:
   ```csharp
   [SettingsApp("IdiotProof", "IdiotProof", Icon = "chart", Description = "...")]
   [UsesCredential("claude", Required = true, Purpose = "Strategy review")]
   public sealed class IdiotProofSettings
   {
       [Setting("Max loss per trade", Group = "Risk", Help = "Dollars per trade", Order = 1)]
       [SettingRange(0, 100000)]
       public decimal MaxLossPerTrade { get; set; } = 50m;   // initializer = today's default
   }
   ```
   - Property initializers are the first-run defaults: exactly what the app uses today when nothing is set.
   - Lists of objects (profiles, users, ...) are `List<TItem>` of a settable class; add `[SettingCollection(ItemName = "profile")]`.
   - **No secrets.** Keys/passwords/tokens never go in this class (the manifest builder throws on secret-looking names).
     Mark a look-alike that isn't a secret with `[Setting(..., NotSecret = true)]`.
   - If the app already used `JsonSettingsStore<T>` at `%APPDATA%\MindAttic\{App}\settings.json`, keep the
     property names so the existing file still loads; just add the attributes.
   - Per-user / per-universe / CMS data stays where it is (RFC section 0).
3. **Load it.** `var store = JsonSettingsStore<TSettings>.ForSettingsApp(); var settings = store.LoadOrSeed();`
   (DI: `services.AddVaultAppSettings<TSettings>("{AppId}")` and inject `ISettingsStore<TSettings>`).
   `LoadOrSeed` writes `settings.manifest.json` beside the file. Hosts built on IConfiguration may instead add
   `configuration.AddMindAtticVaultConfiguration()` and bind `GetSection("MindAttic:Settings:{AppId}")`.
4. **Import today's values once.** When the new `settings.json` doesn't exist yet, read the old storage
   (SQLite row, other JSON, registry, `appsettings.json`, ...) into the class and `Save` it, so the user's
   current values carry over. Never modify or delete the old storage (it is the rollback). Run the app once on
   this machine (or its `--write-settings-manifest`) so the real `%APPDATA%` gets today's values.
5. **Remap consumers.** Every place that read the old storage reads the new class (or the IConfiguration name).
   Grep for the old accessors until none remain.
6. **Keys.** Declare `[UsesCredential]` for every provider the app uses. Resolve keys through the shared
   named lists, never a per-app copy:
   - Desktop/console: `var keys = KeySelector.ForCurrentUser();`
   - IConfiguration hosts: `var keys = configuration.CreateKeySelector();`
   - Wrap calls: `await keys.ExecuteAsync("claude", "{AppId}", (key, ct) => CallAsync(key, ct), ct)` or
     `keys.SendAsync(...)` for raw HTTP. Where a call can't be wrapped (streaming, SDK-owned retries):
     `keys.GetActiveKey(provider, appId)` then `ReportSuccess` / `ReportFailure(provider, name, kind)`.
   - Remove `AppScopedCredentialStore`; at startup call `KeyImporter.ImportAppScopedFor("{AppId}")` once so
     any per-app copy moves into the shared list and stays this app's active key.
   - Catch `AllKeysFailedException` where the app reported a missing key before, and show its message
     (it names keys, never values) plus a button that calls `VaultSettings.Open("{AppId}")`.
7. **Manifest on install.** First line of `Main`/`Program.cs`:
   `if (SettingsManifestCommand.TryRun<TSettings>(args)) return;` and have the repo's `run.ps1` run the built exe
   once with `--write-settings-manifest` after publishing.
8. **Remove the settings screen.** Delete the app-level settings page/window/menu/route. The cog (or settings
   menu/button) calls `VaultSettings.Open("{AppId}")`; a link next to one value can pass its full name:
   `VaultSettings.Open("{AppId}", "MindAttic:Settings:{AppId}:maxLossPerTrade")`. Blazor Server apps call it
   from the server-side handler (the server runs on the user's machine) and may also render
   `VaultSettings.Link(...)` as an `<a href>` for a remote browser. Never host a settings editor in the app.
9. **Tests** (NUnit; the repo's existing test project, or a new `{Project}.Tests`):
   - **One test per setting**, each proving it end to end: the old stored value imports into the right property,
     it round-trips through `JsonSettingsStore` under its manifest name, and the code that consumes it actually
     gets that value.
   - The manifest builds (`SettingsManifestBuilder.Build<TSettings>()`), every field has a label, no secret fields.
   - `LoadOrSeed` on an empty folder seeds exactly today's defaults.
   - LLM apps: first key 401 → the second key is used (KeySelector over a temp `NamedKeyStore`).
   - The old settings UI is gone (type/route/file no longer exists) and the cog calls `VaultSettings.Open`
     (set `VaultSettings.Launcher` to a fake).
   - Use `TempDirectory`-style folders and `MINDATTIC_VAULT_ROAMING_ROOT` / `MINDATTIC_VAULT_LOCAL_ROOT`
     overrides: tests never touch the real `%APPDATA%` (M.VALT-LAW-6).
10. **Verify.** Build the whole solution, run the repo's full test suite, run the import on this machine,
    start the app the way MindAttic.Orchestrator starts it and confirm it behaves as before.

## Rules while migrating

Work on `main` in the repo's own checkout (no branches, no worktrees). One ticket per commit, subject ending
with the ticket id; commit by path (`git commit -m "..." -- <paths>`), never `git add -A`, stash or reset.
Don't push. Close the ticket with `tickets ticket close <ID> --ref "<hash> <test class>"` when every step holds.
