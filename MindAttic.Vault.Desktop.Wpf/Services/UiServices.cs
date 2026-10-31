using System.Diagnostics;
using System.Windows;
using MindAttic.Components.Wpf.Controls;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>Runs work on the UI thread (file watchers and the pipe call back on other threads).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>The WPF dispatcher of the running app; runs inline when there is none (tests, tools).</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>Questions and notices the view models raise; replaced by a fake in tests.</summary>
public interface IDialogs
{
    /// <summary>A yes/no question; true for yes.</summary>
    bool Confirm(string message, string caption);

    void Error(string message, string caption);

    void Info(string message, string caption);

    /// <summary>Opens an https link in the browser.</summary>
    void OpenUrl(string url);
}

/// <summary>Themed message boxes over the main window.</summary>
public sealed class WpfDialogs : IDialogs
{
    public bool Confirm(string message, string caption) =>
        MaMessageBox.Show(Application.Current?.MainWindow, message, caption, MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Error(string message, string caption) =>
        MaMessageBox.Show(Application.Current?.MainWindow, message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Info(string message, string caption) =>
        MaMessageBox.Show(Application.Current?.MainWindow, message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public void OpenUrl(string url)
    {
        // Only web links: a manifest or catalog entry can never make this start a program.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { DesktopLog.For("Dialogs").Warning("Could not open a link: {Type}", ex.GetType().Name); }
    }
}
