using System.Diagnostics;
using System.Runtime.InteropServices;
using MindAttic.Vault.Launch;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>
/// One Vault.Desktop per user: the first start owns <see cref="VaultDesktopContract.MutexName"/> and listens on the
/// current-user-only pipe (<see cref="VaultDesktopPipeServer"/>); a second start hands its request over that pipe
/// (<see cref="VaultDesktopLauncher.TrySend"/>), lets the first take the foreground, and exits.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly Thread owner;

    private SingleInstance(Mutex mutex)
    {
        this.mutex = mutex;
        owner = Thread.CurrentThread;
    }

    /// <summary>The instance when this process is the first; null when another one already runs.</summary>
    public static SingleInstance? TryClaim(string? name = null)
    {
        var mutex = new Mutex(false, name ?? VaultDesktopContract.MutexName);
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; } // the previous owner was killed: ours now
        if (owned) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// A second start's whole job: give the first the right to come forward, send it <paramref name="request"/> (retrying
    /// while the first is still starting, up to <paramref name="wait"/>), and bring its window forward when there's no
    /// request or it couldn't be delivered. True when the request was delivered.
    /// </summary>
    public static bool HandOff(VaultOpenRequest? request, IVaultDesktopLauncher launcher, TimeSpan wait, Action? bringForward = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(AsfwAny);
        var delivered = false;
        if (request is not null)
        {
            var until = DateTime.UtcNow + wait;
            do
            {
                delivered = launcher.TrySend(request);
                if (delivered) break;
                Thread.Sleep(100);
            }
            while (DateTime.UtcNow < until);
        }
        if (!delivered) (bringForward ?? BringOtherForward)();
        return delivered;
    }

    /// <summary>Restores (if minimized) and raises the other Vault.Desktop window.</summary>
    public static void BringOtherForward()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SwRestore);
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Thread.CurrentThread == owner)
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        mutex.Dispose();
    }

    private const int AsfwAny = -1, SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
