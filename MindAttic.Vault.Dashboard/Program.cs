using Azure.Identity;
using MindAttic.Legion;
using MindAttic.Vault.Configuration;
using MindAttic.Vault.Dashboard.Components;
using MindAttic.Vault.Dashboard.Services;

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
