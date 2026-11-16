using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using PocketPad.Shared.Protocol;
using PocketPad.Shared.Transport;

namespace PocketPad.Desktop.Server;

/// <summary>
/// Server side of one phone connection. State machine:
/// Handshaking → (PairRequest → AwaitingApproval → closed) | (AuthRequest → Controlling).
/// Input messages are only accepted in <see cref="SessionStatus.Controlling"/>.
/// </summary>
internal sealed class ClientSession
{
    private readonly ServerContext _ctx;
    private readonly WebSocketChannel _channel;
    private readonly CancellationTokenSource _lifetime = new();
    private volatile SessionStatus _status = SessionStatus.Handshaking;

    public ClientSession(WebSocket socket, string remoteAddress, ServerContext ctx)
    {
        _ctx = ctx;
        RemoteAddress = remoteAddress;
        ConnectedAt = ctx.Time.GetUtcNow();
        _channel = new WebSocketChannel(socket, ctx.Options.Channel, ctx.Time);
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string RemoteAddress { get; }
    public DateTimeOffset ConnectedAt { get; }
    public SessionStatus Status => _status;
    public string? DeviceId { get; private set; }
    public string? DeviceName { get; private set; }

    public ConnectionSnapshot ToSnapshot() => new(Id, RemoteAddress, _status, DeviceId, DeviceName, ConnectedAt);

    public void Close(RejectReason reason) => _channel.RequestClose(reason);

    /// <summary>True once AuthResult is queued: pushes sent earlier would reach the phone before its handshake completes.</summary>
    public bool IsReadyForPush { get; private set; }

    /// <summary>Server-initiated message (e.g. media status). Fire-and-forget into the ordered send queue.</summary>
    public bool TrySend(ProtocolMessage message)
    {
        if (!IsReadyForPush) return false;
        var pending = _channel.SendAsync(message);
        return !pending.IsCompleted || pending.Result;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _ = HandshakeWatchdogAsync(_lifetime.Token);
        var reason = await _channel.RunAsync(OnMessageAsync, cancellationToken).ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_channel.LastProtocolError is { } error)
            _ctx.Logger.LogWarning("Session {Session} from {Remote} closed after protocol error: {Error}", Id, RemoteAddress, error);
        else
            _ctx.Logger.LogInformation("Session {Session} ({Device}) closed: {Reason}", Id, DeviceName ?? RemoteAddress, reason);
    }

