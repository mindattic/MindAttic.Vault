using MindAttic.Vault.Launch;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings.Manifest;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>The apps that have a settings manifest, and a signal when that set may have changed.</summary>
public interface IManifestCatalog
{
    /// <summary>Every valid manifest now on disk, one per app id, ordered by display name.</summary>
    IReadOnlyList<SettingsManifest> Discover();

    /// <summary>Raised (on a worker thread) when a manifest or an app folder was added, changed or removed.</summary>
    event EventHandler? Changed;

    /// <summary>Starts watching; idempotent.</summary>
    void Start();
}

/// <summary>
/// <see cref="SettingsManifestFile.Discover"/> over the roaming root, watched by a <see cref="FileSystemWatcher"/>:
/// a manifest written, replaced or deleted, or an app folder created, renamed or removed, raises
/// <see cref="Changed"/> once per burst (debounced).
/// </summary>
public sealed class FileManifestCatalog : IManifestCatalog, IDisposable
{
    private readonly Func<string> root;
    private readonly TimeSpan debounce;
    private readonly object gate = new();
    private FileSystemWatcher? watcher;
    private Timer? timer;

    public FileManifestCatalog(Func<string>? root = null, TimeSpan? debounce = null)
    {
        this.root = root ?? (() => VaultPaths.RoamingRoot);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(300);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SettingsManifest> Discover() =>
        SettingsManifestFile.Discover(root())
            .Select(d => d.Manifest)
            // The folder name is the app id (M.VALT-LAW-2); anything that isn't a plain id is never a tab.
            .Where(m => VaultOpenRequest.IsValidAppId(m.AppId) && m.AppId is not ("." or ".."))
            .GroupBy(m => m.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    public void Start()
    {
        lock (gate)
        {
            if (watcher is not null) return;
            var path = root();
            Directory.CreateDirectory(path);
            timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, e) => Consider(path, e.FullPath);
            watcher.Changed += (_, e) => Consider(path, e.FullPath);
            watcher.Deleted += (_, e) => Consider(path, e.FullPath);
            watcher.Renamed += (_, e) => { Consider(path, e.FullPath); Consider(path, e.OldFullPath); };
            watcher.Error += (_, _) => Kick(); // overflow: rescan
            watcher.EnableRaisingEvents = true;
        }
    }

    private void Consider(string rootPath, string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        var parent = Path.GetDirectoryName(fullPath);
        // A manifest itself (or its temp file being swapped in), or a top-level app folder.
        if (name.StartsWith(SettingsManifest.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent?.TrimEnd('\\', '/'), rootPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            Kick();
    }

    private void Kick()
    {
        lock (gate) timer?.Change(debounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
            timer?.Dispose();
            timer = null;
        }
    }
}
