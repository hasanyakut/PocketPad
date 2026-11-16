using PocketPad.Mobile.Services;

namespace PocketPad.Mobile;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly IConnectionService _connection;

    public App(AppShell shell, IConnectionService connection)
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
        _shell = shell;
        _connection = connection;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell) { Title = "PocketPad" };
        // Restores the last paired PC and starts the supervised (auto-reconnecting) connection.
        window.Created += async (_, _) => await _connection.InitializeAsync();
        return window;
    }
}
