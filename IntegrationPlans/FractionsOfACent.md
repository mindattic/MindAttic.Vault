# Integration Plan — FractionsOfACent

**Status (audit-verified 2026-05-09):** Cleanest of the six. No Azure deployment pipeline, no Legion dependency, single static helper to retire. Recommended **Phase B pilot**.

**Goal:** retire `Settings.LoadGitHubToken()` in `v2/Shared/Settings.cs` in favour of `IConfiguration`-backed token resolution. Existing legacy file at `%APPDATA%\MindAttic\FractionsOfACent\settings.json` keeps working as a backward-compat fallback.

**Cloud-native impact:** GitHub token resolves through User Secrets locally (`MindAttic:Vault:Tokens:github`) and any Azure host's Application Settings (or Key Vault references) once a deployment pipeline is added.

## Files involved (verified against current source)

| File | Action |
| --- | --- |
| `v2/Shared/FractionsOfACent.Shared.csproj` | Add `<PackageReference Include="MindAttic.Vault" Version="0.2.0" />`. |
| `v2/Blazor/FractionsOfACent.Blazor.csproj` | Add `<UserSecretsId>mindattic-vault-shared</UserSecretsId>`. Add the same package reference. |
| `NuGet.config` (repo root) | New file pointing to the local feed (or to nuget.org once Vault is published there). |
| `v2/Shared/Settings.cs` | Keep `ResolveConnectionString()` and `ConfigPath`. Mark `LoadGitHubToken()` `[Obsolete]` rather than deleting — keep it as the legacy fallback inside the new resolver during the transition. |
| `v2/Blazor/Program.cs` | Wire the cloud-native config chain. Replace the inline two-line token resolution (lines 26–30) with a `GitHubTokenProvider` service that walks the IConfiguration → env → legacy file chain. |

## Current call sites (exact, from source)

```csharp
// v2/Blazor/Program.cs lines 23–31
builder.Services.AddSingleton<GitHubClient>(_ =>
{
    var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Settings.LoadGitHubToken()
        ?? throw new InvalidOperationException(
            "GITHUB_TOKEN env var or settings.json is required to send notices.");
    return new GitHubClient(token);
});
```

```csharp
// v2/Shared/Settings.cs lines 30–49 — legacy reader
public static string? LoadGitHubToken() { ... reads settings.json ... }

private sealed class SettingsFile
{
    [JsonPropertyName("github_token")] public string? GitHubToken { get; set; }
}
```

> **Note.** The legacy JSON property is `github_token` (snake_case), NOT `GitHubToken`. Keep this in mind when migrating users — `dotnet user-secrets set "MindAttic:Vault:Tokens:github" "..."` is the new canonical, but until users move, the legacy file with `{ "github_token": "..." }` keeps working through the fallback.

## Step 1 — repo-level NuGet config + package references

Create `D:\Projects\MindAttic\FractionsOfACent\NuGet.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <!-- Once Vault is published to nuget.org, this LocalNuGet line can be removed. -->
    <add key="LocalNuGet" value="C:\LocalNuGet" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

Edit `v2/Shared/FractionsOfACent.Shared.csproj` and `v2/Blazor/FractionsOfACent.Blazor.csproj`:

```xml
<PropertyGroup>
  <UserSecretsId>mindattic-vault-shared</UserSecretsId>  <!-- Blazor host only -->
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="MindAttic.Vault" Version="0.2.0" />
</ItemGroup>
```

## Step 2 — add `GitHubTokenProvider`

New file `v2/Shared/GitHubTokenProvider.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;

namespace FractionsOfACent;

/// <summary>
/// Resolves the GitHub PAT used by GitHubClient. Priority:
/// 1. IConfiguration["MindAttic:Vault:Tokens:github"] — User Secrets / App Service / Key Vault.
/// 2. TokenStore.ForBucket("GitHub").Get("github") — new canonical %APPDATA%\MindAttic\GitHub\tokens.json.
/// 3. GITHUB_TOKEN env var — legacy.
/// 4. Settings.LoadGitHubToken() — legacy %APPDATA%\MindAttic\FractionsOfACent\settings.json.
/// Drop step 4 in a later release once users have migrated.
/// </summary>
public sealed class GitHubTokenProvider
{
    private readonly IConfiguration config;
    public GitHubTokenProvider(IConfiguration config) => this.config = config;

    public string? Get()
    {
        var fromConfig = config["MindAttic:Vault:Tokens:github"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.Trim();

        var fromTokenStore = TokenStore.ForBucket("GitHub").Get("github");
        if (!string.IsNullOrWhiteSpace(fromTokenStore)) return fromTokenStore;

        var fromEnv = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

#pragma warning disable CS0618  // intentional: legacy fallback while users migrate
        return Settings.LoadGitHubToken();
#pragma warning restore CS0618
    }
}
```

In `v2/Shared/Settings.cs`, mark the legacy reader obsolete (do not delete):

```csharp
[Obsolete("Use GitHubTokenProvider; this fallback will be removed in a future release.")]
public static string? LoadGitHubToken() { ... unchanged ... }
```

## Step 3 — wire DI in `v2/Blazor/Program.cs`

Add to the top of `Program.cs`, after `var builder = WebApplication.CreateBuilder(args);`:

```csharp
using MindAttic.Vault.Configuration;
using MindAttic.Vault.DependencyInjection;

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddMindAtticVaultFiles()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddMindAtticVault(builder.Configuration);
builder.Services.AddSingleton<GitHubTokenProvider>();
```

Replace the GitHubClient registration (current lines 23–31):

```csharp
builder.Services.AddSingleton<GitHubClient>(sp =>
{
    var token = sp.GetRequiredService<GitHubTokenProvider>().Get()
        ?? throw new InvalidOperationException(
            "GitHub token is required. Set it via " +
            "`dotnet user-secrets set \"MindAttic:Vault:Tokens:github\" \"ghp_...\"` " +
            "or the GITHUB_TOKEN env var.");
    return new GitHubClient(token);
});
```

## Step 4 — verify

```powershell
dotnet build D:\Projects\MindAttic\FractionsOfACent\FractionsOfACent.sln
dotnet user-secrets --project D:\Projects\MindAttic\FractionsOfACent\v2\Blazor set "MindAttic:Vault:Tokens:github" "ghp_test_value"
dotnet run --project D:\Projects\MindAttic\FractionsOfACent\v2\Blazor
```

Confirm the dashboard authenticates against GitHub. Hit the "send notice" button and verify the token resolved correctly.

Cleanup:

```powershell
dotnet user-secrets --project D:\Projects\MindAttic\FractionsOfACent\v2\Blazor remove "MindAttic:Vault:Tokens:github"
```

## Rollback

`git restore v2/Shared/ v2/Blazor/Program.cs v2/Blazor/FractionsOfACent.Blazor.csproj` and `rm NuGet.config`. The legacy `%APPDATA%\MindAttic\FractionsOfACent\settings.json` is untouched throughout, so historical behaviour is fully preserved.

## Notes

- This project does not use Legion, so the LLM half of Vault is not exercised here.
- The `FRACTIONS_DB` env var for the connection string remains untouched — Vault doesn't replace `IConfiguration`'s connection-string handling.
- No CI/CD pipeline today, so the nuget.org publish gate doesn't block this app — it can be piloted with the local feed alone.
