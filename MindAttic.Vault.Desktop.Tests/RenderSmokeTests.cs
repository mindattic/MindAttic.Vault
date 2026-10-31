using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

/// <summary>
/// Renders the real window off-screen, one PNG per tab, into <c>VAULT_DESKTOP_RENDER_OUT</c> (or the test folder), to
/// look at the layout without starting the app. Explicit: it builds a WPF Application, which a process can do once.
/// </summary>
[TestFixture, Explicit("Renders PNGs to look at; run on demand")]
public sealed class RenderSmokeTests
{
    [Test, Apartment(ApartmentState.STA)]
    public void Every_tab_renders()
    {
        using var sandbox = new VaultSandbox();
        Samples.Install<AlphaSettings>(sandbox.Roaming);
        var beta = Samples.Beta;
        beta.Credentials[0].Required = true; // OpenAI has no key: Get Started shows
        Settings.Manifest.SettingsManifestFile.WriteIfChanged(Path.Combine(sandbox.Roaming, "Beta"), beta);
        var keys = NamedKeyStore.ForRoamingRoot();
        keys.Add("claude", "key1", "sk-render-test-1");
        keys.Add("claude", "2026-10-31 Alpha", "sk-render-test-2");
        keys.Assignments.Set("Alpha", "claude", new[] { "2026-10-31 Alpha", "key1" });

        var app = new App();
        app.InitializeComponent();
        var context = new DesktopContext(new FakeDialogs(), new InlineDispatcher(), KeyHealthStore.ForLocalRoot(), TimeProvider.System, new SqliteAuditLog());
        var vm = new MainViewModel(new IProfile[] { new DesktopProfile() }, new FileManifestCatalog(), context, new KeyOperations(context, new FakeKeyTester()), getStartedSources: Samples.NoImports);
        vm.InitializeAsync().GetAwaiter().GetResult();

        var window = new MainWindow
        {
            DataContext = vm, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        var outDir = Environment.GetEnvironmentVariable("VAULT_DESKTOP_RENDER_OUT") ?? TestContext.CurrentContext.WorkDirectory;
        Directory.CreateDirectory(outDir);
        foreach (var tab in vm.Tabs.ToList())
        {
            vm.SelectedTab = tab;
            Pump();
            Save((FrameworkElement)window.Content, Path.Combine(outDir, $"render-{tab.Id.Replace(':', '-')}.png"));
        }
        window.Close();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }

    private static void Save(FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle((Brush)Application.Current.FindResource("MaChrome"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
