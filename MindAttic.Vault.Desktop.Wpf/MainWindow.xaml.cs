using System.Windows;
using MindAttic.Vault.Desktop.Services;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop;

/// <summary>The one window: Profile picker, tabs, status line. Remembers its bounds (clamped to a monitor that still exists).</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.ActivationRequested -= OnActivationRequested;
            if (e.NewValue is MainViewModel vm) vm.ActivationRequested += OnActivationRequested;
        };
        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
        StateChanged += (_, _) => Remember();
        Closing += (_, _) =>
        {
            Remember();
            State?.Flush();
        };
    }

    /// <summary>Where the bounds are remembered (null: not remembered).</summary>
    public DesktopStateKeeper? State { get; init; }

    /// <summary>Puts the window where it was last time, on a monitor that still exists. Call before Show.</summary>
    public void RestorePlacement()
    {
        var areas = WindowPlacement.MonitorWorkAreas(WindowPlacement.SystemDipsPerPixel(), out var primary);
        if (WindowPlacement.Clamp(State?.State.Window, areas, primary, MinWidth, MinHeight) is not { } bounds) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (bounds.Maximized) WindowState = WindowState.Maximized;
    }

    private void Remember()
    {
        if (State is null || !IsLoaded || WindowState == WindowState.Minimized) return;
        var normal = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (normal.IsEmpty || double.IsNaN(normal.Left)) return;
        var bounds = new WindowBounds(normal.Left, normal.Top, normal.Width, normal.Height, WindowState == WindowState.Maximized);
        if (bounds != State.State.Window) State.Update(s => s.Window = bounds);
    }

    /// <summary>Comes forward for a deep link (the second start already gave this process the right to).</summary>
    private void OnActivationRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
