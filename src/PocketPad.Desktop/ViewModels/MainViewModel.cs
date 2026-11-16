using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PocketPad.Desktop.Server;
using PocketPad.Desktop.Services;
using PocketPad.Shared.Security;

namespace PocketPad.Desktop.ViewModels;

public sealed record ConnectionItem(string Title, string Address, string StatusText, bool IsControlling);

public sealed record PairedDeviceItem(string DeviceId, string Name, string Details, bool IsConnected);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ControlServer _server;
    private readonly PairingService _pairing;
    private readonly IPairedDeviceStore _devices;
    private readonly SessionRegistry _registry;
    private readonly TrayIconService _tray;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _countdown;
    private DateTimeOffset _pairingExpiresAt;

    public MainViewModel(ControlServer server, PairingService pairing, IPairedDeviceStore devices, SessionRegistry registry,
        TrayIconService tray, IDialogService dialogs, TimeProvider time, ILogger<MainViewModel> logger)
    {
        _dialogs = dialogs;
        _server = server;
        _pairing = pairing;
        _devices = devices;
        _registry = registry;
        _tray = tray;
        _time = time;
        _logger = logger;

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => UpdateCountdown();

        _registry.Changed += (_, _) => OnUi(RefreshConnections);
        _registry.InputReceived += (_, a) => OnUi(() => LastActivity = $"{a.At.ToLocalTime():HH:mm:ss}  {a.DeviceName}: {a.MessageType}");
        _devices.Changed += (_, _) => OnUi(RefreshPairedDevices);
        _pairing.InvitationClosed += (_, _) => OnUi(EndPairingUi);

        RefreshPairedDevices();
    }

    public ObservableCollection<ConnectionItem> Connections { get; } = [];
    public ObservableCollection<PairedDeviceItem> PairedDevices { get; } = [];
    public ObservableCollection<string> LanAddresses { get; } = [];

    [ObservableProperty] public partial string ServerStatus { get; set; } = "Başlatılıyor…";
    [ObservableProperty] public partial bool IsServerRunning { get; set; }
    [ObservableProperty] public partial string Endpoint { get; set; } = "";
    [ObservableProperty] public partial string Fingerprint { get; set; } = "";
    [ObservableProperty] public partial string ControlStatus { get; set; } = "Bağlı cihaz yok";
    [ObservableProperty] public partial bool HasControlSession { get; set; }
    [ObservableProperty] public partial string LastActivity { get; set; } = "—";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartPairingCommand))]
    public partial string? SelectedAddress { get; set; }

    [ObservableProperty] public partial bool IsPairing { get; set; }
    [ObservableProperty] public partial ImageSource? QrImage { get; set; }
    [ObservableProperty] public partial string PairingCountdown { get; set; } = "";

    public async Task StartServerAsync()
    {
        try
        {
            await _server.StartAsync();
            IsServerRunning = true;
            ServerStatus = "Çalışıyor";
            Fingerprint = CertificateFingerprint.Format(_server.CertificateFingerprint);
            RefreshAddresses();
            _tray.SetStatus($"PocketPad – port {_server.Port}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server failed to start.");
            IsServerRunning = false;
            ServerStatus = $"Başlatılamadı: {ex.Message}";
        }
        StartPairingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RefreshAddresses()
    {
        var previous = SelectedAddress;
        LanAddresses.Clear();
        foreach (var ip in LocalNetwork.GetLanAddresses()) LanAddresses.Add(ip.ToString());
        SelectedAddress = LanAddresses.Contains(previous ?? "") ? previous : LanAddresses.FirstOrDefault();
        Endpoint = SelectedAddress is null ? $"Yerel ağ bulunamadı (port {_server.Port})" : $"{SelectedAddress}:{_server.Port}";
    }

    partial void OnSelectedAddressChanged(string? value)
    {
        if (value is not null) Endpoint = $"{value}:{_server.Port}";
    }

    private bool CanStartPairing() => IsServerRunning && SelectedAddress is not null;

    [RelayCommand(CanExecute = nameof(CanStartPairing))]
    private void StartPairing()
    {
        var invitation = _pairing.CreateInvitation(SelectedAddress!, _server.Port, _server.CertificateFingerprint);
        QrImage = QrCodeImage.Create(invitation.ToUri());
        _pairingExpiresAt = invitation.ExpiresAt;
        IsPairing = true;
        UpdateCountdown();
        _countdown.Start();
    }

    [RelayCommand]
    private void CancelPairing() => _pairing.Cancel();

    [RelayCommand]
    private void RemoveDevice(PairedDeviceItem? item)
    {
        if (item is null) return;
        var confirmed = _dialogs.Confirm(
            $"{item.Name} kaldırılsın mı?",
            "Bu telefon bilgisayarı artık kontrol edemez. Tekrar bağlanmak için yeniden eşleştirmeniz gerekir.",
            "Kaldır");
        if (confirmed) _devices.Remove(item.DeviceId);
    }

    private void UpdateCountdown()
    {
        var remaining = _pairingExpiresAt - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            EndPairingUi();
            return;
        }
        PairingCountdown = $"Kod {remaining:m\\:ss} sonra geçersiz olacak · tek kullanımlık";
    }

    private void EndPairingUi()
    {
        _countdown.Stop();
        IsPairing = false;
        QrImage = null; // never leave a used/expired code on screen
        PairingCountdown = "";
    }

    private void RefreshConnections()
    {
        var snapshot = _registry.Snapshot();
        Connections.Clear();
        foreach (var c in snapshot)
        {
            var status = c.Status switch
            {
                SessionStatus.Controlling => "Kontrol ediyor",
                SessionStatus.AwaitingApproval => "Onay bekliyor",
                _ => "Kimlik doğrulanıyor",
            };
            Connections.Add(new ConnectionItem(c.DeviceName ?? "Bilinmeyen cihaz", c.RemoteAddress, status, c.Status == SessionStatus.Controlling));
        }

        var active = snapshot.FirstOrDefault(c => c.Status == SessionStatus.Controlling);
        HasControlSession = active is not null;
        ControlStatus = active is null ? "Bağlı cihaz yok" : $"{active.DeviceName} bağlı";
        _tray.SetStatus(active is null ? "PocketPad – bağlı cihaz yok" : $"PocketPad – {active.DeviceName}");
        RefreshPairedDevices();
    }

    private void RefreshPairedDevices()
    {
        var connected = _registry.Snapshot().Where(c => c.Status == SessionStatus.Controlling).Select(c => c.DeviceId).ToHashSet();
        PairedDevices.Clear();
        foreach (var d in _devices.GetAll().OrderByDescending(d => d.LastSeen ?? d.PairedAt))
        {
            var seen = d.LastSeen is { } s ? $"son görülme {s.ToLocalTime():dd.MM.yyyy HH:mm}" : "henüz bağlanmadı";
            PairedDevices.Add(new PairedDeviceItem(d.DeviceId, d.Name, $"{d.Platform} · eşleşme {d.PairedAt.ToLocalTime():dd.MM.yyyy} · {seen}", connected.Contains(d.DeviceId)));
        }
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.InvokeAsync(action);
    }
}
