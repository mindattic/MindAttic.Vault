using System.Windows;
using System.Windows.Controls;
using MindAttic.Components.Wpf.Controls;
using MindAttic.Vault.Desktop.ViewModels;

namespace MindAttic.Vault.Desktop.Views;

/// <summary>
/// The Keys tab. Key values move only between an <see cref="MaSecretBox"/> and a view-model method, never through a
/// binding: a row's box is filled from <see cref="KeyRowViewModel.ReadSecret"/> when it loads, and an edited or new
/// value is read with <see cref="MaSecretBox.GetSecret"/> on click and cleared right after.
/// </summary>
public partial class KeysView : UserControl
{
    public KeysView() => InitializeComponent();

    private void Secret_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is MaSecretBox box && box.DataContext is KeyRowViewModel row) box.SetSecret(row.ReadSecret());
    }

    private void Secret_Changed(object? sender, EventArgs e)
    {
        // Enable the row's Save value button (the sibling whose Tag is this box).
        if (sender is MaSecretBox box && box.Parent is Panel panel)
            foreach (var button in panel.Children.OfType<Button>())
                if (ReferenceEquals(button.Tag, box)) button.IsEnabled = box.HasValue;
    }

    private async void SaveValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MaSecretBox box, DataContext: KeyRowViewModel row } button) return;
        button.IsEnabled = false;
        var saved = await row.SetValueAsync(box.GetSecret());
        if (!saved) button.IsEnabled = true;
    }

    private async void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MaSecretBox box, DataContext: ProviderKeysViewModel provider } button) return;
        button.IsEnabled = false;
        try
        {
            if (await provider.AddAsync(box.GetSecret())) box.Clear();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
