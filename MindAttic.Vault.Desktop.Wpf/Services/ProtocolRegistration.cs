using Microsoft.Win32;
using MindAttic.Vault.Launch;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>String values under HKEY_CURRENT_USER (and nowhere else); replaced by a dictionary in tests.</summary>
public interface IUserRegistry
{
    /// <summary>The string value <paramref name="name"/> (null = the key's default value) of <paramref name="subKey"/>, or null.</summary>
    string? GetString(string subKey, string? name);

    /// <summary>Creates <paramref name="subKey"/> if needed and sets the string value.</summary>
    void SetString(string subKey, string? name, string value);
}

/// <summary>The real HKCU. Never touches HKLM: registration is per user and needs no elevation.</summary>
public sealed class CurrentUserRegistry : IUserRegistry
{
    public string? GetString(string subKey, string? name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name ?? "") as string;
    }

    public void SetString(string subKey, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
        key.SetValue(name ?? "", value, RegistryValueKind.String);
    }
}

/// <summary>What <see cref="ProtocolRegistration.Ensure"/> did.</summary>
public enum ProtocolRegistrationOutcome
{
    /// <summary>Already points at this exe; nothing written.</summary>
    AlreadyCurrent,
    /// <summary>Was missing, broken or pointed elsewhere; now points at this exe.</summary>
    Registered,
    /// <summary>Points at another existing copy (the installed one) and this is a dev build; left alone.</summary>
    KeptOtherCopy,
}

/// <summary>
/// The per-user <c>mindattic-vault://</c> protocol handler (M.VALT-T56): <c>HKCU\Software\Classes\mindattic-vault</c>
/// with <c>URL Protocol</c> and <c>shell\open\command = "exe" "%1"</c>, so a deployed web app's
/// <c>VaultSettings.Link</c> opens Desktop at a tab. Idempotent: writes only when the registration is missing, broken
/// or points elsewhere. A dev build never takes it over from an installed copy that still exists.
/// </summary>
public sealed class ProtocolRegistration
{
    public const string ClassesKey = @"Software\Classes\" + VaultOpenRequest.Scheme;
    public const string CommandKey = ClassesKey + @"\shell\open\command";
    public const string IconKey = ClassesKey + @"\DefaultIcon";

    private readonly IUserRegistry registry;
    private readonly Func<string, bool> exists;

    public ProtocolRegistration(IUserRegistry registry, Func<string, bool>? exists = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.exists = exists ?? File.Exists;
    }

    /// <summary>The open command for <paramref name="exe"/>: the exe and the URL, each quoted.</summary>
    public static string CommandFor(string exe) => $"\"{exe}\" \"%1\"";

    /// <summary>The exe a registered command starts, or null when it can't be read.</summary>
    public static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    /// <summary>Points the handler at <paramref name="exePath"/> when needed.</summary>
    /// <param name="exePath">The running exe.</param>
    /// <param name="takeOver">True for the installed copy (or <c>--register-protocol</c>): replace a registration of another copy.</param>
    public ProtocolRegistrationOutcome Ensure(string exePath, bool takeOver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var command = registry.GetString(CommandKey, null);
        var protocolMarker = registry.GetString(ClassesKey, "URL Protocol");
        if (string.Equals(command, CommandFor(exePath), StringComparison.OrdinalIgnoreCase) && protocolMarker is not null)
            return ProtocolRegistrationOutcome.AlreadyCurrent;

        var registered = ExeOf(command);
        if (!takeOver && registered is not null && exists(registered)
            && !string.Equals(Path.GetFullPath(registered), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            return ProtocolRegistrationOutcome.KeptOtherCopy;

        registry.SetString(ClassesKey, null, "URL:MindAttic Vault");
        registry.SetString(ClassesKey, "URL Protocol", "");
        registry.SetString(IconKey, null, $"\"{exePath}\",0");
        registry.SetString(CommandKey, null, CommandFor(exePath));
        return ProtocolRegistrationOutcome.Registered;
    }

    /// <summary>True when <paramref name="exePath"/> is the installed copy (<see cref="VaultDesktopContract.DefaultExePath"/>).</summary>
    public static bool IsInstalledCopy(string exePath)
    {
        try
        {
            return string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(VaultDesktopContract.DefaultExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
