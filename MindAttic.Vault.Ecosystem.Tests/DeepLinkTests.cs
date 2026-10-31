using Microsoft.Win32;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Launch;
using NUnit.Framework;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>
/// 7. Every app's cog and link reach its tab: <c>VaultSettings.Link</c> and the <c>--app/--setting</c> command line
/// parse back to the same app and setting for every app and every setting, and <c>mindattic-vault://</c> is registered
/// for the current user and points at the installed Desktop.
/// </summary>
[TestFixture, Category("Ecosystem")]
public sealed class DeepLinkTests
{
    [TestCaseSource(typeof(Ecosystem), nameof(Ecosystem.AppCases))]
    public void Link_and_command_line_round_trip_to_the_app_and_every_setting(string appId)
    {
        var app = Ecosystem.App(appId);
        Assert.That(VaultOpenRequest.IsValidAppId(app.AppId), Is.True, $"{appId}: not a valid deep-link app id");

        var problems = new List<string>();
        Check(app.AppId, null, problems);
        foreach (var field in app.Manifest.AllFields().Where(f => !f.Name.Contains(":*")))
            Check(app.AppId, field.Name, problems);
        Ecosystem.AssertNone(problems, $"{appId} deep links");
    }

    private static void Check(string appId, string? setting, List<string> problems)
    {
        var what = setting ?? "(tab)";
        string link;
        try { link = VaultSettings.Link(appId, setting); }
        catch (ArgumentException) { problems.Add($"{what}: Link refused it"); return; }

        if (!link.StartsWith(VaultOpenRequest.Scheme + "://open?", StringComparison.Ordinal)) problems.Add($"{what}: link isn't a {VaultOpenRequest.Scheme}://open link");
        if (!VaultOpenRequest.TryParseUrl(link, out var fromUrl)) problems.Add($"{what}: link doesn't parse back");
        else if (fromUrl!.AppId != appId || fromUrl.Setting != setting) problems.Add($"{what}: link parses to a different app or setting");
        // The protocol handler starts the exe with the URL as its one argument.
        if (!VaultOpenRequest.TryParseArgs(new[] { link }, out var fromHandler) || fromHandler!.AppId != appId || fromHandler.Setting != setting)
            problems.Add($"{what}: the protocol handler's argument doesn't parse back");

        var args = new VaultOpenRequest(appId, setting).ToArgs();
        if (!VaultOpenRequest.TryParseArgs(args, out var fromArgs) || fromArgs!.AppId != appId || fromArgs.Setting != setting)
            problems.Add($"{what}: --app/--setting don't parse back");
    }

    [Test]
    public void The_protocol_is_registered_for_the_current_user_and_points_at_the_installed_exe()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ProtocolRegistration.ClassesKey);
        Assert.That(root, Is.Not.Null, $"HKCU\\{ProtocolRegistration.ClassesKey} is missing: run the installed Desktop once (or with --register-protocol)");
        Assert.That(root!.GetValue("URL Protocol"), Is.Not.Null, "the 'URL Protocol' marker is missing");

        using var command = Registry.CurrentUser.OpenSubKey(ProtocolRegistration.CommandKey);
        var text = command?.GetValue("") as string;
        Assert.That(text, Is.Not.Null.And.Not.Empty, "no shell\\open\\command");
        var exe = ProtocolRegistration.ExeOf(text);
        // The run points the Vault roots at a guard folder; resolve the installed exe against the real local root.
        var installed = Ecosystem.WithRealRoots(() => VaultDesktopContract.DefaultExePath);
        var opened = Ecosystem.WithRealRoots(() => VaultDesktopContract.FindExe());
        Assert.Multiple(() =>
        {
            Assert.That(installed, Is.EqualTo(Path.Combine(Ecosystem.RealLocal, VaultDesktopContract.InstallFolder, VaultDesktopContract.ExeName)).IgnoreCase);
            Assert.That(exe, Is.EqualTo(installed).IgnoreCase, "the handler doesn't start the installed exe");
            Assert.That(File.Exists(exe), Is.True, "the registered exe doesn't exist");
            Assert.That(text, Is.EqualTo(ProtocolRegistration.CommandFor(exe!)).IgnoreCase, "the command doesn't pass the URL as one quoted argument");
            Assert.That(opened, Is.EqualTo(exe).IgnoreCase, "VaultSettings.Open would start a different exe than the protocol handler");
        });
        // The registration's check, run against the real registry without writing: it must find nothing to do.
        var probe = new ReadOnlyRegistry();
        Assert.That(new ProtocolRegistration(probe).Ensure(exe!, takeOver: false), Is.EqualTo(ProtocolRegistrationOutcome.AlreadyCurrent));
        Assert.That(probe.Writes, Is.Zero);
    }

    /// <summary>The real HKCU for reads; refuses to write.</summary>
    private sealed class ReadOnlyRegistry : IUserRegistry
    {
        private readonly CurrentUserRegistry real = new();
        public int Writes { get; private set; }
        public string? GetString(string subKey, string? name) => real.GetString(subKey, name);
        public void SetString(string subKey, string? name, string value) => Writes++;
    }
}
