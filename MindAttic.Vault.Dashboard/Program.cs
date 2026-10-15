using Azure.Identity;
using Microsoft.Data.Sqlite;
using MindAttic.Legion;
using MindAttic.Log;
using MindAttic.Log.Extensions;
using MindAttic.Log.Migration;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Dashboard.Components;
using MindAttic.Vault.Dashboard.Services;
using MindAttic.Vault.Paths;

var builder = WebApplication.CreateBuilder(args);

// ── Credential sources (lowest precedence first; later wins) ─────────────────
// appsettings → %APPDATA% canonical files → env vars → Azure Key Vault.
// In Azure App Service the managed identity reads KV; locally the APPDATA file
// (providers.json) is the source. Keys are resolved SERVER-SIDE only.
builder.Configuration
    .AddMindAtticVaultFiles()
    .AddEnvironmentVariables();

var keyVaultUri = builder.Configuration["MindAttic:Vault:KeyVaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    // DefaultAzureCredential → managed identity in App Service, az-login locally.
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// Hand the composed configuration to Legion's credential store (read path used
// by LlmHealthCheck / LlmModelDiscovery), so probes resolve keys from KV too.
MindAtticCredentialStore.UseConfiguration(builder.Configuration);

// ── Services ─────────────────────────────────────────────────────────────────
// Phase 2 (MindAttic.Log 3.0.0, see its docs/BIBLE.md §1.1): standalone MindAttic.Log.db under
// Vault's shared roaming root, not the rolled files Phase 1 kept under
// %LocalAppData%\MindAttic\VaultDashboard\logs — superseded so clearing that local folder can no
// longer take the log history with it. HealthMonitorStore/SelfHealer/AlertDispatcher/
// MonitorBackgroundService already call ILogger<T>; no call-site changes.
var vaultDashboardLogDbPath = VaultPaths.LoggingDbPath("VaultDashboard");

// Carry forward any rows left in Phase 1's rolled MindAttic.Log.<yyyy-MM>.db files (LogFileRoller
// is retired), then remove the now-empty rolled files. Idempotent and a clean no-op on every
// later startup.
var oldLogDirectory = Path.Combine(VaultPaths.LocalApp("VaultDashboard"), "logs");
if (Directory.Exists(oldLogDirectory))
{
    foreach (var rolledFile in Directory.GetFiles(oldLogDirectory, "MindAttic.Log.*.db"))
    {
        await MindAtticLogMigrator.MigrateFromSqliteAsync(rolledFile, vaultDashboardLogDbPath, dropSource: true);
        SqliteConnection.ClearAllPools();
        try { File.Delete(rolledFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

builder.Services.AddMindAtticLog(o =>
{
    o.Application = "MindAttic.Vault.Dashboard";
    o.DbPath = vaultDashboardLogDbPath;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<HealthMonitorStore>();
builder.Services.AddSingleton<SelfHealer>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddSingleton<LlmHealthMonitor>();
builder.Services.AddHostedService<MonitorBackgroundService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
