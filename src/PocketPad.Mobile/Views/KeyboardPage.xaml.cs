using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Views;

public partial class KeyboardPage : ContentPage
{
    public KeyboardPage(KeyboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
