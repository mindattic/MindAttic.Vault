namespace MindAttic.Vault.Desktop;

/// <summary>The entry point: builds and runs the app.</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App { StartupArgs = args };
        app.InitializeComponent();
        return app.Run();
    }
}
