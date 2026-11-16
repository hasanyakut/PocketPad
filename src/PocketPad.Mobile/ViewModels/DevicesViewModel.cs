using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PocketPad.Mobile.Services;
using PocketPad.Shared.Client;
using PocketPad.Shared.Pairing;

namespace PocketPad.Mobile.ViewModels;

public sealed record PairedServerItem(PairedServer Server, bool IsActive)
{
    public string Name => Server.ServerName;
    public string Address => $"{Server.Host}:{Server.Port}";
}

public sealed partial class DevicesViewModel : ObservableObject
{
    private readonly IConnectionService _connection;

    public DevicesViewModel(IConnectionService connection, ConnectionStatusViewModel status)
    {
        _connection = connection;
        Status = status;
        _connection.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(Refresh);
        Refresh();
    }

    public ConnectionStatusViewModel Status { get; }
    public ObservableCollection<PairedServerItem> Servers { get; } = [];

    [ObservableProperty] public partial bool HasServers { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }

    [RelayCommand]
    private Task ScanAsync() => Shell.Current.GoToAsync(Routes.Scan);

    [RelayCommand]
    private Task ConnectAsync(PairedServerItem item) => _connection.ConnectAsync(item.Server);

    [RelayCommand]
    private Task DisconnectAsync() => _connection.DisconnectAsync();

    [RelayCommand]
    private async Task ForgetAsync(PairedServerItem item)
    {
        var confirmed = await Shell.Current.DisplayAlertAsync("Eşleştirmeyi kaldır",
            $"{item.Name} bu telefondan kaldırılsın mı? Tekrar bağlanmak için yeniden QR taramanız gerekir.", "Kaldır", "Vazgeç");
        if (confirmed) await _connection.ForgetAsync(item.Server);
    }

    private void Refresh()
    {
        var current = _connection.CurrentServer?.ServerId;
        IsActive = _connection.State != ConnectionState.Disconnected;
        Servers.Clear();
        foreach (var s in _connection.PairedServers.OrderByDescending(s => s.PairedAt))
            Servers.Add(new PairedServerItem(s, s.ServerId == current));
        HasServers = Servers.Count > 0;
    }
}
