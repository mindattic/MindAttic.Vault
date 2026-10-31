using System.ComponentModel;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.ViewModels;

/// <summary>
/// A Profile's own setup screen (Azure: sign-in and the resource picker), registered next to the Profile in DI. Its
/// view is the DataTemplate keyed on <see cref="ViewModel"/>'s type. Shown as the first tab while that Profile is selected.
/// </summary>
public sealed record ProfileSetup(string ProfileId, string Title, INotifyPropertyChanged ViewModel);

/// <summary>The tab that hosts a <see cref="ProfileSetup"/>.</summary>
public sealed class ProfileSetupTabViewModel : TabViewModel
{
    private bool ready = true;

    public ProfileSetupTabViewModel(ProfileSetup setup) => Setup = setup;

    public ProfileSetup Setup { get; }

    /// <summary>The setup screen's own view model (rendered by its DataTemplate).</summary>
    public object Content => Setup.ViewModel;

    public override string Id => "setup:" + Setup.ProfileId;
    public override string Title => Setup.Title;
    public override MaStatus? Status => ready ? null : MaStatus.Warning;

    public override Task LoadAsync(IProfile profile, ProfileReadiness readiness)
    {
        ready = readiness.IsReady;
        Notice = readiness.IsReady ? null : readiness.Message;
        RaiseTabState();
        return Task.CompletedTask;
    }
}
