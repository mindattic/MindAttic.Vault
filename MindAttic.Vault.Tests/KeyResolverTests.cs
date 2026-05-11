using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Credentials;
using MindAttic.Vault.Resolution;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class KeyResolverTests
{
    [Test]
    public void Resolve_Returns_Null_When_Chain_Empty()
    {
        var resolver = new KeyResolver();
        Assert.That(resolver.Resolve("anything"), Is.Null);
    }

    [Test]
    public void Resolve_Returns_First_Non_Empty_Step()
    {
        var resolver = KeyResolver
            .From(_ => null)
            .Then(_ => "")
            .Then(id => id == "claude" ? "winner" : null)
            .Then(_ => "should-not-reach");

        Assert.That(resolver.Resolve("claude"), Is.EqualTo("winner"));
    }

    [Test]
    public void Resolve_Trims_Returned_Value()
    {
        var resolver = KeyResolver.From(_ => "  padded  ");
        Assert.That(resolver.Resolve("anything"), Is.EqualTo("padded"));
    }

    [Test]
    public void Resolve_Survives_Throwing_Step()
    {
        var resolver = KeyResolver
            .From(_ => throw new InvalidOperationException("bang"))
            .Then(_ => "fallback");

        Assert.That(resolver.Resolve("anything"), Is.EqualTo("fallback"));
    }

    [Test]
    public void Explicit_Step_Matches_Provider_Id_Case_Insensitively()
    {
        var step = KeyResolver.Explicit("Claude", "explicit-key");
        Assert.That(step("claude"), Is.EqualTo("explicit-key"));
        Assert.That(step("gemini"), Is.Null);
    }

    [Test]
    public void Env_Step_Reads_Env_Var()
    {
        const string envVar = "MINDATTIC_VAULT_TEST_ENV_STEP";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "from-env");
            var step = KeyResolver.Env("openai", envVar);
            Assert.That(step("openai"), Is.EqualTo("from-env"));
            Assert.That(step("claude"), Is.Null);
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    [Test]
    public void EnvByConvention_Reads_UpperSnake_Provider_Plus_Suffix()
    {
        const string envVar = "ALPACA_PAPER_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "PK-conv");
            var step = KeyResolver.EnvByConvention();
            Assert.That(step("alpaca-paper"), Is.EqualTo("PK-conv"));
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    [Test]
    public void FromStore_Step_Delegates_To_Credential_Store()
    {
        using var tmp = new TempDirectory();
        var inner = new CredentialStore(tmp.Path);
        inner.SetKey("acme", "store-key");

        var resolver = KeyResolver.From(KeyResolver.FromStore(inner));
        Assert.That(resolver.Resolve("acme"), Is.EqualTo("store-key"));
    }

    [Test]
    public void Full_Chain_Honours_Priority_Order()
    {
        const string envVar = "ALPHA_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "from-env");

            using var tmp = new TempDirectory();
            var inner = new CredentialStore(tmp.Path);
            inner.SetKey("alpha", "from-store");

            var resolver = KeyResolver
                .From(KeyResolver.Explicit("alpha", "from-explicit"))
                .Then(KeyResolver.EnvByConvention())
                .Then(KeyResolver.FromStore(inner));

            Assert.That(resolver.Resolve("alpha"), Is.EqualTo("from-explicit"));
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    // ── Argument validation ─────────────────────────────────────────────────────

    [Test]
    public void From_Throws_For_Null_Resolver()
    {
        Assert.Throws<ArgumentNullException>(() => KeyResolver.From(null!));
    }

    [Test]
    public void Then_Throws_For_Null_Resolver()
    {
        var resolver = new KeyResolver();
        Assert.Throws<ArgumentNullException>(() => resolver.Then(null!));
    }

    [Test]
    public void Resolve_Empty_Provider_Id_Returns_Null()
    {
        var resolver = KeyResolver.From(_ => "would-have-returned");
        Assert.That(resolver.Resolve(""),    Is.Null);
        Assert.That(resolver.Resolve("   "), Is.Null);
        Assert.That(resolver.Resolve(null!), Is.Null);
    }

    // ── Step builder edge cases ─────────────────────────────────────────────────

    [Test]
    public void Explicit_Empty_Or_Whitespace_Value_Yields_No_Match()
    {
        Assert.That(KeyResolver.Explicit("claude", "")    ("claude"), Is.Null);
        Assert.That(KeyResolver.Explicit("claude", "   ") ("claude"), Is.Null);
        Assert.That(KeyResolver.Explicit("claude", null)  ("claude"), Is.Null);
    }

    [Test]
    public void Env_Step_Returns_Null_For_Empty_EnvVar_Name()
    {
        var step = KeyResolver.Env("openai", "");
        Assert.That(step("openai"), Is.Null);
    }

    [Test]
    public void Env_Step_Returns_Null_When_Env_Value_Whitespace()
    {
        const string envVar = "MINDATTIC_VAULT_TEST_WHITESPACE";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "   ");
            var step = KeyResolver.Env("openai", envVar);
            Assert.That(step("openai"), Is.Null);
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    [Test]
    public void EnvByConvention_Empty_Provider_Id_Returns_Null()
    {
        var step = KeyResolver.EnvByConvention();
        Assert.That(step(""),    Is.Null);
        Assert.That(step("   "), Is.Null);
    }

    [Test]
    public void EnvByConvention_Honours_Custom_Suffix()
    {
        const string envVar = "OPENAI_TOKEN";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "k");
            var step = KeyResolver.EnvByConvention("_TOKEN");
            Assert.That(step("openai"), Is.EqualTo("k"));
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    [Test]
    public void EnvByConvention_Normalises_Non_Alphanumeric_Characters()
    {
        const string envVar = "ALPACA_PAPER_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "PK");
            var step = KeyResolver.EnvByConvention();
            Assert.That(step("alpaca-paper"),  Is.EqualTo("PK"));
            Assert.That(step("alpaca.paper"),  Is.EqualTo("PK")); // dots → underscores too.
            Assert.That(step("ALPACA_PAPER"),  Is.EqualTo("PK")); // already-normalised input.
        }
        finally { Environment.SetEnvironmentVariable(envVar, original); }
    }

    [Test]
    public void FromStore_Null_Store_Returns_Null()
    {
        var step = KeyResolver.FromStore(null!);
        Assert.That(step("anything"), Is.Null);
    }

    [Test]
    public void FromConfiguration_Throws_For_Null_Configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => KeyResolver.FromConfiguration(null!, MindAttic.Vault.Configuration.VaultConfigurationKeys.LlmSection));
    }

    [Test]
    public void FromConfiguration_Throws_For_Empty_Bucket_Section()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.Throws<ArgumentException>(() => KeyResolver.FromConfiguration(config, ""));
        Assert.Throws<ArgumentException>(() => KeyResolver.FromConfiguration(config, "   "));
    }

    [Test]
    public void FromConfiguration_Empty_Provider_Id_Returns_Null()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MindAttic:Vault:LLM:claude:apiKey"] = "k"
            })
            .Build();

        var step = KeyResolver.FromConfiguration(config, MindAttic.Vault.Configuration.VaultConfigurationKeys.LlmSection);
        Assert.That(step(""),    Is.Null);
        Assert.That(step("   "), Is.Null);
    }
}
