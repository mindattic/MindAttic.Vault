using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Launch;

namespace MindAttic.Vault.Desktop;

/// <summary>
/// The entry point. One Vault.Desktop per user: a second start hands its <c>--app/--setting</c> request (or
/// <c>mindattic-vault://</c> link) to the running one over the current-user-only pipe and exits before WPF loads.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Only the exact shapes VaultOpenRequest accepts; anything else is ignored (and logged by the app, without values).
        var valid = VaultOpenRequest.TryParseArgs(args, out var request);

        using var instance = SingleInstance.TryClaim();
        if (instance is null)
        {
            SingleInstance.HandOff(request, new VaultDesktopLauncher(), TimeSpan.FromSeconds(5));
            return 0;
        }

        var app = new App { StartupArgs = args, StartupRequest = request, IgnoredArgumentCount = valid ? 0 : args.Length };
        app.InitializeComponent();
        return app.Run();
    }
}
