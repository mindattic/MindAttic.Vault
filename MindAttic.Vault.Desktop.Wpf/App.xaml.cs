using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using MindAttic.Vault.Launch;
using MindAttic.Vault.Settings;

namespace MindAttic.Vault.Desktop;

/// <summary>
/// MindAttic Vault: the only place any MindAttic app's settings or keys are edited (RFC 0003, LAMP).
/// </summary>
public partial class App : Application
{
    // WCAG 1.4.13: a tooltip stays up until the pointer or focus leaves or Esc is pressed.
    static App() => MaToolTips.KeepOpen();

    private readonly CancellationTokenSource stopListening = new();
    private ServiceProvider? services;

    /// <summary>The command line, set by <see cref="Program"/>.</summary>
    public string[] StartupArgs { get; init; } = [];

    /// <summary>The validated <c>--app/--setting</c> request (or <c>mindattic-vault://</c> link) this start carried, if any.</summary>
    public VaultOpenRequest? StartupRequest { get; init; }

    /// <summary>How many arguments were ignored because they weren't a valid request (logged as a count, never their text).</summary>
    public int IgnoredArgumentCount { get; init; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DesktopLog.Start();
        var log = DesktopLog.For("App");
        // Type, message and stack only: never an exception's Data or a view model's state.
        DispatcherUnhandledException += (_, x) =>
        {
            log.Fatal("Unhandled on the UI thread: {Exception}", DesktopLog.Describe(x.Exception));
            x.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, x) => log.Fatal("Unhandled: {Exception}", DesktopLog.Describe(x.ExceptionObject as Exception));
        TaskScheduler.UnobservedTaskException += (_, x) =>
        {
            log.Error("Unobserved task exception: {Exception}", DesktopLog.Describe(x.Exception));
            x.SetObserved();
        };

        // The Global tab's manifest exists from the first run on, even before any app has run.
        MindAtticGlobalSettings global;
        try { global = MindAtticGlobalSettings.Store().LoadOrSeed(); }
        catch (Exception ex)
        {
            log.Warning("Could not seed the Global settings: {Type}", ex.GetType().Name);
            global = new MindAtticGlobalSettings();
        }
        ApplyTheme(global.Theme);
        RegisterProtocol();

        services = DesktopServices.Build();
        var main = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = main, State = services.GetRequiredService<DesktopStateKeeper>() };
        window.RestorePlacement();
        MaWindow.SetThemedTitleBar(window, true);
        MainWindow = window;
        window.Show();
        log.Information("Started {Version}", typeof(App).Assembly.GetName().Version);
        if (IgnoredArgumentCount > 0)
            log.Warning("Ignored {Count} startup argument(s): only --app <id> [--setting <name>] or one mindattic-vault:// link is accepted",
                IgnoredArgumentCount);

        var ready = main.InitializeAsync();

        // The second-start hand-off and VaultSettings.Open: a current-user-only pipe, never a network listener. Listening
        // starts at once; a request that arrives while the tabs are still loading waits for them.
        _ = VaultDesktopPipeServer.RunAsync(
            request => Dispatcher.BeginInvoke(async () =>
            {
                await ready;
                await main.OpenAsync(request);
            }),
            reason => DesktopLog.For("Open").Warning("Ignored a pipe request: {Reason}", reason),
            cancellationToken: stopListening.Token);

        await ready;
        if (StartupRequest is not null) await main.OpenAsync(StartupRequest);
        await main.CheckFolderAccessAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        stopListening.Cancel();
        services?.Dispose(); // flushes the remembered state
        DesktopLog.Stop();
        base.OnExit(e);
    }

    /// <summary>Points the per-user mindattic-vault:// handler at this exe when it's missing or stale (HKCU only).</summary>
    private static void RegisterProtocol()
    {
        var log = DesktopLog.For("Protocol");
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var outcome = new ProtocolRegistration(new CurrentUserRegistry()).Ensure(exe, ProtocolRegistration.IsInstalledCopy(exe));
            if (outcome != ProtocolRegistrationOutcome.AlreadyCurrent) log.Information("mindattic-vault:// handler: {Outcome}", outcome);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            log.Warning("Could not register the mindattic-vault:// handler: {Type}", ex.GetType().Name);
        }
    }

    /// <summary>Follows the shared Global theme: Dark, Light, or System (the Windows app theme).</summary>
    private static void ApplyTheme(string? theme)
    {
        var light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase) && WindowsUsesLightTheme());
        MaTheme.Set(light ? MaThemeMode.Light : MaThemeMode.Dark);
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}
