using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PocketPad.Shared.Pairing;

namespace PocketPad.Shared.Discovery;

/// <summary>
/// LAN discovery: the phone broadcasts <see cref="Request"/> on UDP <see cref="Port"/>; a PC that is in
/// pairing mode ("Cihaz eşleştir" open) answers with its current pairing invitation (same content as the QR code).
/// Outside pairing mode the PC does not answer. Pairing still requires the user's approval on the PC.
/// </summary>
public static class DiscoveryProtocol
{
    public const int Port = 45901;
    public const int MaxResponseBytes = 1024;
    public static ReadOnlySpan<byte> Request => "PocketPad-Discover/1"u8;
}

public static class LanDiscoveryClient
{
    /// <summary>
    /// Broadcasts a discovery request and collects answers until <paramref name="listenTime"/> elapses.
    /// The returned payloads use the address the answer came from as host.
    /// </summary>
    public static async Task<IReadOnlyList<PairingQrPayload>> DiscoverAsync(
        TimeSpan listenTime,
        IEnumerable<IPEndPoint>? targets = null,
        CancellationToken cancellationToken = default)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var request = DiscoveryProtocol.Request.ToArray();

        foreach (var target in targets ?? BroadcastTargets(DiscoveryProtocol.Port))
        {
            try { await udp.SendAsync(request, target, cancellationToken).ConfigureAwait(false); }
            catch (SocketException) { /* interface down / not routable: try the others */ }
        }

        var found = new Dictionary<string, PairingQrPayload>(StringComparer.Ordinal);
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listen.CancelAfter(listenTime);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(listen.Token).ConfigureAwait(false);
                if (result.Buffer.Length > DiscoveryProtocol.MaxResponseBytes) continue;

                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!PairingQrPayload.TryParse(text, out var payload, out _)) continue;

                var source = result.RemoteEndPoint.Address;
                if (source.IsIPv4MappedToIPv6) source = source.MapToIPv4();
                found[payload!.ServerId] = payload with { Host = source.ToString() };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // listen time over
        }
        catch (SocketException)
        {
            // e.g. ICMP port unreachable on some platforms: return what we have
        }

        return [.. found.Values];
    }

    /// <summary>Limited broadcast plus the directed broadcast of every active IPv4 interface.</summary>
    public static IReadOnlyList<IPEndPoint> BroadcastTargets(int port)
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0)) continue;
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    targets.Add(new IPEndPoint(new IPAddress(broadcast), port));
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotImplementedException)
        {
            // Interface enumeration is restricted on some Android versions; the limited broadcast still works.
        }
        return [.. targets.DistinctBy(t => t.ToString())];
    }
}
