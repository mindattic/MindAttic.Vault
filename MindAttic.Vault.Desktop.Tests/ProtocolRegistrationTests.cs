using MindAttic.Vault.Desktop.Services;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class ProtocolRegistrationTests
{
    private sealed class FakeRegistry : IUserRegistry
    {
        public Dictionary<(string Key, string Name), string> Values { get; } = new();
        public int Writes { get; private set; }
        public string? GetString(string subKey, string? name) => Values.TryGetValue((subKey, name ?? ""), out var v) ? v : null;
        public void SetString(string subKey, string? name, string value)
        {
            Writes++;
            Values[(subKey, name ?? "")] = value;
        }
    }

    private const string Installed = @"C:\Users\me\AppData\Local\MindAttic\Vault.Desktop\MindAttic.Vault.Desktop.Wpf.exe";
    private const string DevBuild = @"D:\src\bin\Debug\MindAttic.Vault.Desktop.Wpf.exe";

    [Test]
    public void A_missing_registration_is_written_under_HKCU_classes_only()
    {
        var registry = new FakeRegistry();

        var outcome = new ProtocolRegistration(registry, _ => false).Ensure(Installed, takeOver: true);

        Assert.That(outcome, Is.EqualTo(ProtocolRegistrationOutcome.Registered));
        Assert.That(registry.GetString(@"Software\Classes\mindattic-vault\shell\open\command", null),
            Is.EqualTo($"\"{Installed}\" \"%1\""));
        Assert.That(registry.GetString(@"Software\Classes\mindattic-vault", "URL Protocol"), Is.EqualTo(""));
        Assert.That(registry.GetString(@"Software\Classes\mindattic-vault", null), Is.EqualTo("URL:MindAttic Vault"));
        Assert.That(registry.Values.Keys.All(k => k.Key.StartsWith(@"Software\Classes\mindattic-vault", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Registering_again_is_a_no_op()
    {
        var registry = new FakeRegistry();
        var registration = new ProtocolRegistration(registry, _ => true);
        registration.Ensure(Installed, takeOver: true);
        var writes = registry.Writes;

        Assert.That(registration.Ensure(Installed, takeOver: false), Is.EqualTo(ProtocolRegistrationOutcome.AlreadyCurrent));
        Assert.That(registry.Writes, Is.EqualTo(writes));
    }

    [Test]
    public void A_dev_build_leaves_an_existing_installed_copy_registered()
    {
        var registry = new FakeRegistry();
        new ProtocolRegistration(registry, _ => true).Ensure(Installed, takeOver: true);

        var outcome = new ProtocolRegistration(registry, path => path == Installed).Ensure(DevBuild, takeOver: false);

        Assert.That(outcome, Is.EqualTo(ProtocolRegistrationOutcome.KeptOtherCopy));
        Assert.That(ProtocolRegistration.ExeOf(registry.GetString(ProtocolRegistration.CommandKey, null)), Is.EqualTo(Installed));
    }

    [Test]
    public void A_registration_pointing_at_a_deleted_exe_is_replaced()
    {
        var registry = new FakeRegistry();
        new ProtocolRegistration(registry, _ => true).Ensure(Installed, takeOver: true);

        var outcome = new ProtocolRegistration(registry, _ => false).Ensure(DevBuild, takeOver: false);

        Assert.That(outcome, Is.EqualTo(ProtocolRegistrationOutcome.Registered));
        Assert.That(ProtocolRegistration.ExeOf(registry.GetString(ProtocolRegistration.CommandKey, null)), Is.EqualTo(DevBuild));
    }

    [Test]
    public void The_installed_copy_takes_the_registration_back()
    {
        var registry = new FakeRegistry();
        new ProtocolRegistration(registry, _ => true).Ensure(DevBuild, takeOver: true);

        Assert.That(new ProtocolRegistration(registry, _ => true).Ensure(Installed, takeOver: true),
            Is.EqualTo(ProtocolRegistrationOutcome.Registered));
        Assert.That(ProtocolRegistration.ExeOf(registry.GetString(ProtocolRegistration.CommandKey, null)), Is.EqualTo(Installed));
    }

    [TestCase("\"C:\\a b\\x.exe\" \"%1\"", "C:\\a b\\x.exe")]
    [TestCase("C:\\x.exe %1", "C:\\x.exe")]
    [TestCase("", null)]
    public void The_registered_exe_is_read_from_the_command(string command, string? exe) =>
        Assert.That(ProtocolRegistration.ExeOf(command), Is.EqualTo(exe));

    [Test]
    public void The_installed_copy_is_recognized_by_the_default_path()
    {
        using var sandbox = new VaultSandbox();
        Assert.That(ProtocolRegistration.IsInstalledCopy(Launch.VaultDesktopContract.DefaultExePath), Is.True);
        Assert.That(ProtocolRegistration.IsInstalledCopy(DevBuild), Is.False);
    }
}
