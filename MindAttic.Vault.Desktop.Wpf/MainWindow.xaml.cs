using System.Windows;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop;

/// <summary>The one window: Profile picker, tabs, status line.</summary>
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
