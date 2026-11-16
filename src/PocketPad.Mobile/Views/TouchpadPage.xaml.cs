using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Views;

public partial class TouchpadPage : ContentPage
{
    public TouchpadPage(TouchpadViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        Surface.GestureDetected += (_, gesture) => viewModel.OnGesture(gesture);
        Surface.GestureCancelled += (_, _) => viewModel.OnGestureCancelled();
    }
}
