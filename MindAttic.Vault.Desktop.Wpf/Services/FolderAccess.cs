using System.Security.AccessControl;
using System.Security.Principal;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>An allow entry on a folder that lets an account read it (or files in it).</summary>
public sealed record AclEntry(string Sid, string Account, bool Inherited);

/// <summary>A Vault folder other accounts can read, and who.</summary>
public sealed record AclProblem(string Folder, IReadOnlyList<string> Accounts);

/// <summary>Reads and tightens folder ACLs; replaced by a fake in tests.</summary>
public interface IFolderSecurity
{
    bool Exists(string path);

    /// <summary>Every allow entry that grants reading the folder's contents.</summary>
    IReadOnlyList<AclEntry> Readers(string path);

    /// <summary>Disables inheritance and leaves only full control for <paramref name="keepSids"/> (inherited by everything inside).</summary>
    void Restrict(string path, IReadOnlyCollection<string> keepSids);
}

/// <summary>The real NTFS ACLs.</summary>
public sealed class WindowsFolderSecurity : IFolderSecurity
{
    private const FileSystemRights GenericRead = (FileSystemRights)unchecked((int)0x80000000);
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;

    public bool Exists(string path) => Directory.Exists(path);

    public IReadOnlyList<AclEntry> Readers(string path)
    {
        var rules = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var readers = new List<AclEntry>();
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.FileSystemRights & (FileSystemRights.ReadData | GenericRead | GenericAll)) == 0) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            readers.Add(new AclEntry(sid.Value, NameOf(sid), rule.IsInherited));
        }
        return readers;
    }

    public void Restrict(string path, IReadOnlyCollection<string> keepSids)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in keepSids)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static string NameOf(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { return sid.Value; }
        catch (SystemException) { return sid.Value; }
    }
}

/// <summary>
/// The startup check (RFC 0003 Security 5): local keys are plain text, so the folders holding them must not be readable
/// by Everyone, Users, Authenticated Users or any other account. The one-click fix removes those entries and stops
/// inheriting, keeping the current user, SYSTEM and Administrators.
/// </summary>
public sealed class FolderAccessCheck
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string CreatorOwnerSid = "S-1-3-0";

    private readonly IFolderSecurity security;
    private readonly Func<IReadOnlyList<string>> folders;

    public FolderAccessCheck(IFolderSecurity security, string currentUserSid, Func<IReadOnlyList<string>>? folders = null)
    {
        this.security = security ?? throw new ArgumentNullException(nameof(security));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        CurrentUserSid = currentUserSid;
        this.folders = folders ?? DefaultFolders;
    }

    /// <summary>The check for this Windows user over the real folders.</summary>
    public static FolderAccessCheck ForCurrentUser() =>
        new(new WindowsFolderSecurity(), WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID."));

    public string CurrentUserSid { get; }

    /// <summary>%APPDATA%\MindAttic\LLM, %APPDATA%\MindAttic\Vault and %LOCALAPPDATA%\MindAttic\Vault.</summary>
    public static IReadOnlyList<string> DefaultFolders() => new[]
    {
        Path.Combine(VaultPaths.RoamingRoot, "LLM"),
        Path.Combine(VaultPaths.RoamingRoot, "Vault"),
        Path.Combine(VaultPaths.LocalRoot, "Vault"),
    };

    /// <summary>The accounts that may read the folders: the current user, SYSTEM, Administrators (and CREATOR OWNER, which is the creator).</summary>
    public IReadOnlyCollection<string> AllowedSids => new[] { CurrentUserSid, SystemSid, AdministratorsSid, CreatorOwnerSid };

    /// <summary>Each existing folder some other account can read, with those accounts. Empty when all is well.</summary>
    public IReadOnlyList<AclProblem> Check()
    {
        var allowed = new HashSet<string>(AllowedSids, StringComparer.OrdinalIgnoreCase);
        var problems = new List<AclProblem>();
        foreach (var folder in folders())
        {
            if (!security.Exists(folder)) continue;
            var others = security.Readers(folder)
                .Where(e => !allowed.Contains(e.Sid))
                .Select(e => e.Account)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (others.Count > 0) problems.Add(new AclProblem(folder, others));
        }
        return problems;
    }

    /// <summary>Restricts every folder in <paramref name="problems"/>, then checks again. Returns what's still open.</summary>
    public IReadOnlyList<AclProblem> Fix(IReadOnlyList<AclProblem> problems)
    {
        // The current user, SYSTEM and Administrators only; CREATOR OWNER isn't needed once the user has full control.
        var keep = new[] { CurrentUserSid, SystemSid, AdministratorsSid };
        foreach (var problem in problems) security.Restrict(problem.Folder, keep);
        return Check();
    }

    /// <summary>"LLM is readable by Everyone, BUILTIN\Users" for the warning.</summary>
    public static string Describe(IReadOnlyList<AclProblem> problems) =>
        string.Join("; ", problems.Select(p => $"{p.Folder} is readable by {string.Join(", ", p.Accounts)}"));
}
