using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BurrowWin.ViewModels;

namespace BurrowWin.Pages;

public sealed partial class PurgePage : Page
{
    public PurgePage()
    {
        InitializeComponent();
        ViewModel = App.GetService<PurgeViewModel>();
        DataContext = ViewModel;
    }

    public PurgeViewModel ViewModel { get; }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove project artifacts?",
            Content = "BurrowWin will move only the selected, previewed build artifacts to the Windows Recycle Bin. Paths are checked again immediately before removal.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var authorization = ViewModel.CreateRemovalAuthorization();
            await ViewModel.RemoveAsync(authorization);
        }
    }
}
