using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PocketPad.Mobile.Services;
using PocketPad.Shared.Client;
using PocketPad.Shared.Discovery;
using PocketPad.Shared.Pairing;

namespace PocketPad.Mobile.ViewModels;

public sealed record DiscoveredPc(PairingQrPayload Payload)
{
    public string Name => Payload.ServerName;
    public string Address => Payload.Host;
}

/// <summary>Pairing page: QR scanning plus LAN discovery of PCs that are in pairing mode.</summary>
public sealed partial class ScanViewModel(IConnectionService connection) : ObservableObject
{
    private static readonly TimeSpan DiscoveryListenTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DiscoveryPause = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _pairingCts;
    private CancellationTokenSource? _discoveryCts;

    public ObservableCollection<DiscoveredPc> FoundPcs { get; } = [];

    [ObservableProperty] public partial bool IsDetecting { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "Bilgisayarda \"Cihaz eşleştir\"e basın; QR kodunu tarayın veya aşağıdaki listeden bilgisayarı seçin.";
    [ObservableProperty] public partial bool HasCameraPermission { get; set; } = true;
    [ObservableProperty] public partial bool HasFoundPcs { get; set; }

    /// <summary>Called by the page when the camera reports a barcode.</summary>
    public async Task OnCodeDetectedAsync(string? text)
    {
        if (IsBusy) return;
        if (!PairingQrPayload.TryParse(text, out var qr, out var error))
        {
            Message = error ?? "Geçersiz kod.";
            return;
        }
        await PairAsync(qr!);
    }

    [RelayCommand]
    private Task PairWithAsync(DiscoveredPc pc) => IsBusy ? Task.CompletedTask : PairAsync(pc.Payload);

    private async Task PairAsync(PairingQrPayload invitation)
    {
        IsBusy = true;
        IsDetecting = false;
        Message = $"{invitation.ServerName} bilgisayarına bağlanılıyor… Bilgisayarda isteği onaylayın.";
        _pairingCts = new CancellationTokenSource();
        try
        {
            var server = await connection.PairAsync(invitation, _pairingCts.Token);
            Message = $"{server.ServerName} ile eşleştirildi.";
            await Shell.Current.GoToAsync("..");
        }
        catch (PairingException ex)
        {
            Message = StatusText.ForFailure(ex.Failure, ex.Reason) ?? "Eşleştirme başarısız oldu.";
            IsDetecting = HasCameraPermission;
        }
        catch (OperationCanceledException)
        {
            Message = "Eşleştirme iptal edildi.";
            IsDetecting = HasCameraPermission;
        }
        finally
        {
            IsBusy = false;
            _pairingCts?.Dispose();
            _pairingCts = null;
        }
    }

    public void OnPermissionResult(bool granted)
    {
        HasCameraPermission = granted;
        IsDetecting = granted && !IsBusy;
        if (!granted) Message = "Kamera izni yok. Bilgisayarda \"Cihaz eşleştir\"e basın ve aşağıdaki listeden seçin.";
    }

    /// <summary>Repeats discovery in the background while the page is visible; never blocks the UI thread.</summary>
    public void StartDiscovery()
    {
        StopDiscovery();
        var cts = _discoveryCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var found = await LanDiscoveryClient.DiscoverAsync(DiscoveryListenTime, cancellationToken: cts.Token);
                    MainThread.BeginInvokeOnMainThread(() => UpdateFound(found));
                    await Task.Delay(DiscoveryPause, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Network not available (e.g. Wi-Fi off): retry on the next round.
                    try { await Task.Delay(DiscoveryPause, cts.Token); } catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    private void UpdateFound(IReadOnlyList<PairingQrPayload> found)
    {
        if (_discoveryCts is null) return;
        FoundPcs.Clear();
        foreach (var pc in found.OrderBy(p => p.ServerName)) FoundPcs.Add(new DiscoveredPc(pc));
        HasFoundPcs = FoundPcs.Count > 0;
    }

    private void StopDiscovery()
    {
        _discoveryCts?.Cancel();
        _discoveryCts?.Dispose();
        _discoveryCts = null;
    }

    [RelayCommand]
    private void Cancel() => _pairingCts?.Cancel();

    public void OnDisappearing()
    {
        IsDetecting = false;
        StopDiscovery();
        _pairingCts?.Cancel();
    }
}
