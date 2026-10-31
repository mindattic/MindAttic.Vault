using System.Windows.Controls;

namespace MindAttic.Vault.Desktop.Azure.Setup;

/// <summary>Sign-in and resource picker for the Azure Profile. Bind its DataContext to an <see cref="AzureSetupViewModel"/>.</summary>
public partial class AzureSetupView : UserControl
{
    public AzureSetupView() => InitializeComponent();
}
