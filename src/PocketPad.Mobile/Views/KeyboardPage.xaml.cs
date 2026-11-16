using PocketPad.Mobile.ViewModels;

namespace PocketPad.Mobile.Views;

public partial class KeyboardPage : ContentPage
{
    private readonly KeyboardViewModel _viewModel;

    public KeyboardPage(KeyboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    /// <summary>Shortcut tile tapped (the tile animates itself).</summary>
    private void OnShortcutTapped(object? sender, ShortcutItem item)
    {
        if (Shortcuts.IsEnabled) _viewModel.SendShortcutCommand.Execute(item);
    }

    /// <summary>F-key tapped: short press animation, then send.</summary>
    private async void OnFunctionKeyTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not View tile || tile.BindingContext is not ShortcutItem item || !Shortcuts.IsEnabled) return;
        _viewModel.SendShortcutCommand.Execute(item);
        await tile.ScaleToAsync(0.93, 60, Easing.CubicOut);
        await tile.ScaleToAsync(1, 90, Easing.CubicIn);
    }
}
