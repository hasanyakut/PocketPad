using System.Windows;
using PocketPad.Desktop.Server;

namespace PocketPad.Desktop.Views;

public partial class ApprovalWindow : Window
{
    public ApprovalWindow(PairingApprovalRequest request)
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DeviceNameText.Text = request.DeviceName;
        PlatformText.Text = request.Platform;
        AddressText.Text = request.RemoteAddress;
    }

    /// <summary>True only after an explicit click on "İzin ver". Closing the window any other way denies.</summary>
    public bool Approved { get; private set; }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        Approved = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e) => Close();
}
