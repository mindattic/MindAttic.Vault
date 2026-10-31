using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using MindAttic.Vault.Paths;

namespace MindAttic.Vault.Launch;

/// <summary>
/// A request to show an app's settings tab (and optionally one setting) in MindAttic.Vault.Desktop.Wpf.
/// The only thing the Desktop accepts from outside: <c>--app &lt;id&gt; [--setting &lt;full name&gt;]</c>,
/// or the same as <c>mindattic-vault://open?app=&lt;id&gt;&amp;setting=&lt;name&gt;</c>.
/// </summary>
public sealed record VaultOpenRequest
{
    public const int MaxAppIdLength = 64;
    public const int MaxSettingLength = 256;
    public const string Scheme = "mindattic-vault";

    private static readonly Regex AppIdPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public VaultOpenRequest(string appId, string? setting = null)
    {
        if (!IsValidAppId(appId)) throw new ArgumentException("App id must be 1-64 letters, digits, '.', '_' or '-'.", nameof(appId));
        if (setting is not null && !IsValidSetting(setting)) throw new ArgumentException($"Setting names are 1-{MaxSettingLength} printable characters.", nameof(setting));
        AppId = appId;
        Setting = string.IsNullOrWhiteSpace(setting) ? null : setting;
    }

    public string AppId { get; }
    public string? Setting { get; }

    public static bool IsValidAppId(string? appId) => appId is not null && AppIdPattern.IsMatch(appId);

    public static bool IsValidSetting(string? setting) =>
        setting is not null && setting.Length is > 0 and <= MaxSettingLength && !setting.Any(char.IsControl);

    /// <summary>The command line: <c>--app &lt;id&gt; [--setting &lt;name&gt;]</c>.</summary>
    public string[] ToArgs() => Setting is null ? new[] { "--app", AppId } : new[] { "--app", AppId, "--setting", Setting };

    /// <summary>The browser link a web app can offer.</summary>
    public string ToUrl() =>
        $"{Scheme}://open?app={Uri.EscapeDataString(AppId)}" + (Setting is null ? "" : $"&setting={Uri.EscapeDataString(Setting)}");

    /// <summary>
    /// Parses exactly <c>--app &lt;id&gt; [--setting &lt;name&gt;]</c> (either order), or a single
    /// <c>mindattic-vault://</c> URL (how the protocol handler starts the Desktop). Anything else is rejected.
    /// </summary>
    public static bool TryParseArgs(IReadOnlyList<string>? args, out VaultOpenRequest? request)
    {
        request = null;
        if (args is null || args.Count == 0) return false;
        if (args.Count == 1) return TryParseUrl(args[0], out request);
        if (args.Count is not (2 or 4)) return false;

        string? app = null, setting = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case "--app" when app is null: app = value; break;
                case "--setting" when setting is null: setting = value; break;
                default: return false;
            }
        }
        if (!IsValidAppId(app) || (setting is not null && !IsValidSetting(setting))) return false;
        request = new VaultOpenRequest(app!, setting);
        return true;
    }

    /// <summary>Parses <c>mindattic-vault://open?app=&lt;id&gt;[&amp;setting=&lt;name&gt;]</c>.</summary>
    public static bool TryParseUrl(string? url, out VaultOpenRequest? request)
    {
        request = null;
        if (url is null || url.Length > 1024) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "open", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.AbsolutePath is not ("" or "/")) return false;

        string? app = null, setting = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) return false;
            var key = pair[..eq];
            string value;
            try { value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')); }
            catch { return false; }
            switch (key)
            {
                case "app" when app is null: app = value; break;
                case "setting" when setting is null: setting = value; break;
                default: return false;
            }
        }
        if (!IsValidAppId(app) || (setting is not null && !IsValidSetting(setting))) return false;
        request = new VaultOpenRequest(app!, setting);
        return true;
    }
}

/// <summary>Names shared by the Desktop and every app that opens it.</summary>
public static class VaultDesktopContract
{
    public const string ExeName = "MindAttic.Vault.Desktop.Wpf.exe";
    public const string ExePathEnvVar = "MINDATTIC_VAULT_DESKTOP_EXE";
    public const string InstallFolder = "Vault.Desktop";

    /// <summary>Per-user pipe; created with <see cref="PipeOptions.CurrentUserOnly"/> so no other user can reach it.</summary>
    public static string PipeName { get; } = "MindAttic.Vault.Desktop." + UserTag();

