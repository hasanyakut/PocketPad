using System.Windows;
using PocketPad.Desktop.Server;
using PocketPad.Desktop.Views;

namespace PocketPad.Desktop.Services;

/// <summary>Shows <see cref="ApprovalWindow"/> on the UI thread; closes it when the request is cancelled or times out.</summary>
public sealed class WpfPairingApprovalService(TrayIconService tray) : IPairingApprovalService
{
    public Task<bool> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken cancellationToken)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Application.Current.Dispatcher;

        dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.TrySetCanceled(cancellationToken);
                return;
            }

            var window = new ApprovalWindow(request);
            var registration = cancellationToken.Register(() => dispatcher.InvokeAsync(() =>
            {
                result.TrySetCanceled(cancellationToken);
                window.Close();
            }));
            window.Closed += (_, _) =>
            {
                registration.Dispose();
                result.TrySetResult(window.Approved);
            };
            window.Show();
            window.Activate();
            tray.Notify("Eşleştirme isteği", $"{request.DeviceName} bu bilgisayarı kontrol etmek istiyor.");
        });

        return result.Task;
    }
}
