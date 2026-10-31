using System.Text.RegularExpressions;
using MindAttic.Vault.Desktop.Azure.Keys;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Azure.Tests;

/// <summary>Contract tests for the one function that names Key Vault secrets.</summary>
[TestFixture]
public sealed class SecretNamesTests
{
    private static readonly Regex Shape = new("^llm-[a-z0-9]+(-[a-z0-9]+)*-[0-9a-f]{6}$");

    private static readonly string[] Awkward =
    {
        "key1",
        "2026-10-31 IdiotProof",
        "  padded name  ",
        "Café crème",
        "日本語 キー",
        "😀 emoji only 😀",
        "😀😀😀",
        "---",
        "a,b*c\\d/e?f#g",
        "tab\tand\nnewline",
        new string('x', 100),
        string.Concat(Enumerable.Repeat("long name with spaces ", 5)).Trim()[..100],
        "Ünïcödé ÅÉÎÕÜ",
        "x",
    };

    [TestCaseSource(nameof(Awkward))]
    public void Generate_IsAValidKeyVaultSecretName(string keyName)
    {
        foreach (var provider in new[] { "claude", "openai", "google-maps", "Some Unknown Provider!" })
        {
            var name = SecretNames.Generate(provider, keyName);
            Assert.That(SecretNames.IsValid(name), Is.True, name);
            Assert.That(name.Length, Is.LessThanOrEqualTo(127));
            Assert.That(name, Does.Match(Shape));
        }
    }

    [TestCaseSource(nameof(Awkward))]
    public void Generate_IsStable(string keyName)
    {
        Assert.That(SecretNames.Generate("claude", keyName), Is.EqualTo(SecretNames.Generate("claude", keyName)));
        Assert.That(SecretNames.Generate("claude", keyName), Is.EqualTo(SecretNames.Generate("anthropic", keyName)), "aliases share a provider");
        Assert.That(SecretNames.Generate("claude", keyName), Is.EqualTo(SecretNames.Generate("claude", keyName.ToUpperInvariant())),
            "names are unique case-insensitively, so case doesn't change the secret");
    }

    [Test]
    public void Generate_KnownValue_NeverChanges()
    {
        // Pinned: a change here would orphan every existing secret's naming scheme for new keys.
        Assert.That(SecretNames.Generate("claude", "2026-10-31 IdiotProof"), Does.StartWith("llm-claude-2026-10-31-idiotproof-"));
        Assert.That(SecretNames.Generate("claude", "Café crème"), Does.StartWith("llm-claude-cafe-creme-"));
        Assert.That(SecretNames.Generate("claude", "日本語 キー"), Does.StartWith("llm-claude-key-"));
        Assert.That(SecretNames.Generate("google-maps", "x"), Does.StartWith("llm-google-maps-x-"));
    }

    [Test]
    public void Generate_NamesThatSlugTheSame_GetDifferentSecrets()
    {
        var colliding = new[] { "My Key", "my-key", "My_Key", "my  key", "MY.KEY", "my/key", "日本", "中文" };
        var names = colliding.Select(n => SecretNames.Generate("claude", n)).ToList();
        Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count), string.Join("\n", names));
    }

    [Test]
    public void Generate_ProvidersDiffer_AndAttemptsDiffer()
    {
        Assert.That(SecretNames.Generate("claude", "k"), Is.Not.EqualTo(SecretNames.Generate("openai", "k")));
        var attempts = Enumerable.Range(0, 32).Select(i => SecretNames.Generate("claude", "k", i)).ToList();
        Assert.That(attempts.Distinct().Count(), Is.EqualTo(32));
        Assert.That(attempts, Has.All.Matches<string>(SecretNames.IsValid));
    }

    [Test]
    public void Generate_LongInputs_AreCapped()
    {
        var name = SecretNames.Generate(new string('p', 300), new string('n', 300));
        Assert.That(name.Length, Is.LessThanOrEqualTo(127));
        Assert.That(SecretNames.IsValid(name), Is.True);
    }

    [Test]
    public void Generate_RejectsEmptyNames()
    {
        Assert.Throws<ArgumentException>(() => SecretNames.Generate("claude", "  "));
        Assert.Throws<ArgumentException>(() => SecretNames.Generate(" ", "k"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SecretNames.Generate("claude", "k", -1));
    }

    [TestCase("abc-123", true)]
    [TestCase("ABC", true)]
    [TestCase("a_b", false)]
    [TestCase("a b", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValid(string? name, bool valid) => Assert.That(SecretNames.IsValid(name), Is.EqualTo(valid));
}
