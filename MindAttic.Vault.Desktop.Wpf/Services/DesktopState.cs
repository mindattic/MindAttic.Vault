using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using MindAttic.Vault.Settings;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>
/// What Vault.Desktop remembers between runs, in <c>%APPDATA%\MindAttic\Vault.Desktop\state.json</c>. Nothing secret:
/// Profile and tab ids, window bounds, collapsed groups, and an opaque slot the Azure Profile may use for its
/// environment mappings.
/// </summary>
public sealed class VaultDesktopState
{
    public string? LastProfile { get; set; }

    /// <summary>The last tab's id: <c>keys</c>, <c>app:{AppId}</c>, ...</summary>
    public string? LastTab { get; set; }

    public WindowBounds? Window { get; set; }

    /// <summary>Keys tab groups the user collapsed.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>Azure environment mappings, owned by the Azure Profile (opaque here).</summary>
    public JsonObject? AzureEnvironments { get; set; }
}

/// <summary>The normal (restored) window rectangle in device-independent pixels, and whether it was maximized.</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized = false);

/// <summary>
/// Loads <see cref="VaultDesktopState"/> once and saves it debounced after each change (and at once on
/// <see cref="Flush"/>, at exit). A missing or corrupt file is defaults (<see cref="JsonSettingsStore{T}"/>'s rule).
/// </summary>
public sealed class DesktopStateKeeper : IDisposable
{
    public const string Bucket = "Vault.Desktop";
    public const string FileName = "state.json";

    private readonly JsonSettingsStore<VaultDesktopState> store;
    private readonly TimeSpan debounce;
    private readonly object gate = new();
    private readonly Timer timer;
    private bool pending;

    public DesktopStateKeeper(JsonSettingsStore<VaultDesktopState>? store = null, TimeSpan? debounce = null)
    {
        this.store = store ?? JsonSettingsStore<VaultDesktopState>.ForBucket(Bucket, FileName);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(750);
        State = this.store.Load();
        timer = new Timer(_ => Flush());
    }

    public string FilePath => store.FilePath;

    /// <summary>The current state. Change it only through <see cref="Update"/>.</summary>
    public VaultDesktopState State { get; }

    /// <summary>Applies a change and schedules a save.</summary>
    public void Update(Action<VaultDesktopState> change)
    {
        lock (gate)
        {
            change(State);
            pending = true;
            timer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Saves now when a change is waiting.</summary>
    public void Flush()
    {
        lock (gate)
        {
            if (!pending) return;
            pending = false;
            try { store.Save(State); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DesktopLog.For("State").Warning("Saving the window state failed: {Type}", ex.GetType().Name);
            }
        }
    }

    public void Dispose()
    {
        Flush();
        timer.Dispose();
    }
}

/// <summary>Puts a remembered window back on a monitor that still exists.</summary>
public static class WindowPlacement
{
    /// <summary>How much of the window (DIPs, each way) must overlap a monitor for it to stay where it was.</summary>
    public const double MinVisible = 80;

    /// <summary>
    /// <paramref name="saved"/> clamped into <paramref name="workAreas"/>: kept on the monitor it overlaps most when
    /// enough of it is visible there, else centred on <paramref name="primary"/>; never larger than its monitor, never
    /// smaller than <paramref name="minWidth"/> x <paramref name="minHeight"/>. Null when nothing was saved.
    /// </summary>
    public static WindowBounds? Clamp(WindowBounds? saved, IReadOnlyList<Rect> workAreas, Rect primary, double minWidth = 640, double minHeight = 420)
    {
        if (saved is null || double.IsNaN(saved.Left) || double.IsNaN(saved.Top) || saved.Width <= 0 || saved.Height <= 0) return null;
        var areas = workAreas.Count > 0 ? workAreas : new[] { primary };
        var rect = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);

        var best = areas.Select(a => (Area: a, Overlap: Rect.Intersect(a, rect)))
            .Where(x => !x.Overlap.IsEmpty)
            .OrderByDescending(x => x.Overlap.Width * x.Overlap.Height)
            .FirstOrDefault();
        var onScreen = best.Overlap is { IsEmpty: false } o && o.Width >= MinVisible && o.Height >= MinVisible;
        var area = onScreen ? best.Area : primary;

        var width = Math.Min(Math.Max(saved.Width, Math.Min(minWidth, area.Width)), area.Width);
        var height = Math.Min(Math.Max(saved.Height, Math.Min(minHeight, area.Height)), area.Height);
        double left, top;
        if (onScreen)
        {
            left = Math.Clamp(saved.Left, area.Left, area.Right - width);
            top = Math.Clamp(saved.Top, area.Top, area.Bottom - height);
        }
        else
        {
            left = area.Left + (area.Width - width) / 2;
            top = area.Top + (area.Height - height) / 2;
        }
        return new WindowBounds(left, top, width, height, saved.Maximized);
    }

    /// <summary>Every monitor's work area in DIPs (<paramref name="dipsPerPixel"/> = 1 / DPI scale), primary first.</summary>
    public static IReadOnlyList<Rect> MonitorWorkAreas(double dipsPerPixel, out Rect primary)
    {
        var areas = new List<(Rect Area, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.Work;
                areas.Add((new Rect(w.Left * dipsPerPixel, w.Top * dipsPerPixel, (w.Right - w.Left) * dipsPerPixel, (w.Bottom - w.Top) * dipsPerPixel),
                    (info.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        primary = areas.FirstOrDefault(a => a.Primary).Area;
        if (primary.IsEmpty) primary = SystemParameters.WorkArea;
        return areas.OrderByDescending(a => a.Primary).Select(a => a.Area).ToList();
    }

    /// <summary>DIPs per physical pixel at the system DPI (1 at 96 DPI, 0.5 at 192).</summary>
    public static double SystemDipsPerPixel()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi > 0 ? 96.0 / dpi : 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Native Monitor;
        public Native Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    private delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
