using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.ViewModels;
using MindAttic.Vault.Settings;

namespace MindAttic.Vault.Desktop;

/// <summary>
/// MindAttic Vault: the only place any MindAttic app's settings or keys are edited (RFC 0003, LAMP).
/// </summary>
public partial class App : Application
{
    // WCAG 1.4.13: a tooltip stays up until the pointer or focus leaves or Esc is pressed.
    static App() => MaToolTips.KeepOpen();

    private ServiceProvider? services;

    /// <summary>The command line, set by <see cref="Program"/>.</summary>
    public string[] StartupArgs { get; init; } = [];

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

        services = DesktopServices.Build();
        var main = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = main };
        MaWindow.SetThemedTitleBar(window, true);
        MainWindow = window;
        window.Show();
        log.Information("Started {Version}", typeof(App).Assembly.GetName().Version);
        await main.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        services?.Dispose();
        DesktopLog.Stop();
        base.OnExit(e);
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
