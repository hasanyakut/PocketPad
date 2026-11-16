using System.Net;
using System.Net.WebSockets;
using PocketPad.Shared.Pairing;
using PocketPad.Shared.Protocol;
using PocketPad.Shared.Security;
using PocketPad.Shared.Transport;

namespace PocketPad.Shared.Client;

/// <summary>
/// Platform neutral PocketPad client: certificate-pinned WSS, pairing, authentication,
/// heartbeat and automatic reconnection. Used by the Android app (and by integration tests).
/// </summary>
public sealed class PocketPadClient : IAsyncDisposable
{
    private readonly ClientOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisor;
    private Session? _session;
    private volatile bool _stopRequested;

    public PocketPadClient(ClientOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new ClientOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ConnectionFailure LastFailure { get; private set; }
    public RejectReason LastReason { get; private set; }
    public PairedServer? CurrentServer { get; private set; }

    /// <summary>Raised on a background thread.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Non-handshake messages from the server. Raised on a background thread.</summary>
    public event EventHandler<ProtocolMessage>? MessageReceived;

    public bool IsRunning => _supervisor is { IsCompleted: false };

    /// <summary>Pairs with the PC from a scanned QR code. Waits for the Windows user's approval.</summary>
    public async Task<PairedServer> PairAsync(PairingQrPayload qr, string deviceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(qr);
        if (IsRunning) throw new InvalidOperationException("Stop the active connection before pairing.");

        SetState(ConnectionState.Connecting);
        try
        {
            ClientWebSocket socket;
            try
            {
                socket = await OpenSocketAsync(qr.Host, qr.Port, qr.CertificateFingerprint, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectionFailedException ex)
            {
                throw new PairingException(ex.Failure, RejectReason.None, ex.InnerException);
            }

            using (socket)
            {
                var session = Session.Start(this, socket, cancellationToken);
                SetState(ConnectionState.AwaitingApproval);
                var response = await session.RequestAsync(
                    new PairRequestMessage { PairingToken = qr.PairingToken, DeviceName = SanitizeDeviceName(deviceName) },
                    _options.PairingTimeout,
                    cancellationToken).ConfigureAwait(false);

                session.Channel.RequestClose();
                await session.Run.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                return response switch
                {
                    PairResultMessage { Accepted: true } ok when ok.ServerId == qr.ServerId => new PairedServer
                    {
                        ServerId = qr.ServerId,
                        ServerName = string.IsNullOrWhiteSpace(ok.ServerName) ? qr.ServerName : ok.ServerName,
                        Host = qr.Host,
                        Port = qr.Port,
                        CertificateFingerprint = qr.CertificateFingerprint,
                        DeviceId = ok.DeviceId!,
                        DeviceSecret = ok.DeviceSecret!,
                        PairedAt = _time.GetUtcNow(),
                    },
                    PairResultMessage { Accepted: true } => throw new PairingException(ConnectionFailure.ProtocolError, RejectReason.ProtocolError),
                    PairResultMessage rejected => throw new PairingException(ConnectionFailure.Rejected, rejected.Reason),
                    ErrorMessage error => throw new PairingException(ConnectionFailure.ProtocolError, error.Reason),
                    _ => throw new PairingException(
                        session.Run.IsCompleted ? ConnectionFailure.ConnectionLost : ConnectionFailure.HandshakeTimeout,
                        session.DisconnectNotice),
                };
            }
        }
        finally
        {
            if (!IsRunning) SetState(ConnectionState.Disconnected);
        }
    }

    /// <summary>Starts (or restarts) a supervised connection that authenticates with stored credentials and reconnects on loss.</summary>
    public async Task StartAsync(PairedServer server, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(server);
        await StopAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _stopRequested = false;
            CurrentServer = server;
            _supervisorCts = new CancellationTokenSource();
            var token = _supervisorCts.Token;
            var name = SanitizeDeviceName(deviceName);
            _supervisor = Task.Run(() => SuperviseAsync(server, name, token), CancellationToken.None);
        }
    }

    /// <summary>Gracefully disconnects (sends a disconnect notice) and stops reconnecting.</summary>
    public async Task StopAsync()
    {
        Task? supervisor;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _stopRequested = true;
            supervisor = _supervisor;
            cts = _supervisorCts;
            _session?.Channel.RequestClose();
        }

        if (supervisor is null) return;
        await Task.WhenAny(supervisor, Task.Delay(_options.Channel.CloseTimeout, _time)).ConfigureAwait(false);
        cts?.Cancel();
        try { await supervisor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        cts?.Dispose();

        lock (_gate)
        {
            _supervisor = null;
            _supervisorCts = null;
        }
        SetState(ConnectionState.Disconnected);
    }

    /// <summary>Queues a message on the active control session. Returns false when not connected.</summary>
    public ValueTask<bool> SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        var session = _session;
        return session is null ? ValueTask.FromResult(false) : session.Channel.SendAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task SuperviseAsync(PairedServer server, string deviceName, CancellationToken ct)
    {
        var attempt = 0;
        var failure = ConnectionFailure.None;
        var reason = RejectReason.None;

        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            SetState(attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting, failure, reason);
            var outcome = await RunOnceAsync(server, deviceName, ct).ConfigureAwait(false);
            (failure, reason) = (outcome.Failure, outcome.Reason);

            if (ct.IsCancellationRequested || _stopRequested) break;
            if (outcome.Permanent)
            {
                SetState(ConnectionState.Disconnected, failure, reason);
                return;
            }

            if (outcome.WasConnected) attempt = 0;
            var delay = _options.Reconnect.GetDelay(attempt++);
            if (delay is null)
            {
                SetState(ConnectionState.Disconnected, failure, reason);
                return;
            }

            SetState(ConnectionState.Reconnecting, failure, reason);
            try { await Task.Delay(delay.Value, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        SetState(ConnectionState.Disconnected);
    }

    private readonly record struct Outcome(bool WasConnected, ConnectionFailure Failure, RejectReason Reason, bool Permanent);

    private async Task<Outcome> RunOnceAsync(PairedServer server, string deviceName, CancellationToken ct)
    {
        ClientWebSocket socket;
        try
        {
            socket = await OpenSocketAsync(server.Host, server.Port, server.CertificateFingerprint, ct).ConfigureAwait(false);
        }
        catch (ConnectionFailedException ex)
        {
            // A wrong certificate is never retried: it means the PC was reset or someone is impersonating it.
            return new Outcome(false, ex.Failure, RejectReason.None, ex.Failure == ConnectionFailure.CertificateMismatch);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(false, ConnectionFailure.None, RejectReason.None, false);
        }

        using (socket)
        {
            var session = Session.Start(this, socket, ct);
            var response = await session.RequestAsync(
                new AuthRequestMessage { DeviceId = server.DeviceId, DeviceSecret = server.DeviceSecret, DeviceName = deviceName },
                _options.HandshakeTimeout,
                ct).ConfigureAwait(false);

            if (response is AuthResultMessage { Accepted: true })
            {
                lock (_gate)
                {
                    if (_stopRequested) session.Channel.RequestClose();
                    _session = session;
                }
                SetState(ConnectionState.Connected);
                await session.Run.ConfigureAwait(false);
                lock (_gate) _session = null;

                var notice = session.DisconnectNotice;
                return new Outcome(true, ConnectionFailure.ConnectionLost, notice,
                    notice is RejectReason.DeviceRemoved or RejectReason.Replaced);
            }

            session.Channel.RequestClose();
            await session.Run.ConfigureAwait(false);

            return response switch
            {
                AuthResultMessage rejected => new Outcome(false, ConnectionFailure.Rejected, rejected.Reason,
                    rejected.Reason is RejectReason.UnknownDevice or RejectReason.Unauthorized),
                ErrorMessage error => new Outcome(false, ConnectionFailure.ProtocolError, error.Reason, false),
                _ => new Outcome(false,
                    session.Run.IsCompletedSuccessfully && !ct.IsCancellationRequested ? ConnectionFailure.ConnectionLost : ConnectionFailure.HandshakeTimeout,
                    session.DisconnectNotice, false),
            };
        }
    }

    private async Task<ClientWebSocket> OpenSocketAsync(string host, int port, string fingerprint, CancellationToken ct)
    {
        var certificateMismatch = false;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero; // application level heartbeat is used instead
        // Trust is anchored on the fingerprint from the QR code only; this callback is scoped to this socket,
        // so certificate validation is not relaxed anywhere else in the process.
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            var ok = CertificateFingerprint.Matches(certificate, fingerprint);
            if (!ok) certificateMismatch = true;
            return ok;
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(BuildUri(host, port), timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new ConnectionFailedException(
                certificateMismatch ? ConnectionFailure.CertificateMismatch : ConnectionFailure.Unreachable, ex);
        }
    }

    internal static Uri BuildUri(string host, int port)
    {
        var builder = new UriBuilder("wss", host, port, ProtocolInfo.WebSocketPath);
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            builder.Host = $"[{ip}]";
        return builder.Uri;
    }

    internal static string SanitizeDeviceName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) clean = "Android";
        return clean.Length > ProtocolInfo.MaxDeviceNameLength ? clean[..ProtocolInfo.MaxDeviceNameLength] : clean;
    }

    private void SetState(ConnectionState state, ConnectionFailure failure = ConnectionFailure.None, RejectReason reason = RejectReason.None)
    {
        lock (_gate)
        {
            if (State == state && LastFailure == failure && LastReason == reason) return;
            State = state;
            LastFailure = failure;
            LastReason = reason;
        }
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, failure, reason));
    }

    /// <summary>One socket: runs the channel and routes the handshake response.</summary>
    private sealed class Session
    {
        private readonly PocketPadClient _owner;
        private readonly TaskCompletionSource<ProtocolMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Session(PocketPadClient owner, WebSocketChannel channel)
        {
            _owner = owner;
            Channel = channel;
        }

        public WebSocketChannel Channel { get; }
        public Task<ChannelCloseReason> Run { get; private set; } = null!;
        public RejectReason DisconnectNotice { get; private set; }

        public static Session Start(PocketPadClient owner, WebSocket socket, CancellationToken ct)
        {
            var session = new Session(owner, new WebSocketChannel(socket, owner._options.Channel, owner._time));
            session.Run = session.Channel.RunAsync(session.OnMessageAsync, ct);
            return session;
        }

        public async Task<ProtocolMessage?> RequestAsync(ProtocolMessage request, TimeSpan timeout, CancellationToken ct)
        {
            if (!await Channel.SendAsync(request, ct).ConfigureAwait(false)) return null;
            var delay = Task.Delay(timeout, _owner._time, ct);
            var winner = await Task.WhenAny(_response.Task, Run, delay).ConfigureAwait(false);
            return winner == _response.Task ? _response.Task.Result : null;
        }

        private ValueTask OnMessageAsync(ProtocolMessage message)
        {
            switch (message)
            {
                case DisconnectMessage disconnect:
                    DisconnectNotice = disconnect.Reason;
                    break;
                case PairResultMessage or AuthResultMessage or ErrorMessage when !_response.Task.IsCompleted:
                    _response.TrySetResult(message);
                    break;
                default:
                    if (_response.Task.IsCompleted) _owner.MessageReceived?.Invoke(_owner, message);
                    break;
            }
            return ValueTask.CompletedTask;
        }
    }
}
