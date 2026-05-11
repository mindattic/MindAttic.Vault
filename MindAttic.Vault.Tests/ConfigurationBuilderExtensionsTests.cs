using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Configuration;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

/// <summary>
/// Locks down the <see cref="ConfigurationBuilderExtensions.AddMindAtticVaultFiles"/>
/// extension behaviour: argument validation, fluent return, and configure callback.
/// </summary>
[TestFixture]
public class ConfigurationBuilderExtensionsTests
{
    [Test]
    public void AddMindAtticVaultFiles_Throws_For_Null_Builder()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IConfigurationBuilder)null!).AddMindAtticVaultFiles());
    }

    [Test]
    public void AddMindAtticVaultFiles_Returns_Same_Builder_For_Chaining()
    {
        var builder = new ConfigurationBuilder();
        var returned = builder.AddMindAtticVaultFiles();
        Assert.That(returned, Is.SameAs(builder));
    }

    [Test]
    public void AddMindAtticVaultFiles_Invokes_Configure_Callback()
    {
        using var tmp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "GitHub"));
        File.WriteAllText(Path.Combine(tmp.Path, "GitHub", "providers.json"),
            """ { "primary": { "apiKey": "ghp_abc" } } """);

        var builder = new ConfigurationBuilder();
        builder.AddMindAtticVaultFiles(source =>
        {
            source.RoamingRoot = tmp.Path;
            source.Buckets = new[] { "GitHub" };
        });

        var config = builder.Build();
        Assert.That(config["MindAttic:Vault:GitHub:primary:apiKey"], Is.EqualTo("ghp_abc"));
    }

    [Test]
    public void AddMindAtticVaultFiles_Without_Configure_Uses_Defaults()
    {
        // Without a configure callback the source binds to the real %APPDATA%
        // root — we just confirm the call doesn't throw and doesn't break the
        // overall builder chain.
        var builder = new ConfigurationBuilder();
        Assert.DoesNotThrow(() => builder.AddMindAtticVaultFiles());
        Assert.DoesNotThrow(() => builder.Build());
    }
}
