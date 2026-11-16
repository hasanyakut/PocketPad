using PocketPad.Mobile.Views;

namespace PocketPad.Mobile;

public static class Routes
{
    public const string Scan = "scan";
}

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Scan, typeof(ScanPage));
    }
}
