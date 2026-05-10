# Integration Plan — FractionsOfACent

**Goal:** retire the `Settings.LoadGitHubToken()` one-off in `v2/Shared/Settings.cs` in favour of `TokenStore` and `IConfiguration`-backed token reads. The whole settings file in this project is ~30 lines and exists *only* to read a GitHub PAT — Vault eliminates that entire helper.

**Cloud-native impact:** the GitHub token resolves through User Secrets locally (`MindAttic:Vault:Tokens:github`) and App Service Application Settings (or Key Vault references) in production. The legacy `%APPDATA%\MindAttic\FractionsOfACent\settings.json` file keeps working as a fallback during the transition.

## Files involved

| File | Action |
| --- | --- |
| `v2/Shared/FractionsOfACent.Shared.csproj` (or wherever `Settings.cs` lives) | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `v2/Blazor/FractionsOfACent.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. |
| `v2/Shared/Settings.cs` | **Delete** the `LoadGitHubToken()` method (and the `Settings` class if that's all it does). Replace call sites with `IConfiguration["MindAttic:Vault:Tokens:github"]` (or a thin `IGitHubTokenProvider` interface that wraps the lookup). |
| `v2/Blazor/Program.cs` | Wire the cloud-native config chain and call `services.AddMindAtticVault(builder.Configuration);`. |

## On-disk migration

| Source | Path / setting |
| --- | --- |
| **Legacy** | `%APPDATA%\MindAttic\FractionsOfACent\settings.json` containing `{ "GitHubToken": "ghp_..." }` |
| **New canonical (dev)** | `dotnet user-secrets set "MindAttic:Vault:Tokens:github" "ghp_..."` |
| **New canonical (prod)** | App Service Application Setting `MindAttic__Vault__Tokens__github` (or Key Vault reference) |
| **TokenStore equivalent** | `%APPDATA%\MindAttic\GitHub\tokens.json` containing `{ "github": "ghp_..." }` |

Two strategies for the transition:

1. **Read both paths during the transition.** Try `IConfiguration["MindAttic:Vault:Tokens:github"]` first (cloud-native), then `TokenStore.ForBucket("GitHub").Get("github")` (file canonical), then the legacy `%APPDATA%\MindAttic\FractionsOfACent\settings.json` as a final fallback. Drop the legacy fallback in a later release.
2. **One-time migration on startup.** Read the legacy `settings.json`, call `TokenStore.ForBucket("GitHub").Set("github", ...)`, delete the legacy file. Less code to maintain but loses backward compat for users who haven't launched the app in a while.

Recommended: start with strategy 1.

```csharp
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;

public sealed class GitHubTokenProvider
{
    private readonly IConfiguration config;
    public GitHubTokenProvider(IConfiguration config) => this.config = config;

    public string? Get()
    {
        // 1. Cloud-native: User Secrets / App Service / Key Vault.
        var fromConfig = config["MindAttic:Vault:Tokens:github"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.Trim();

        // 2. New canonical file: %APPDATA%\MindAttic\GitHub\tokens.json
        var fromTokens = TokenStore.ForBucket("GitHub").Get("github");
        if (!string.IsNullOrWhiteSpace(fromTokens)) return fromTokens;

        // 3. Legacy %APPDATA%\MindAttic\FractionsOfACent\settings.json — drop next release.
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MindAttic", "FractionsOfACent", "settings.json");
        if (!File.Exists(legacy)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(legacy));
            return doc.RootElement.TryGetProperty("GitHubToken", out var t)
                   && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch { return null; }
    }
}
```

## Step 1 — package reference + shared User Secrets ID

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

## Step 2 — replace call sites

Anywhere the project calls `Settings.LoadGitHubToken()`, switch to a constructor-injected `GitHubTokenProvider.Get()`. Search the solution for the symbol and verify the count is small (audit reported a single helper).

## Step 3 — wire DI

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddSingleton<GitHubTokenProvider>();
```

## Step 4 — verify

```powershell
dotnet build D:\Projects\MindAttic\FractionsOfACent\FractionsOfACent.sln
dotnet run   --project v2/Blazor
```

Confirm the dashboard still authenticates against GitHub and pulls the disclosure feed. Rerun any Cypress specs that exercise the GitHub auth path.

## Rollback

`git restore v2/Shared/ v2/Blazor/Program.cs v2/Blazor/FractionsOfACent.Blazor.csproj`. The legacy `settings.json` is never deleted by strategy 1's fallback path, so historical behaviour is preserved.

## Notes

- This project does not use Legion, so the LLM half of Vault is not exercised here.
- The `FRACTIONS_DB` env var for the connection string remains untouched — Vault doesn't replace `IConfiguration`'s connection-string handling.
