using System.ComponentModel;
using System.Windows;
using PocketPad.Desktop.Services;
using PocketPad.Desktop.ViewModels;

namespace PocketPad.Desktop.Views;

/// <summary>Closing or minimizing hides the window to the tray; the server keeps running.</summary>
public partial class MainWindow : Window
{
    private readonly TrayIconService _tray;
    private bool _allowClose;
    private bool _trayHintShown;

    public MainWindow(MainViewModel viewModel, TrayIconService tray)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = viewModel;
        _tray = tray;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Called on real exit (tray → Çıkış).</summary>
    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized) HideToTray();
        base.OnStateChanged(e);
    }

    private void HideToTray()
    {
        Hide();
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray.Notify("PocketPad arka planda çalışıyor", "Açmak için tepsi simgesine çift tıklayın, kapatmak için sağ tıklayıp Çıkış'ı seçin.");
    }
}
