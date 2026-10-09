using Azure.Identity;
using MindAttic.Legion;
using MindAttic.Log;
using MindAttic.Log.Extensions;
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
// No database of its own — rolled-SQLite no-database tier (see MindAttic.Log repo's
// docs/MIGRATION.md). HealthMonitorStore/SelfHealer/AlertDispatcher/MonitorBackgroundService
// already call ILogger<T>; this only adds the sink underneath, no call-site changes.
builder.Services.AddMindAtticLog(o =>
{
    o.Application = "MindAttic.Vault.Dashboard";
    o.Destination = LogDestination.Sqlite;
    o.FileDirectory = VaultPaths.Ensure(Path.Combine(VaultPaths.LocalApp("VaultDashboard"), "logs"));
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
