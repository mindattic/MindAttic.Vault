using System.Windows;
using System.Windows.Controls;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop.Views;

/// <summary>Get Started. A pasted key goes from its <see cref="MaSecretBox"/> straight to the view model on click, then the box is cleared.</summary>
public partial class GetStartedView : UserControl
{
    public GetStartedView() => InitializeComponent();

    private async void AddAndTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MaSecretBox box, DataContext: RequiredProviderViewModel row } button) return;
        button.IsEnabled = false;
        try
        {
            if (await row.AddAndTestAsync(box.GetSecret()) is not null) box.Clear();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
