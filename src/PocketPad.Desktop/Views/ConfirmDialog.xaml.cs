using System.Windows;

namespace PocketPad.Desktop.Views;

/// <summary>Dark, app-styled replacement for MessageBox Yes/No on destructive actions. "Vazgeç" is the default.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
