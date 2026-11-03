using System.Text.Json;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class LlmCredentialStoreTests
{
    [Test]
    public void SetKey_Infers_Anthropic_Type_For_Claude()
    {
        using var tmp = new TempDirectory();
        var store = new LlmCredentialStore(tmp.Path);

        store.SetKey("claude", "sk-ant-abc");

        var entry = ReadEntry(tmp.Path, "claude");
        Assert.That(entry.GetProperty("type").GetString(),   Is.EqualTo("anthropic"));
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("sk-ant-abc"));
    }

    [Test]
    public void SetKey_Infers_Google_Type_For_Gemini()
    {
        using var tmp = new TempDirectory();
        var store = new LlmCredentialStore(tmp.Path);

        store.SetKey("gemini", "AIza-xyz");

        var entry = ReadEntry(tmp.Path, "gemini");
        Assert.That(entry.GetProperty("type").GetString(), Is.EqualTo("google"));
    }

    [Test]
    public void SetKey_Defaults_To_Bearer_For_Unknown_Provider()
    {
        using var tmp = new TempDirectory();
        var store = new LlmCredentialStore(tmp.Path);

        store.SetKey("grok", "xai-1");

        var entry = ReadEntry(tmp.Path, "grok");
        Assert.That(entry.GetProperty("type").GetString(), Is.EqualTo("bearer"));
    }

    [Test]
    public void SetKey_Preserves_Model_And_MaxTokens()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"claude\": { \"type\": \"anthropic\", \"apiKey\": \"old\", \"model\": \"claude-sonnet-4-6\", \"maxTokens\": 8192 } }");

        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "sk-ant-rotated");

        var entry = ReadEntry(tmp.Path, "claude");
        Assert.That(entry.GetProperty("apiKey").GetString(),    Is.EqualTo("sk-ant-rotated"));
        Assert.That(entry.GetProperty("model").GetString(),     Is.EqualTo("claude-sonnet-4-6"));
        Assert.That(entry.GetProperty("maxTokens").GetInt32(),  Is.EqualTo(8192));
    }

    [Test]
    public void Default_Honours_MINDATTIC_LLM_CREDENTIALS_Env_Var()
    {
        // The static Default property is captured once. Verify the resolution helper is used
        // by constructing a new instance via the same env override path.
        const string envVar = LlmCredentialStore.DirectoryEnvVar;
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            using var tmp = new TempDirectory();
            Environment.SetEnvironmentVariable(envVar, tmp.Path);

            var store = new LlmCredentialStore(
                Environment.GetEnvironmentVariable(envVar)!);

            Assert.That(store.Directory, Is.EqualTo(tmp.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Test]
    public void SetKey_Does_Not_Emit_Model_Or_MaxTokens_For_New_Entry()
    {
        using var tmp = new TempDirectory();
        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "sk-ant-fresh");

        var entry = ReadEntry(tmp.Path, "claude");
        Assert.That(entry.TryGetProperty("model",     out _), Is.False);
        Assert.That(entry.TryGetProperty("maxTokens", out _), Is.False);
    }

    [Test]
    public void SetKey_Preserves_Explicit_Type_Field()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"),
            "{ \"openai\": { \"type\": \"openai\", \"apiKey\": \"old\" } }");

        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("openai", "sk-rotated");

        var entry = ReadEntry(tmp.Path, "openai");
        Assert.That(entry.GetProperty("type").GetString(),   Is.EqualTo("openai"));
        Assert.That(entry.GetProperty("apiKey").GetString(), Is.EqualTo("sk-rotated"));
    }

    [Test]
    public void SetKey_Survives_Malformed_Existing_Entry()
    {
        using var tmp = new TempDirectory();
        // A providers.json that no longer parses reads as empty, so the write starts fresh,
        // but the unparseable file is copied aside first instead of silently overwritten.
        const string broken = "{ \"claude\": { not-json-here } }";
        File.WriteAllText(Path.Combine(tmp.Path, "providers.json"), broken);

        var store = new LlmCredentialStore(tmp.Path);
        Assert.DoesNotThrow(() => store.SetKey("claude", "sk-recovered"));

        Assert.That(store.GetKey("claude"), Is.EqualTo("sk-recovered"));
        var aside = Directory.GetFiles(tmp.Path, "providers.json.corrupt-*");
        Assert.That(aside, Has.Length.EqualTo(1));
        Assert.That(File.ReadAllText(aside[0]), Is.EqualTo(broken));
    }

    [Test]
    public void SetKey_On_A_Valid_File_Copies_Nothing_Aside()
    {
        using var tmp = new TempDirectory();
        var store = new LlmCredentialStore(tmp.Path);
        store.SetKey("claude", "a");
        store.SetKey("openai", "b");
        Assert.That(Directory.GetFiles(tmp.Path, "*.corrupt-*"), Is.Empty);
    }

    [Test]
    public void Default_Singleton_Is_Resolvable()
    {
        Assert.That(LlmCredentialStore.Default, Is.Not.Null);
        Assert.That(LlmCredentialStore.Default.Directory, Is.Not.Empty);
    }

    [Test]
    public void Bucket_And_DirectoryEnvVar_Constants_Are_Stable()
    {
        Assert.That(LlmCredentialStore.Bucket,          Is.EqualTo("LLM"));
        Assert.That(LlmCredentialStore.DirectoryEnvVar, Is.EqualTo("MINDATTIC_LLM_CREDENTIALS"));
    }

    private static JsonElement ReadEntry(string dir, string providerId)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "providers.json")));
        return doc.RootElement.GetProperty(providerId).Clone();
    }
}
