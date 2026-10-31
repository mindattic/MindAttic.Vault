using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class CompositionTests
{
    [Test]
    public void The_container_builds_and_resolves_the_main_view_model()
    {
        using var sandbox = new VaultSandbox();
        using var services = DesktopServices.Build();

        var main = services.GetRequiredService<MainViewModel>();
        Assert.That(main.Profiles.Select(p => p.Id), Is.EqualTo(new[] { "Desktop", "Azure" }), "Desktop first, then every registered Profile");
    }

    [Test]
    public void Crash_text_has_type_message_and_stack_only()
    {
        Exception caught;
        try { throw new InvalidOperationException("outer", new ArgumentException("inner")) { Data = { ["vm"] = "state dump" } }; }
        catch (Exception ex) { caught = ex; }

        var text = DesktopLog.Describe(caught);

        Assert.That(text, Does.Contain("System.InvalidOperationException: outer"));
        Assert.That(text, Does.Contain("System.ArgumentException: inner"));
        Assert.That(text, Does.Contain(nameof(Crash_text_has_type_message_and_stack_only)));
        Assert.That(text, Does.Not.Contain("state dump"));
    }
}
