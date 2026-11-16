using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using PocketPad.Shared.Discovery;

namespace PocketPad.Desktop.Server;

/// <summary>
/// Answers LAN discovery requests from phones, but only while a pairing invitation is active
/// (user pressed "Cihaz eşleştir") and only for private-network senders. The answer is the same
/// invitation the QR code carries; the user still has to approve the pairing on this PC.
/// </summary>
internal sealed class DiscoveryResponder : IAsyncDisposable
{
    private static readonly TimeSpan MinReplyInterval = TimeSpan.FromMilliseconds(100);

    private readonly PairingService _pairing;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private long _lastReply = long.MinValue;

    public DiscoveryResponder(IPAddress listenAddress, int port, PairingService pairing, TimeProvider time, ILogger logger)
    {
        _pairing = pairing;
        _time = time;
        _logger = logger;
        _udp = new UdpClient(new IPEndPoint(listenAddress, port));
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // e.g. ICMP reset from a previous reply; keep listening
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!request.Buffer.AsSpan().SequenceEqual(DiscoveryProtocol.Request)) continue;
            if (!LocalNetwork.IsAllowedRemote(request.RemoteEndPoint.Address)) continue;
            if (!_pairing.TryGetActiveInvitation(out var invitation)) continue;

            var now = _time.GetTimestamp();
            if (_lastReply != long.MinValue && _time.GetElapsedTime(_lastReply, now) < MinReplyInterval) continue;
            _lastReply = now;

            try
            {
                await _udp.SendAsync(Encoding.UTF8.GetBytes(invitation!.ToUri()), request.RemoteEndPoint, ct).ConfigureAwait(false);
                _logger.LogInformation("Answered discovery request from {Remote}.", request.RemoteEndPoint.Address);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _udp.Dispose();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