    /// <summary>Per-user single-instance mutex.</summary>
    public static string MutexName { get; } = @"Local\MindAttic.Vault.Desktop." + UserTag();

    /// <summary>Default install location: <c>%LOCALAPPDATA%\MindAttic\Vault.Desktop\MindAttic.Vault.Desktop.Wpf.exe</c>.</summary>
    public static string DefaultExePath => Path.Combine(VaultPaths.LocalApp(InstallFolder), ExeName);

    /// <summary>
    /// Where the Desktop exe is: <see cref="ExePathEnvVar"/> when set and present, else
    /// <see cref="DefaultExePath"/>, else the first match on PATH; null when it isn't installed.
    /// </summary>
    public static string? FindExe(Func<string, string?>? getEnv = null, Func<string, bool>? exists = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        exists ??= File.Exists;
        var overridePath = getEnv(ExePathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && exists(overridePath)) return overridePath;
        if (exists(DefaultExePath)) return DefaultExePath;
        foreach (var dir in (getEnv("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), ExeName); }
            catch { continue; }
            if (exists(candidate)) return candidate;
        }
        return null;
    }

    private static string UserTag()
    {
        var who = $"{Environment.UserDomainName}\\{Environment.UserName}".ToUpperInvariant();
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in who) hash = (hash ^ c) * 16777619u;
            return hash.ToString("x8");
        }
    }
}

/// <summary>How <see cref="VaultSettings"/> reaches the Desktop; swappable for tests.</summary>
public interface IVaultDesktopLauncher
{
    /// <summary>Hands the request to a running Desktop. False when none answers.</summary>
    bool TrySend(VaultOpenRequest request);

    /// <summary>Starts the Desktop with the request. False when it isn't installed or can't start.</summary>
    bool TryStart(VaultOpenRequest request);
}

/// <summary>The real launcher: the per-user pipe, then the installed exe.</summary>
public sealed class VaultDesktopLauncher : IVaultDesktopLauncher
{
    public static int ConnectTimeoutMs { get; set; } = 500;

    public bool TrySend(VaultOpenRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", VaultDesktopContract.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', request.ToArgs()) + "\n");
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryStart(VaultOpenRequest request)
    {
        var exe = VaultDesktopContract.FindExe();
        if (exe is null) return false;
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in request.ToArgs()) info.ArgumentList.Add(arg);
            return Process.Start(info) is not null;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// What every app's settings cog calls: shows the app's tab in MindAttic.Vault.Desktop.Wpf (starting
/// it if needed) and scrolls to <c>setting</c> when given. Works from WPF, Blazor Server and console
/// apps on the user's machine; a deployed web app offers <see cref="Link"/> instead.
/// </summary>
public static class VaultSettings
{
    /// <summary>Replace for tests.</summary>
    public static IVaultDesktopLauncher Launcher { get; set; } = new VaultDesktopLauncher();

    /// <summary>Opens the Desktop at the app's tab. False when it isn't installed and isn't running.</summary>
    public static bool Open(string appId, string? setting = null)
    {
        var request = new VaultOpenRequest(appId, setting);
        return Launcher.TrySend(request) || Launcher.TryStart(request);
    }

    /// <summary>A <c>mindattic-vault://</c> link that opens the Desktop at the app's tab from a browser.</summary>
    public static string Link(string appId, string? setting = null) => new VaultOpenRequest(appId, setting).ToUrl();
}

/// <summary>
/// The Desktop's side of the pipe: accepts one request per connection (at most 4 KB), validates it with
/// <see cref="VaultOpenRequest.TryParseArgs"/>, and hands valid ones to the callback. Invalid input is
/// reported through <c>onRejected</c> and otherwise ignored. Never a network listener.
/// </summary>
public static class VaultDesktopPipeServer
{
    public const int MaxMessageBytes = 4096;

    public static async Task RunAsync(Action<VaultOpenRequest> onRequest, Action<string>? onRejected = null,
        string? pipeName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onRequest);
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(pipeName ?? VaultDesktopContract.PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[MaxMessageBytes + 1];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
                    total += read;

                if (total > MaxMessageBytes) { onRejected?.Invoke("message too large"); continue; }
                var lines = Encoding.UTF8.GetString(buffer, 0, total).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (VaultOpenRequest.TryParseArgs(lines, out var request)) onRequest(request!);
                else onRejected?.Invoke("unrecognized request");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A client that disconnects early is not an error.
            }
            finally
            {
                server?.Dispose();
            }
        }
    }
}
