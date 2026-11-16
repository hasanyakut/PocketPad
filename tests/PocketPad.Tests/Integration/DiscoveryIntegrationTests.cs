using System.Net;
using System.Net.Sockets;
using PocketPad.Shared.Client;
using PocketPad.Shared.Discovery;

namespace PocketPad.Tests.Integration;

public class DiscoveryIntegrationTests
{
    private static readonly TimeSpan Listen = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Discovery_AnswersOnlyInPairingMode_AndDiscoveredPcCanBePaired()
    {
        var udpPort = TestHost.FreeUdpPort();
        await using var host = await TestHost.StartAsync(o => o with { DiscoveryPort = udpPort });
        IPEndPoint[] target = [new(IPAddress.Loopback, udpPort)];

        // Not in pairing mode: silent.
        Assert.Empty(await LanDiscoveryClient.DiscoverAsync(Listen, target));

        host.Pairing.CreateInvitation("192.168.50.50", host.Port, host.Fingerprint);
        var found = Assert.Single(await LanDiscoveryClient.DiscoverAsync(Listen, target));
        Assert.Equal(host.Identity.ServerId, found.ServerId);
        Assert.Equal("127.0.0.1", found.Host); // reply source address wins over the advertised one
        Assert.Equal(host.Fingerprint, found.CertificateFingerprint);

        // Pairing via discovery still needs the PC user's approval.
        host.Approval.Decide = (_, _) => Task.FromResult(false);
        await using (var denied = new PocketPadClient(TestHost.FastClient))
        {
            var ex = await Assert.ThrowsAsync<PairingException>(() => denied.PairAsync(found, "Phone", CancellationToken.None));
            Assert.Equal(Shared.Protocol.RejectReason.UserDenied, ex.Reason);
        }
        Assert.Empty(host.Devices.GetAll());

        host.Pairing.CreateInvitation("192.168.50.50", host.Port, host.Fingerprint);
        found = Assert.Single(await LanDiscoveryClient.DiscoverAsync(Listen, target));
        host.Approval.Decide = (_, _) => Task.FromResult(true);
        await using var client = new PocketPadClient(TestHost.FastClient);
        var credentials = await client.PairAsync(found, "Phone", CancellationToken.None);
        Assert.Equal(host.Identity.ServerId, credentials.ServerId);

        // Invitation consumed: no longer discoverable.
        Assert.Empty(await LanDiscoveryClient.DiscoverAsync(Listen, target));
    }

    [Fact]
    public async Task Discovery_IgnoresForeignRequests()
    {
        var udpPort = TestHost.FreeUdpPort();
        await using var host = await TestHost.StartAsync(o => o with { DiscoveryPort = udpPort });
        host.Pairing.CreateInvitation("127.0.0.1", host.Port, host.Fingerprint);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync("hello"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, udpPort));
        using var cts = new CancellationTokenSource(Listen);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await udp.ReceiveAsync(cts.Token));
    }

    [Fact]
    public void BroadcastTargets_AlwaysIncludeLimitedBroadcast() =>
        Assert.Contains(LanDiscoveryClient.BroadcastTargets(DiscoveryProtocol.Port), t => t.Address.Equals(IPAddress.Broadcast));
}