    private async Task HandshakeWatchdogAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_ctx.Options.HandshakeTimeout, _ctx.Time, ct).ConfigureAwait(false);
            if (_status == SessionStatus.Handshaking)
            {
                _ctx.Logger.LogInformation("Session {Session} from {Remote} did not authenticate in time.", Id, RemoteAddress);
                Close(RejectReason.Unauthorized);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async ValueTask OnMessageAsync(ProtocolMessage message)
    {
        switch (_status, message)
        {
            case (_, DisconnectMessage):
                // Peer closes right after; nothing to do.
                break;
            case (SessionStatus.Handshaking, PairRequestMessage pair):
                _status = SessionStatus.AwaitingApproval;
                DeviceName = pair.DeviceName;
                _ctx.Registry.RaiseChanged();
                // Do not block the receive loop: heartbeats must keep flowing while the user decides.
                _ = HandlePairingAsync(pair);
                break;
            case (SessionStatus.Handshaking, AuthRequestMessage auth):
                await HandleAuthAsync(auth).ConfigureAwait(false);
                break;
            case (SessionStatus.Controlling, IInputMessage) when _ctx.Registry.IsActive(this):
                // Only the single active, authenticated control session reaches the input controller.
                var delivered = _ctx.Input.Dispatch(message);
                _ctx.Registry.ReportInput(this, message);
                if (message is MediaCommandMessage media)
                {
                    // Tell the phone whether Windows accepted the key; whether an app reacted shows in the next status.
                    await _channel.SendAsync(new MediaCommandResultMessage { Command = media.Command, Delivered = delivered }).ConfigureAwait(false);
                    _ctx.Media?.RequestRefresh();
                }
                break;
            default:
                _ctx.Logger.LogWarning("Session {Session} from {Remote} sent unauthorized '{Type}' in state {State}.",
                    Id, RemoteAddress, MessageSerializer.GetTypeName(message), _status);
                await _channel.SendAsync(new ErrorMessage { Reason = RejectReason.Unauthorized }).ConfigureAwait(false);
                Close(RejectReason.Unauthorized);
                break;
        }
    }

    private async Task HandleAuthAsync(AuthRequestMessage auth)
    {
        var device = _ctx.Devices.Verify(auth.DeviceId, auth.DeviceSecret);
        if (device is null)
        {
            _ctx.Logger.LogWarning("Rejected unknown device credentials from {Remote}.", RemoteAddress);
            await RejectAsync(new AuthResultMessage { Accepted = false, Reason = RejectReason.UnknownDevice }, RejectReason.UnknownDevice).ConfigureAwait(false);
            return;
        }

        DeviceId = device.DeviceId;
        DeviceName = auth.DeviceName;

        if (_ctx.Registry.TryActivate(this) == ActivationResult.Busy)
        {
            _ctx.Logger.LogInformation("Device {DeviceId} refused: another control session is active.", device.DeviceId);
            await RejectAsync(new AuthResultMessage { Accepted = false, Reason = RejectReason.Busy }, RejectReason.Busy).ConfigureAwait(false);
            return;
        }

        _status = SessionStatus.Controlling;
        _ctx.Devices.MarkSeen(device.DeviceId, auth.DeviceName);
        await _channel.SendAsync(new AuthResultMessage { Accepted = true, ServerName = _ctx.Identity.ServerName }).ConfigureAwait(false);
        IsReadyForPush = true;
        _ctx.Media?.OnControlSessionReady();
        _ctx.Registry.RaiseChanged();
        _ctx.Logger.LogInformation("Device {DeviceId} ({Name}) is now the control session.", device.DeviceId, auth.DeviceName);
    }

    private async Task HandlePairingAsync(PairRequestMessage pair)
    {
        try
        {
            if (!_ctx.Pairing.TryConsume(pair.PairingToken))
            {
                _ctx.Logger.LogWarning("Pairing attempt from {Remote} with an invalid, used or expired code.", RemoteAddress);
                await RejectAsync(new PairResultMessage { Accepted = false, Reason = RejectReason.InvalidPairingToken },
                    RejectReason.InvalidPairingToken).ConfigureAwait(false);
                return;
            }

            bool approved;
            RejectReason denyReason;
            using (var timeout = new CancellationTokenSource(_ctx.Options.ApprovalTimeout, _ctx.Time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token))
            {
                try
                {
                    approved = await _ctx.Approval.RequestApprovalAsync(
                        new PairingApprovalRequest(pair.DeviceName, pair.Platform, RemoteAddress), linked.Token).ConfigureAwait(false);
                    denyReason = RejectReason.UserDenied;
                }
                catch (OperationCanceledException)
                {
                    approved = false;
                    denyReason = RejectReason.ApprovalTimeout;
                }
            }

            if (_lifetime.IsCancellationRequested)
                return; // phone went away; do not create credentials nobody will receive

            if (!approved)
            {
                _ctx.Logger.LogInformation("Pairing of '{Name}' from {Remote} was not approved ({Reason}).", pair.DeviceName, RemoteAddress, denyReason);
                await RejectAsync(new PairResultMessage { Accepted = false, Reason = denyReason }, denyReason).ConfigureAwait(false);
                return;
            }

            var (device, secret) = _ctx.Devices.Add(pair.DeviceName, pair.Platform);
            DeviceId = device.DeviceId;
            await _channel.SendAsync(new PairResultMessage
            {
                Accepted = true,
                DeviceId = device.DeviceId,
                DeviceSecret = secret,
                ServerId = _ctx.Identity.ServerId,
                ServerName = _ctx.Identity.ServerName,
            }).ConfigureAwait(false);
            // Pairing sockets never become control sessions; the phone reconnects and authenticates.
            Close(RejectReason.None);
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "Pairing failed unexpectedly.");
            Close(RejectReason.ProtocolError);
        }
    }

    private async Task RejectAsync(ProtocolMessage response, RejectReason reason)
    {
        await _channel.SendAsync(response).ConfigureAwait(false);
        Close(reason);
    }
}
