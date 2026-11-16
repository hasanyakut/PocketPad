using System.Windows;
using PocketPad.Desktop.Views;

namespace PocketPad.Desktop.Services;

/// <summary>Keeps dialog windows out of view models.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, string confirmText);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, string confirmText)
    {
        var dialog = new ConfirmDialog(title, message, confirmText);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != dialog)
                    ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }
}
