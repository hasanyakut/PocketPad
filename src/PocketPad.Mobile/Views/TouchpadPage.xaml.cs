using System.ComponentModel;
using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Views;

public partial class TouchpadPage : ContentPage
{
    private readonly TouchpadViewModel _viewModel;

    public TouchpadPage(TouchpadViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        Surface.GestureDetected += (_, gesture) => viewModel.OnGesture(gesture);
        Surface.GestureCancelled += (_, _) => viewModel.OnGestureCancelled();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDisappearing()
    {
        _viewModel.IsTyping = false; // switching tabs closes the soft keyboard
        base.OnDisappearing();
    }

    private async void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TouchpadViewModel.IsTyping)) return;
        if (_viewModel.IsTyping) await TypingBar.OpenKeyboardAsync();
        else await TypingBar.CloseKeyboardAsync();
    }
}
