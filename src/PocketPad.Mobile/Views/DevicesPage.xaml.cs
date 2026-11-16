using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Views;

public partial class DevicesPage : ContentPage
{
    public DevicesPage(DevicesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
