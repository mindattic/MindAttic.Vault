using MindAttic.Components.Wpf.Mvvm;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>The window: tab host and status line.</summary>
public sealed class MainViewModel : ObservableObject
{
    private string statusText = "";

    public string Title => "MindAttic Vault";

    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    public Task InitializeAsync() => Task.CompletedTask;
}
