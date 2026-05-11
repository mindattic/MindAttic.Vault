using MindAttic.Vault.Paths;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class EnvironmentOverlayTests
{
    private const string Var = "MINDATTIC_VAULT_TEST_OVERLAY";
    private string? original;

    [SetUp]
    public void SetUp() => original = Environment.GetEnvironmentVariable(Var);

    [TearDown]
    public void TearDown() => Environment.SetEnvironmentVariable(Var, original);

    [Test]
    public void Apply_Invokes_Setter_When_Var_Has_Value()
    {
        Environment.SetEnvironmentVariable(Var, "yes");
        string? captured = null;
        EnvironmentOverlay.Apply(Var, v => captured = v);
        Assert.That(captured, Is.EqualTo("yes"));
    }

    [Test]
    public void Apply_Skips_Setter_When_Var_Unset()
    {
        Environment.SetEnvironmentVariable(Var, null);
        var called = false;
        EnvironmentOverlay.Apply(Var, _ => called = true);
        Assert.That(called, Is.False);
    }

    [Test]
    public void Apply_Skips_Setter_When_Var_Whitespace()
    {
        Environment.SetEnvironmentVariable(Var, "   ");
        var called = false;
        EnvironmentOverlay.Apply(Var, _ => called = true);
        Assert.That(called, Is.False);
    }

    [Test]
    public void ApplyAll_Walks_Every_Pair()
    {
        Environment.SetEnvironmentVariable(Var, "alpha");
        var hits = new List<string>();
        EnvironmentOverlay.ApplyAll(new (string, Action<string>)[]
        {
            (Var,         v => hits.Add("a:" + v)),
            ("MINDATTIC_VAULT_NEVER_SET_XYZ", v => hits.Add("b:" + v)),
            (Var,         v => hits.Add("c:" + v)),
        });
        Assert.That(hits, Is.EquivalentTo(new[] { "a:alpha", "c:alpha" }));
    }

    [Test]
    public void Apply_Returns_When_EnvVar_Is_Empty_Or_Null()
    {
        var called = false;
        EnvironmentOverlay.Apply("",    _ => called = true);
        EnvironmentOverlay.Apply("   ", _ => called = true);
        EnvironmentOverlay.Apply(null!, _ => called = true);
        Assert.That(called, Is.False);
    }

    [Test]
    public void Apply_Returns_When_Setter_Is_Null()
    {
        // Should not throw — null setter is silently ignored even when the var is set.
        Environment.SetEnvironmentVariable(Var, "value");
        Assert.DoesNotThrow(() => EnvironmentOverlay.Apply(Var, null!));
    }

    [Test]
    public void ApplyAll_Returns_Silently_For_Null_Pairs_Enumerable()
    {
        Assert.DoesNotThrow(() => EnvironmentOverlay.ApplyAll(null!));
    }
}
