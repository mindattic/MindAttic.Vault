using System.Windows;
using System.Windows.Controls;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop.Views;

/// <summary>
/// The Credentials tab. A secret field's value moves only between its <see cref="MaSecretBox"/> and view-model methods,
/// never through a binding: the box is filled from <see cref="CredentialFieldViewModel.ReadSecret"/> when it loads, and
/// each edit is handed to <see cref="CredentialFieldViewModel.EditSecret"/>.
/// </summary>
public partial class CredentialsView : UserControl
{
    public CredentialsView() => InitializeComponent();

    private void Secret_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is MaSecretBox box && box.DataContext is CredentialFieldViewModel { IsSecret: true } field) box.SetSecret(field.ReadSecret());
    }

    private void Secret_Changed(object? sender, EventArgs e)
    {
        if (sender is MaSecretBox box && box.DataContext is CredentialFieldViewModel { IsSecret: true } field) field.EditSecret(box.GetSecret());
    }
}
