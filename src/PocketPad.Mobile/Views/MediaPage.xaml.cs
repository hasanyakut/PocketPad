using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Views;

public partial class MediaPage : ContentPage
{
    public MediaPage(MediaViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
