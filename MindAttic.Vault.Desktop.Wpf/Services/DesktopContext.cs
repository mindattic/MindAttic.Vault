using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Credentials;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>The services every tab shares.</summary>
public sealed class DesktopContext(IDialogs dialogs, IUiDispatcher ui, KeyHealthStore health, TimeProvider time)
{
    public IDialogs Dialogs { get; } = dialogs;
    public IUiDispatcher Ui { get; } = ui;

    /// <summary>Key health is observed on this machine whatever the Profile (keyed by provider and key name).</summary>
    public KeyHealthStore Health { get; } = health;

    public TimeProvider Time { get; } = time;
}

/// <summary>How a key's health shows: a status light and a word.</summary>
public static class KeyStatusDisplay
{
    public static MaStatus Light(KeyStatus status) => status switch
    {
        KeyStatus.Healthy => MaStatus.Good,
        KeyStatus.CoolingDown => MaStatus.Warning,
        KeyStatus.Failed => MaStatus.Bad,
        _ => MaStatus.Other,
    };

    public static string Label(KeyStatus status) => status switch
    {
        KeyStatus.Healthy => "Healthy",
        KeyStatus.CoolingDown => "Cooling down",
        KeyStatus.Failed => "Failed",
        KeyStatus.Disabled => "Disabled",
        _ => "Never used",
    };
}
