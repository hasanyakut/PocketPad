using PocketPad.Shared.Client;
using PocketPad.Shared.Pairing;
using PocketPad.Shared.Protocol;

namespace PocketPad.Mobile.Services;

public interface IConnectionService
{
    ConnectionState State { get; }
    ConnectionFailure LastFailure { get; }
    RejectReason LastReason { get; }
    PairedServer? CurrentServer { get; }
    IReadOnlyList<PairedServer> PairedServers { get; }

    /// <summary>Raised on a background thread whenever state or the paired list changes.</summary>
    event EventHandler? Changed;

    Task InitializeAsync();
    Task<PairedServer> PairAsync(PairingQrPayload qr, CancellationToken cancellationToken);
    Task ConnectAsync(PairedServer server);
    Task DisconnectAsync();
    Task ForgetAsync(PairedServer server);
    ValueTask<bool> SendAsync(ProtocolMessage message);
}

/// <summary>App level orchestration: the shared <see cref="PocketPadClient"/> plus credential persistence.</summary>
public sealed class ConnectionService : IConnectionService
{
    private readonly PocketPadClient _client;
    private readonly ICredentialStore _store;
    private readonly IDeviceInfo _deviceInfo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PairedServer> _servers = [];
    private bool _initialized;

    public ConnectionService(PocketPadClient client, ICredentialStore store, IDeviceInfo deviceInfo)
    {
        _client = client;
        _store = store;
        _deviceInfo = deviceInfo;
        _client.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public ConnectionState State => _client.State;
    public ConnectionFailure LastFailure => _client.LastFailure;
    public RejectReason LastReason => _client.LastReason;
    public PairedServer? CurrentServer => _client.IsRunning ? _client.CurrentServer : null;
    public IReadOnlyList<PairedServer> PairedServers => _servers;

    public event EventHandler? Changed;

    private string DeviceName => string.IsNullOrWhiteSpace(_deviceInfo.Name) ? $"{_deviceInfo.Manufacturer} {_deviceInfo.Model}" : _deviceInfo.Name;

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_initialized) return;
            _initialized = true;
            _servers = [.. await _store.LoadAsync()];
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);

        var lastId = await _store.GetLastServerIdAsync();
        var last = _servers.FirstOrDefault(s => s.ServerId == lastId);
        if (last is not null) await _client.StartAsync(last, DeviceName);
    }

    public async Task<PairedServer> PairAsync(PairingQrPayload qr, CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        var server = await _client.PairAsync(qr, DeviceName, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _servers.RemoveAll(s => s.ServerId == server.ServerId);
            _servers.Add(server);
            await _store.SaveAsync(_servers);
            await _store.SetLastServerIdAsync(server.ServerId);
        }
        finally
        {
            _gate.Release();
        }

        await _client.StartAsync(server, DeviceName);
        Changed?.Invoke(this, EventArgs.Empty);
        return server;
    }

    public async Task ConnectAsync(PairedServer server)
    {
        await _store.SetLastServerIdAsync(server.ServerId);
        await _client.StartAsync(server, DeviceName);
    }

    public async Task DisconnectAsync()
    {
        await _store.SetLastServerIdAsync(null);
        await _client.StopAsync();
    }

    public async Task ForgetAsync(PairedServer server)
    {
        if (_client.CurrentServer?.ServerId == server.ServerId) await DisconnectAsync();

        await _gate.WaitAsync();
        try
        {
            _servers.RemoveAll(s => s.ServerId == server.ServerId);
            await _store.SaveAsync(_servers);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask<bool> SendAsync(ProtocolMessage message) => _client.SendAsync(message);
}
