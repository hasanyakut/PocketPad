using System.Net.WebSockets;
using System.Threading.Channels;
using PocketPad.Shared.Protocol;

namespace PocketPad.Shared.Transport;

public sealed record ChannelOptions
{
    public int MaxMessageBytes { get; init; } = ProtocolInfo.MaxMessageBytes;
    /// <summary>How often a ping is sent.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Connection is considered dead when nothing was received for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int SendQueueCapacity { get; init; } = 256;
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

public enum ChannelCloseReason
{
    /// <summary>This side asked to close (<see cref="WebSocketChannel.RequestClose"/>).</summary>
    LocalClose,
    RemoteClosed,
    ConnectionLost,
    Timeout,
    ProtocolViolation,
    MessageTooLarge,
    Cancelled,
}

/// <summary>
/// One WebSocket with a receive loop, a single ordered send queue and an application level
/// ping/pong heartbeat. Used by both the Windows server and the Android client.
/// </summary>
public sealed class WebSocketChannel
{
    private readonly WebSocket _socket;
    private readonly ChannelOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<ProtocolMessage> _sendQueue;
    private long _lastReceivedTimestamp;
    private int _closeReason = -1;
    private RejectReason _closeNotice;
    private int _started;

    public WebSocketChannel(WebSocket socket, ChannelOptions? options = null, TimeProvider? timeProvider = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _options = options ?? new ChannelOptions();
        _time = timeProvider ?? TimeProvider.System;
        _sendQueue = Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(_options.SendQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _lastReceivedTimestamp = _time.GetTimestamp();
    }

    /// <summary>Last protocol error seen on the receive path (for logging, never contains payload data).</summary>
    public string? LastProtocolError { get; private set; }

    /// <summary>Queues a message. Messages are written strictly in queue order. Returns false once the channel is closing.</summary>
    public async ValueTask<bool> SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await _sendQueue.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>Flushes already queued messages (optionally followed by a <see cref="DisconnectMessage"/>) and closes the socket.</summary>
    public void RequestClose(RejectReason notice = RejectReason.None)
    {
        _closeNotice = notice;
        SetReason(ChannelCloseReason.LocalClose);
        _sendQueue.Writer.TryComplete();
    }

    /// <summary>Runs until the socket closes, the heartbeat times out, a protocol violation occurs or the token is cancelled.</summary>
    public async Task<ChannelCloseReason> RunAsync(
        Func<ProtocolMessage, ValueTask> onMessage,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("Channel already running.");

        using var loopsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveLoopAsync(onMessage, loopsCts.Token);
        var heartbeat = HeartbeatLoopAsync(loopsCts.Token);
        var send = SendLoopAsync(loopsCts.Token);

        await Task.WhenAny(receive, heartbeat, send).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) SetReason(ChannelCloseReason.Cancelled);

        // Let already queued messages (e.g. an error/disconnect notice) go out before tearing down.
        _sendQueue.Writer.TryComplete();
        await Task.WhenAny(send, Task.Delay(_options.CloseTimeout, _time, CancellationToken.None)).ConfigureAwait(false);

        // Cancelling a pending WebSocket operation aborts the socket (and may drop unsent data),
        // so close gracefully first and only cancel what is still running afterwards.
        if (send.IsCompleted)
        {
            await CloseSocketAsync().ConfigureAwait(false);
            await Task.WhenAny(receive, Task.Delay(_options.CloseTimeout, _time, CancellationToken.None)).ConfigureAwait(false);
        }

        loopsCts.Cancel();
        try { await Task.WhenAll(receive, heartbeat, send).ConfigureAwait(false); }
        catch { /* loop failures are already reflected in the close reason */ }

        SetReason(ChannelCloseReason.ConnectionLost); // no-op unless nothing else was recorded
        return (ChannelCloseReason)Volatile.Read(ref _closeReason);
    }

    private async Task ReceiveLoopAsync(Func<ProtocolMessage, ValueTask> onMessage, CancellationToken ct)
    {
        var buffer = new byte[_options.MaxMessageBytes];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var count = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (count >= buffer.Length)
                    {
                        Violation(ChannelCloseReason.MessageTooLarge, "Message too large.");
                        return;
                    }
                    result = await _socket.ReceiveAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SetReason(ChannelCloseReason.RemoteClosed);
                        return;
                    }
                    count += result.Count;
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    Violation(ChannelCloseReason.ProtocolViolation, "Binary frames are not allowed.");
                    return;
                }

                Interlocked.Exchange(ref _lastReceivedTimestamp, _time.GetTimestamp());

                var read = MessageSerializer.Deserialize(buffer.AsSpan(0, count), _options.MaxMessageBytes);
                if (!read.Success)
                {
                    Violation(ChannelCloseReason.ProtocolViolation, $"{read.Error}: {read.Detail}");
                    return;
                }

                switch (read.Message)
                {
                    case PingMessage ping:
                        _sendQueue.Writer.TryWrite(new PongMessage { Timestamp = ping.Timestamp });
                        break;
                    case PongMessage:
                        break;
                    default:
                        await onMessage(read.Message!).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            SetReason(ChannelCloseReason.ConnectionLost);
        }
        catch (IOException)
        {
            SetReason(ChannelCloseReason.ConnectionLost);
        }
    }

    private void Violation(ChannelCloseReason reason, string detail)
    {
        LastProtocolError = detail;
        SetReason(reason);
        _closeNotice = RejectReason.ProtocolError;
        _sendQueue.Writer.TryWrite(new ErrorMessage { Reason = RejectReason.ProtocolError, Detail = "Invalid message." });
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var idle = _time.GetElapsedTime(Interlocked.Read(ref _lastReceivedTimestamp));
                if (idle >= _options.IdleTimeout)
                {
                    SetReason(ChannelCloseReason.Timeout);
                    return;
                }
                _sendQueue.Writer.TryWrite(new PingMessage { Timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds() });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WriteAsync(message, ct).ConfigureAwait(false);
            }

            // Queue completed normally: send the graceful notice if this side initiated the close.
            if (_closeNotice != RejectReason.None || Volatile.Read(ref _closeReason) == (int)ChannelCloseReason.LocalClose)
                await WriteAsync(new DisconnectMessage { Reason = _closeNotice }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            SetReason(ChannelCloseReason.ConnectionLost);
        }
    }

    private ValueTask WriteAsync(ProtocolMessage message, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open) return ValueTask.CompletedTask;
        return _socket.SendAsync(new ReadOnlyMemory<byte>(MessageSerializer.Serialize(message)), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task CloseSocketAsync()
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        var reason = (ChannelCloseReason)Volatile.Read(ref _closeReason);
        var status = reason switch
        {
            ChannelCloseReason.ProtocolViolation => WebSocketCloseStatus.PolicyViolation,
            ChannelCloseReason.MessageTooLarge => WebSocketCloseStatus.MessageTooBig,
            _ when _closeNotice is RejectReason.Unauthorized or RejectReason.UnknownDevice or RejectReason.InvalidPairingToken
                => WebSocketCloseStatus.PolicyViolation,
            _ => WebSocketCloseStatus.NormalClosure,
        };

        using var cts = new CancellationTokenSource(_options.CloseTimeout);
        try
        {
            await _socket.CloseOutputAsync(status, reason.ToString(), cts.Token).ConfigureAwait(false);
        }
        catch
        {
            _socket.Abort();
        }
    }

    private void SetReason(ChannelCloseReason reason) =>
        Interlocked.CompareExchange(ref _closeReason, (int)reason, -1);
}
