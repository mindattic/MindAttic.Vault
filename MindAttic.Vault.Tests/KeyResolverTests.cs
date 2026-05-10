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
}
