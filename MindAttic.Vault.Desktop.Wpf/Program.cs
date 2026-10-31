using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Launch;

namespace MindAttic.Vault.Desktop;

/// <summary>
/// The entry point. One Vault.Desktop per user: a second start hands its <c>--app/--setting</c> request (or
/// <c>mindattic-vault://</c> link) to the running one over the current-user-only pipe and exits before WPF loads.
/// <c>--register-protocol</c> (run.ps1) registers the per-user protocol handler for this exe and exits.
/// </summary>
public static class Program
{
    public const string RegisterProtocolSwitch = "--register-protocol";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [RegisterProtocolSwitch]) return RegisterProtocol();

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

    private static int RegisterProtocol()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            new ProtocolRegistration(new CurrentUserRegistry()).Ensure(exe, takeOver: true);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
