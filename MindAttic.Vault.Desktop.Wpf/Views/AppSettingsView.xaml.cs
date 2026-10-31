using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop.Views;

/// <summary>
/// Hosts an app tab's <see cref="MaSettingsForm"/>. The tab control reuses one view across app tabs, so it re-attaches
/// on every DataContext change: renders the tab's manifest and document, reports changed fields and hands the form's
/// validation to the view model.
/// </summary>
public partial class AppSettingsView : UserControl
{
    private AppSettingsViewModel? vm;

    public AppSettingsView()
    {
        InitializeComponent();
        Form.FieldChanged += (_, e) => vm?.MarkChanged(e.FieldName);
        DataContextChanged += (_, _) => Attach(DataContext as AppSettingsViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as AppSettingsViewModel);
    }

    private void Attach(AppSettingsViewModel? next)
    {
        if (ReferenceEquals(next, vm)) return;
        if (vm is not null)
        {
            vm.DocumentReloaded -= OnDocumentReloaded;
            vm.ScrollRequested -= OnScrollRequested;
            vm.Validator = null;
        }
        vm = next;
        if (vm is null) return;
        vm.DocumentReloaded += OnDocumentReloaded;
        vm.ScrollRequested += OnScrollRequested;
        vm.Validator = () => Form.Validate().Select(e => e.FieldName).ToList();
        Render();
    }

    private void OnDocumentReloaded(object? sender, EventArgs e) => Render();

    private void OnScrollRequested(object? sender, string setting) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollTo(setting));

    private void Render()
    {
        if (vm is null) return;
        Form.Load(vm.ManifestJson, vm.Document ?? new JsonObject());
        if (vm.PendingScroll is { } setting)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollTo(setting));
    }

    private void ScrollTo(string setting)
    {
        if (vm is null || vm.PendingScroll != setting) return;
        vm.ClearPendingScroll();
        if (!Form.ScrollTo(setting))
            DesktopLog.For("Tabs").Information("Deep link to an unknown setting of {App} ignored", vm.AppId);
    }
}
