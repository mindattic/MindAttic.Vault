using System.Security.AccessControl;
using System.Security.Principal;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;
using NUnit.Framework;

namespace MindAttic.Vault.Desktop.Tests;

[TestFixture]
public sealed class FolderAccessTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string Everyone = "S-1-1-0";
    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string SomeoneElse = "S-1-5-21-1-2-3-1002";

    private sealed class FakeSecurity : IFolderSecurity
    {
        public Dictionary<string, List<AclEntry>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Path, string[] Keep)> Restricted { get; } = new();

        public bool Exists(string path) => Folders.ContainsKey(path);
        public IReadOnlyList<AclEntry> Readers(string path) => Folders[path];

        public void Restrict(string path, IReadOnlyCollection<string> keepSids)
        {
            Restricted.Add((path, keepSids.ToArray()));
            Folders[path] = keepSids.Select(s => new AclEntry(s, s, false)).ToList();
        }
    }

    private static AclEntry Ace(string sid, string name, bool inherited = true) => new(sid, name, inherited);

    private static List<AclEntry> Private() =>
        new() { Ace(Me, "PC\\me"), Ace(FolderAccessCheck.SystemSid, "SYSTEM"), Ace(FolderAccessCheck.AdministratorsSid, "BUILTIN\\Administrators") };

    [Test]
    public void Private_folders_pass_and_missing_folders_are_skipped()
    {
        var security = new FakeSecurity { Folders = { [@"C:\r\LLM"] = Private() } };
        var check = new FolderAccessCheck(security, Me, () => new[] { @"C:\r\LLM", @"C:\r\Vault" });

        Assert.That(check.Check(), Is.Empty);
    }

    [Test]
    public void Everyone_Users_Authenticated_Users_and_other_users_are_flagged()
    {
        var llm = Private();
        llm.Add(Ace(Everyone, "Everyone"));
        llm.Add(Ace(Users, "BUILTIN\\Users"));
        var vault = Private();
        vault.Add(Ace(AuthenticatedUsers, "NT AUTHORITY\\Authenticated Users", inherited: false));
        vault.Add(Ace(SomeoneElse, "PC\\guest"));
        var security = new FakeSecurity { Folders = { [@"C:\r\LLM"] = llm, [@"C:\l\Vault"] = vault } };
        var check = new FolderAccessCheck(security, Me, () => new[] { @"C:\r\LLM", @"C:\l\Vault" });

        var problems = check.Check();

        Assert.That(problems.Select(p => p.Folder), Is.EqualTo(new[] { @"C:\r\LLM", @"C:\l\Vault" }));
        Assert.That(problems[0].Accounts, Is.EqualTo(new[] { "Everyone", "BUILTIN\\Users" }));
        Assert.That(problems[1].Accounts, Is.EqualTo(new[] { "NT AUTHORITY\\Authenticated Users", "PC\\guest" }));
        Assert.That(FolderAccessCheck.Describe(problems), Does.Contain(@"C:\r\LLM is readable by Everyone, BUILTIN\Users"));
    }

    [Test]
    public void Fix_keeps_only_the_user_SYSTEM_and_Administrators()
    {
        var llm = Private();
        llm.Add(Ace(Everyone, "Everyone"));
        var security = new FakeSecurity { Folders = { [@"C:\r\LLM"] = llm, [@"C:\r\Vault"] = Private() } };
        var check = new FolderAccessCheck(security, Me, () => new[] { @"C:\r\LLM", @"C:\r\Vault" });

        var remaining = check.Fix(check.Check());

        Assert.That(remaining, Is.Empty);
        Assert.That(security.Restricted.Single().Path, Is.EqualTo(@"C:\r\LLM"), "only the open folder is touched");
        Assert.That(security.Restricted.Single().Keep, Is.EquivalentTo(new[] { Me, FolderAccessCheck.SystemSid, FolderAccessCheck.AdministratorsSid }));
    }

    [Test]
    public void The_default_folders_are_the_key_and_vault_folders()
    {
        using var sandbox = new VaultSandbox();

        Assert.That(FolderAccessCheck.DefaultFolders(), Is.EqualTo(new[]
        {
            Path.Combine(sandbox.Roaming, "LLM"), Path.Combine(sandbox.Roaming, "Vault"), Path.Combine(sandbox.Local, "Vault"),
        }));
    }

    [Test]
    public async Task The_window_warns_and_the_one_click_fix_clears_it_and_is_audited()
    {
        using var sandbox = new VaultSandbox();
        var llm = Private();
        llm.Add(Ace(Everyone, "Everyone"));
        var security = new FakeSecurity { Folders = { [@"C:\r\LLM"] = llm } };
        var audit = new SqliteAuditLog();
        var context = Samples.Context(localRoot: sandbox.Local, audit: audit);
        var main = new MainViewModel(new Profiles.IProfile[] { new FakeProfile("Desktop") }, new FakeCatalog(), context,
            new KeyOperations(context, new FakeKeyTester()), getStartedSources: Samples.NoImports,
            folderAccess: new FolderAccessCheck(security, Me, () => new[] { @"C:\r\LLM" }));
        await main.InitializeAsync();

        await main.CheckFolderAccessAsync();
        Assert.That(main.HasFolderAccessWarning, Is.True);
        Assert.That(main.FolderAccessWarning, Does.Contain("Everyone"));

        await main.FixFolderAccessAsync();
        Assert.That(main.HasFolderAccessWarning, Is.False);
        Assert.That(audit.ReadAll().Single().Action, Is.EqualTo(AuditActions.AclFix));
    }

    [Test]
    public void The_real_check_and_fix_work_on_an_NTFS_folder()
    {
        using var sandbox = new VaultSandbox();
        var folder = Directory.CreateDirectory(Path.Combine(sandbox.Root, "open")).FullName;
        File.WriteAllText(Path.Combine(folder, "providers.json"), "{}");
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Everyone), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var check = new FolderAccessCheck(new WindowsFolderSecurity(), me, () => new[] { folder });

        var problems = check.Check();
        Assert.That(problems.Single().Accounts, Does.Contain("Everyone"));

        Assert.That(check.Fix(problems), Is.Empty);
        var after = new DirectoryInfo(folder).GetAccessControl();
        Assert.That(after.AreAccessRulesProtected, Is.True, "inheritance is off");
        Assert.That(File.ReadAllText(Path.Combine(folder, "providers.json")), Is.EqualTo("{}"), "the user can still read its keys");
        File.WriteAllText(Path.Combine(folder, "providers.json"), "{ }");
    }
}
