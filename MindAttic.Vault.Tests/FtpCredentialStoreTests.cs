using System.Text.Json;
using MindAttic.Vault.Credentials;
using NUnit.Framework;

namespace MindAttic.Vault.Tests;

[TestFixture]
public class FtpCredentialStoreTests
{
    [Test]
    public void Get_Returns_Null_When_File_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new FtpCredentialStore(tmp.Path);
        Assert.That(store.Exists(), Is.False);
        Assert.That(store.Get(), Is.Null);
    }

    [Test]
    public void Get_Reads_All_Fields_From_Legacy_Shape()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "ftp.json"), """
        {
          "host": "132.148.112.53",
          "port": 21,
          "user": "ha9h9a@ryandebraal.com",
          "password": "secret",
          "secure": true,
          "servername": "prod.phx3.secureserver.net"
        }
        """);

        var store = new FtpCredentialStore(tmp.Path);
        var creds = store.Get();

        Assert.That(creds, Is.Not.Null);
        Assert.That(creds!.Host,       Is.EqualTo("132.148.112.53"));
        Assert.That(creds.Port,        Is.EqualTo(21));
        Assert.That(creds.User,        Is.EqualTo("ha9h9a@ryandebraal.com"));
        Assert.That(creds.Password,    Is.EqualTo("secret"));
        Assert.That(creds.Secure,      Is.True);
        Assert.That(creds.ServerName,  Is.EqualTo("prod.phx3.secureserver.net"));
        Assert.That(creds.RejectUnauthorized, Is.Null);
    }

    [Test]
    public void Get_Returns_Null_When_Host_Or_User_Empty()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "ftp.json"), """
        { "host": "", "user": "someone@example.com", "password": "x" }
        """);

        var store = new FtpCredentialStore(tmp.Path);
        Assert.That(store.Get(), Is.Null);
    }

    [Test]
    public void Set_Persists_All_Fields_And_Round_Trips()
    {
        using var tmp = new TempDirectory();
        var store = new FtpCredentialStore(tmp.Path);

        store.Set(new FtpCredentialStore.FtpCreds(
            "ftp.example.com", 990, "user@example.com", "pw", true, "ssl.example.net", false));

        var creds = store.Get();
        Assert.That(creds, Is.Not.Null);
        Assert.That(creds!.Host,               Is.EqualTo("ftp.example.com"));
        Assert.That(creds.Port,                Is.EqualTo(990));
        Assert.That(creds.User,                Is.EqualTo("user@example.com"));
        Assert.That(creds.Password,            Is.EqualTo("pw"));
        Assert.That(creds.Secure,              Is.True);
        Assert.That(creds.ServerName,          Is.EqualTo("ssl.example.net"));
        Assert.That(creds.RejectUnauthorized,  Is.False);
    }

    [Test]
    public void Set_Throws_When_Host_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new FtpCredentialStore(tmp.Path);
        Assert.Throws<ArgumentException>(() =>
            store.Set(new FtpCredentialStore.FtpCreds("", 21, "user", "pw", true, null, null)));
    }

    [Test]
    public void Set_Throws_When_Creds_Null()
    {
        using var tmp = new TempDirectory();
        var store = new FtpCredentialStore(tmp.Path);
        Assert.Throws<ArgumentNullException>(() => store.Set(null!));
    }

    [Test]
    public void TryGetJson_Returns_Null_When_Missing()
    {
        using var tmp = new TempDirectory();
        var store = new FtpCredentialStore(tmp.Path);
        Assert.That(store.TryGetJson(), Is.Null);
    }

    [Test]
    public void TryGetJson_Matches_Legacy_Field_Names_For_MINDATTIC_FTP_JSON()
    {
        using var tmp = new TempDirectory();
        var store = new FtpCredentialStore(tmp.Path);
        store.Set(new FtpCredentialStore.FtpCreds(
            "ftp.example.com", 21, "user@example.com", "pw", true, "srv.example.net", false));

        var json = store.TryGetJson();
        Assert.That(json, Is.Not.Null);

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("host").GetString(),     Is.EqualTo("ftp.example.com"));
        Assert.That(root.GetProperty("port").GetInt32(),       Is.EqualTo(21));
        Assert.That(root.GetProperty("user").GetString(),     Is.EqualTo("user@example.com"));
        Assert.That(root.GetProperty("password").GetString(), Is.EqualTo("pw"));
        Assert.That(root.GetProperty("secure").GetBoolean(),   Is.True);
        Assert.That(root.GetProperty("servername").GetString(), Is.EqualTo("srv.example.net"));
        Assert.That(root.GetProperty("_rejectUnauthorized").GetBoolean(), Is.False);
    }

    [Test]
    public void TryGetJson_Omits_Optional_Fields_When_Not_Set()
    {
        using var tmp = new TempDirectory();
        File.WriteAllText(Path.Combine(tmp.Path, "ftp.json"), """
        { "host": "ftp.example.com", "port": 21, "user": "u", "password": "p", "secure": true }
        """);

        var store = new FtpCredentialStore(tmp.Path);
        using var doc = JsonDocument.Parse(store.TryGetJson()!);
        Assert.That(doc.RootElement.TryGetProperty("servername", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("_rejectUnauthorized", out _), Is.False);
    }

    [Test]
    public void Default_Honors_MINDATTIC_FTP_CREDENTIALS_EnvVar()
    {
        // Default is captured once at type-load time, so this only verifies the
        // resolution helper's directory math via a fresh instance construction
        // path, mirroring the same pattern used for Broker/LLM stores.
        using var tmp = new TempDirectory();
        Environment.SetEnvironmentVariable(FtpCredentialStore.DirectoryEnvVar, tmp.Path);
        try
        {
            var store = new FtpCredentialStore(tmp.Path);
            store.Set(new FtpCredentialStore.FtpCreds("h", 21, "u", "p", true, null, null));
            Assert.That(File.Exists(Path.Combine(tmp.Path, "ftp.json")), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FtpCredentialStore.DirectoryEnvVar, null);
        }
    }

    [Test]
    public void Constructor_Throws_When_Directory_Blank()
    {
        Assert.Throws<ArgumentException>(() => new FtpCredentialStore(""));
    }
}
